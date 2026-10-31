"""WhaleGo AstrBot 插件的 L1 MCP 连接层。

本模块不得 import astrbot。

设计要点（ARCHITECTURE §4.6 / PROTOCOL §7 / D12）：
- `McpConnection.connect()` 每次连接都在一个独立的 owner task 中进入/退出所有
  anyio cancel scope（AsyncExitStack），避免 "exit cancel scope in a different task"；
- `ClientSession.call_tool` 由调用方用 `asyncio.wait_for` 约束时长；超时/断线后
  连接标记 unclean，动作类**绝不重发**（由 client.py 呈现 DISPATCH_UNCERTAIN）；
- `ReconnectSupervisor` 后台指数退避重连（1s → 30s），不阻塞 AstrBot 启动；
- 401/协议不匹配由 `McpClientError.kind` 区分，上层不重试。
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator, Callable
from contextlib import AsyncExitStack
from enum import StrEnum
from typing import Any, Final

from . import compat
from .config import ConnectionConfig

LOGGER = logging.getLogger("astrbot.whalego.mcp_client")

DEFAULT_CONNECT_TIMEOUT: Final = 15.0
RECONNECT_INITIAL_DELAY: Final = 1.0
RECONNECT_MAX_DELAY: Final = 30.0


class ConnectionState(StrEnum):
    DISCONNECTED = "disconnected"
    CONNECTING = "connecting"
    READY = "ready"
    BACKOFF = "backoff"
    CLOSED = "closed"


class McpClientError(Exception):
    """连接/调用层错误；kind ∈ compat.CONNECTION_ERROR_KINDS。"""

    def __init__(self, kind: str, message: str = "", *, cause: BaseException | None = None) -> None:
        self.kind = kind
        super().__init__(message or f"mcp {kind} error")
        if cause is not None:
            self.__cause__ = cause


def translate_exception(exc: BaseException, *, phase: str = "call") -> McpClientError:
    """把原始异常翻译成带 kind 的 McpClientError。"""
    kind = compat.classify_exception(exc)
    if phase == "connect" and kind in (compat.LOST_ERROR, compat.TIMEOUT_ERROR):
        # 连接阶段无法区分“服务没起来”与“连上后立刻断开”，按不可达/超时均可排查；
        # 统一按 unreachable 呈现（ERROR[EXECUTOR_UNREACHABLE]）。
        kind = compat.UNREACHABLE_ERROR
    return McpClientError(kind, f"mcp {phase} failed: {exc!r}", cause=exc)


def backoff_delays(
    initial: float = RECONNECT_INITIAL_DELAY,
    maximum: float = RECONNECT_MAX_DELAY,
) -> AsyncIterator[float]:
    """指数退避序列：1, 2, 4, ... 封顶 30（生成器，便于单测锁定）。"""
    delay = initial
    while True:
        yield delay
        delay = min(delay * 2, maximum)


class McpConnection:
    """一个 Streamable HTTP 连接的生命周期门面。

    owner task 持有连接（AsyncExitStack 与 anyio cancel scope），调用方通过
    `call()` 直接使用 ClientSession（MCP 请求可并发），通过 `close()` 结束连接。
    """

    def __init__(
        self,
        config: ConnectionConfig,
        *,
        transport: Callable[[ConnectionConfig], Any] | None = None,
        session_factory: Callable[[Any, Any], Any] | None = None,
        connect_timeout: float = DEFAULT_CONNECT_TIMEOUT,
        logger: logging.Logger | None = None,
    ) -> None:
        self.config = config
        self._transport = transport or (lambda cfg: compat.open_transport(cfg))
        self._session_factory = session_factory or compat.create_session
        self._connect_timeout = max(1.0, float(connect_timeout))
        self._logger = logger or LOGGER

        self._state = ConnectionState.DISCONNECTED
        self._unclean = False
        self._ever_connected = False
        self._closed = False
        self.auth_failed = False
        self._session: Any | None = None
        self._owner_task: asyncio.Task[None] | None = None
        self._old_tasks: list[asyncio.Task[None]] = []
        self._connect_lock = asyncio.Lock()
        self._ready = asyncio.Event()
        self._disconnected = asyncio.Event()
        self._disconnected.set()
        self.last_error: McpClientError | None = None

    # ------------------------------------------------------------------ 属性
    @property
    def state(self) -> ConnectionState:
        return self._state

    @property
    def ready(self) -> bool:
        return self._state is ConnectionState.READY and not self._unclean

    @property
    def unclean(self) -> bool:
        return self._unclean

    @property
    def ever_connected(self) -> bool:
        return self._ever_connected

    @property
    def disconnected_event(self) -> asyncio.Event:
        return self._disconnected

    # ------------------------------------------------------------------ 连接
    async def connect(self) -> None:
        """单次连接尝试；失败抛 McpClientError（kind 决定上层语义）。"""
        async with self._connect_lock:
            if self._closed:
                raise McpClientError(compat.UNREACHABLE_ERROR, "连接已关闭")
            if self.ready:
                return
            await self._cancel_owner()
            self._unclean = False
            self._state = ConnectionState.CONNECTING
            ready_future: asyncio.Future[None] = asyncio.get_running_loop().create_future()
            self._owner_task = asyncio.create_task(self._run_owner(ready_future), name="whalego-mcp-owner")
            try:
                await asyncio.wait_for(asyncio.shield(ready_future), timeout=self._connect_timeout)
            except TimeoutError as exc:
                await self._cancel_owner()
                self._mark_disconnected()
                if ready_future.done():
                    with contextlib.suppress(BaseException):
                        ready_future.exception()
                error = McpClientError(
                    compat.UNREACHABLE_ERROR,
                    f"连接执行器超时（>{self._connect_timeout:.0f}s）：{self.config.url}",
                    cause=exc,
                )
                self.last_error = error
                raise error from exc
            except McpClientError as exc:
                await self._cancel_owner()
                self._mark_disconnected()
                self.last_error = exc
                # 401/403 不重试（PROTOCOL §8.1：AUTH_FAILED/never）；标记出来让上层
                # 工具调用直接给出 AUTH_FAILED，而不是误报“不可达/连接丢失”。
                self.auth_failed = exc.kind == compat.AUTH_ERROR
                raise
            except Exception as exc:  # pragma: no cover - 防御性兜底
                await self._cancel_owner()
                self._mark_disconnected()
                error = translate_exception(exc, phase="connect")
                self.last_error = error
                raise error from exc

    async def _run_owner(self, ready_future: asyncio.Future[None]) -> None:
        """连接 owner task：进入运输层/会话，成功后 resolve ready_future 并保持。

        注意：底层传输失败（如 401）在 mcp SDK 的 anyio TaskGroup 里会先以
        CancelledError 投递到本 task，真实原因保存在 AsyncExitStack.aclose() 抛出的
        ExceptionGroup 中（含 httpx.HTTPStatusError）。因此不能一见到 CancelledError
        就当作“关闭”，必须等资源关闭后再翻译真实原因，否则 401 会被误判为 lost。
        """
        stack = AsyncExitStack()
        failure: BaseException | None = None
        cancelled: asyncio.CancelledError | None = None
        try:
            raw_streams = await stack.enter_async_context(self._transport(self.config))
            streams = compat.coerce_streams(raw_streams)
            session = await stack.enter_async_context(self._session_factory(streams.read, streams.write))
            await asyncio.wait_for(session.initialize(), timeout=self._connect_timeout)
            self._session = session
            self._unclean = False
            self._ever_connected = True
            self.auth_failed = False
            self._state = ConnectionState.READY
            self._ready.set()
            self._disconnected.clear()
            if not ready_future.done():
                ready_future.set_result(None)
            self._logger.info("WhaleGo MCP 已连接：%s", self.config.url)
            await asyncio.Event().wait()
        except asyncio.CancelledError as exc:
            cancelled = exc
        except BaseException as exc:  # noqa: BLE001 - 连接失败需翻译后交给调用方
            failure = exc
        finally:
            self._session = None
            self._ready.clear()
            self._disconnected.set()
            close_error: BaseException | None = None
            try:
                await stack.aclose()
            except asyncio.CancelledError as exc:
                close_error = exc
            except Exception as exc:  # noqa: BLE001 - 关闭失败不应掩盖主错误
                close_error = exc
                self._logger.debug("关闭 MCP 连接资源失败：%r", exc)
            if self._state is not ConnectionState.CLOSED:
                self._state = ConnectionState.DISCONNECTED
            if not ready_future.done() and not self._closed:
                underlying = failure if failure is not None else (close_error or cancelled)
                error = (
                    translate_exception(underlying, phase="connect")
                    if underlying is not None
                    else McpClientError(compat.LOST_ERROR, "连接未建立且无明确异常")
                )
                self.last_error = error
                if not ready_future.done():
                    ready_future.set_exception(error)
                self._logger.warning("WhaleGo MCP 连接失败：%s", error)
        if cancelled is not None:
            raise cancelled

    async def close(self) -> None:
        """关闭连接（幂等）；必须在可等待上下文中调用。

        先取消 owner task（唤醒阻塞在 connect() 里的等待者），再取连接锁收尾，
        避免 terminate() 在初始连接超时窗口内被拖住。
        """
        if self._closed:
            return
        self._closed = True
        self._state = ConnectionState.CLOSED
        self._session = None
        self._ready.clear()
        self._disconnected.set()
        owner = self._owner_task
        self._owner_task = None
        if owner is not None and not owner.done():
            owner.cancel()
            self._old_tasks.append(owner)
        async with self._connect_lock:
            await self._reap_old_tasks()

    async def _cancel_owner(self) -> None:
        task = self._owner_task
        self._owner_task = None
        if task is not None and not task.done():
            task.cancel()
            self._old_tasks.append(task)
        await self._reap_old_tasks()

    async def _reap_old_tasks(self) -> None:
        pending = [task for task in self._old_tasks if not task.done()]
        self._old_tasks = [task for task in self._old_tasks if not task.done()]
        if not pending:
            return
        await asyncio.wait(pending, timeout=5.0)

    def _mark_disconnected(self) -> None:
        self._session = None
        self._ready.clear()
        self._disconnected.set()
        if self._state is not ConnectionState.CLOSED:
            self._state = ConnectionState.DISCONNECTED

    def mark_unclean(self) -> None:
        """调用超时/断线后标记连接不干净，触发 supervisor 重连。"""
        self._unclean = True
        self._session = None
        self._ready.clear()
        self._disconnected.set()
        if self._state is not ConnectionState.CLOSED:
            self._state = ConnectionState.BACKOFF

    # ------------------------------------------------------------------ 调用
    async def call(self, tool: str, args: dict[str, Any], timeout: float) -> Any:
        """调用 MCP 工具；超时/断线会标记 unclean 并抛 McpClientError。"""
        if self._state is ConnectionState.CLOSED:
            raise McpClientError(compat.UNREACHABLE_ERROR, "连接已关闭")
        if self._unclean:
            raise McpClientError(compat.LOST_ERROR, "连接不干净，需要重连")
        session = self._session
        if self._state is not ConnectionState.READY or session is None:
            kind = compat.LOST_ERROR if self._ever_connected else compat.UNREACHABLE_ERROR
            raise McpClientError(kind, "MCP 连接尚未就绪")
        try:
            return await asyncio.wait_for(session.call_tool(tool, args), timeout=timeout)
        except TimeoutError as exc:
            self.mark_unclean()
            raise McpClientError(
                compat.TIMEOUT_ERROR, f"调用 {tool} 超时（>{timeout:.1f}s）", cause=exc
            ) from exc
        except asyncio.CancelledError:
            self.mark_unclean()
            raise
        except McpClientError:
            raise
        except Exception as exc:  # noqa: BLE001 - 统一翻译为协议层错误
            self.mark_unclean()
            error = translate_exception(exc, phase="call")
            raise error from exc

    async def wait_ready(self, timeout: float) -> bool:
        """等待连接就绪（供只读工具“重连后重试一次”使用）。"""
        if self.ready:
            return True
        with contextlib.suppress(TimeoutError):
            await asyncio.wait_for(self._ready.wait(), timeout=timeout)
        return self.ready


class ReconnectSupervisor:
    """后台指数退避重连循环（1s → 30s），非阻塞、可取消。

    `run()` 在独立 task 中执行：连接失败按退避等待；连接成功后等待断线事件；
    收到 stop 请求后在本 task 内关闭连接（保证 anyio cancel scope 的 owner 一致）。
    """

    def __init__(
        self,
        connection: McpConnection,
        *,
        initial_delay: float = RECONNECT_INITIAL_DELAY,
        max_delay: float = RECONNECT_MAX_DELAY,
        sleep: Callable[[float], Any] | None = None,
        logger: logging.Logger | None = None,
        on_connected: Callable[[], Any] | None = None,
    ) -> None:
        self._connection = connection
        self._initial_delay = initial_delay
        self._max_delay = max_delay
        self._sleep = sleep or asyncio.sleep
        self._logger = logger or LOGGER
        self._on_connected = on_connected
        self._stop = asyncio.Event()
        self._task: asyncio.Task[None] | None = None
        self.attempts = 0
        self.last_error: McpClientError | None = None

    @property
    def stopped(self) -> bool:
        return self._stop.is_set()

    def request_stop(self) -> None:
        self._stop.set()

    async def run(self) -> None:
        self._task = asyncio.current_task()
        delay = self._initial_delay
        try:
            while not self._stop.is_set():
                if self._connection.state is ConnectionState.CLOSED:
                    break
                if not self._connection.ready:
                    try:
                        await self._connection.connect()
                    except McpClientError as exc:
                        self.attempts += 1
                        self.last_error = exc
                        self._logger.warning(
                            "WhaleGo 执行器连接失败（第 %d 次），%.0fs 后重试：%s",
                            self.attempts,
                            delay,
                            exc,
                        )
                        if await self._sleep_or_stop(delay):
                            break
                        delay = min(delay * 2, self._max_delay)
                        continue
                    delay = self._initial_delay
                    self.attempts = 0
                    if self._on_connected is not None:
                        try:
                            result = self._on_connected()
                            if asyncio.iscoroutine(result):
                                await result
                        except Exception as exc:  # noqa: BLE001 - 回调失败不影响重连
                            self._logger.warning("WhaleGo 连接后回调失败：%r", exc)
                    continue
                await self._wait_for_disconnect()
            self._logger.info("WhaleGo 重连 supervisor 退出")
        finally:
            self._task = None
            with contextlib.suppress(Exception):
                await self._connection.close()

    async def stop(self, timeout: float = 10.0) -> None:
        """请求停止并等待 run() 完成（含连接关闭）。"""
        self.request_stop()
        task = self._task
        if task is None or task.done():
            with contextlib.suppress(Exception):
                await self._connection.close()
            return
        with contextlib.suppress(TimeoutError, asyncio.CancelledError):
            await asyncio.wait_for(asyncio.shield(task), timeout=timeout)

    async def _wait_for_disconnect(self) -> None:
        stopper = asyncio.create_task(self._stop.wait(), name="whalego-stop-wait")
        disconnect = asyncio.create_task(
            self._connection.disconnected_event.wait(), name="whalego-disconnect-wait"
        )
        try:
            await asyncio.wait({stopper, disconnect}, return_when=asyncio.FIRST_COMPLETED)
        finally:
            for task in (stopper, disconnect):
                task.cancel()
            await asyncio.gather(stopper, disconnect, return_exceptions=True)

    async def _sleep_or_stop(self, delay: float) -> bool:
        stopper = asyncio.create_task(self._stop.wait(), name="whalego-stop-wait")
        sleeper = asyncio.create_task(self._sleep(delay), name="whalego-backoff-sleep")
        try:
            await asyncio.wait({stopper, sleeper}, return_when=asyncio.FIRST_COMPLETED)
        finally:
            for task in (stopper, sleeper):
                task.cancel()
            await asyncio.gather(stopper, sleeper, return_exceptions=True)
        return self._stop.is_set()


__all__ = [
    "ConnectionState",
    "McpClientError",
    "McpConnection",
    "ReconnectSupervisor",
    "backoff_delays",
    "translate_exception",
]
