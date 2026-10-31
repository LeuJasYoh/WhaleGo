from __future__ import annotations

import inspect
import json

import mcp.types as mcp_types
from _helpers import (
    FakeClient,
    image_result,
    json_ok,
    make_context,
    text_result,
)
from astrbot_plugin_whalego.whalego import config, convert, errors, policy, session
from astrbot_plugin_whalego.whalego import tools as tools_module

UMO = "webchat:FriendMessage:whalego-tools-test"
SID = session.session_id_for_umo(UMO)


def make_tools(
    *,
    client: FakeClient | None = None,
    policy_engine: policy.PolicyEngine | None = None,
    allow_screenshot: bool = True,
    image_supported=None,
    timeout: float = 45.0,
) -> list:
    sessions = session.SessionMap()
    engine = policy_engine or policy.PolicyEngine(sessions=sessions)
    fake_client = client or FakeClient()
    kwargs = {
        "client": fake_client,
        "sessions": sessions,
        "policy": engine,
        "allow_screenshot": allow_screenshot,
        "image_supported": image_supported,
        "timeout": timeout,
    }
    return [cls(**kwargs) for cls in tools_module.TOOL_CLASSES]


def one_tool(name: str, **kwargs):
    for tool in make_tools(**kwargs):
        if tool.name == name:
            return tool
    raise AssertionError(name)


def first_text(result: mcp_types.CallToolResult) -> str:
    return next(item.text for item in result.content if isinstance(item, mcp_types.TextContent))


# ---------------------------------------------------------------------------
# 子类风格 / schema / B1（handler=None）
# ---------------------------------------------------------------------------


def test_seven_tools_exact_names_and_handler_none():
    tools = make_tools()
    assert [tool.name for tool in tools] == list(config.TOOL_NAMES)
    for tool in tools:
        assert tool.handler is None, "handler 必须为 None（避免加载期重绑定，评审 B1）"
        assert tool.handler_module_path is None
        assert isinstance(tool.parameters, dict)


def test_subclass_overrides_call_not_handler():
    from _stubs import FunctionTool

    for tool in make_tools():
        overrides = [
            cls
            for cls in type(tool).__mro__
            if "call" in cls.__dict__ and cls.__dict__["call"] is not FunctionTool.call
        ]
        assert overrides, f"{tool.name} 必须 override call()"
        assert "handler" not in type(tool).__dict__


def test_model_parameters_match_contract_and_hide_internal_params():
    contract = config.load_contract()
    for tool in make_tools():
        expected = config.model_parameters(contract, tool.name)
        assert tool.parameters == expected
        dumped = json.dumps(tool.parameters)
        for internal in config.INTERNAL_PARAMS:
            assert internal not in dumped


def test_description_comes_from_contract():
    contract = config.load_contract()
    for tool in make_tools():
        assert tool.description == config.contract_description(contract, tool.name)
        assert tool.description


def test_astrbot_tool_schema_validation_accepts_parameters():
    from _stubs import ToolSchema

    for tool in make_tools():
        schema = ToolSchema(name=tool.name, description=tool.description, parameters=tool.parameters)
        assert schema.name == tool.name


# ---------------------------------------------------------------------------
# 调用接线：session 注入 / 参数映射 / 结果转换
# ---------------------------------------------------------------------------


async def test_go_health_call_wiring():
    client = FakeClient()
    tool = one_tool("go_health", client=client)
    result = await tool.call(make_context(UMO))
    assert isinstance(result, mcp_types.CallToolResult)
    assert first_text(result).startswith("OK:")
    method, sid, kwargs = client.calls[0]
    assert method == "health"
    assert sid == SID
    assert kwargs["timeout"] == 45.0


async def test_go_apps_call_wiring():
    client = FakeClient()
    tool = one_tool("go_apps", client=client)
    await tool.call(make_context(UMO), include_windows=True)
    method, sid, kwargs = client.calls[0]
    assert (method, sid) == ("apps", SID)
    assert kwargs["include_windows"] is True


