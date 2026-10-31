"""宿主侧最小 AstrBot stub（仅 tests 使用）。

容器冒烟测试会用真实 AstrBot 源码验证；宿主 pytest 在无法 import astrbot 时，
用与 AstrBot 4.29.3 同构的最小 pydantic dataclass 验证 FunctionTool 子类可实例化、
call() 接线与注册行为（评审 B1 的 subclass + handler=None 方式）。
"""

from __future__ import annotations

import sys
import types
from typing import Any, Generic

from pydantic import Field
from pydantic.dataclasses import dataclass as pdataclass
from typing_extensions import TypeVar

TContext = TypeVar("TContext", default=Any)
ToolExecResult = Any

_STUBBED = False


@pdataclass
class ToolSchema:
    name: str
    description: str
    parameters: dict


@pdataclass
class FunctionTool(ToolSchema, Generic[TContext]):
    handler: Any = None
    handler_module_path: str | None = None
    active: bool = True
    is_background_task: bool = False

    async def call(self, context: Any, **kwargs: Any) -> ToolExecResult:
        raise NotImplementedError("FunctionTool.call() must be implemented by subclasses")


@pdataclass
class ContextWrapper(Generic[TContext]):
    context: TContext
    messages: list[Any] = Field(default_factory=list)
    tool_call_timeout: int = 120


@pdataclass
class AstrAgentContext:
    __pydantic_config__ = {"arbitrary_types_allowed": True}

    context: Any = None
    event: Any = None
    extra: dict[str, str] = Field(default_factory=dict)


def _module(name: str) -> types.ModuleType:
    module = types.ModuleType(name)
    sys.modules[name] = module
    return module


def install_astrbot_stubs() -> None:
    """幂等安装 astrbot.core.agent.tool / run_context / astr_agent_context stub。"""
    global _STUBBED
    if _STUBBED:
        return
    astrbot = _module("astrbot")
    core = _module("astrbot.core")
    agent = _module("astrbot.core.agent")
    tool_module = _module("astrbot.core.agent.tool")
    run_context = _module("astrbot.core.agent.run_context")
    agent_context = _module("astrbot.core.astr_agent_context")

    tool_module.ToolSchema = ToolSchema
    tool_module.FunctionTool = FunctionTool
    tool_module.ToolExecResult = ToolExecResult
    run_context.ContextWrapper = ContextWrapper
    run_context.TContext = TContext
    agent_context.AstrAgentContext = AstrAgentContext
    agent_context.AgentContextWrapper = ContextWrapper

    astrbot.core = core
    core.agent = agent
    agent.tool = tool_module
    agent.run_context = run_context
    core.astr_agent_context = agent_context
    _STUBBED = True
