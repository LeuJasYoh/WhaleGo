"""WhaleGo AstrBot 插件入口（L4 Star Adapter，只做接线）。

职责：
- 校验配置（config.py）→ 构造连接/会话/策略/客户端 → 实例化 7 个 FunctionTool 子类并经
  `context.add_llm_tools(...)` 注册（官方推荐方式，handler=None，不触发加载期重绑定，评审 B1）；
- 生命周期：`initialize()` 异步建立连接 + 后台指数退避重连（失败只告警，不阻塞 AstrBot 启动）；
  `terminate()` 取消任务并关闭连接；
- `/whalego`（别名 `/wg`）命令：status/health/stop/resume/readonly/help（权限见 NAMING §4.3）；
- `on_llm_request` 注入 prompts/usage.md（≤15 行，与 PROTOCOL §12 同步）。
"""

from __future__ import annotations

import asyncio
import contextlib
from typing import Any

from astrbot.api.event import AstrMessageEvent, filter
from astrbot.api.provider import ProviderRequest
from astrbot.api.star import Context, Star

from .whalego import config as config_module
from .whalego import convert, errors, mcp_client, policy, session
from .whalego import tools as tools_module
from .whalego.client import WhaleGoClient

USAGE_MARKER = "## WhaleGo 桌面工具使用规则"
HELP_TEXT = """WhaleGo（go_* 是品牌前缀，不是 Go 语言）命令：
/whalego status  连接状态/会话/最近观察（成员）
/whalego health  调用执行器 go_health（成员）
/whalego stop    只停止本会话的动作；恢复仅限本机（成员）
/whalego resume  提示到 Windows 托盘/设置窗口恢复（成员，不做远程恢复）
/whalego readonly on|off  插件只读开关（管理员）
/whalego help    本帮助；别名 /wg
注意：暂停/停止只能在本机 GUI/托盘恢复（二次确认）；截图默认可用但默认不自动附图。"""
RESUME_TEXT = (
    "WhaleGo 的暂停/停止恢复只能在 Windows 本机完成：请到托盘/「WhaleGo 设置」窗口点恢复（二次确认）。\n"
    "容器与模型都没有恢复能力（D2/D18）；恢复后请先重新 go_observe。"
)


