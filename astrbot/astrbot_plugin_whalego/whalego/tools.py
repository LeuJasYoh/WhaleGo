"""WhaleGo AstrBot 插件的 L3 工具门面：7 个 FunctionTool 子类（官方推荐方式）。

允许 import `astrbot.core.agent.tool` / `run_context` / `astr_agent_context`
（ARCHITECTURE §2.2 的唯一例外），不允许碰生命周期/事件总线；接线在 main.py。

**禁止** `FunctionTool(handler=插件绑定方法)`（评审 B1）：AstrBot 加载期会把
`handler.__module__ == 插件模块` 的工具重绑定为 `partial(raw, star_cls)`，绑定方法会被
错位成 `(star_cls, event)` → TypeError。因此本模块全部使用
`@dataclass class GoXxxTool(FunctionTool[AstrAgentContext])` + `async def call(...)`，
且 `handler` 永远为 None。
"""

from __future__ import annotations

import asyncio
import copy
import logging
from collections.abc import Callable
from typing import Any

from astrbot.core.agent.run_context import ContextWrapper
from astrbot.core.agent.tool import FunctionTool, ToolExecResult
from astrbot.core.astr_agent_context import AstrAgentContext
from pydantic import Field
from pydantic.dataclasses import dataclass as pydantic_dataclass

from . import config as config_module
from . import convert, errors
from .convert import ToolCallOutcome
from .errors import GoError

LOGGER = logging.getLogger("astrbot.whalego.tools")


def load_tool_specs() -> dict[str, dict[str, Any]]:
    """从包内契约文件构造 7 个工具的 description/parameters（已过滤内部参数）。"""
    contract = config_module.load_contract()
    specs: dict[str, dict[str, Any]] = {}
    for tool_name in config_module.TOOL_NAMES:
        specs[tool_name] = {
            "description": config_module.contract_description(contract, tool_name),
            "parameters": config_module.model_parameters(contract, tool_name),
        }
    return specs


SPECS: dict[str, dict[str, Any]] = load_tool_specs()


def _parameters(tool_name: str) -> dict[str, Any]:
    return copy.deepcopy(SPECS[tool_name]["parameters"])


def _description(tool_name: str) -> str:
    return SPECS[tool_name]["description"]


@pydantic_dataclass
class _WhaleGoToolBase(FunctionTool[AstrAgentContext]):
    """注入依赖与统一调用编排的基类；子类只声明 name/description/parameters + invoke。"""

    client: Any = None
    sessions: Any = None
    policy: Any = None
    allow_screenshot: bool = True
    image_supported: Callable[[str], bool] | None = None
    timeout: float = float(config_module.DEFAULT_TIMEOUT)

    async def call(self, context: ContextWrapper[AstrAgentContext], **kwargs: Any) -> ToolExecResult:
        """AstrBot 本地工具执行入口：校验 → 会话注入 → 并发护栏 → MCP 调用 → 转换。"""
        event = getattr(getattr(context, "context", None), "event", None)
        if event is None:
            return convert.error_result(errors.make_error("INTERNAL", "工具调用缺少 AstrBot 消息事件上下文。"))
        umo = getattr(event, "unified_msg_origin", "") or ""
        is_admin = bool(event.is_admin()) if hasattr(event, "is_admin") else False
        if self.policy is not None:
            policy_error: GoError | None = self.policy.check_tool(self.name, is_admin=is_admin)
            if policy_error is not None:
                return convert.error_result(policy_error)
        if self.sessions is None or self.client is None:
            return convert.error_result(
                errors.make_error("INTERNAL", "插件尚未完成初始化（client/sessions 缺失）。")
            )
        session_id = self.sessions.session_id(umo)
        effective_timeout = self._effective_timeout(context)
        # 内部参数只允许插件注入：模型即使伪造也不接受（模型面 schema 已过滤）。
        model_kwargs = {key: value for key, value in kwargs.items() if key not in config_module.INTERNAL_PARAMS}
        try:
            slot = self.policy.call_slot(umo) if self.policy is not None else _NullSlot()
            async with slot:
                outcome = await self.invoke(self.client, session_id, _timeout=effective_timeout, **model_kwargs)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 - 工具失败必须转为模型可见错误，不能抛出
            LOGGER.exception("WhaleGo 工具 %s 调用异常", self.name)
            return convert.error_result(errors.make_error("INTERNAL", details={"tool": self.name}))
        self._after_success(umo, outcome)
        image_supported = True
        if self.image_supported is not None:
            try:
                image_supported = bool(self.image_supported(umo))
            except Exception:  # noqa: BLE001 - 能力探测失败时保守保留图片
                image_supported = True
        return convert.to_tool_result(
            outcome,
            allow_screenshot=bool(self.allow_screenshot),
            image_supported=image_supported,
        )

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        raise NotImplementedError

    def _effective_timeout(self, context: ContextWrapper[AstrAgentContext]) -> float:
        """插件 timeout 与 AstrBot 本次 tool_call_timeout-5s 取较小值（D12）。"""
        base = float(self.timeout)
        astr_timeout = getattr(context, "tool_call_timeout", None)
        if isinstance(astr_timeout, (int, float)) and astr_timeout > 0:
            base = min(base, max(5.0, float(astr_timeout) - config_module.ASTRBOT_TIMEOUT_MARGIN))
        return base

    def _after_success(self, umo: str, outcome: ToolCallOutcome) -> None:
        """成功后的可选钩子（go_observe 记录最近观察提示）。"""


