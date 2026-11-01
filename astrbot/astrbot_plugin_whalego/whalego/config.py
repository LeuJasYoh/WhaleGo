"""WhaleGo AstrBot 插件的 L0 配置层。

本模块不得 import astrbot（见 ARCHITECTURE §2.2 / §4.1）：配置校验、契约加载与
资源加载都必须能在 `uv` venv + pytest 下脱离 AstrBot 直接测试。

关键约束（PROTOCOL v1.3 / contracts contract_version=1.3）：
- 契约文件是工具面的单一事实源；模型面 schema 必须过滤掉 `x-internal: true` 的参数。
- 插件 timeout 默认 45s，必须 clamp 到 AstrBot `tool_call_timeout - 5s`；
  若结果 <= 执行器整体预算（<=40s）必须告警（PROTOCOL §7 / D12）。
"""

from __future__ import annotations

import copy
import json
from collections.abc import Mapping
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Any, Final

PLUGIN_NAME: Final = "astrbot_plugin_whalego"
PLUGIN_VERSION: Final = "0.1.0"
CONTRACT_VERSION: Final = "1.3"
PROTOCOL_VERSION: Final = 1

DEFAULT_URL: Final = "http://host.docker.internal:8765/mcp"
DEFAULT_TIMEOUT: Final = 45
MIN_TIMEOUT: Final = 5
MAX_TIMEOUT: Final = 300
ASTRBOT_TIMEOUT_MARGIN: Final = 5
EXECUTOR_BUDGET_SECONDS: Final = 40

TOOL_NAMES: Final[tuple[str, ...]] = (
    "go_health",
    "go_apps",
    "go_windows",
    "go_observe",
    "go_act",
    "go_launch",
    "go_stop",
)

#: 模型面 schema 绝对不得出现的内部参数（PROTOCOL §0.9 / §3.3）。
INTERNAL_PARAMS: Final[tuple[str, ...]] = ("session_id", "request_id", "scope")

#: 只读模式下被插件拦截的动作工具（PROTOCOL §8：READONLY_MODE）。
READONLY_BLOCKED_TOOLS: Final[frozenset[str]] = frozenset({"go_act", "go_launch"})

PACKAGE_DIR: Final = Path(__file__).resolve().parent
CONTRACTS_PATH: Final = PACKAGE_DIR / "contracts" / "whalego.tools.v1.json"
USAGE_PROMPT_PATH: Final = PACKAGE_DIR.parent / "prompts" / "usage.md"


def _warn(warnings: list[str], message: str) -> None:
    warnings.append(message)


def _as_int(value: Any, *, name: str, default: int, warnings: list[str]) -> int:
    if isinstance(value, bool) or value is None:
        _warn(warnings, f"{name}={value!r} 不是整数，已回退默认值 {default}。")
        return default
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(round(value))
    if isinstance(value, str):
        text = value.strip()
        try:
            return int(float(text))
        except ValueError:
            _warn(warnings, f"{name}={value!r} 不是整数，已回退默认值 {default}。")
            return default
    _warn(warnings, f"{name}={value!r} 不是整数，已回退默认值 {default}。")
    return default


def _as_bool(value: Any, *, name: str, default: bool, warnings: list[str]) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        return value != 0
    if isinstance(value, str):
        text = value.strip().lower()
        if text in {"true", "1", "yes", "on"}:
            return True
        if text in {"false", "0", "no", "off"}:
            return False
    _warn(warnings, f"{name}={value!r} 不是布尔值，已回退默认值 {default}。")
    return default


def _as_str(value: Any, *, name: str, default: str, warnings: list[str]) -> str:
    if isinstance(value, str):
        return value
    if value is None:
        return default
    _warn(warnings, f"{name}={value!r} 不是字符串，已回退默认值 {default!r}。")
    return default


def _validate_url(value: Any, warnings: list[str]) -> str:
    url = _as_str(value, name="url", default=DEFAULT_URL, warnings=warnings).strip()
    if not url:
        _warn(warnings, f"url 为空，已回退默认值 {DEFAULT_URL}。")
        return DEFAULT_URL
    lowered = url.lower()
    if not (lowered.startswith("http://") or lowered.startswith("https://")):
        _warn(warnings, f"url={url!r} 不是 http(s) 地址，已回退默认值 {DEFAULT_URL}。")
        return DEFAULT_URL
    if not lowered.rstrip("/").endswith("/mcp"):
        _warn(warnings, f"url={url!r} 未以 /mcp 结尾，MCP Streamable HTTP 端点通常为 .../mcp。")
    return url