async def test_go_windows_call_wiring():
    client = FakeClient()
    tool = one_tool("go_windows", client=client)
    await tool.call(make_context(UMO), app_id="win-exe:abc")
    assert client.calls[0][0] == "windows"
    assert client.calls[0][2]["app_id"] == "win-exe:abc"


async def test_go_observe_call_wiring_and_hint():
    payload = {
        "state_id": "st-1234abcd",
        "elements": [{"index": 0}],
        "frame": {"image_width": 800},
        "target": {"window": {"window_id": "w-01"}},
    }
    client = FakeClient(outcome=convert.ok_outcome(json_ok(payload)))
    tool = one_tool("go_observe", client=client)
    sessions = tool.sessions
    await tool.call(
        make_context(UMO),
        window_id="w-01",
        include_screenshot=True,
        max_value_chars=1000,
        max_elements=50,
        settle_ms=0,
    )
    method, sid, kwargs = client.calls[0]
    assert method == "observe"
    assert sid == SID
    assert kwargs["window_id"] == "w-01"
    assert kwargs["include_screenshot"] is True
    assert kwargs["max_value_chars"] == 1000
    assert kwargs["max_elements"] == 50
    assert kwargs["settle_ms"] == 0
    assert sessions.state_hint(UMO) is not None
    assert "st-1234abcd" in sessions.state_hint(UMO)


async def test_go_observe_respects_plugin_allow_screenshot():
    client = FakeClient(outcome=convert.ok_outcome(text_result("OK: observe")))
    tool = one_tool("go_observe", client=client, allow_screenshot=False)
    await tool.call(make_context(UMO), include_screenshot=True)
    assert client.calls[0][2]["include_screenshot"] is False


async def test_go_act_call_wiring_and_internal_kwargs_ignored():
    client = FakeClient()
    tool = one_tool("go_act", client=client)
    target = {"kind": "element", "index": 2, "state_id": "st-1234abcd"}
    await tool.call(
        make_context(UMO),
        op="click",
        target=target,
        args={"button": "left", "count": 1},
        session_id="model-forged-session",
    )
    method, sid, kwargs = client.calls[0]
    assert method == "act"
    assert sid == SID
    assert sid != "model-forged-session"
    assert kwargs["op"] == "click"
    assert kwargs["target"] == target
    assert kwargs["args"] == {"button": "left", "count": 1}


async def test_go_act_without_target_returns_invalid_argument():
    client = FakeClient(outcome=convert.error_outcome(errors.make_error("INVALID_ARGUMENT")))
    tool = one_tool("go_act", client=client)
    result = await tool.call(make_context(UMO), op="click")
    assert isinstance(result, mcp_types.CallToolResult)
    # 工具层把 None target 传给 client；真实 client 会拒绝。这里断言 client 收到 None。
    assert client.calls[0][2]["target"] is None


async def test_go_launch_call_wiring():
    client = FakeClient()
    tool = one_tool("go_launch", client=client)
    await tool.call(make_context(UMO), app="notepad", wait_ms=3000)
    method, sid, kwargs = client.calls[0]
    assert (method, sid) == ("launch", SID)
    assert kwargs["app"] == "notepad"
    assert kwargs["wait_ms"] == 3000
    assert kwargs["app_id"] is None


async def test_go_stop_injects_scope_session_and_truncates_reason():
    client = FakeClient()
    tool = one_tool("go_stop", client=client)
    await tool.call(make_context(UMO), reason="x" * 500)
    method, sid, kwargs = client.calls[0]
    assert (method, sid) == ("stop", SID)
    assert kwargs["scope"] == "session"
    assert len(kwargs["reason"]) == 200


# ---------------------------------------------------------------------------
# 策略：admin_only / readonly / allowed_tools
# ---------------------------------------------------------------------------


async def test_admin_only_blocks_member_without_calling_client():
    client = FakeClient()
    tool = one_tool("go_act", client=client)
    result = await tool.call(
        make_context(UMO, is_admin=False),
        op="click",
        target={"kind": "element", "index": 1, "state_id": "st-1"},
    )
    text = first_text(result)
    assert text.startswith("ERROR[AUTH_FAILED]: ")
    assert client.calls == []


