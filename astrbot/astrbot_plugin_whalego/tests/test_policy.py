from __future__ import annotations

import asyncio

from astrbot_plugin_whalego.whalego import config, policy, session


def make_policy(**kwargs) -> policy.PolicyEngine:
    return policy.PolicyEngine(sessions=session.SessionMap(), **kwargs)


def test_readonly_blocks_act_and_launch_only():
    engine = make_policy(readonly=True)
    assert engine.check_tool("go_act", is_admin=True).code == "READONLY_MODE"  # type: ignore[union-attr]
    assert engine.check_tool("go_launch", is_admin=True).code == "READONLY_MODE"  # type: ignore[union-attr]
    for tool in ("go_health", "go_apps", "go_windows", "go_observe", "go_stop"):
        assert engine.check_tool(tool, is_admin=True) is None
    assert engine.check_tool("go_act", is_admin=True).retry == "never"  # type: ignore[union-attr]


def test_readonly_can_be_toggled_at_runtime():
    engine = make_policy()
    assert engine.check_tool("go_act", is_admin=True) is None
    engine.set_readonly(True)
    assert engine.readonly is True
    assert engine.check_tool("go_act", is_admin=True) is not None
    engine.set_readonly(False)
    assert engine.check_tool("go_act", is_admin=True) is None


def test_admin_only_blocks_non_admin_for_all_tools():
    engine = make_policy(admin_only=True)
    for tool in config.TOOL_NAMES:
        error = engine.check_tool(tool, is_admin=False)
        assert error is not None, tool
        assert error.code == "AUTH_FAILED"
        assert error.retry == "never"
        assert engine.check_tool(tool, is_admin=True) is None


def test_admin_only_false_allows_members():
    engine = make_policy(admin_only=False)
    for tool in config.TOOL_NAMES:
        assert engine.check_tool(tool, is_admin=False) is None


def test_allowed_tools_whitelist_blocks_other_tools():
    engine = make_policy(allowed_tools=("go_health", "go_observe"))
    assert engine.check_tool("go_windows", is_admin=True).code == "INVALID_ARGUMENT"  # type: ignore[union-attr]
    assert engine.check_tool("go_health", is_admin=True) is None


def test_unknown_tool_rejected():
    engine = make_policy()
    error = engine.check_tool("go_nope", is_admin=True)
    assert error is not None
    assert error.code == "INVALID_ARGUMENT"


def test_readonly_takes_effect_after_admin_check():
    engine = make_policy(admin_only=True, readonly=True)
    non_admin = engine.check_tool("go_act", is_admin=False)
    assert non_admin is not None
    assert non_admin.code == "AUTH_FAILED"


def test_status_snapshot():
    engine = make_policy(readonly=True, allowed_tools=("go_health",))
    status = engine.status()
    assert status["readonly"] is True
    assert status["admin_only"] is True
    assert status["allowed_tools"] == ["go_health"]
    assert status["max_concurrent"] == 2


async def test_call_gate_tracks_concurrency_and_peak():
    sessions = session.SessionMap()
    gate = policy.CallGate(sessions)
    assert gate.active_calls == 0
    gate_peak = 0

    async def worker(umo: str) -> None:
        nonlocal gate_peak
        async with gate.slot(umo):
            gate_peak = max(gate_peak, gate.active_calls)
            await asyncio.sleep(0.01)

    await asyncio.gather(*(worker(f"umo-{index}") for index in range(4)))
    assert gate_peak <= 2
    assert gate.active_calls == 0
    assert gate.peak_calls <= 2
    assert 1 <= gate.peak_calls <= 2


async def test_policy_call_slot_serializes_same_umo():
    engine = make_policy()
    order: list[str] = []

    async def worker(label: str) -> None:
        async with engine.call_slot("same-umo"):
            order.append(label)
            await asyncio.sleep(0.01)

    await asyncio.gather(worker("a"), worker("b"))
    assert order in (["a", "b"], ["b", "a"])
