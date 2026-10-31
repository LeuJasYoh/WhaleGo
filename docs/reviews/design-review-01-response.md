# 设计评审 01 处理记录（Lead）

> 评审输入：`docs/reviews/design-review-01.md`（独立评审员，2026-10-31，3 blocker + 13 major + 9 minor，结论"需修订后实现"）。
> 修订对象：`docs/DESIGN.md`、`docs/ARCHITECTURE.md`、`docs/PROTOCOL.md`（均已更新到 v0.3 / v0.3 / v1.1）。
> 本文逐条记录"采纳 / 部分采纳 / 不采纳"及落点。

## 1. Blocker（全部采纳，已改）

| # | 结论 | 处理 |
|---|---|---|
| B1 插件注册方式会失败 | 采纳（已源码复核） | 工具改为官方子类方式：`@dataclass class XTool(FunctionTool[AstrAgentContext]) + async call(context, **kwargs)`，`handler=None`。DESIGN §6.2 / ARCHITECTURE §4.2 示例重写；加"禁止 handler=绑定方法"红线与容器内真实冒烟验收（DESIGN §10.2/§11）。 |
| B2 Executor 无法引用 Platform | 采纳 | Executor TFM 改 `net10.0-windows`（DESIGN §5.1/§9.1、ARCHITECTURE §3.1）；测试在 Windows 跑（本来也只在 Windows 有意义）。 |
| B3 失败/回执双通道 | 采纳 | PROTOCOL v1.1 §2 定死单通道：成功 = `isError=false && dispatch_status="accepted"`；`not_sent`/`possibly_sent` 一律 `isError=true` + 错误对象内嵌完整 `action_receipt`；文本首行强制 `OK:` / `ERROR[CODE]:`（AstrBot 不读 isError）。"回执三态"表删除 not_sent/possibly_sent 的成功语义。 |

## 2. Major（13 条全部处理）

| # | 结论 | 处理 |
|---|---|---|
| M1 新鲜度契约矛盾 | 采纳 | 模型不传 frame_id（PROTOCOL §3.6/§3.7/§5；DESIGN §4.3/§7）；动作进入派发后 state/frame **立即失效**；element 不因几何移动失效、frame 依赖几何/DPI/TTL；**标题变化不再作为失效条件**；多窗口按 session 当前窗口 + controller lease 管理。 |
| M2 坐标链缺 UIA hit-test / actionable 无判据 | 采纳 | 坐标链固定为 ① UIA ElementFromPoint hit-test → ② 宿主分类/丢弃矩阵 → ③ PostMessage → ④ 前台 SendInput（ARCHITECTURE §3.6 / DESIGN §5.5）；定义 `frame.actionable` 判据（采集/几何/DPI/TTL/非黑帧/宿主已验证）与 `capture_degraded`（PROTOCOL §3.6/§6）。 |
| M3 工具面未闭环（launch/settle/advisories/app_id/错误码） | 采纳 | 新增第 7 个工具 `pc_launch`（启动/激活 + 有界等待）；observe 增加 `settle_ms`；advisories 定闭集；app_id 规则定稿（`win-exe:<basename>` / `win-aumid:`，标题不作身份）；补 `APP_NOT_FOUND/AMBIGUOUS_APP/LAUNCH_FAILED/LAUNCH_TIMEOUT`。 |
| M4 工具权限默认值与 resume | 采纳 | 删除插件 `tool_permission` 配置；改为 `admin_only: true` + `event.is_admin()` 兜底 + README 配置指引（默认权限实为 member，已源码核实）；`pc_stop` 只"停"，恢复仅管理员命令/托盘/全局热键；托盘+热键提前到 P1。 |
| M5 密码/敏感窗口/截图外发 | 采纳 | PROTOCOL §11 隐私规则：IsPassword → `***`；敏感窗口黑名单；截图双开关默认关闭；插件结果附"不得转发"提示；威胁模型与 README 写明。 |
| M6 审计 drop-oldest 矛盾 | 采纳 | 动作审计返回前落盘（fail-closed：`AUDIT_UNAVAILABLE` 且 not_sent）；观察审计可异步丢并计数进 health。 |
| M7 取消语义过度承诺 | 采纳 | PROTOCOL §7 明确：派发前可取消→not_sent；进入原生调用不可撤销→可能 possibly_sent；插件超时读 `tool_call_timeout` 并 clamp、超时后 unclean 重连、不重发。 |
| M8 多会话/租约/停止作用域 | 采纳 | 新增全局 `controller lease`（TTL 60s，动作续期；非持有者 `CONTROLLER_BUSY` + retry_after_ms）；`pc_stop` 清租约、scope=session|all；多会话测试加入验收。 |
| M9 mcp 版本证据链 | 采纳 | DESIGN §3.3：`requirements.txt` 固定 `mcp>=1.30,<2`；附录 A 记录容器 `pip list = mcp 1.30.0`；compat 覆盖 legacy headers= 与新版 http_client(httpx2) 两条路径；health 暴露 `mcp_protocol_version`；容器内 E2E 作为验收。 |
| M10 v1 能力与验收不匹配 | 采纳 | v1 schema 收窄：click 仅 left 单击、key 仅单键、scroll 仅 up/down；其余返回 `UNSUPPORTED_ACTION`；P1 验收追加 WPF + Edge + 高完整性窗口 + 用户切走 + noop 路径记录。 |
| M11 pc_act schema 不自洽 | 采纳 | 删除 `strategy` 与 `perform_action`；用 `allOf/if/then` 对每个 op 定义 target/args 的条件必填与枚举（PROTOCOL §3.7）。 |
| M12 工具数量不一致 | 采纳 | 统一为 **7 个必需工具**（health/apps/windows/observe/act/launch/stop）；删除 `pc_session` 工具（会话由 `session_id` 参数承载）；pc_stop 进 P0。 |
| M13 AstrBot isError/modality 兜底 | 采纳 | 插件转换层：失败文本 `ERROR[code]:` 前缀、成功 `OK:`；截图前检查 provider modalities，不支持则文本降级；README 说明内置 MCP 路径风险。 |

