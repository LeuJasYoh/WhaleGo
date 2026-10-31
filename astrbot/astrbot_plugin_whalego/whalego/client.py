"""WhaleGo AstrBot 插件的 L2 应用层：7 个工具的强类型封装与调用编排。

本模块不得 import astrbot。

调用编排（ARCHITECTURE §4.3 / PROTOCOL §7 / D12）：
- 每个工具调用内部注入 `session_id`（umo 派生，模型不可见）；go_stop 额外注入 scope="session"；
- `asyncio.wait_for(timeout)` 约束单次 MCP 调用；
- 只读类工具（health/apps/windows/observe/stop）失败后：重连一次并重试一次，
  再失败映射为 EXECUTOR_TIMEOUT / CONNECTION_LOST / EXECUTOR_UNREACHABLE；
- 动作类（go_act）与写动作（go_launch）超时/断线：**绝不重发**，按 DISPATCH_UNCERTAIN
  （possibly_sent、retry=reobserve）呈现；
- 401 → AUTH_FAILED、协议不匹配 → PROTOCOL_MISMATCH，均不重试。
"""

from __future__ import annotations

import logging
import time
from typing import Any, Final

from . import compat, convert, errors
from .config import PROTOCOL_VERSION, ConnectionConfig, WhaleGoConfig
from .convert import ToolCallOutcome, error_outcome, ok_outcome
from .errors import GoError
from .mcp_client import McpClientError, McpConnection
from .session import session_id_for_umo

LOGGER = logging.getLogger("astrbot.whalego.client")

#: 只读/查询类工具（PROTOCOL §2.1：这些工具成功判据为文本首行 OK:）。
READ_TOOLS: Final[frozenset[str]] = frozenset({"go_health", "go_apps", "go_windows", "go_observe", "go_stop"})
#: 动作类工具（成功 = accepted；失败/超时/断线按 possibly_sent 呈现）。
ACTION_TOOLS: Final[frozenset[str]] = frozenset({"go_act"})
#: 写动作但无 dispatch_status（go_launch）；超时/断线同样不重发。
WRITE_TOOLS: Final[frozenset[str]] = frozenset({"go_launch"})
ALL_TOOLS: Final[frozenset[str]] = READ_TOOLS | ACTION_TOOLS | WRITE_TOOLS

#: 协议自检使用的内部 session（不是某个 umo，仅用于 go_health 会话绑定）。
#: 必须是合法的 wg-<sha16>：执行器会校验内部 session_id 格式（真实链路回归发现）。
SELF_CHECK_SESSION: Final = session_id_for_umo("whalego:protocol-selfcheck")
DEFAULT_READY_WAIT: Final = 20.0


