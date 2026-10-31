"""WhaleGo AstrBot 插件的 L0 错误类型与模型文案。

本模块不得 import astrbot。

错误码是 PROTOCOL §8（执行器）与 §8.1（插件侧）的闭集；模型可见文本 = 常量中文
+ 固定英文前缀（`ERROR[CODE]:`）。动作类超时/断线必须呈现为 `DISPATCH_UNCERTAIN`
（possibly_sent，禁止自动重试，只能重新观察）。
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Any, Final

ERROR_SCHEMA_VERSION: Final = "whalego-error-v1"

RETRY_NEVER: Final = "never"
RETRY_RETRY: Final = "retry"
RETRY_REOBSERVE: Final = "reobserve"
RETRY_VALUES: Final[tuple[str, ...]] = (RETRY_NEVER, RETRY_RETRY, RETRY_REOBSERVE)

#: PROTOCOL §8 错误码 → retry（唯一事实源：PROTOCOL / contracts.error_codes）。
EXECUTOR_ERROR_RETRY: Final[Mapping[str, str]] = {
    "INVALID_ARGUMENT": RETRY_NEVER,
    "UNSUPPORTED_PROTOCOL": RETRY_NEVER,
    "UNSUPPORTED_ACTION": RETRY_NEVER,
    "SESSION_NOT_FOUND": RETRY_NEVER,
    "SESSION_EXPIRED": RETRY_NEVER,
    "SESSION_STOPPED": RETRY_NEVER,
    "PAUSED": RETRY_NEVER,
    "SESSION_LOCKED": RETRY_NEVER,
    "CONTROLLER_BUSY": RETRY_RETRY,
    "READONLY_MODE": RETRY_NEVER,
    "RATE_LIMITED": RETRY_RETRY,
    "AUDIT_UNAVAILABLE": RETRY_RETRY,
    "STALE_STATE": RETRY_REOBSERVE,
    "FRAME_NOT_ACTIONABLE": RETRY_REOBSERVE,
    "ELEMENT_UNAVAILABLE": RETRY_REOBSERVE,
    "ELEMENT_ACTION_UNAVAILABLE": RETRY_REOBSERVE,
    "WINDOW_NOT_FOUND": RETRY_REOBSERVE,
    "WINDOW_CHANGED": RETRY_REOBSERVE,
    "APP_NOT_FOUND": RETRY_REOBSERVE,
    "AMBIGUOUS_APP": RETRY_NEVER,
    "LAUNCH_FAILED": RETRY_NEVER,
    "LAUNCH_TIMEOUT": RETRY_NEVER,
    "FOREGROUND_REQUIRED": RETRY_REOBSERVE,
    "UIPI_BLOCKED": RETRY_NEVER,
    "ACTION_TIMEOUT": RETRY_RETRY,
    "DISPATCH_UNCERTAIN": RETRY_REOBSERVE,
    "INTERNAL": RETRY_NEVER,
}

#: PROTOCOL §8.1 插件侧错误码（执行器不会返回）→ retry。
PLUGIN_ERROR_RETRY: Final[Mapping[str, str]] = {
    "EXECUTOR_UNREACHABLE": RETRY_RETRY,
    "EXECUTOR_TIMEOUT": RETRY_REOBSERVE,
    "AUTH_FAILED": RETRY_NEVER,
    "PROTOCOL_MISMATCH": RETRY_NEVER,
    "CONNECTION_LOST": RETRY_REOBSERVE,
}

#: 执行器错误码 → dispatch_status（DISPATCH_UNCERTAIN 是唯一 possibly_sent 的码）。
EXECUTOR_ERROR_DISPATCH: Final[Mapping[str, str]] = {
    "INVALID_ARGUMENT": "not_sent",
    "UNSUPPORTED_PROTOCOL": "not_sent",
    "UNSUPPORTED_ACTION": "not_sent",
    "SESSION_NOT_FOUND": "not_sent",
    "SESSION_EXPIRED": "not_sent",
    "SESSION_STOPPED": "not_sent",
    "PAUSED": "not_sent",
    "SESSION_LOCKED": "not_sent",
    "CONTROLLER_BUSY": "not_sent",
    "READONLY_MODE": "not_sent",
    "RATE_LIMITED": "not_sent",
    "AUDIT_UNAVAILABLE": "not_sent",
    "STALE_STATE": "not_sent",
    "FRAME_NOT_ACTIONABLE": "not_sent",
    "ELEMENT_UNAVAILABLE": "not_sent",
    "ELEMENT_ACTION_UNAVAILABLE": "not_sent",
    "WINDOW_NOT_FOUND": "not_sent",
    "WINDOW_CHANGED": "not_sent",
    "APP_NOT_FOUND": "not_sent",
    "AMBIGUOUS_APP": "not_sent",
    "LAUNCH_FAILED": "not_sent",
    "LAUNCH_TIMEOUT": "not_sent",
    "FOREGROUND_REQUIRED": "not_sent",
    "UIPI_BLOCKED": "not_sent",
    "ACTION_TIMEOUT": "not_sent",
    "DISPATCH_UNCERTAIN": "possibly_sent",
    "INTERNAL": "not_sent_or_possibly_sent",
}

#: 插件生成错误时的固定中文文案（模型可见；不含内部派发术语）。
DEFAULT_MESSAGES: Final[Mapping[str, str]] = {
    # 插件侧（PROTOCOL §8.1）
    "EXECUTOR_UNREACHABLE": (
        "无法连接 Windows 执行器：请确认 WhaleGo 已手动启动、地址/端口正确、防火墙放行，并检查 token。"
    ),
    "EXECUTOR_TIMEOUT": "调用执行器超时（只读类已重连重试一次），请稍后重试或先重新观察。",
    "AUTH_FAILED": "执行器认证失败：token 错误或已被轮换，请在插件配置中更新 token 后重载插件。",
    "PROTOCOL_MISMATCH": "执行器协议版本不兼容：请升级插件或执行器（protocol_version 必须为 1）。",
    "CONNECTION_LOST": "与执行器的连接已断开，请先重新观察后再操作。",
    # 插件自身策略（复用执行器契约码）
    "READONLY_MODE": "当前为只读模式，动作已被禁止；如需操作请让管理员用 /whalego readonly off 关闭只读。",
    "INVALID_ARGUMENT": "参数或工具不被插件允许，请检查后重试。",
    "INTERNAL": "插件内部错误，请稍后重试；若持续出现请查看 AstrBot 插件日志。",
    # 动作不确定（固定文案，禁止重试）
    "DISPATCH_UNCERTAIN": "动作可能已发送，禁止重试，请先重新观察。",
}

#: 用于命令/日志展示的“下一步”提示。
NEXT_STEP_HINTS: Final[Mapping[str, str]] = {
    "EXECUTOR_UNREACHABLE": "在 Windows 托盘确认 WhaleGo 正在运行（手动启动），然后 /whalego health。",
    "AUTH_FAILED": "在 Windows 端「设置 → 连接与配对」复制插件配置，更新 token 后重载插件。",
    "PROTOCOL_MISMATCH": "升级插件或执行器，使 protocol_version 一致（当前契约 v1.2）。",
    "READONLY_MODE": "管理员执行 /whalego readonly off（或修改插件配置）。",
    "DISPATCH_UNCERTAIN": "先 go_observe 重新观察，再决定是否继续；不要重复调用同一个动作。",
}

ACTION_UNCERTAIN_RECEIPT: Final[Mapping[str, Any]] = {
    "action_sent": True,
    "dispatch_status": "possibly_sent",
    "retry_action": False,
    "target_kind": "unknown",
    "escalated": None,
    "verification": {"status": "unverified", "reason": "plugin_timeout_or_disconnect"},
    "state_available": False,
}


@dataclass(frozen=True)
class GoError:
    """`whalego-error-v1` 错误对象（模型可见的失败通道）。"""

    code: str
    message: str
    retry: str
    retry_after_ms: int | None = None
    details: Mapping[str, Any] | None = None
    action_receipt: Mapping[str, Any] | None = None
    diagnostic: Mapping[str, Any] | None = None

    def to_dict(self) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "schema_version": ERROR_SCHEMA_VERSION,
            "code": self.code,
            "message": self.message,
            "retry": self.retry,
            "retry_after_ms": self.retry_after_ms,
            "details": dict(self.details) if self.details else None,
            "action_receipt": dict(self.action_receipt) if self.action_receipt else None,
            "diagnostic": dict(self.diagnostic) if self.diagnostic else None,
        }
        return payload

    def to_text(self) -> str:
        return f"ERROR[{self.code}]: {self.message}"


def retry_for(code: str) -> str:
    if code in PLUGIN_ERROR_RETRY:
        return PLUGIN_ERROR_RETRY[code]
    return EXECUTOR_ERROR_RETRY.get(code, RETRY_NEVER)


def make_error(
    code: str,
    message: str | None = None,
    *,
    retry: str | None = None,
    retry_after_ms: int | None = None,
    details: Mapping[str, Any] | None = None,
    action_receipt: Mapping[str, Any] | None = None,
    diagnostic: Mapping[str, Any] | None = None,
) -> GoError:
    """构造模型可见错误；未给出文案时使用固定中文文案。"""
    resolved_message = message or DEFAULT_MESSAGES.get(code) or f"执行器返回错误 {code}。"
    return GoError(
        code=code,
        message=resolved_message,
        retry=retry or retry_for(code),
        retry_after_ms=retry_after_ms,
        details=details,
        action_receipt=action_receipt,
        diagnostic=diagnostic,
    )


def action_uncertain_error(
    *,
    tool: str,
    reason: str,
    elapsed_ms: int | None = None,
) -> GoError:
    """动作类调用超时/断线：按 DISPATCH_UNCERTAIN（possibly_sent）呈现。"""
    details: dict[str, Any] = {"tool": tool, "reason": reason}
    diagnostic: dict[str, Any] = {"phase": "plugin_dispatch", "source": "astrbot_plugin_whalego"}
    if elapsed_ms is not None:
        diagnostic["elapsed_ms"] = elapsed_ms
    return make_error(
        "DISPATCH_UNCERTAIN",
        details=details,
        action_receipt=dict(ACTION_UNCERTAIN_RECEIPT),
        diagnostic=diagnostic,
    )


def permission_error(tool: str) -> GoError:
    """非管理员调用受保护工具（插件侧兜底；WebUI 也应把工具权限设为 admin）。"""
    return make_error(
        "AUTH_FAILED",
        f"仅管理员可以使用工具 {tool}：请在 AstrBot WebUI 将 WhaleGo 工具权限设为 admin，或联系管理员。",
        details={"tool": tool, "reason": "admin_only"},
    )


def whitelist_error(tool: str) -> GoError:
    """工具被插件 allowed_tools 白名单禁用。"""
    return make_error(
        "INVALID_ARGUMENT",
        f"工具 {tool} 已被插件 allowed_tools 白名单禁用。",
        details={"tool": tool, "reason": "not_in_allowed_tools"},
    )


def readonly_error(tool: str) -> GoError:
    return make_error(
        "READONLY_MODE",
        details={"tool": tool, "reason": "plugin_readonly"},
    )


@dataclass
class PluginStateError(Exception):
    """插件内部状态错误（例如配置非法）；不直接展示给模型。"""

    message: str
    code: str = "INTERNAL"
    details: dict[str, Any] = field(default_factory=dict)

    def to_error(self) -> GoError:
        return make_error(self.code, self.message, details=self.details)
