# 设计评审 01 复审结论（DESIGN v0.3 / ARCHITECTURE v0.3 / PROTOCOL v1.1）

> 复审方式：逐条核对 design-review-01 的 3 blocker + 13 major + 9 minor 的修订落点与行号，不重复全量评审。
> 证据基线：DESIGN 783 行 / ARCHITECTURE 895 行 / PROTOCOL 552 行 / contracts/pcctrl.tools.v1.json 587 行。
> **结论：可进入实现**。无 blocker；有 4 项开工前小修（文字/契约一致性，不改变架构），另有若干残留措辞。

## Blocker

- **B1 PASS**：改为官方子类方式 + `handler=None`，明确禁止绑定方法，并加容器真实冒烟（DESIGN §6.2:436-461；ARCHITECTURE §4.2:697-727；验收 DESIGN:707、ARCHITECTURE:780-781）。子类放 pcctrl/tools.py 也安全：模块路径含 `plugins` 时 `_resolve_tool_handler_module_path` 能解析回插件 main（ref/AstrBot/star/context.py:109-122），且 handler=None 跳过加载期重绑定（star_manager.py:1281-1306）。nit：ARCHITECTURE:623 的 L3 导入白名单只写 `astrbot.core.agent.tool`，但示例还导入了 `astrbot.core.agent.run_context`/`astr_agent_context`（:701-703），需补全。
- **B2 PASS**：Executor 改 `net10.0-windows` 并说明原因（ARCHITECTURE §3.1:112-115；DESIGN §5.1:224、§9.1:632）。nit：测试项目 TFM 未显式写出。
- **B3 PASS（核心）/部分**：单失败通道、错误内嵌 receipt、`OK:`/`ERROR[<code>]:` 前缀、回执仅 accepted 已落地（PROTOCOL §0:15-19、§2:42-88、§3.7:315-365；ARCHITECTURE:485-487、510-512）。部分：成功判据写成全局 `isError=false && dispatch_status=accepted`，但 health/apps/windows/observe/launch/stop 没有 dispatch_status（PROTOCOL:16、§2.1:42；contracts notes 同），应限定为"动作类"。

## Major

- **M1 部分**：frame_id 内部化/模型不传、动作后 state+frame 立即失效、标题不失效、element 与 frame 分离均已落地（PROTOCOL:20/256/466、§5.2:440-453；DESIGN:328-330；ARCHITECTURE:796）。残留：ARCHITECTURE 不变量 1 仍写坐标目标 `frame_id + 像素`（:25），改一句即可。
- **M2 PASS**：hit-test 主/次级 + 派发路径语义 + actionable 判据 + capture_degraded 已写（PROTOCOL §3.6:251-253、§6:469-471；ARCHITECTURE:430/435/459；DESIGN:325/342）。
- **M3 部分**：pc_launch/settle_ms/advisories 闭集/APP 错误码已加（PROTOCOL:105/205/248-249/367-395；DESIGN:536-546）。残留：app_id 用 basename+#N（PROTOCOL:167），#N 随枚举顺序变化、跨会话不稳定，与研究"canonical exe 绝对路径"不一致；contracts 的 pc_observe 额外 `anyOf` 必填 window_id/app_id（contracts:94），与 PROTOCOL §3.6:197-211 不一致。
- **M4 PASS**：删除插件 tool_permission；admin_only 默认 + is_admin 兜底；pc_stop 无 resume；托盘/热键提前 P1（DESIGN §6.6:500、§6.7:509、§8.3:618；PROTOCOL §3.9:397-410；ARCHITECTURE:747-751）。
- **M5 PASS（核心）/部分**：IsPassword→***、敏感窗口黑名单、截图双开关、不得转发提示已写（PROTOCOL:24/546-551；DESIGN:321/620；ARCHITECTURE:438/766）。残留：DESIGN 威胁模型仍写"可脱敏（P3）"（:598），应改 day-1。
- **M6 PASS**：写动作审计返回前落盘 fail-closed、观察审计丢计数（ARCHITECTURE:35-36/521-523；DESIGN:371-372；PROTOCOL:505）。
- **M7 PASS**：取消/超时契约、原生调用不可撤销、插件 clamp + unclean 重连（PROTOCOL §7:477-489；ARCHITECTURE:33-34；DESIGN:468-470）。
- **M8 PASS**：controller lease/ CONTROLLER_BUSY/stop scope/多会话验收（PROTOCOL §5.3:455-460、§3.9:397-410；DESIGN:360-363、:715）。
- **M9 部分**：requirements `mcp>=1.30,<2`、容器 pip list 1.30.0、compat 双路径、health `mcp_protocol_version`、容器 E2E 已加（DESIGN:30-31/137-138/附录A#19:772；ARCHITECTURE:637/661；PROTOCOL:34/118）。残留：DESIGN:145 仍写"SSE 兼容仅作退路（服务端一行开关）"；DESIGN:420 仍写 `mcp>=1.30（镜像已内置，声明即可）`。
- **M10 PASS**：v1 动作面收窄 + UNSUPPORTED_ACTION + P1 验收追加 WPF/Edge/高完整性/切走（PROTOCOL:310-311/266-306；DESIGN:688）。
- **M11 PASS**：删除 strategy/perform_action，oneOf/allOf 条件必填（PROTOCOL:259-307；DESIGN:552）。
- **M12 PASS**：统一 7 个必需工具、删除 pc_session、pc_stop 进 P0（DESIGN:536-546/687；PROTOCOL:12/96-106；ARCHITECTURE:484；contracts 恰 7 tools）。
- **M13 PASS**：OK:/ERROR 前缀、provider modality 检查、内置 MCP 备选路径风险说明（DESIGN:94/485-488；ARCHITECTURE:763-766；PROTOCOL §10:539-540）。

