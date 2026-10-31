# 审计 02 处理与最终裁决记录（Lead，2026-10-31）

> 输入：
> - `docs/reviews/design-review-02.md`（第二轮独立审计：0 blocker / 9 major / 10 minor）
> - `docs/reviews/workflow-trace-01.md`（5 个端到端场景走查 + 模型可见性缺口）
> - `docs/GUI.md`（托盘 + 设置 GUI 讨论稿 v0.1）
>
> 结论：**采纳并修订；修订后可进入实现。** 本文是裁决与规格增量的唯一清单，正文文档按本文落地。
> 修订版本：DESIGN **v0.4** / ARCHITECTURE **v0.4** / PROTOCOL **v1.2** / contracts `contract_version = "1.2"` / GUI **v0.2** / README **v0.4**。
> 协议兼容：全部改动都在 v1 冻结前完成，因此 `protocol_version` 保持 **1**；新增可选字段/枚举值不升主版本（PROTOCOL §9）。

## 0. 一句话结论

架构（观察/动作分离、state/frame 内部绑定、回执三态、hit-test 链、全局输入租约）不变；
补齐的是**模型面最小权限**（会话由插件注入、模型不可见 session_id）、**三态止血**（只读/暂停/停止）、
**失败面**（超时链、插件侧错误码、possibly_sent 呈现）、**观察可用性**（全文读取、frame 像素换算、escape 规则）、
**GUI 宿主契约**（单进程托盘 + IExecutorControl 端口 + secrets/control-state 文件）与**可测验收**（夹具/oracle 表）。

---

## 1. 最终裁决（D1–D20）

### D1 会话身份（修 WG-M01 / MX-11 / MX-12）
- **模型永远看不到也传不了** `session_id` / `request_id` / `scope`；这些是**插件注入的内部参数**（contracts 标 `"internal": true`）。
- 插件注入值：`session_id = "wg-" + sha256(umo)[:16]`（如 `wg-8f21cd04`）；executor 解析顺序：内部参数 → MCP 连接身份 → `"default"`。
- `go_launch` / `go_stop` 同样接受内部注入的 `session_id`；模型面 schema 只有 `reason`（stop）/ `app|app_id`（launch）。
- `go_health.os.session_id` 改名 **`os.windows_session_id`**；`controller` 不下发会话 id：
  `"controller": { "held": true, "mine": false, "expires_in_ms": 43000 }`（无租约时 `null` 或 `held:false`）。
- 业务会话统一叫 `session_id`（wire），文档中不再出现 `astrbot-<sha16>` 示例。

### D2 三态止血 + 恢复权限（修 WG-M03 / C2 / C3 / S5-4）
| 态 | 触发 | 动作工具 | 观察 | 恢复 | 持久化 |
|---|---|---|---|---|---|
| **只读** `readonly` | 配置（执行器/插件任一） | 拒绝 `READONLY_MODE` | 允许 | 改配置 | 配置 |
| **暂停** `paused` | 本机 GUI/托盘「暂停」（`reason=user_button`）或启动恢复 | 拒绝 **`PAUSED`**（新增码） | 允许 | 本机 GUI/托盘一键恢复 | 不持久化（重启回运行） |
| **停止** `stopped` | 本机热键/托盘「紧急停止」/管理员命令；模型 `go_stop` 只停**本会话** | 全局停止 → 拒绝 `SESSION_STOPPED`；会话停止 → 只该会话 `SESSION_STOPPED` | 允许 | **仅本机** GUI/托盘恢复（二次确认） | 全局停止持久化到 `%LOCALAPPDATA%\WhaleGo\control-state.json` |
- **恢复只允许在本机**：删除/改写 `/whalego resume` 为“提示到 Windows 托盘点恢复”的只读命令（不远程解除止血；模型与容器都不能解除）。
- 暂停/停止都**立即释放 controller lease、取消排队动作**；在途派发不撤销（走 `possibly_sent`）。
- `go_stop`（模型）默认且只能停本会话；`scope=all` 仅管理员命令/本机 GUI 使用（内部参数）。

