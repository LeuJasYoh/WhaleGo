"""WhaleGo AstrBot 插件的 L2 会话层：umo → session_id、会话锁与观察提示。

本模块不得 import astrbot。

PROTOCOL §3.3 / D1：业务 session_id 由插件按 umo 派生注入：
`"wg-" + sha256(umo.encode("utf-8")).hexdigest()[:16]`（16 位十六进制，文档简写为 wg-<sha16>）。
模型不可见、不可传；插件不缓存 UI 状态，仅保存最近一次 observe 的 state_id/window_id 作为提示。
"""

from __future__ import annotations

import asyncio
import hashlib
import time
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from typing import Any, Final
from weakref import WeakValueDictionary

SESSION_PREFIX: Final = "wg-"
SESSION_HASH_CHARS: Final = 16
DEFAULT_MAX_CONCURRENT: Final = 2


def session_id_for_umo(umo: str) -> str:
    """umo → wg-<sha256(umo)[:16]>（确定性、不可逆；模型不可见）。"""
    if not isinstance(umo, str) or not umo:
        raise ValueError("umo 必须是非空字符串")
    digest = hashlib.sha256(umo.encode("utf-8")).hexdigest()
    return f"{SESSION_PREFIX}{digest[:SESSION_HASH_CHARS]}"


@dataclass
class RecentObservation:
    """最近一次 go_observe 的提示信息（不是 UI 事实源，执行器才是）。"""

    state_id: str | None = None
    window_id: str | None = None
    app_id: str | None = None
    elements: int | None = None
    screenshot: bool | None = None
    observed_at: float = field(default_factory=time.time)

    def summary(self) -> str:
        parts: list[str] = []
        if self.window_id:
            parts.append(f"window_id={self.window_id}")
        if self.state_id:
            parts.append(f"state_id={self.state_id}")
        if self.elements is not None:
            parts.append(f"elements={self.elements}")
        return " ".join(parts) if parts else "（无）"


class SessionMap:
    """umo → session_id 映射 + 每 umo 串行锁 + 全局并发信号量。

    - 同一 umo 的工具调用串行（同一会话的观察/动作不交错）；
    - 不同会话可并发，但全局最多 `max_concurrent`（默认 2）个并行调用。
    """

    def __init__(self, max_concurrent: int = DEFAULT_MAX_CONCURRENT) -> None:
        if max_concurrent < 1:
            raise ValueError("max_concurrent 必须 >= 1")
        self._locks: WeakValueDictionary[str, asyncio.Lock] = WeakValueDictionary()
        self._semaphore = asyncio.Semaphore(max_concurrent)
        self._recent: dict[str, RecentObservation] = {}
        self.max_concurrent = max_concurrent

    # ------------------------------------------------------------------ 映射
    def session_id(self, umo: str) -> str:
        return session_id_for_umo(umo)

    # 语义别名（DESIGN §6.4）
    def for_umo(self, umo: str) -> str:
        return self.session_id(umo)

    def lock_for(self, umo: str) -> asyncio.Lock:
        lock = self._locks.get(umo)
        if lock is None:
            lock = asyncio.Lock()
            self._locks[umo] = lock
        return lock

    @property
    def semaphore(self) -> asyncio.Semaphore:
        return self._semaphore

    @property
    def concurrent_in_use(self) -> int:
        return self.max_concurrent - self._semaphore._value  # noqa: SLF001 - 只读诊断用

    @asynccontextmanager
    async def serialized(self, umo: str) -> AsyncIterator[None]:
        """同 umo 串行 + 全局并发上限（SessionMap 级并发护栏）。"""
        async with self.lock_for(umo):
            async with self._semaphore:
                yield

    # ------------------------------------------------------------------ 提示
    def remember_observe(self, umo: str, payload: dict[str, Any]) -> RecentObservation:
        """记录最近一次 observe 的 state_id/window_id（仅提示，不参与新鲜度判定）。"""
        frame = payload.get("frame") if isinstance(payload.get("frame"), dict) else {}
        target = payload.get("target") if isinstance(payload.get("target"), dict) else {}
        window = target.get("window") if isinstance(target.get("window"), dict) else {}
        elements_raw = payload.get("elements")
        recent = RecentObservation(
            state_id=payload.get("state_id") if isinstance(payload.get("state_id"), str) else None,
            window_id=window.get("window_id") if isinstance(window.get("window_id"), str) else None,
            app_id=None,
            elements=len(elements_raw) if isinstance(elements_raw, list) else None,
            screenshot=bool(frame) or None,
        )
        app = target.get("app") if isinstance(target.get("app"), dict) else {}
        if isinstance(app.get("app_id"), str):
            recent.app_id = app["app_id"]
        if umo not in self._recent and len(self._recent) >= 512:
            # 简单上限，避免长期运行后每个 umo 的提示无限累积。
            self._recent.pop(next(iter(self._recent)))
        self._recent[umo] = recent
        return recent

    def recent(self, umo: str) -> RecentObservation | None:
        return self._recent.get(umo)

    def state_hint(self, umo: str) -> str | None:
        recent = self._recent.get(umo)
        if recent is None or not recent.state_id:
            return None
        suffix = f"（window_id={recent.window_id}）" if recent.window_id else ""
        return f"最近一次观察 state_id={recent.state_id}{suffix}；动作后 state 失效，请重新 go_observe。"

    def clear(self, umo: str) -> None:
        self._recent.pop(umo, None)
