from __future__ import annotations

import json

from _helpers import FakeConnection, mcp_error, text_result
from astrbot_plugin_whalego.whalego import compat, errors
from astrbot_plugin_whalego.whalego.client import WhaleGoClient
from astrbot_plugin_whalego.whalego.config import WhaleGoConfig


def make_client(connection: FakeConnection | None = None, **kwargs) -> tuple[WhaleGoClient, FakeConnection]:
    cfg = WhaleGoConfig.from_astrbot_config(
        {"url": "http://127.0.0.1:8765/mcp", "token": "wg_test", "timeout": 45}
    )
    conn = connection or FakeConnection()
    client = WhaleGoClient(conn, cfg, ready_wait=kwargs.pop("ready_wait", 0.01), **kwargs)
    return client, conn


async def test_every_tool_call_injects_session_id():
    client, conn = make_client()
    await client.health("wg-abc")
    await client.apps("wg-abc")
    await client.windows("wg-abc")
    await client.observe("wg-abc")
    await client.act("wg-abc", op="click", target={"kind": "element", "index": 1, "state_id": "st-1"})
    await client.launch("wg-abc", app="notepad")
    await client.stop("wg-abc")
    assert [call[0] for call in conn.calls] == [
        "go_health",
        "go_apps",
        "go_windows",
        "go_observe",
        "go_act",
        "go_launch",
        "go_stop",
    ]
    for tool, args, _timeout in conn.calls:
        assert "session_id" in args, tool


async def test_optional_args_are_omitted_not_null():
    client, conn = make_client()
    await client.apps("wg-abc")
    assert conn.calls[0][1] == {"session_id": "wg-abc"}
    await client.observe("wg-abc", window_id="w-1", include_screenshot=True)
    assert conn.calls[1][1] == {
        "session_id": "wg-abc",
        "window_id": "w-1",
        "include_screenshot": True,
    }


async def test_stop_injects_scope_session_and_limits_reason():
    client, conn = make_client()
    await client.stop("wg-abc", reason="x" * 300)
    tool, args, _ = conn.calls[0]
    assert tool == "go_stop"
    assert args["scope"] == "session"
    assert len(args["reason"]) == 300  # 由工具层截断到 200；client 层原样透传


async def test_act_requires_target():
    client, conn = make_client()
    outcome = await client.act("wg-abc", op="click", target=None)
    assert outcome.error is not None
    assert outcome.error.code == "INVALID_ARGUMENT"
    assert conn.calls == []