### D3 `go_health` 新增字段（修 S5-4 / S4-3 / MX-9）
在 `whalego-health-v1` 顶层新增（可选、additive）：
```jsonc
"paused": false, "pause_reason": null, "paused_since_ms": null,
"stopped": false, "session_stopped": false, "accepting_actions": true
```
- `accepting_actions = !paused && !stopped && !session_stopped`（只读是另一维度，不并入；`ok` 仍表示服务健康）。
- `capabilities.launch_allowlist: ["notepad","calc",...]`；`capabilities.observation` **只在 screenshot 启用时**含 `screenshot_printwindow`。
- `/healthz`（免认证，最小面）返回 `{ok, version, protocol_version, uptime_s, paused, stopped, accepting_actions}`；
  配置 `listen.healthzRequireToken`（默认 false）可要求 token。

### D4 `go_launch` 收窄 + 写动作语义（修 WG-M04 / WG-m10）
- 模型面参数：`app`（allowlist 名称，**唯一启动方式**）或 `app_id`（激活已有应用主窗口），二选一；**不接受 PATH 名/相对路径/arguments**（P2 再扩）。
- 配置新增 `launch.allowlist: [{ "name": "notepad", "path": "C:\\Windows\\System32\\notepad.exe" }]`（默认 notepad/calc/mspaint/explorer；GUI 可编辑）。
- `go_launch` 属于**写动作**：审计 fail-closed（返回前落盘）；成功判据仍为 `isError=false` 且首行 `OK:`（无 dispatch_status）。
- `LAUNCH_TIMEOUT` 改 **`retry=never`**，文案固定“进程可能已启动，禁止重试，请用 go_apps 确认”；成功尽量回 `pid`。
- P0 交付（与 DESIGN/README 一致），PROTOCOL 去掉“P1”标注。

### D5 读回/verification 契约（修 WG-M02）
PROTOCOL §3.7 增加 verification 闭集与每 op 规则：
`status ∈ {verified, mismatched, unverified}`；
`set_value`（元素）读回 == 请求值 → verified，否则 mismatched；
`type`（元素）读回包含请求文本 → verified，否则 mismatched；`click/key/scroll` → unverified。
**mismatched 只允许 reobserve，不得自动重放；且不改变 `dispatch_status=accepted`。** DESIGN §5.5 删除“元素动作不读回”表述，ARCH §3.12 改为引用 PROTOCOL。

### D6 `type`/`key` 目标（修 WG-M09 / S2-2）
- 所有 op（click/set_value/type/key/scroll）**必须**带 `target`；`type`/`key` 只接受 **element target**。
- 删除 v1 的“无 target 前台 Unicode 输入”路径；若元素路径需要前台但无法安全激活 → `FOREGROUND_REQUIRED`（零发送）。
- 永远不向“当前用户焦点窗口”盲发按键；目标窗口不是会话当前窗口时也必须显式 element target。

### D7 观察读全文（修 S1-1 / P-1）
- `go_observe` 新增 `max_value_chars`（integer，1..4000，默认 48）：控制 `elements[].value` 的单值上限；文本树仍固定截 48。
- 元素新增 `value_truncated: boolean`；总预算仍受 `limits.maxObserveChars` 约束（默认 20000），超出时截断并给 `tree_budget_exceeded` advisory。
- OK 摘要行固定含 `elements=… sparse=yes|no`；tool description 写明“读长文用 max_value_chars=4000；index 只在本次 state_id 内有效”。
- 验收：500 字记事本 → `max_value_chars=1000` 能读回第 100 字之后内容。

### D8 frame/坐标像素换算（修 WG-M08 / S3-1/S3-3）
- 截图**只截客户区**（client area）；`frame` 新增：`image_width, image_height, client_width, client_height, scale_x, scale_y, capture_region:"client"`。
- **模型坐标 = 图片像素**（左上原点）；内部换算：client_x = image_x * client_width/image_width（四舍五入）。
- 无截图 observe **不清理**同窗口既有 frame（仍校验 TTL/几何/DPI/actionable）；切换窗口/任何 act 进入派发 → frame 失效。
- 非“已验证宿主”的 frame → `actionable=false` + `capture_degraded`；Chromium/WPF 走**元素路径**，坐标路径按设计降级（P2 上 WGC）。