## 3. Minor（9 条处理）

| # | 处理 |
|---|---|
| m1 命名/配置散乱 | PROTOCOL 补"字段/键名"统一约定；executor 配置（DESIGN §5.8）与插件配置（§6.6）分列并互引；统一 `keys`/`session_id`/`state_ttl_seconds` 等；删 `is_error` 字段。 |
| m2 版本四套 | 统一 `protocol_version=1`（pc_health）+ 载荷 `schema_version=pcctrl-*-v1`；请求不带 schema_version。 |
| m3 mode=diff 语义空 | v1 删除 `mode` 字段；diff 观察推迟到 P2 并需先定义。 |
| m4 预算/图片顺序/DPI 断言 | 预算改为自定值注明；image 放 content 最前；DPI 断言改为"样例窗口实测，实现中夹具验证"。 |
| m5 分层规则矛盾 | L0-L2 不 import astrbot；L3 允许 import `astrbot.core.agent.tool`；FunctionTool 实例化在 L4 main.py；删除"观察缓存"改"最近 state_id 提示"。 |
| m6 错误码漂移 | 错误码唯一事实源改为 PROTOCOL §8；DESIGN §5.9 只引用；补 `retry_after_ms`、`UNSUPPORTED_ACTION` 等。 |
| m7 receipt 泄漏内部术语 | 模型面 receipt 删除 `dispatch.method`，只留 `escalated`；method 只进审计/诊断。 |
| m8 插件预算/超时表述 | 插件文本预算对齐 AstrBot ≈7000 tokens；区分"LLM 工具调用有 wait_for"与"事件 handler 无超时"。 |
| m9 SSE 一行开关未验证 | 删除"服务端一行开关"承诺，SSE 标注"未验证，P3+"。 |

## 4. 遗留（不采纳/延后，均有理由）

- **L2 每调用 token（ZCode 式）**：P3，不在 v1（单用户个人机，L1 + controller lease + 审计已覆盖主要威胁）。
- **确认流程**：P2（用户已选"全自动 + 审计"；只读/stop/热键先兜底）。
- **UIA hit-test 之外的合成弹层/多 surface**：P2。
- **性能基准/大规模矩阵**：P2 起按宿主逐项补。

## 5. 结论

评审的 3 个 blocker 与 13 个 major 全部落到三份文档与契约文件；设计可进入实现阶段（建议按 ARCHITECTURE §7 的 T1-T6 分工，
先 T1/T4 最小闭环 + T5 插件冒烟，再 T2/T3 宿主矩阵）。