class WhaleGoClient:
    """对 7 个 MCP 工具的强类型封装；唯一懂协议细节的模块。"""

    def __init__(
        self,
        connection: McpConnection,
        config: WhaleGoConfig,
        *,
        logger: logging.Logger | None = None,
        ready_wait: float = DEFAULT_READY_WAIT,
    ) -> None:
        self._connection = connection
        self._config = config
        self._logger = logger or LOGGER
        self._ready_wait = ready_wait
        self.protocol_ok: bool | None = None
        self.protocol_version: int | None = None
        self.last_error: GoError | None = None

    @property
    def connection_config(self) -> ConnectionConfig:
        return self._config.connection

    @property
    def connection(self) -> McpConnection:
        return self._connection

    # ------------------------------------------------------------------ 协议自检
    async def check_protocol(self) -> bool:
        """连接成功后调用一次 go_health，校验 protocol_version（PROTOCOL §9）。"""
        outcome = await self._invoke(
            "go_health",
            {"session_id": SELF_CHECK_SESSION},
            timeout=self._config.connection.timeout,
        )
        if outcome.error is not None:
            self._logger.warning("WhaleGo 协议自检失败：%s", outcome.error.message)
            return False
        payload = convert.parse_json_payload(outcome.result) if outcome.result is not None else None
        version = payload.get("protocol_version") if isinstance(payload, dict) else None
        if not isinstance(version, int):
            self._logger.warning("WhaleGo 协议自检：go_health 未返回 protocol_version，跳过检查。")
            return False
        self.protocol_version = version
        self.protocol_ok = version == PROTOCOL_VERSION
        if self.protocol_ok:
            self._logger.info("WhaleGo 协议版本检查通过（protocol_version=%d）", version)
        else:
            self._logger.error(
                "WhaleGo 协议版本不兼容：执行器 protocol_version=%s，插件要求 %s（不会静默降级）。",
                version,
                PROTOCOL_VERSION,
            )
        return self.protocol_ok

    # ------------------------------------------------------------------ 7 个工具
    async def health(self, session_id: str, *, timeout: float | None = None) -> ToolCallOutcome:
        return await self._invoke("go_health", {"session_id": session_id}, timeout=timeout)

    async def apps(
        self,
        session_id: str,
        *,
        include_windows: bool | None = None,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        args = _compact({"session_id": session_id, "include_windows": include_windows})
        return await self._invoke("go_apps", args, timeout=timeout)

    async def windows(
        self,
        session_id: str,
        *,
        app_id: str | None = None,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        args = _compact({"session_id": session_id, "app_id": app_id})
        return await self._invoke("go_windows", args, timeout=timeout)

    async def observe(
        self,
        session_id: str,
        *,
        window_id: str | None = None,
        app_id: str | None = None,
        include_screenshot: bool | None = None,
        settle_ms: int | None = None,
        max_elements: int | None = None,
        max_value_chars: int | None = None,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        args = _compact(
            {
                "session_id": session_id,
                "window_id": window_id,
                "app_id": app_id,
                "include_screenshot": include_screenshot,
                "settle_ms": settle_ms,
                "max_elements": max_elements,
                "max_value_chars": max_value_chars,
            }
        )
        return await self._invoke("go_observe", args, timeout=timeout)

    async def act(
        self,
        session_id: str,
        *,
        op: str,
        target: dict[str, Any] | None,
        args: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        if not isinstance(target, dict) or not target:
            return error_outcome(
                errors.make_error(
                    "INVALID_ARGUMENT",
                    "go_act 必须带 target（元素 index+state_id 或坐标 x,y）。",
                    details={"reason": "missing_target"},
                )
            )
        payload = _compact({"session_id": session_id, "op": op, "target": target, "args": args})
        return await self._invoke("go_act", payload, timeout=timeout)

    async def launch(
        self,
        session_id: str,
        *,
        app: str | None = None,
        app_id: str | None = None,
        wait_ms: int | None = None,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        if not app and not app_id:
            return error_outcome(
                errors.make_error(
                    "INVALID_ARGUMENT",
                    "go_launch 必须提供 app（allowlist 内名称）或 app_id 之一。",
                    details={"reason": "missing_app"},
                )
            )
        payload = _compact({"session_id": session_id, "app": app, "app_id": app_id, "wait_ms": wait_ms})
        return await self._invoke("go_launch", payload, timeout=timeout)

    async def stop(
        self,
        session_id: str,
        *,
        reason: str | None = None,
        scope: str = "session",
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        """模型/成员只停本会话；scope 固定 "session"（D1/D2：全局停止仅本机）。"""
        payload = _compact({"session_id": session_id, "reason": reason, "scope": scope})
        return await self._invoke("go_stop", payload, timeout=timeout)

    # ------------------------------------------------------------------ 编排
    async def _invoke(
        self,
        tool: str,
        args: dict[str, Any],
        *,
        timeout: float | None = None,
    ) -> ToolCallOutcome:
        if self.protocol_ok is False:
            return error_outcome(
                errors.make_error(
                    "PROTOCOL_MISMATCH",
                    f"执行器 protocol_version={self.protocol_version!r} 与插件要求的 {PROTOCOL_VERSION} 不兼容。",
                    details={"expected": PROTOCOL_VERSION, "actual": self.protocol_version},
                )
            )
        effective_timeout = float(timeout or self._config.connection.timeout)
        started = time.monotonic()
        if not await self._ensure_ready():
            if getattr(self._connection, "auth_failed", False):
                return self._fail("AUTH_FAILED", tool, None)
            return self._connection_error_outcome(tool)
        try:
            result = await self._connection.call(tool, args, effective_timeout)
        except McpClientError as exc:
            if exc.kind == compat.AUTH_ERROR:
                return self._fail("AUTH_FAILED", tool, exc)
            if exc.kind == compat.PROTOCOL_ERROR:
                return self._fail("PROTOCOL_MISMATCH", tool, exc)
            if tool in READ_TOOLS:
                return await self._retry_read_tool(tool, args, effective_timeout, exc)
            return self._dispatch_uncertain_outcome(tool, exc, started)
        except Exception as exc:  # noqa: BLE001 - 防漏网；不向模型暴露堆栈
            self._logger.exception("WhaleGo 工具 %s 调用异常", tool)
            if tool in READ_TOOLS:
                return self._fail("CONNECTION_LOST", tool, exc)
            return self._dispatch_uncertain_outcome(tool, exc, started)
        return ok_outcome(result)

    async def _retry_read_tool(
        self,
        tool: str,
        args: dict[str, Any],
        timeout: float,
        first_error: McpClientError,
    ) -> ToolCallOutcome:
        """只读类失败：重连一次后重试一次（PROTOCOL §7 / D12）。"""
        self._logger.warning(
            "WhaleGo 只读工具 %s 调用失败（%s），尝试重连后重试一次。",
            tool,
            first_error.kind,
        )
        if not await self._connection.wait_ready(self._ready_wait):
            return self._fail(
                "EXECUTOR_TIMEOUT" if first_error.kind == compat.TIMEOUT_ERROR else "CONNECTION_LOST",
                tool,
                first_error,
            )
        try:
            result = await self._connection.call(tool, args, timeout)
        except McpClientError as exc:
            if exc.kind == compat.AUTH_ERROR:
                return self._fail("AUTH_FAILED", tool, exc)
            if exc.kind == compat.PROTOCOL_ERROR:
                return self._fail("PROTOCOL_MISMATCH", tool, exc)
            if exc.kind == compat.TIMEOUT_ERROR:
                return self._fail("EXECUTOR_TIMEOUT", tool, exc)
            if exc.kind == compat.UNREACHABLE_ERROR:
                return self._fail("EXECUTOR_UNREACHABLE", tool, exc)
            return self._fail("CONNECTION_LOST", tool, exc)
        return ok_outcome(result)

    async def _ensure_ready(self) -> bool:
        if self._connection.ready:
            return True
        return await self._connection.wait_ready(self._ready_wait)

    def _connection_error_outcome(self, tool: str) -> ToolCallOutcome:
        code = "CONNECTION_LOST" if self._connection.ever_connected else "EXECUTOR_UNREACHABLE"
        return self._fail(code, tool, None)

    def _dispatch_uncertain_outcome(self, tool: str, exc: BaseException, started: float) -> ToolCallOutcome:
        elapsed_ms = int((time.monotonic() - started) * 1000)
        reason = getattr(exc, "kind", "lost")
        error = errors.action_uncertain_error(tool=tool, reason=str(reason), elapsed_ms=elapsed_ms)
        self.last_error = error
        return error_outcome(error)

    def _fail(self, code: str, tool: str, exc: BaseException | None) -> ToolCallOutcome:
        details: dict[str, Any] = {"tool": tool}
        if isinstance(exc, McpClientError):
            details["kind"] = exc.kind
        error: GoError = errors.make_error(code, details=details)
        self.last_error = error
        self._logger.warning("WhaleGo 工具 %s 失败：%s", tool, error.to_text())
        return error_outcome(error)


def _compact(payload: dict[str, Any]) -> dict[str, Any]:
    """省略 None 值参数（避免把 null 当成显式参数发给执行器）。"""
    return {key: value for key, value in payload.items() if value is not None}
