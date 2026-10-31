from __future__ import annotations

import asyncio

import httpx
import pytest
from _helpers import (
    FakeSession,
    fake_session_manager,
    fake_transport_factory,
    mcp_error,
    text_result,
)
from astrbot_plugin_whalego.whalego import compat
from astrbot_plugin_whalego.whalego.config import ConnectionConfig
from astrbot_plugin_whalego.whalego.mcp_client import (
    ConnectionState,
    McpClientError,
    McpConnection,
    ReconnectSupervisor,
    backoff_delays,
)

CFG = ConnectionConfig(url="http://127.0.0.1:8765/mcp", token="wg_test", timeout=5)


def _connect(connection: McpConnection) -> asyncio.Task[None]:
    return asyncio.create_task(connection.connect())


async def test_connect_success_sets_ready_and_session():
    session = FakeSession()
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    await connection.connect()
    assert connection.state is ConnectionState.READY
    assert connection.ready is True
    assert connection.ever_connected is True
    assert session.initialized == 1
    assert connection.disconnected_event.is_set() is False
    await connection.close()
    assert connection.state is ConnectionState.CLOSED
    assert connection.ready is False


async def test_connect_failure_auth_401():
    request = httpx.Request("POST", "http://127.0.0.1:8765/mcp")
    response = httpx.Response(401, request=request)
    error = httpx.HTTPStatusError("unauthorized", request=request, response=response)
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory(error=error),
        session_factory=lambda read, write: fake_session_manager(FakeSession()),
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.connect()
    assert excinfo.value.kind == compat.AUTH_ERROR
    assert connection.auth_failed is True, "401 必须标记 auth_failed，上层才能返回 AUTH_FAILED"
    assert connection.state is ConnectionState.DISCONNECTED
    await connection.close()


async def test_connect_auth_via_anyio_cancel_and_close_error():
    """真实链路回归：mcp SDK 的 anyio TaskGroup 会先用 CancelledError 结束 owner task，
    真实 401 在 AsyncExitStack.aclose() 的 ExceptionGroup 里；必须翻译成 auth 而不是 lost。"""
    request = httpx.Request("POST", "http://127.0.0.1:8765/mcp")
    response = httpx.Response(401, request=request)
    http_401 = httpx.HTTPStatusError("unauthorized", request=request, response=response)

    class CancelThenGroupCtx:
        async def __aenter__(self):
            return (object(), object(), None)

        async def __aexit__(self, exc_type, exc, tb):
            raise ExceptionGroup("unhandled errors in a TaskGroup", [http_401])

    class CancelledInitializeSession(FakeSession):
        async def initialize(self) -> None:
            self.initialized += 1
            raise asyncio.CancelledError()

    connection = McpConnection(
        CFG,
        transport=lambda config: CancelThenGroupCtx(),
        session_factory=lambda read, write: fake_session_manager(CancelledInitializeSession()),
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.connect()
    assert excinfo.value.kind == compat.AUTH_ERROR
    assert connection.auth_failed is True
    await connection.close()


async def test_successful_connect_clears_auth_failed_flag():
    session = FakeSession()
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    connection.auth_failed = True
    await connection.connect()
    assert connection.auth_failed is False
    await connection.close()


async def test_connect_error_is_unreachable():
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory(error=httpx.ConnectError("refused")),
        session_factory=lambda read, write: fake_session_manager(FakeSession()),
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.connect()
    assert excinfo.value.kind == compat.UNREACHABLE_ERROR
    await connection.close()


async def test_connect_timeout_maps_to_unreachable():
    class SlowSession(FakeSession):
        async def initialize(self) -> None:
            self.initialized += 1
            await asyncio.sleep(5)

    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(SlowSession()),
        connect_timeout=0.05,
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.connect()
    assert excinfo.value.kind == compat.UNREACHABLE_ERROR
    await connection.close()


async def test_call_success_and_args():
    session = FakeSession()
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    await connection.connect()
    result = await connection.call("go_health", {"session_id": "wg-1"}, 2.0)
    assert result.content[0].text == "OK: 完成"
    assert session.calls == [("go_health", {"session_id": "wg-1"})]
    await connection.close()


async def test_call_timeout_marks_unclean_and_raises_timeout():
    session = FakeSession()
    session.call_delay = 0.5
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    await connection.connect()
    with pytest.raises(McpClientError) as excinfo:
        await connection.call("go_act", {"session_id": "wg-1"}, 0.05)
    assert excinfo.value.kind == compat.TIMEOUT_ERROR
    assert connection.unclean is True
    assert connection.ready is False
    assert connection.state is ConnectionState.BACKOFF
    await connection.close()


async def test_call_without_connection_raises_unreachable():
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory(),
        session_factory=lambda r, w: fake_session_manager(FakeSession()),
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.call("go_health", {}, 1.0)
    assert excinfo.value.kind == compat.UNREACHABLE_ERROR
    await connection.close()


async def test_wait_ready_returns_false_on_timeout():
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory(),
        session_factory=lambda r, w: fake_session_manager(FakeSession()),
    )
    assert await connection.wait_ready(0.01) is False
    await connection.close()