def _validate_allowed_tools(value: Any, warnings: list[str]) -> tuple[str, ...]:
    if value is None:
        return TOOL_NAMES
    if not isinstance(value, (list, tuple)) or not all(isinstance(item, str) for item in value):
        _warn(warnings, f"allowed_tools={value!r} 不是字符串列表，已回退全部 7 个工具。")
        return TOOL_NAMES
    selected = {item.strip() for item in value if item.strip()}
    unknown = sorted(selected - set(TOOL_NAMES))
    if unknown:
        _warn(warnings, f"allowed_tools 含未知工具 {unknown}，已忽略。")
    ordered = tuple(name for name in TOOL_NAMES if name in selected)
    if not ordered:
        _warn(warnings, "allowed_tools 为空，已回退全部 7 个工具。")
        return TOOL_NAMES
    return ordered


@dataclass(frozen=True)
class ConnectionConfig:
    """MCP 连接配置（url/token/timeout），token 不出现在 repr/日志。"""

    url: str = DEFAULT_URL
    token: str = field(default="", repr=False)
    timeout: float = float(DEFAULT_TIMEOUT)

    def headers(self) -> dict[str, str]:
        """返回 Streamable HTTP 需要的认证头；无 token 时返回空 dict。"""
        if not self.token:
            return {}
        return {"Authorization": f"Bearer {self.token}"}


@dataclass(frozen=True)
class WhaleGoConfig:
    """插件配置（已完成防御性校验与 clamp）。"""

    connection: ConnectionConfig
    readonly: bool = False
    admin_only: bool = True
    allow_screenshot: bool = True
    inject_usage_prompt: bool = True
    allowed_tools: tuple[str, ...] = TOOL_NAMES
    astr_tool_call_timeout: int = 120
    warnings: tuple[str, ...] = ()

    @classmethod
    def from_astrbot_config(
        cls,
        raw: Mapping[str, Any] | None,
        *,
        astr_tool_call_timeout: int = 120,
    ) -> WhaleGoConfig:
        """从 AstrBot 插件配置 dict 构造；类型/范围非法时回退默认并记录告警。"""
        data: dict[str, Any] = dict(raw or {})
        warnings: list[str] = []
        astr_timeout = _as_int(
            astr_tool_call_timeout,
            name="astr_tool_call_timeout",
            default=120,
            warnings=warnings,
        )
        url = _validate_url(data.get("url", DEFAULT_URL), warnings)
        token = _as_str(data.get("token", ""), name="token", default="", warnings=warnings).strip()
        if not token:
            _warn(warnings, "token 为空：执行器会返回 401（AUTH_FAILED），请在配对后填入 wg_ 前缀 token。")

        configured_timeout = _as_int(
            data.get("timeout", DEFAULT_TIMEOUT),
            name="timeout",
            default=DEFAULT_TIMEOUT,
            warnings=warnings,
        )
        if not MIN_TIMEOUT <= configured_timeout <= MAX_TIMEOUT:
            _warn(warnings, f"timeout={configured_timeout} 超出 [{MIN_TIMEOUT}, {MAX_TIMEOUT}]，已 clamp。")
            configured_timeout = min(max(configured_timeout, MIN_TIMEOUT), MAX_TIMEOUT)

        clamp_ceiling = max(MIN_TIMEOUT, astr_timeout - ASTRBOT_TIMEOUT_MARGIN)
        if configured_timeout > clamp_ceiling:
            _warn(
                warnings,
                f"timeout={configured_timeout}s 超过 AstrBot tool_call_timeout({astr_timeout}s)-"
                f"{ASTRBOT_TIMEOUT_MARGIN}s，已 clamp 为 {clamp_ceiling}s。",
            )
            configured_timeout = clamp_ceiling
        if configured_timeout <= EXECUTOR_BUDGET_SECONDS:
            _warn(
                warnings,
                f"timeout={configured_timeout}s <= 执行器整体预算 {EXECUTOR_BUDGET_SECONDS}s："
                "动作可能先在插件侧超时（按 DISPATCH_UNCERTAIN 呈现）。请调大 AstrBot "
                "agent_runner.config.misc.tool_call_timeout。",
            )

        readonly = _as_bool(data.get("readonly", False), name="readonly", default=False, warnings=warnings)
        admin_only = _as_bool(data.get("admin_only", True), name="admin_only", default=True, warnings=warnings)
        allow_screenshot = _as_bool(
            data.get("allow_screenshot", True), name="allow_screenshot", default=True, warnings=warnings
        )
        inject_usage_prompt = _as_bool(
            data.get("inject_usage_prompt", True),
            name="inject_usage_prompt",
            default=True,
            warnings=warnings,
        )
        allowed_tools = _validate_allowed_tools(data.get("allowed_tools", list(TOOL_NAMES)), warnings)

        return cls(
            connection=ConnectionConfig(url=url, token=token, timeout=float(configured_timeout)),
            readonly=readonly,
            admin_only=admin_only,
            allow_screenshot=allow_screenshot,
            inject_usage_prompt=inject_usage_prompt,
            allowed_tools=allowed_tools,
            astr_tool_call_timeout=astr_timeout,
            warnings=tuple(warnings),
        )