### D9 隐私：自身窗口 + 敏感窗口（修 M5 补强 / GUI 相关）
- 执行器**永不枚举/观察/截图自身进程（WhaleGo.*）的窗口**；`go_apps`/`go_windows` 过滤，自检与 GUI 属于白名单例外。
- 敏感窗口命中：`go_observe` 仍返回树（`OK:` + `advisories=["screenshot_suppressed"]`），**不返回图片**；坐标动作 → `FRAME_NOT_ACTIONABLE`。
- `screenshot.enabled=false` 且模型显式请求截图：返回树 + `advisories=["screenshot_disabled"]`，首行 `screenshot=no(disabled)`；不报错。
- screenshot 三开关优先级（**AND**）：执行器 `screenshot.enabled` && (模型 `include_screenshot` || 执行器 `screenshot.defaultInclude`) && 插件 `allow_screenshot`。
- advisory 闭集新增：`uia_unavailable`、`screenshot_disabled`、`screenshot_suppressed`（冻结前加入，不升版本）。

### D10 文本树与模型可见文本（修 MX-2/MX-3/MX-6/MX-7/MX-8/MX-12/MX-14）
- 文本树转义：`"`→`\"`、`\\`→`\\\\`、换行/制表/控制字符→`\n/\t/\uXXXX`；截断按 Unicode 码点（不拆代理对），尾部用 `…`。
- `elements[].bounds` 改名 **`bounds_screen_diag`**（明确诊断用），tool description/OK 行说明“坐标必须是截图图片像素”。
- **模型可见文本语言**：简体中文 + 固定英文前缀（`OK:`/`ERROR[CODE]:`）；错误 `message` 用中文，错误码保持英文。
- 模型可见规则由插件注入：新增插件配置 `inject_usage_prompt`（默认 true），通过 `on_llm_request` 注入 ≤15 行使用说明（动作后必须重观察、possibly_sent 禁止重试、先 go_apps、index/state 只本次有效、保存类限制、截图不得转发）。内容放插件包内 `prompts/usage.md` 并由单测锁定与 PROTOCOL §12 同步。NAMING 的 tool description 字数限制对“使用说明”开例外（P1 实现）。
- 截图图片存在时：隐私提示放 **OK 摘要行最前**（“截图含用户隐私，未经用户明确要求不得转发”），避免与 AstrBot 的“可发图”提示冲突。
- `go_windows` 排序：前台窗口置前；`go_apps` 给出“推荐下一步（window_id）”；标题只用于人读。

### D11 租约生命周期（修 S4-1/S4-4 / P-11/P-12）
- 获取：`go_act` 与“需要激活窗口的 `go_launch`”在**进入派发前**获取；观察/纯激活失败不获取。
- TTL 60s，动作续期；同一调用内最多等待 `policy.controllerWaitMs`（默认 2000，可配 0–10000）。
- 失败 `CONTROLLER_BUSY`：`retry=retry` + `retry_after_ms = min(剩余租约, 5000)` + `controller.mine=false`；**不使 state/frame 失效**。
- 释放：会话关闭/空闲回收、`go_stop`、暂停、执行器退出；`go_health.controller` 暴露 `{held, mine, expires_in_ms}`。
- 文档统一措辞“全局输入租约（跨会话唯一）”，不再写“全局 vs 会话级”混用。

### D12 超时链与插件侧错误（修 S5-1/S5-2/S5-3 / P-14/P-15/P-16）
- 执行器预算：observe 树 2s + 截图 3s；act 5s；launch 等待 ≤10s；**工具整体 ≤ 40s**。
- 插件：`timeout` 默认 **45s**，clamp 到 AstrBot `tool_call_timeout-5s`；必须 > 执行器整体预算 5s 以上（启动自检/告警）。
- 插件侧超时/断线**必须**给模型固定文案 + 语义：
  - 动作类调用超时/断线 → 按 `DISPATCH_UNCERTAIN` 呈现（“可能已发送，禁止重试，先 go_observe”）；
  - 只读类调用超时/断线 → 可重连重试一次，再失败返回 `EXECUTOR_TIMEOUT`。
- 插件侧错误码（**执行器契约之外**，PROTOCOL §8.1 单列）：`EXECUTOR_UNREACHABLE`、`EXECUTOR_TIMEOUT`、`AUTH_FAILED`、`PROTOCOL_MISMATCH`、`CONNECTION_LOST`（`retry` 语义见文档；全部 `isError 风格文本前缀`）。
- 401/协议不匹配：**不重试**，提示检查 token/版本；容器重启/重连后：旧 state 一律 `STALE_STATE`（会话 epoch 递增，插件需重新 observe）。