async def test_session_initialize_error_propagates_as_mcp_error():
    session = FakeSession(initialize_error=RuntimeError("handshake boom"))
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    with pytest.raises(McpClientError) as excinfo:
        await connection.connect()
    assert excinfo.value.kind in (compat.LOST_ERROR, compat.UNREACHABLE_ERROR)
    await connection.close()


def test_backoff_delays_sequences_and_cap():
    generator = backoff_delays()
    delays = [next(generator) for _ in range(7)]
    assert delays == [1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 30.0]


class FakeSupervisorConnection:
    def __init__(self, results: list[object]) -> None:
        self._results = list(results)
        self.state = ConnectionState.DISCONNECTED
        self.ready = False
        self.unclean = False
        self.ever_connected = False
        self.disconnected_event = asyncio.Event()
        self.disconnected_event.set()
        self.connect_calls = 0
        self.closed = False

    async def connect(self) -> None:
        self.connect_calls += 1
        outcome = self._results.pop(0)
        if isinstance(outcome, BaseException):
            self.state = ConnectionState.DISCONNECTED
            self.ready = False
            raise outcome
        self.state = ConnectionState.READY
        self.ready = True
        self.ever_connected = True
        self.disconnected_event.clear()

    async def close(self) -> None:
        self.state = ConnectionState.CLOSED
        self.ready = False
        self.closed = True


async def test_reconnect_supervisor_backs_off_then_connects():
    connection = FakeSupervisorConnection(
        [mcp_error(compat.UNREACHABLE_ERROR), mcp_error(compat.UNREACHABLE_ERROR), None]
    )
    delays: list[float] = []

    async def fake_sleep(delay: float) -> None:
        delays.append(delay)
        await asyncio.sleep(0)

    supervisor = ReconnectSupervisor(connection, sleep=fake_sleep)
    task = asyncio.create_task(supervisor.run())
    for _ in range(50):
        if connection.ready:
            break
        await asyncio.sleep(0.01)
    assert connection.ready is True
    assert connection.connect_calls == 3
    assert delays == [1.0, 2.0]
    # 模拟断线：supervisor 应重新连接
    connection.ready = False
    connection.unclean = True
    connection.disconnected_event.set()
    connection._results = [None]
    for _ in range(50):
        if connection.connect_calls == 4:
            break
        await asyncio.sleep(0.01)
    assert connection.connect_calls == 4
    supervisor.request_stop()
    connection.disconnected_event.set()
    await asyncio.wait_for(task, timeout=5)
    assert connection.closed is True


async def test_reconnect_supervisor_stop_while_waiting():
    connection = FakeSupervisorConnection([mcp_error(compat.UNREACHABLE_ERROR)] * 100)
    started = asyncio.Event()

    async def fake_sleep(delay: float) -> None:
        started.set()
        await asyncio.sleep(10)

    supervisor = ReconnectSupervisor(connection, sleep=fake_sleep)
    task = asyncio.create_task(supervisor.run())
    await started.wait()
    supervisor.request_stop()
    await asyncio.wait_for(task, timeout=5)
    assert connection.closed is True


async def test_connection_result_ok_after_call():
    session = FakeSession()
    session.response = text_result("OK: okay")
    connection = McpConnection(
        CFG,
        transport=fake_transport_factory((object(), object(), lambda: "sid")),
        session_factory=lambda read, write: fake_session_manager(session),
    )
    await connection.connect()
    result = await connection.call("go_observe", {"session_id": "x"}, 1.0)
    assert result.content[0].text == "OK: okay"
    await connection.close()