def read_astrbot_tool_call_timeout(config: Mapping[str, Any] | None) -> int:
    """从 AstrBot 全局配置读取本机生效的 tool_call_timeout（默认 120s）。

    4.29.3 的键为 `agent_runner.config.misc.tool_call_timeout`；为兼容旧配置/文档，
    同时接受 `provider_settings.tool_call_timeout`（PROTOCOL 文档措辞）。
    """
    if not isinstance(config, Mapping):
        return 120
    warnings: list[str] = []
    runner = config.get("agent_runner")
    if isinstance(runner, Mapping):
        runner_config = runner.get("config")
        if isinstance(runner_config, Mapping):
            misc = runner_config.get("misc")
            if isinstance(misc, Mapping) and "tool_call_timeout" in misc:
                return max(
                    MIN_TIMEOUT,
                    _as_int(
                        misc["tool_call_timeout"], name="tool_call_timeout", default=120, warnings=warnings
                    ),
                )
    provider_settings = config.get("provider_settings")
    if isinstance(provider_settings, Mapping) and "tool_call_timeout" in provider_settings:
        return max(
            MIN_TIMEOUT,
            _as_int(
                provider_settings["tool_call_timeout"],
                name="provider_settings.tool_call_timeout",
                default=120,
                warnings=warnings,
            ),
        )
    return 120


def masked_token(token: str) -> str:
    """GUI 规范：首 5 后 4 掩码；过短时全掩码。"""
    if not token:
        return "(未配置)"
    if len(token) <= 9:
        return "***"
    return f"{token[:5]}••••••••{token[-4:]}"


@lru_cache(maxsize=1)
def load_contract(path: str | None = None) -> dict[str, Any]:
    """加载包内 contracts/whalego.tools.v1.json（单一事实源副本）。"""
    contract_path = Path(path) if path else CONTRACTS_PATH
    with contract_path.open(encoding="utf-8-sig") as handle:
        contract = json.load(handle)
    if not isinstance(contract, dict) or not isinstance(contract.get("tools"), list):
        raise ValueError(f"契约文件结构非法：{contract_path}")
    return contract


def contract_tool(contract: Mapping[str, Any], tool_name: str) -> Mapping[str, Any]:
    for tool in contract.get("tools", []):
        if tool.get("name") == tool_name:
            return tool
    raise KeyError(f"契约中不存在工具：{tool_name}")


def _strip_internal(schema: Any) -> Any:
    """递归移除 `x-internal: true` 的属性及其在 required 中的条目。"""
    if isinstance(schema, dict):
        cleaned: dict[str, Any] = {}
        internal_keys: set[str] = set()
        properties = schema.get("properties")
        if isinstance(properties, dict):
            kept: dict[str, Any] = {}
            for key, value in properties.items():
                if isinstance(value, dict) and value.get("x-internal") is True:
                    internal_keys.add(key)
                    continue
                kept[key] = _strip_internal(value)
            cleaned["properties"] = kept
        required = schema.get("required")
        if isinstance(required, list):
            cleaned["required"] = [
                item for item in required if isinstance(item, str) and item not in internal_keys
            ]
        for key, value in schema.items():
            if key in {"properties", "required"}:
                continue
            if key == "x-internal":
                continue
            cleaned[key] = _strip_internal(value)
        return cleaned
    if isinstance(schema, list):
        return [_strip_internal(item) for item in schema]
    return copy.deepcopy(schema)


def model_parameters(contract: Mapping[str, Any], tool_name: str) -> dict[str, Any]:
    """返回模型面 inputSchema：已过滤内部参数（session_id/request_id/scope）。"""
    tool = contract_tool(contract, tool_name)
    schema = tool.get("inputSchema")
    if not isinstance(schema, dict):
        raise ValueError(f"契约工具 {tool_name} 缺少 inputSchema")
    filtered: dict[str, Any] = _strip_internal(schema)
    dumped_properties = filtered.get("properties", {})
    for param in INTERNAL_PARAMS:
        if param in dumped_properties:
            raise ValueError(f"模型面 schema 不得包含内部参数：{tool_name}.{param}")
    return filtered


def contract_description(contract: Mapping[str, Any], tool_name: str) -> str:
    description = contract_tool(contract, tool_name).get("description")
    return description if isinstance(description, str) else ""


@lru_cache(maxsize=1)
def load_usage_prompt(path: str | None = None) -> str:
    """加载 prompts/usage.md（经 on_llm_request 注入，内容与 PROTOCOL §12 同步）。"""
    prompt_path = Path(path) if path else USAGE_PROMPT_PATH
    return prompt_path.read_text(encoding="utf-8").strip()