### D13 token 与 secrets（修 C1 / WG-M05）
- 生成：32 字节 CSPRNG → base64url，前缀 `wg_`；认证用 SHA-256 常数时间比较。
- 存储：`config.json` 只存 `auth.tokens[].{name, sha256, created_at}`；明文 token 存 `%LOCALAPPDATA%\WhaleGo\secrets.json`（仅当前用户 ACL；DPAPI 可选 P2）。
- GUI：掩码显示（首 5 后 4）、一键复制插件配置（含明文 token，剪贴板 60s 条件清空）、轮换后旧 token 立即 401。
- token 不进入工具参数/返回值/日志/审计/托盘 tooltip；`go_health` 不含 token/指纹。
- GUI/配置风险：0.0.0.0 横幅 + 防火墙指引；`127.0.0.1` 选项必须提示“容器连不上”。

### D14 配置来源与键位（修 C4 / WG-m03 / C7）
- 优先级：`appsettings.json` < `%APPDATA%\WhaleGo\config.json` < 环境变量 `WHALEGO_*` < 命令行；GUI 显示来源徽标，环境变量覆盖项只读。
- 新增配置组（DESIGN §5.8 同步）：`hotkeys.{enabled, emergencyStop, openSettings}`、`ui.notificationsLevel`、`logging.{level,dir}`、`launch.allowlist`、`listen.healthzRequireToken`。
  （原列的 `startup.autoStart` 已被 D21/Q5 删除：不做开机自启，手动启动。）
- 单位统一：`maxImageBytes = 200000`（约 200 KB）；wire snake_case / 本地 camelCase，映射表进 ARCHITECTURE §3.8。
- 删除 `screenshot.defaultInclude`？**保留**，语义 = 模型省略 `include_screenshot` 时执行器是否自动附图（默认 false，仍受三开关 AND）。
- 插件 `_conf_schema` 注释里的 limits 删除（执行器侧约束，插件不重复）。

### D15 GUI 宿主形态与项目结构（修 WG-M05 / C6 / GUI C1-C7）
- **WinForms + 单进程托盘宿主**：`WhaleGo.Executor.exe`（WinExe，唯一组合根）内嵌 kernel + 托盘 + 设置窗口；GUI 为独立项目 `windows/src/WhaleGo.Gui/`（WinForms 库，net10.0-windows），只引用 Contracts，禁止引用 Platform/AspNetCore。
- 端口 `IExecutorControl`（Contracts 定义）：GetStatus / GetRecentRequests / GetToolCounters / Pause / Resume / Stop / GetConfig / SetConfig / RotateToken / RevealTokenForCopy / RunSelfCheck / ReadAuditTail / OpenDataFolder / SetStartupShortcut / RequestRestart。
- 单实例：`Local\WhaleGo.Executor.SingleInstance` 互斥 + `Local\WhaleGo.Executor.ShowSettings` 事件；`--headless` 共用互斥（冲突退出码 2）。
- 关闭设置窗口 = 隐藏到托盘；唯一退出在托盘菜单（确认框，含租约警告）。
- 自启：启动文件夹快捷方式（GUI 复选框），任务计划“仅登录时”P2；**禁止**“不管用户是否登录”。
- `--headless` / `--self-check --json` / `--config <path>` / `--no-hotkey` / `--diagnostics-dump` 为无头 seam。
- 托盘 5 态（运行·已连接/运行·无连接/已暂停/已停止/异常）；热键默认 `Ctrl+Alt+Shift+F12` **只触发紧急停止、不恢复、不弹确认**；恢复一律本机二次确认。
- GUI 设置 6 页：概览 / 连接与配对 / 执行策略 / 隐私与审计 / 诊断与日志 / 关于与限制。
- DESIGN §5.10、§9.1、§10.1/10.4、§11；ARCHITECTURE §2.1（新增 L7 UI 行）、§3.1（新增 WhaleGo.Gui 矩阵行）、§3.5（新增 ControlService/StatusService/SelfCheckService/ConfigStore/TokenService）、§3.9（启动顺序）、§3.14（seam）、§6（目录）、§7（新增 T7）同步。

