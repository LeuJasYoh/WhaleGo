from __future__ import annotations

import asyncio
import hashlib

import pytest
from astrbot_plugin_whalego.whalego import session


def test_session_id_derivation_is_deterministic_and_short():
    umo = "webchat:FriendMessage:whalego-test"
    expected = "wg-" + hashlib.sha256(umo.encode("utf-8")).hexdigest()[:16]
    sid = session.session_id_for_umo(umo)
    assert sid == expected
    assert sid.startswith("wg-")
    assert len(sid) == 19
    assert session.session_id_for_umo(umo) == sid
    assert session.session_id_for_umo(umo + "x") != sid


def test_session_id_never_leaks_umo():
    umo = "webchat:FriendMessage:secret-user-123"
    sid = session.session_id_for_umo(umo)
    assert "secret-user" not in sid
    assert "webchat" not in sid


def test_empty_umo_raises():
    with pytest.raises(ValueError):
        session.session_id_for_umo("")


def test_session_map_locks_are_per_umo_and_global_semaphore_is_two():
    sessions = session.SessionMap()
    lock_a = sessions.lock_for("umo-a")
    lock_b = sessions.lock_for("umo-b")
    assert sessions.lock_for("umo-a") is lock_a
    assert lock_a is not lock_b
    assert sessions.max_concurrent == 2
    # Semaphore(2) 内部计数：acquire 两次后仍可拿一次失败
    assert sessions.semaphore._value == 2  # noqa: SLF001 - 单测直接校验初始容量


async def test_serialized_limits_global_concurrency_to_two():
    sessions = session.SessionMap()
    active = 0
    peak = 0

    async def worker(umo: str) -> None:
        nonlocal active, peak
        async with sessions.serialized(umo):
            active += 1
            peak = max(peak, active)
            await asyncio.sleep(0.02)
            active -= 1

    await asyncio.gather(*(worker(f"umo-{index}") for index in range(5)))
    assert peak <= 2


async def test_serialized_same_umo_is_exclusive():
    sessions = session.SessionMap()
    order: list[str] = []

    async def worker(label: str) -> None:
        async with sessions.serialized("same-umo"):
            order.append(f"{label}-start")
            await asyncio.sleep(0.01)
            order.append(f"{label}-end")

    await asyncio.gather(worker("a"), worker("b"))
    # 同 umo 严格串行：start/end 必须成对相邻
    assert order in (
        ["a-start", "a-end", "b-start", "b-end"],
        ["b-start", "b-end", "a-start", "a-end"],
    )


def test_remember_observe_and_hint():
    sessions = session.SessionMap()
    umo = "webchat:FriendMessage:whalego-test"
    payload = {
        "state_id": "st-1234abcd",
        "elements": [{"index": 0}],
        "frame": {"image_width": 800},
        "target": {"app": {"app_id": "win-exe:abc"}, "window": {"window_id": "w-01"}},
    }
    recent = sessions.remember_observe(umo, payload)
    assert recent.state_id == "st-1234abcd"
    assert recent.window_id == "w-01"
    assert recent.app_id == "win-exe:abc"
    assert recent.elements == 1
    hint = sessions.state_hint(umo)
    assert hint is not None
    assert "st-1234abcd" in hint
    assert "重新 go_observe" in hint
    sessions.clear(umo)
    assert sessions.recent(umo) is None
    assert sessions.state_hint(umo) is None