class WhaleGoPlugin(Star):
    """AstrBot Star 插件主类：全部接线集中在这里（≤200 行）。"""

    def __init__(self, context: Context, config: dict[str, Any] | None = None) -> None:
        super().__init__(context)
        self._config_obj = config
        raw_config = dict(config or {})
        astr_timeout = config_module.read_astrbot_tool_call_timeout(self.context.get_config())
        self.config = config_module.WhaleGoConfig.from_astrbot_config(
            raw_config, astr_tool_call_timeout=astr_timeout
        )
        self.sessions = session.SessionMap()
        self.policy = policy.PolicyEngine(
            sessions=self.sessions,
            readonly=self.config.readonly,
            admin_only=self.config.admin_only,
            allowed_tools=self.config.allowed_tools,
        )
        self.connection = mcp_client.McpConnection(self.config.connection, logger=self.logger)
        self.client = WhaleGoClient(self.connection, self.config, logger=self.logger)
        self.supervisor = mcp_client.ReconnectSupervisor(self.connection, logger=self.logger)
        self._usage_prompt = config_module.load_usage_prompt() if self.config.inject_usage_prompt else ""
        self._supervisor_task: asyncio.Task[None] | None = None
        self._bootstrap_task: asyncio.Task[None] | None = None
        self._tools = [cls(**self._tool_dependencies()) for cls in tools_module.TOOL_CLASSES]
        self.context.add_llm_tools(*self._tools)
        self.logger.info(
            "WhaleGo 插件已加载：%d 个工具，url=%s，readonly=%s，allow_screenshot=%s",
            len(self._tools),
            self.config.connection.url,
            self.config.readonly,
            self.config.allow_screenshot,
        )

    def _tool_dependencies(self) -> dict[str, Any]:
        return {
            "client": self.client,
            "sessions": self.sessions,
            "policy": self.policy,
            "allow_screenshot": self.config.allow_screenshot,
            "image_supported": self._provider_supports_image,
            "timeout": self.config.connection.timeout,
        }

    def _provider_supports_image(self, umo: str) -> bool:
        """按 AstrBot 规则判断当前 provider 是否接受 image（空 modalities 视为支持）。"""
        try:
            provider = self.context.get_using_provider(umo)
        except Exception:  # noqa: BLE001 - 探测失败时保守保留图片
            return True
        config = getattr(provider, "provider_config", None) if provider else None
        modalities = config.get("modalities") if isinstance(config, dict) else None
        if not modalities:
            return True
        return isinstance(modalities, (list, tuple)) and "image" in modalities

    # ------------------------------------------------------------------ 生命周期
    async def initialize(self) -> None:
        for warning in self.config.warnings:
            self.logger.warning("WhaleGo 配置：%s", warning)
        self._supervisor_task = asyncio.create_task(self.supervisor.run(), name="whalego-reconnect")
        self._bootstrap_task = asyncio.create_task(self._bootstrap(), name="whalego-bootstrap")

    async def _bootstrap(self) -> None:
        """连接一次并做协议版本自检；失败只告警，不阻塞 AstrBot（后台继续重连）。"""
        if not await self.connection.wait_ready(8.0):
            self.logger.warning(
                "WhaleGo 执行器暂不可达（后台继续重连）：%s；请确认 Windows 端已手动启动。",
                self.config.connection.url,
            )
            return
        await self.client.check_protocol()

    async def terminate(self) -> None:
        self.supervisor.request_stop()
        task = self._bootstrap_task
        if task is not None and not task.done():
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
        self._bootstrap_task = None
        # supervisor 在自己的 task 内关闭连接（anyio cancel scope owner 一致）。
        await self.supervisor.stop(timeout=10.0)
        self._supervisor_task = None
        self.logger.info("WhaleGo 插件已卸载")

    # ------------------------------------------------------------------ 命令
    @filter.command("whalego", alias={"wg"})
    async def whalego(self, event: AstrMessageEvent, sub: str = "", arg: str = ""):
        command = (sub or "").strip().lower()
        try:
            if command in ("", "help"):
                yield event.plain_result(HELP_TEXT)
            elif command == "status":
                yield event.plain_result(self._status_text(event))
            elif command == "health":
                yield event.plain_result(await self._health_text(event))
            elif command == "stop":
                yield event.plain_result(await self._stop_text(event))
            elif command == "resume":
                yield event.plain_result(RESUME_TEXT)
            elif command in ("readonly", "ro"):
                reply = await self._set_readonly(event, (arg or "").strip().lower())
                yield event.plain_result(reply)
            else:
                yield event.plain_result(f"未知子命令：{command}\n\n{HELP_TEXT}")
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 - 命令失败不能拖垮事件循环
            self.logger.exception("WhaleGo 命令执行失败")
            yield event.plain_result("WhaleGo 命令执行失败，请查看 AstrBot 插件日志。")

    def _status_text(self, event: AstrMessageEvent) -> str:
        umo = event.unified_msg_origin
        state = self.connection.state.value
        lines = [
            f"WhaleGo 状态：连接={state}，协议={'ok' if self.client.protocol_ok else ('不兼容' if self.client.protocol_ok is False else '未知')}",
            f"地址：{self.config.connection.url}；token：{config_module.masked_token(self.config.connection.token)}",
            f"策略：readonly={self.policy.readonly}，admin_only={self.policy.admin_only}，"
            f"allow_screenshot={self.config.allow_screenshot}",
            f"超时：{self.config.connection.timeout:g}s（AstrBot tool_call_timeout={self.config.astr_tool_call_timeout}s）",
            f"会话：{self.sessions.session_id(umo)}；最近观察：{self.sessions.recent(umo).summary() if self.sessions.recent(umo) else '（无）'}",
        ]
        if self.supervisor.attempts:
            lines.append(
                f"重连：已连续失败 {self.supervisor.attempts} 次；最近错误：{self.supervisor.last_error}"
            )
        if self.client.last_error is not None:
            lines.append(f"最近错误：{self.client.last_error.to_text()}")
        return "\n".join(lines)

    async def _health_text(self, event: AstrMessageEvent) -> str:
        session_id = self.sessions.session_id(event.unified_msg_origin)
        outcome = await self.client.health(session_id)
        if outcome.error is not None:
            return outcome.error.to_text()
        if convert.outcome_failed(outcome):
            return convert.first_text(outcome.result) or "ERROR[INTERNAL]: go_health 调用失败。"
        payload = convert.parse_json_payload(outcome.result) if outcome.result is not None else None
        if not isinstance(payload, dict):
            return "ERROR[INTERNAL]: go_health 返回中找不到结构化 JSON。"
        capabilities = payload.get("capabilities") if isinstance(payload.get("capabilities"), dict) else {}
        lines = [
            f"WhaleGo 执行器 ok={payload.get('ok')} version={payload.get('version')} "
            f"protocol_version={payload.get('protocol_version')} mcp_protocol_version={payload.get('mcp_protocol_version')}",
            f"paused={payload.get('paused')} stopped={payload.get('stopped')} "
            f"session_stopped={payload.get('session_stopped')} accepting_actions={payload.get('accepting_actions')}",
            f"actions={capabilities.get('actions')}；launch_allowlist={capabilities.get('launch_allowlist')}",
            f"会话：{session_id}",
        ]
        return "\n".join(lines)

    async def _stop_text(self, event: AstrMessageEvent) -> str:
        session_id = self.sessions.session_id(event.unified_msg_origin)
        sender = event.get_sender_name() if hasattr(event, "get_sender_name") else "user"
        outcome = await self.client.stop(session_id, reason=f"chat_command:{sender}"[:200])
        if outcome.error is not None:
            return outcome.error.to_text()
        if convert.outcome_failed(outcome):
            return convert.first_text(outcome.result) or "ERROR[INTERNAL]: go_stop 调用失败。"
        return (
            "OK: 已停止本会话动作（scope=session）：后续 go_act/go_launch 会返回 SESSION_STOPPED，"
            "观察仍可用。\n注意：模型/容器只能停本会话；全局停止与恢复都只能在本机托盘/设置窗口完成。"
        )

    async def _set_readonly(self, event: AstrMessageEvent, arg: str) -> str:
        if not event.is_admin():
            return errors.permission_error("/whalego readonly").to_text()
        if arg not in {"on", "off"}:
            return "用法：/whalego readonly on|off（管理员）"
        enabled = arg == "on"
        self.policy.set_readonly(enabled)
        await self._persist_readonly(enabled)
        return f"OK: 插件只读已{'开启' if enabled else '关闭'}（readonly={enabled}）。" + (
            "动作工具将返回 ERROR[READONLY_MODE]。" if enabled else "动作工具已恢复可用。"
        )

    async def _persist_readonly(self, enabled: bool) -> None:
        """尽力把 readonly 写回插件配置（失败只告警，内存状态仍生效）。"""
        config_obj = self._config_obj
        if config_obj is None:
            return
        try:
            config_obj["readonly"] = enabled
            save_async = getattr(config_obj, "save_config_async", None)
            if callable(save_async):
                await save_async()
            else:
                save = getattr(config_obj, "save_config", None)
                if callable(save):
                    save()
        except Exception:  # noqa: BLE001 - 配置持久化失败不影响运行
            self.logger.warning("WhaleGo readonly 持久化失败（内存状态已生效）", exc_info=True)

    # ------------------------------------------------------------------ 提示注入
    @filter.on_llm_request()
    async def inject_usage_prompt(self, event: AstrMessageEvent, req: ProviderRequest) -> None:
        if not self.config.inject_usage_prompt or not self._usage_prompt:
            return
        current = getattr(req, "system_prompt", "") or ""
        if USAGE_MARKER in current:
            return
        req.system_prompt = f"{current}\n\n{USAGE_MARKER}\n{self._usage_prompt}".strip()