### D16 app_id / window 身份（修 WG-m05 / MX-5）
- `win-exe:<sha1(规范化 exe 绝对路径小写)[:12]>`（稳定、跨重启）；`win-aumid:<aumid>`；**删除 `#2/#3` 后缀**。
- 同名同 exe 的多窗口由 `window_id` 区分；`go_observe(app_id)` 多窗口 → `AMBIGUOUS_APP`（提示用 window_id）。
- 示例同步更新（DESIGN §5.4、PROTOCOL §3.4/3.5/3.6、contracts）。

### D17 文档版本与命名残留（修 WG-m01 / R-5）
- DESIGN/ARCH 页眉 v0.4；PROTOCOL v1.2；README 状态 v0.4；合同 `contract_version="1.2"`。
- `PcSession/PcError/PcTools/PcToolException/PcObserveTool` → `GoSession/GoError/GoTools/WhaleGoToolException/GoObserveTool`（或直接用 `WhaleGo` 前缀）。
- 会话示例统一 `wg-…`（删除 `astrbot-…`）。

### D18 命令与权限（修 WG-M03 / WG-m07）
以 NAMING §4.3 为唯一事实源：`status/health/stop/help` = 成员（止血对成员开放）；`readonly on|off` = 管理员；**`resume` 不是远程命令**（改为提示本机恢复；如需命令保留只读说明）。
别名 `/wg`；`/whalego help`；ARCHITECTURE §4.4 删除“统一 permission_type(ADMIN)”。

### D19 验收可测（修 WG-M06 / D-5 / tracer §11）
DESIGN §11 增“验收夹具与 oracle”表：每条验收写 前置/夹具/oracle/预期输出。至少覆盖：
首启配对、暂停可见（PAUSED + health.paused）、热键 3 秒停止、关闭窗口仍在托盘、读 500 字正文（D7）、
保存两支（有保存控件/无保存控件 → 明确失败）、焦点/两记事本不串窗、坐标换算 100/125/200% DPI、
非客户区拒绝、双会话租约、插件超时按 DISPATCH_UNCERTAIN、审计只读目录负例、锁屏 SESSION_LOCKED。
夹具交付进 T6：`WhaleGo.Fixtures.Elevated`（高完整性）、焦点 watcher、双 MCP 连接脚本、审计目录只读注入、provider 无 image 桩。
高完整性夹具需“以管理员运行”说明（不进 CI 主链路，标 P1 手工）。

### D20 其它事实修正
- `mcp_protocol_version` 示例改“运行时协商值（如 2025-06-18）”。
- SSE：“未验证，P3+”；宿主 mcp 2.3.0 证据链：注明 `.spike` venv 已删，实现阶段复验并在 `docs/evidence/` 补记录。
- `signature` 闭集：`nonblank_ok|blank|black|failed`；`resolution` 闭集：`exact|fallback|unavailable`；新增值需升协议。
- PROTOCOL §5.1/§5.2 措辞统一“会话只保留最近一个窗口的 state；切换窗口即丢弃旧窗口 state”。
- 插件 `on_llm_request` 注入说明：R-3（P1 实现，文档先写规则）。

---

## 2. 审计条目处置表（WG-M01..m10）

