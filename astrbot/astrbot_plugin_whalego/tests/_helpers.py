"""测试辅助：伪 AstrBot 事件/上下文、伪 MCP 结果、伪连接/客户端、伪 transport。"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from types import SimpleNamespace
from typing import Any

import mcp.types as mcp_types
from _stubs import install_astrbot_stubs

install_astrbot_stubs()

from astrbot.core.agent.run_context import ContextWrapper  # noqa: E402
from astrbot.core.astr_agent_context import AstrAgentContext  # noqa: E402
from astrbot_plugin_whalego.whalego import convert  # noqa: E402

# ---------------------------------------------------------------------------
# 模型可见结果构造
# ---------------------------------------------------------------------------


def text_result(*texts: str, is_error: bool = False) -> mcp_types.CallToolResult:
    content = [mcp_types.TextContent(type="text", text=text) for text in texts]
    return mcp_types.CallToolResult(content=content, isError=is_error)


def image_result(
    *texts: str,
    images: list[tuple[str, str]] | None = None,
    is_error: bool = False,
) -> mcp_types.CallToolResult:
    content: list[Any] = [
        mcp_types.ImageContent(type="image", data=data, mimeType=mime)
        for data, mime in (images or [("aGVsbG8=", "image/png")])
    ]
    content.extend(mcp_types.TextContent(type="text", text=text) for text in texts)
    return mcp_types.CallToolResult(content=content, isError=is_error)


def ok(text: str = "OK: 观察完成 state_id=st-1234abcd") -> mcp_types.CallToolResult:
    return text_result(text)


def json_ok(payload: dict[str, Any], summary: str = "OK: 完成") -> mcp_types.CallToolResult:
    import json

    return text_result(summary, json.dumps(payload, ensure_ascii=False))


def outcome_ok(text: str = "OK: 完成") -> convert.ToolCallOutcome:
    return convert.ok_outcome(text_result(text))


def outcome_error(code: str = "STALE_STATE", **kwargs: Any) -> convert.ToolCallOutcome:
    from astrbot_plugin_whalego.whalego import errors

    return convert.error_outcome(errors.make_error(code, **kwargs))


# ---------------------------------------------------------------------------
# 伪 AstrBot 事件 / 上下文
# ---------------------------------------------------------------------------


@dataclass
class FakeEvent:
    unified_msg_origin: str = "webchat:FriendMessage:whalego-test"
    admin: bool = True

    def is_admin(self) -> bool:
        return self.admin

    def get_sender_name(self) -> str:
        return "tester"


def make_context(
    umo: str = "webchat:FriendMessage:whalego-test",
    *,
    is_admin: bool = True,
    tool_call_timeout: int = 120,
) -> ContextWrapper:
    event = FakeEvent(unified_msg_origin=umo, admin=is_admin)
    agent_context = AstrAgentContext(context=SimpleNamespace(event=event), event=event)
    return ContextWrapper(context=agent_context, tool_call_timeout=tool_call_timeout)


# ---------------------------------------------------------------------------
# 伪 WhaleGoClient（tools.py 测试用）
# ---------------------------------------------------------------------------


class FakeClient:
    """记录所有调用；默认返回一个成功 outcome。"""

    def __init__(self, outcome: convert.ToolCallOutcome | None = None) -> None:
        self.outcome = outcome or outcome_ok()
        self.calls: list[tuple[str, str, dict[str, Any]]] = []

    def _record(self, method: str, session_id: str, kwargs: dict[str, Any]) -> convert.ToolCallOutcome:
        self.calls.append((method, session_id, kwargs))
        return self.outcome

    async def health(self, session_id: str, *, timeout: float | None = None) -> convert.ToolCallOutcome:
        return self._record("health", session_id, {"timeout": timeout})

    async def apps(
        self, session_id: str, *, include_windows: bool | None = None, timeout: float | None = None
    ) -> convert.ToolCallOutcome:
        return self._record("apps", session_id, {"include_windows": include_windows, "timeout": timeout})

    async def windows(
        self, session_id: str, *, app_id: str | None = None, timeout: float | None = None
    ) -> convert.ToolCallOutcome:
        return self._record("windows", session_id, {"app_id": app_id, "timeout": timeout})

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
    ) -> convert.ToolCallOutcome:
        return self._record(
            "observe",
            session_id,
            {
                "window_id": window_id,
                "app_id": app_id,
                "include_screenshot": include_screenshot,
                "settle_ms": settle_ms,
                "max_elements": max_elements,
                "max_value_chars": max_value_chars,
                "timeout": timeout,
            },
        )

    async def act(
        self,
        session_id: str,
        *,
        op: str,
        target: dict[str, Any] | None,
        args: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> convert.ToolCallOutcome:
        return self._record(
            "act",
            session_id,
            {"op": op, "target": target, "args": args, "timeout": timeout},
        )

    async def launch(
        self,
        session_id: str,
        *,
        app: str | None = None,
        app_id: str | None = None,
        wait_ms: int | None = None,
        timeout: float | None = None,
    ) -> convert.ToolCallOutcome:
        return self._record(
            "launch",
            session_id,
            {"app": app, "app_id": app_id, "wait_ms": wait_ms, "timeout": timeout},
        )

    async def stop(
        self,
        session_id: str,
        *,
        reason: str | None = None,
        scope: str = "session",
        timeout: float | None = None,
    ) -> convert.ToolCallOutcome:
        return self._record("stop", session_id, {"reason": reason, "scope": scope, "timeout": timeout})


# ---------------------------------------------------------------------------
# 伪 MCP 连接（client.py 测试用）
# ---------------------------------------------------------------------------


@dataclass
class FakeConnection:
    """实现 WhaleGoClient 依赖的 McpConnection 子集。"""

    state: Any = None
    ready: bool = True
    unclean: bool = False
    ever_connected: bool = True
    wait_ready_result: bool = True
    auth_failed: bool = False
    responses: list[Any] = field(default_factory=list)
    calls: list[tuple[str, dict[str, Any], float]] = field(default_factory=list)
    wait_ready_calls: int = 0
    closed: bool = False

    def __post_init__(self) -> None:
        from astrbot_plugin_whalego.whalego.mcp_client import ConnectionState

        if self.state is None:
            self.state = ConnectionState.READY
        self.disconnected_event = asyncio.Event()

    async def call(self, tool: str, args: dict[str, Any], timeout: float) -> Any:
        self.calls.append((tool, args, timeout))
        if not self.responses:
            return text_result("OK: 完成")
        response = self.responses.pop(0)
        if isinstance(response, BaseException):
            raise response
        return response

    async def wait_ready(self, timeout: float) -> bool:
        self.wait_ready_calls += 1
        return self.wait_ready_result

    async def close(self) -> None:
        self.closed = True


def mcp_error(kind: str, message: str = "") -> Any:
    from astrbot_plugin_whalego.whalego.mcp_client import McpClientError

    return McpClientError(kind, message or f"{kind} error")


# ---------------------------------------------------------------------------
# 伪 Streamable HTTP transport（mcp_client/compat 测试用）
# ---------------------------------------------------------------------------


class FakeSession:
    def __init__(self, *, initialize_error: BaseException | None = None) -> None:
        self.initialize_error = initialize_error
        self.initialized = 0
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.call_error: BaseException | None = None
        self.call_delay: float = 0.0
        self.response: Any = text_result("OK: 完成")

    async def initialize(self) -> None:
        self.initialized += 1
        if self.initialize_error is not None:
            raise self.initialize_error

    async def call_tool(self, name: str, arguments: dict[str, Any]) -> Any:
        self.calls.append((name, arguments))
        if self.call_delay:
            await asyncio.sleep(self.call_delay)
        if self.call_error is not None:
            raise self.call_error
        return self.response


@asynccontextmanager
async def fake_session_manager(session: FakeSession):
    yield session


def fake_transport_factory(streams: tuple[Any, ...] | None = None, *, error: BaseException | None = None):
    @asynccontextmanager
    async def _manager(_config: Any):
        if error is not None:
            raise error
        yield streams or (object(), object(), lambda: "session-1")

    return _manager