async def test_launch_requires_app_or_app_id():
    client, conn = make_client()
    outcome = await client.launch("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "INVALID_ARGUMENT"
    assert conn.calls == []


async def test_action_timeout_is_dispatch_uncertain_without_retry():
    conn = FakeConnection(responses=[mcp_error(compat.TIMEOUT_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.act("wg-abc", op="click", target={"kind": "element", "index": 1, "state_id": "st-1"})
    assert outcome.error is not None
    assert outcome.error.code == "DISPATCH_UNCERTAIN"
    assert outcome.error.retry == "reobserve"
    assert outcome.error.message == errors.DEFAULT_MESSAGES["DISPATCH_UNCERTAIN"]
    assert outcome.error.to_dict()["action_receipt"]["dispatch_status"] == "possibly_sent"
    assert len(conn.calls) == 1, "动作类失败绝不能自动重发"


async def test_launch_timeout_is_dispatch_uncertain_without_retry():
    conn = FakeConnection(responses=[mcp_error(compat.TIMEOUT_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.launch("wg-abc", app="notepad")
    assert outcome.error is not None
    assert outcome.error.code == "DISPATCH_UNCERTAIN"
    assert len(conn.calls) == 1


async def test_action_connection_lost_is_dispatch_uncertain():
    conn = FakeConnection(responses=[mcp_error(compat.LOST_ERROR), mcp_error(compat.LOST_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")  # 只读工具会重试
    assert outcome.error is not None
    # 只读重试用尽 → CONNECTION_LOST
    assert outcome.error.code == "CONNECTION_LOST"
    conn2 = FakeConnection(responses=[mcp_error(compat.LOST_ERROR)])
    client2, _ = make_client(conn2)
    outcome2 = await client2.act("wg-abc", op="click", target={"kind": "coordinate", "x": 1, "y": 2})
    assert outcome2.error is not None
    assert outcome2.error.code == "DISPATCH_UNCERTAIN"
    assert len(conn2.calls) == 1


async def test_read_tool_retry_once_after_reconnect_then_success():
    conn = FakeConnection(
        responses=[mcp_error(compat.TIMEOUT_ERROR), text_result("OK: health")],
        wait_ready_result=True,
    )
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")
    assert outcome.error is None
    assert outcome.result is not None
    assert len(conn.calls) == 2
    assert conn.wait_ready_calls == 1


async def test_read_tool_retry_twice_timeout_becomes_executor_timeout():
    conn = FakeConnection(responses=[mcp_error(compat.TIMEOUT_ERROR), mcp_error(compat.TIMEOUT_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.observe("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "EXECUTOR_TIMEOUT"
    assert outcome.error.retry == "reobserve"
    assert len(conn.calls) == 2


async def test_read_tool_retry_failure_connection_lost():
    conn = FakeConnection(responses=[mcp_error(compat.TIMEOUT_ERROR), mcp_error(compat.LOST_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.stop("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "CONNECTION_LOST"
    assert outcome.error.retry == "reobserve"


async def test_auth_error_never_retried():
    conn = FakeConnection(responses=[mcp_error(compat.AUTH_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "AUTH_FAILED"
    assert outcome.error.retry == "never"
    assert len(conn.calls) == 1


async def test_protocol_error_never_retried():
    conn = FakeConnection(responses=[mcp_error(compat.PROTOCOL_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.windows("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "PROTOCOL_MISMATCH"
    assert outcome.error.retry == "never"


async def test_unreachable_when_never_connected_and_no_retry_for_action():
    conn = FakeConnection(ready=False, ever_connected=False, wait_ready_result=False)
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "EXECUTOR_UNREACHABLE"
    assert outcome.error.retry == "retry"
    assert conn.calls == []


async def test_auth_failed_connect_state_maps_to_auth_failed_without_retry():
    conn = FakeConnection(ready=False, ever_connected=False, wait_ready_result=False, auth_failed=True)
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "AUTH_FAILED"
    assert outcome.error.retry == "never"
    assert conn.calls == []


async def test_protocol_mismatch_flag_blocks_all_calls():
    client, conn = make_client()
    client.protocol_ok = False
    client.protocol_version = 2
    outcome = await client.health("wg-abc")
    assert outcome.error is not None
    assert outcome.error.code == "PROTOCOL_MISMATCH"
    assert conn.calls == []


def test_self_check_session_id_is_valid_executor_format():
    import re

    from astrbot_plugin_whalego.whalego.client import SELF_CHECK_SESSION

    # 执行器会校验内部 session_id 格式（真实容器→宿主链路回归发现：wg-<16 hex>）。
    assert re.fullmatch(r"wg-[0-9a-f]{16}", SELF_CHECK_SESSION), SELF_CHECK_SESSION


async def test_check_protocol_ok_and_payload_parsing():
    conn = FakeConnection(responses=[text_result("OK: 完成", json.dumps({"protocol_version": 1, "ok": True}))])
    client, _ = make_client(conn)
    assert await client.check_protocol() is True
    assert client.protocol_ok is True
    assert client.protocol_version == 1
    from astrbot_plugin_whalego.whalego.client import SELF_CHECK_SESSION

    assert conn.calls[0][1] == {"session_id": SELF_CHECK_SESSION}


async def test_check_protocol_mismatch():
    conn = FakeConnection(responses=[text_result("OK: 完成", json.dumps({"protocol_version": 2}))])
    client, _ = make_client(conn)
    assert await client.check_protocol() is False
    assert client.protocol_ok is False
    assert client.protocol_version == 2


async def test_failure_sets_last_error_and_success_does_not():
    conn = FakeConnection(responses=[mcp_error(compat.TIMEOUT_ERROR), mcp_error(compat.TIMEOUT_ERROR)])
    client, _ = make_client(conn)
    outcome = await client.health("wg-abc")
    assert outcome.error is not None
    assert client.last_error is not None
    assert client.last_error.code == "EXECUTOR_TIMEOUT"


async def test_all_tool_outcomes_use_mcp_result():
    conn = FakeConnection(responses=[text_result("OK: a")] * 7)
    client, _ = make_client(conn)
    for coro in (
        client.health("wg-abc"),
        client.apps("wg-abc"),
        client.windows("wg-abc"),
        client.observe("wg-abc"),
        client.act("wg-abc", op="click", target={"kind": "element", "index": 1, "state_id": "st-1"}),
        client.launch("wg-abc", app="notepad"),
        client.stop("wg-abc"),
    ):
        outcome = await coro
        assert outcome.error is None
        assert outcome.result is not None
        assert outcome.ok is True