| ID | 处置 | 落点 |
|---|---|---|
| WG-M01 会话身份/作用域 | 采纳 → D1、D2、D11 | PROTOCOL §3.3/§3.6/§3.7/§3.8/§3.9/§3.2；contracts internal 标记；DESIGN §4.3/§5.6；ARCHITECTURE §3.11 |
| WG-M02 verification | 采纳 → D5 | PROTOCOL §3.7；DESIGN §5.5/§11；ARCHITECTURE §3.12 |
| WG-M03 命令权限 | 采纳 → D18 | DESIGN §6.7；ARCHITECTURE §4.4；NAMING §4.3 |
| WG-M04 launch | 采纳 → D4、D19 | PROTOCOL §2.1/§3.1/§3.8/§8；DESIGN §5.5/§10.3；contracts |
| WG-M05 GUI 契约 | 采纳 → D15 | 新增 docs/GUI.md（v0.2 定稿）+ DESIGN §5.10/§9；ARCHITECTURE 层/项目/服务/任务；PROTOCOL 不新增路由 |
| WG-M06 验收不可测 | 采纳 → D19 | DESIGN §11 + ARCHITECTURE §7/T6 |
| WG-M07 单一事实源漂移 | 采纳 → 契约对齐 | contracts 全量同步 PROTOCOL；DESIGN §7/ARCHITECTURE §5 摘要改为“动作类/查询类”并指向 PROTOCOL |
| WG-M08 frame 生命周期 | 采纳 → D8 | PROTOCOL §3.6/§5.2 |
| WG-M09 type/key 焦点 | 采纳 → D6 | PROTOCOL §3.7 |
| WG-m01 版本/品牌/Pc* | 采纳 → D17 | 四文档页眉、Pc* 改名、session 前缀 |
| WG-m02 healthz | 采纳 → D3 | PROTOCOL §1；DESIGN §5.7/§9.1；ARCHITECTURE §3.7 |
| WG-m03 截图开关/单位/键名 | 采纳 → D9、D14 | DESIGN §5.8/§6.6；PROTOCOL §11；ARCHITECTURE §3.8 映射表 |
| WG-m04 contract privacy/freshness | 采纳 | contracts 补字段（D8/D9） |
| WG-m05 app_id #N | 采纳 → D16 | PROTOCOL §3.4/§3.5/§3.6；contracts |
| WG-m06 事实精度 | 采纳 → D20 | DESIGN 附录 A；ARCHITECTURE §8；docs/evidence 待补 |
| WG-m07 命令别名/help | 采纳 → D18 | DESIGN §6.7；ARCHITECTURE §4.4；NAMING |
| WG-m08 多窗口 state 措辞 | 采纳 → D20 | PROTOCOL §5.1/§5.2 |
| WG-m09 signature/resolution 闭集 | 采纳 → D20 | PROTOCOL §3.6 |
| WG-m10 launch P0/P1 | 采纳 → D4 | PROTOCOL §3.1/§3.8；DESIGN §10.3；README |

## 3. 场景走查缺口处置（P0/P1 前）

| 缺口 | 处置 | 落点 |
|---|---|---|
| S1-1 读正文只 48 字 | D7 | PROTOCOL §3.6；DESIGN §5.4；验收 D19 |
| S1-3 UIA 不可用无指引 | advisory `uia_unavailable` + 截图仍返回（D9） | PROTOCOL §3.6/§4 |
| S2-2 type/key 焦点 | D6 | PROTOCOL §3.7 |
| S2-3 op 语义表 | D5/D6 + PROTOCOL §3.7 语义表 | PROTOCOL §3.7 |
| S2-4 菜单/弹层 | v1 明确不支持；`go_windows` 增加 `owner_window_id/parent_window_id/kind/is_modal`（字段增强，仍单窗观察） | PROTOCOL §3.5；README 限制 |
| S2-5 等待新窗口 | 明确 v1 无等待原语：`settle_ms` 仅树稳定；保存 Dialog 用轮次 go_windows 兜底；README/说明写死 | PROTOCOL §3.6/§12 |
| S2-6 type 长度 | `type.text` maxLength 1500（与 45s 预算对齐）+ 超长 `INVALID_ARGUMENT` | PROTOCOL §3.7 |
| S3-1/S3-3 像素换算 | D8 | PROTOCOL §3.6/§6 |
| S3-2 未验证宿主坐标 | D8 + P1 验收范围写死（Chromium 元素路径） | PROTOCOL §3.6；DESIGN §11 |
| S4 租约细节 | D11 | PROTOCOL §5.3/§3.2 |
| S5-1 超时链 | D12 | PROTOCOL §7；ARCHITECTURE §3.11；DESIGN §6.3 |
| S5-2 插件侧错误码 | D12 | PROTOCOL §8.1；DESIGN §6.5；ARCHITECTURE §4.5 |
| S5-5 epoch/重连 | D12 | PROTOCOL §3.3；ARCHITECTURE §3.11 |
| S5-6 锁屏 | `SESSION_LOCKED` 错误码（`ok=false` 仍可观察树）；health 暴露 `locked` | PROTOCOL §8/§3.2 |
| S5-7 结果截断 | 插件按 token 预算裁剪；JSON 不可截断（超预算 → 结构化错误/降级） | DESIGN §6.5；PROTOCOL §4 |
| MX-1/2/3/4/5/6/7/8/9/10/11/12/14 | D1/D6/D7/D10/D11/D12/D13 | PROTOCOL §3/§4/§7；插件 prompt（D10） |
| MX-13 模型看不到规则 | D10（插件注入 usage prompt；P1 实现） | DESIGN §6.5；NAMING 例外 |