## Minor

- **m1 部分**：keys/session_id/state_ttl/is_error 已统一（PROTOCOL:208/280/131/75-88）；但 DESIGN:419/420/429/430/615 仍有旧 _conf_schema 注释、"观察缓存"、require_confirm、policy"危险动作确认"残留；DESIGN §5.8 camelCase 与 wire snake_case 未声明映射。
- **m2 PASS**：protocol_version=1、请求不带 schema_version（PROTOCOL:4/35/522；无 `pcctrl/1` 残留）。
- **m3 PASS**：observe 无 mode；diff 移出 v1（PROTOCOL:197-211；contracts pc_observe 无 mode）。
- **m4 部分**：预算已改自定值（DESIGN:324）；图片顺序三处冲突：PROTOCOL §2.1:51/57 与 ARCHITECTURE:488 说"放最前"，PROTOCOL §3.6:213-218 与 DESIGN:556 把 image 放最后。
- **m5 部分**：ARCHITECTURE:623/645/648 已改；残留 DESIGN:429 "观察缓存"，且 L3 导入白名单未覆盖 run_context/astr_agent_context。
- **m6 部分**：DESIGN §5.9 已改为引用 PROTOCOL §8，并补 retry_after_ms/UNSUPPORTED_ACTION；但 contracts:573 有 `INTERNAL`，PROTOCOL §8 表（:497-516）无 `INTERNAL` → 两个"单一事实源"漂移。
- **m7 部分**：PROTOCOL:323-331 模型面 receipt 已无 method；ARCHITECTURE:32 仍写"只进审计与 receipt 元数据"，改为"审计/诊断"即可。
- **m8 PASS**：插件文本预算对齐 ≈7000 tokens、区分两类 handler 超时（DESIGN:484/517）。
- **m9 部分**：同 M9 残留（DESIGN:145/420）。

## 结论与开工前小修

**可进入实现**：3 个 blocker 均已实质解决，13 个 major 中 11 个 PASS、2 个仅剩单行文字问题，9 个 minor 中 4 个 PASS。开工前建议 30 分钟内完成 4 项小修（不影响架构）：

1. ARCHITECTURE:25 坐标目标改为"会话内部 frame + 像素"；
2. 统一 image 顺序（建议 image 最前），同步 PROTOCOL §2.1/§3.6 与 DESIGN §7.3；
3. contracts 与 PROTOCOL 对齐：pc_observe 的 anyOf 二选一必填写进 PROTOCOL §3.6，`INTERNAL` 在 PROTOCOL §8 增补或从 contracts 删除；
4. DESIGN:145 删"服务端一行开关"、:420 改 `mcp>=1.30,<2`、:598 改 day-1、:429/419/430/615 清理旧措辞（另：PROTOCOL 成功判据限定"动作类"）。

## 未验证 / 保留

- 容器 `mcp 1.30.0` 仍为文档自述（未把 `docker exec ... pip list` 原始输出入库）；宿主 .spike venv 的 mcp 2.3.0 证据成立。
- B1 修复必须按 DESIGN:707/ARCHITECTURE:780 做容器内真实加载+调用冒烟（mock 不算）；该验收已写进文档，实现阶段执行即可。