async def test_readonly_blocks_act_and_launch():
    sessions = session.SessionMap()
    engine = policy.PolicyEngine(sessions=sessions, readonly=True)
    client = FakeClient()
    act = one_tool("go_act", client=client, policy_engine=engine)
    launch = one_tool("go_launch", client=client, policy_engine=engine)
    result = await act.call(
        make_context(UMO), op="click", target={"kind": "element", "index": 1, "state_id": "st-1"}
    )
    assert first_text(result).startswith("ERROR[READONLY_MODE]: ")
    result2 = await launch.call(make_context(UMO), app="notepad")
    assert first_text(result2).startswith("ERROR[READONLY_MODE]: ")
    assert client.calls == []


async def test_allowed_tools_blocks_tool_call():
    sessions = session.SessionMap()
    engine = policy.PolicyEngine(sessions=sessions, allowed_tools=("go_health",))
    client = FakeClient()
    tool = one_tool("go_windows", client=client, policy_engine=engine)
    result = await tool.call(make_context(UMO))
    assert first_text(result).startswith("ERROR[INVALID_ARGUMENT]: ")
    assert client.calls == []


# ---------------------------------------------------------------------------
# 图片能力 / 原生高亮
# ---------------------------------------------------------------------------


async def test_observe_image_kept_with_privacy_hint():
    raw = image_result("OK: 观察完成 screenshot=yes")
    client = FakeClient(outcome=convert.ok_outcome(raw))
    tool = one_tool("go_observe", client=client, image_supported=lambda umo: True)
    result = await tool.call(make_context(UMO), window_id="w-1", include_screenshot=True)
    assert any(isinstance(item, mcp_types.ImageContent) for item in result.content)
    assert first_text(result).startswith("OK: " + convert.PRIVACY_HINT)


async def test_observe_image_dropped_when_provider_unsupported():
    raw = image_result("OK: 观察完成 screenshot=yes")
    client = FakeClient(outcome=convert.ok_outcome(raw))
    tool = one_tool("go_observe", client=client, image_supported=lambda umo: False)
    result = await tool.call(make_context(UMO), window_id="w-1", include_screenshot=True)
    assert not any(isinstance(item, mcp_types.ImageContent) for item in result.content)
    texts = [item.text for item in result.content if isinstance(item, mcp_types.TextContent)]
    assert convert.IMAGE_UNSUPPORTED_NOTE in texts[-1]


# ---------------------------------------------------------------------------
# timeout / 异常兜底 / 上下文缺失
# ---------------------------------------------------------------------------


async def test_effective_timeout_clamped_by_astrbot_context():
    tool = one_tool("go_health", timeout=45.0)
    assert tool._effective_timeout(make_context(UMO, tool_call_timeout=120)) == 45.0
    assert tool._effective_timeout(make_context(UMO, tool_call_timeout=30)) == 25.0


async def test_tool_exception_becomes_internal_error_not_raise():
    class BrokenClient(FakeClient):
        async def health(self, session_id, *, timeout=None):
            raise RuntimeError("boom")

    tool = one_tool("go_health", client=BrokenClient())
    result = await tool.call(make_context(UMO))
    text = next(item.text for item in result.content if isinstance(item, mcp_types.TextContent))
    assert text.startswith("ERROR[INTERNAL]: ")


async def test_missing_event_returns_internal_error():
    tool = one_tool("go_health")

    class EmptyContext:
        context = None
        tool_call_timeout = 120

    result = await tool.call(EmptyContext())
    text = next(item.text for item in result.content if isinstance(item, mcp_types.TextContent))
    assert text.startswith("ERROR[INTERNAL]: ")


def test_tool_instances_keep_injected_dependencies():
    sessions = session.SessionMap()
    engine = policy.PolicyEngine(sessions=sessions)
    client = FakeClient()
    kwargs = {
        "client": client,
        "sessions": sessions,
        "policy": engine,
        "allow_screenshot": True,
        "timeout": 45.0,
    }
    for cls in tools_module.TOOL_CLASSES:
        instance = cls(**kwargs)
        assert instance.client is client
        assert instance.sessions is sessions
        assert instance.policy is engine
        assert inspect.iscoroutinefunction(instance.call)