## 4. 明确不做（v1 限制，写进 README 与 tool description）

组合键/右键/双击/拖拽/粘贴；菜单弹层可靠编排；保存对话框全自动（仅元素点击可及则尽力，否则明确失败）；
长文档全量读取（受 `max_value_chars` 与预算约束）；Chromium/GPU 未验证宿主坐标点击；多屏负坐标坐标点击；
锁屏/安全桌面操作；文件系统读写；远程恢复止血；深色模式/多语言/自动更新/安装器/代码签名；多执行器/TLS。

## 5. 版本与同步规则

1. `PROTOCOL.md` 是工具面唯一事实源；`contracts/whalego.tools.v1.json` 与其做 CI diff（`scripts/check-contract.ps1`）。
2. 模型面 schema **不得**出现 `session_id`/`request_id`/`scope`；contract 中以 `"internal": true` 标注。
3. `protocol_version` 保持 1；新增可选字段/枚举值不升版本；删除字段/改语义/改工具名才升 2。
4. 每次修订同步更新四份文档页眉版本、README 状态行、`contract_version`。

## 6. D21 用户拍板（2026-10-31 晚，GUI 六问 + 新增要求）

| 问题 | 用户选择 | 落地影响 |
|---|---|---|
| Q1 设置首屏 | **A 概览**（状态 + 两个止血按钮 + 配对入口） | GUI 已按此设计，无需改 |
| Q2 点 × | **A 隐藏到托盘** | 维持 |
| Q3 止血档位 | **A 两档**（暂停一键恢复 / 紧急停止二次确认） | 维持 |
| Q4 截图默认 | **B 默认开** | 执行器 `screenshot.enabled=true`、插件 `allow_screenshot=true`；仍保留敏感窗口抑制/自身窗口排除；模型按需 `include_screenshot=true`（默认不自动附图，避免每轮传图；若要自动附图再开 `defaultInclude`） |
| Q5 开机自启 | **不做**（手动启动程序） | 删除 `startup.*`、启动文件夹快捷方式、任务计划说明、`SetStartupShortcut` 端口、相关测试与文档；GUI/README 明确“手动启动” |
| Q6 UI 外观 | **要好看 + 深色模式**，视觉参考 deepseek.com；**不做 WebUI** | 设置窗口技术栈由 WinForms 改为 **WPF**（浅/深主题、圆角卡片、清爽排版）；托盘图标用 WinForms `NotifyIcon`（同进程 `UseWPF`+`UseWindowsForms`，零第三方依赖）；深色跟随系统 + 手动切换、热切换不重启；**不嵌 WebView、不做网页版** |

**落点**：DESIGN §5.8/§5.10/§8/§9/§10/§11/§12.1；ARCHITECTURE §2.1/§3.1/§3.3/§3.5/§3.9/§3.14/§6/§7/§8；NAMING §0/§4.4；README；
`docs/GUI.md` v0.3；PROTOCOL §0/§11/§12；contracts `privacy.screenshot_default: true`。

> 明确非目标：不做 WebUI/WebView；不做自动更新/安装器/代码签名；不做开机自启。

### D22 对 GUI 待裁决 4 项的裁决（2026-10-31）

1. **采纳**：Contracts 新增两个窄端口 `IWindowTheme`（DWM 暗色标题栏）与 `IHotkeyRegistrar`（RegisterHotKey）——GUI 不写 P/Invoke；落点 DESIGN §5.3、ARCHITECTURE §3.3、GUI §4.6。
2. **采纳**：`ui.theme ∈ {system,light,dark}`，默认 `system`，热切换；环境变量覆盖名 `WHALEGO_UI__THEME`。
3. **采纳**：`startup.*` 全量删除（配置组/环境变量/`SetStartupShortcut` 端口/文档/脚本/测试）；启动方式 = 手动启动。
4. **采纳**：DESIGN/ARCHITECTURE/README → **v0.5**，GUI → **v0.3**；contracts `contract_version` 保持 1.2（无 wire 变化）。
