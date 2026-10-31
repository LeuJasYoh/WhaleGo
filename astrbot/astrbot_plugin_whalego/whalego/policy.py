"""WhaleGo AstrBot 插件的 L2 策略层：只读 / 管理员 / 白名单 / 并发护栏。

本模块不得 import astrbot。

权限模型以 docs/NAMING.md §4.3 为唯一事实源：
- `admin_only=true`（默认）时，7 个工具只允许 `event.is_admin()` 的调用者（AstrBot
  非内置工具默认权限是 member，插件必须兜底）；
- `readonly=true` 时插件侧拦截 go_act/go_launch（ERROR[READONLY_MODE]）；
- `allowed_tools` 白名单在调用时二次校验（模型仍有工具定义，但调用会被拒绝）；
- 每次调用受 per-umo 锁 + 全局信号量约束（并发护栏）。
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any

from .config import READONLY_BLOCKED_TOOLS, TOOL_NAMES
from .errors import GoError, permission_error, readonly_error, whitelist_error
from .session import SessionMap


class CallGate:
    """per-call 并发护栏：per-umo 串行 + 全局并发上限 + 在飞计数（诊断用）。"""

    def __init__(self, sessions: SessionMap) -> None:
        self._sessions = sessions
        self._active = 0
        self._peak = 0

    @property
    def active_calls(self) -> int:
        return self._active

    @property
    def peak_calls(self) -> int:
        return self._peak

    @asynccontextmanager
    async def slot(self, umo: str) -> AsyncIterator[None]:
        async with self._sessions.serialized(umo):
            self._active += 1
            self._peak = max(self._peak, self._active)
            try:
                yield
            finally:
                self._active -= 1


class PolicyEngine:
    """插件侧权限/只读/白名单策略；可运行时切换 readonly（/whalego readonly）。"""

    def __init__(
        self,
        *,
        sessions: SessionMap | None = None,
        readonly: bool = False,
        admin_only: bool = True,
        allowed_tools: tuple[str, ...] = TOOL_NAMES,
    ) -> None:
        self.sessions = sessions or SessionMap()
        self._readonly = bool(readonly)
        self._admin_only = bool(admin_only)
        self._allowed_tools = tuple(name for name in TOOL_NAMES if name in set(allowed_tools))
        self.gate = CallGate(self.sessions)

    # ------------------------------------------------------------------ 状态
    @property
    def readonly(self) -> bool:
        return self._readonly

    @property
    def admin_only(self) -> bool:
        return self._admin_only

    @property
    def allowed_tools(self) -> tuple[str, ...]:
        return self._allowed_tools

    def set_readonly(self, value: bool) -> None:
        self._readonly = bool(value)

    def status(self) -> dict[str, Any]:
        return {
            "readonly": self._readonly,
            "admin_only": self._admin_only,
            "allowed_tools": list(self._allowed_tools),
            "max_concurrent": self.sessions.max_concurrent,
            "active_calls": self.gate.active_calls,
            "peak_calls": self.gate.peak_calls,
        }

    # ------------------------------------------------------------------ 校验
    def check_tool(self, tool_name: str, *, is_admin: bool) -> GoError | None:
        """返回 None 表示允许；否则返回模型可见的 GoError（不抛异常）。"""
        if tool_name not in TOOL_NAMES:
            return whitelist_error(tool_name)
        if tool_name not in self._allowed_tools:
            return whitelist_error(tool_name)
        if self._admin_only and not is_admin:
            return permission_error(tool_name)
        if self._readonly and tool_name in READONLY_BLOCKED_TOOLS:
            return readonly_error(tool_name)
        return None

    def check_call(self, tool_name: str, *, is_admin: bool) -> GoError | None:
        """check_tool 的语义别名（便于阅读）。"""
        return self.check_tool(tool_name, is_admin=is_admin)

    # ------------------------------------------------------------------ 并发
    def call_slot(self, umo: str) -> Any:
        """获取一次调用的并发 slot（async context manager）。"""
        return self.gate.slot(umo)