class _NullSlot:
    async def __aenter__(self) -> None:
        return None

    async def __aexit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        return None


@pydantic_dataclass
class GoHealthTool(_WhaleGoToolBase):
    name: str = "go_health"
    description: str = _description("go_health")
    parameters: dict = Field(default_factory=lambda: _parameters("go_health"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        return await client.health(session_id, timeout=_timeout)


@pydantic_dataclass
class GoAppsTool(_WhaleGoToolBase):
    name: str = "go_apps"
    description: str = _description("go_apps")
    parameters: dict = Field(default_factory=lambda: _parameters("go_apps"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        include_windows = kwargs.get("include_windows")
        return await client.apps(
            session_id,
            include_windows=include_windows if isinstance(include_windows, bool) else None,
            timeout=_timeout,
        )


@pydantic_dataclass
class GoWindowsTool(_WhaleGoToolBase):
    name: str = "go_windows"
    description: str = _description("go_windows")
    parameters: dict = Field(default_factory=lambda: _parameters("go_windows"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        app_id = kwargs.get("app_id")
        return await client.windows(
            session_id, app_id=app_id if isinstance(app_id, str) else None, timeout=_timeout
        )


@pydantic_dataclass
class GoObserveTool(_WhaleGoToolBase):
    name: str = "go_observe"
    description: str = _description("go_observe")
    parameters: dict = Field(default_factory=lambda: _parameters("go_observe"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        include_screenshot = kwargs.get("include_screenshot")
        if not isinstance(include_screenshot, bool):
            include_screenshot = None
        elif not self.allow_screenshot:
            include_screenshot = False  # 插件侧开关：三开关 AND 的兜底
        outcome = await client.observe(
            session_id,
            window_id=_as_optional_str(kwargs.get("window_id")),
            app_id=_as_optional_str(kwargs.get("app_id")),
            include_screenshot=include_screenshot,
            settle_ms=_as_optional_int(kwargs.get("settle_ms")),
            max_elements=_as_optional_int(kwargs.get("max_elements")),
            max_value_chars=_as_optional_int(kwargs.get("max_value_chars")),
            timeout=_timeout,
        )
        return outcome

    def _after_success(self, umo: str, outcome: ToolCallOutcome) -> None:
        if outcome.result is None or self.sessions is None:
            return
        payload = convert.parse_json_payload(outcome.result)
        if isinstance(payload, dict):
            try:
                self.sessions.remember_observe(umo, payload)
            except Exception:  # noqa: BLE001 - 提示失败不影响主流程
                LOGGER.debug("记录最近观察提示失败", exc_info=True)


@pydantic_dataclass
class GoActTool(_WhaleGoToolBase):
    name: str = "go_act"
    description: str = _description("go_act")
    parameters: dict = Field(default_factory=lambda: _parameters("go_act"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        target = kwargs.get("target")
        raw_args = kwargs.get("args")
        op = kwargs.get("op")
        return await client.act(
            session_id,
            op=op if isinstance(op, str) else "",
            target=target if isinstance(target, dict) else None,
            args=raw_args if isinstance(raw_args, dict) else None,
            timeout=_timeout,
        )


@pydantic_dataclass
class GoLaunchTool(_WhaleGoToolBase):
    name: str = "go_launch"
    description: str = _description("go_launch")
    parameters: dict = Field(default_factory=lambda: _parameters("go_launch"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        return await client.launch(
            session_id,
            app=_as_optional_str(kwargs.get("app")),
            app_id=_as_optional_str(kwargs.get("app_id")),
            wait_ms=_as_optional_int(kwargs.get("wait_ms")),
            timeout=_timeout,
        )


@pydantic_dataclass
class GoStopTool(_WhaleGoToolBase):
    name: str = "go_stop"
    description: str = _description("go_stop")
    parameters: dict = Field(default_factory=lambda: _parameters("go_stop"))

    async def invoke(self, client: Any, session_id: str, *, _timeout: float, **kwargs: Any) -> ToolCallOutcome:
        reason = _as_optional_str(kwargs.get("reason"))
        if reason is not None:
            reason = reason[:200]
        # scope 是内部参数：模型面不可见，插件固定注入 "session"（只停本会话，D1/D2）。
        return await client.stop(session_id, reason=reason, scope="session", timeout=_timeout)


def _as_optional_str(value: Any) -> str | None:
    return value if isinstance(value, str) else None


def _as_optional_int(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    return value if isinstance(value, int) else None


TOOL_CLASSES: tuple[type[FunctionTool], ...] = (
    GoHealthTool,
    GoAppsTool,
    GoWindowsTool,
    GoObserveTool,
    GoActTool,
    GoLaunchTool,
    GoStopTool,
)

__all__ = [
    "SPECS",
    "TOOL_CLASSES",
    "GoActTool",
    "GoAppsTool",
    "GoHealthTool",
    "GoLaunchTool",
    "GoObserveTool",
    "GoStopTool",
    "GoWindowsTool",
    "load_tool_specs",
]
