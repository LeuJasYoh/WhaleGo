# 实现验收结果（DESIGN §11 oracle 表 + 真实执行记录）

> 采集时间：2026-10-31 19:25–19:36（Asia/Shanghai）｜执行者：T6（docs/scripts/fixtures/E2E）；D-1 修复后已用新二进制复跑契约门禁 + E2E
> 基线文档：DESIGN/ARCHITECTURE/README **v0.5**、PROTOCOL **v1.2**、contracts `contract_version="1.2"`、GUI **v0.3**
> 标注约定：**PASS** = 本机真实执行并通过（复现命令见 §7）；**PARTIAL** = 部分子项已自动验证、其余为手工/未覆盖；**MANUAL** = 需人工或桌面状态，未在 CI 自动验证。
> 原则：只记录真实跑过的结果；未能自动化的项如实列在 §5，不伪装通过。

---

## 0. 结论摘要（本机真实运行）

| 验收块 | 结果 | 关键证据 |
|---|---|---|
| 构建（solution） | **PASS** | `scripts/build.ps1` → `BUILD OK (Release, 3.7 s)`；0 warnings / 0 errors |
| .NET 单元/集成测试（4 个项目） | **PASS** | 82（Contracts）+ 111（Platform）+ 62（Executor）+ 125（Gui）= **380 passed / 0 failed / 0 skipped** |
| AstrBot 插件 pytest | **PASS** | **150 passed**（T5 final；uv ephemeral env，mcp==1.30.0；命令见 §7.0） |
| 插件 ruff | **PASS** | `All checks passed!` + `24 files already formatted` |
| 契约门禁（tools/list vs contracts） | **PASS** | 8 项断言全 PASS（§2）；`CONTRACT CHECK PASSED` |
| 端到端 E2E（真实执行器 + 真实 mcp 1.30 客户端） | **PASS** | **86 passed / 0 failed / 0 skipped**（normal 72 + readonly 14）+ **审计断言 9/9 PASS**，`E2E PASSED` |
| 执行器自检 `--self-check --json` | **PASS** | exit 0，`ok=true`，12 项（10 ok + 2 warn：hotkey/theme，headless 预期），输出 ASCII-only、不含 token |
| 容器 → 宿主链路 | **PASS** | `docker exec AstrBot curl http://host.docker.internal:<port>/healthz` → **200**（无 token/带 Bearer 均 200）；容器内 `POST /mcp` + Bearer 到达 MCP 端点（空 body → 400 解析错误，非 401） |
| 审计/日志隐私 | **PASS** | 输入明文/token 在 audit + executor 日志中 **0 命中**；`type/set_value` 审计已写入 `text_length` + `text_sha256_prefix`（D-1 已修复），E2E 内置 **9 项审计断言全 PASS**（见 §3.3/§6） |
| GUI（WPF 设置窗口/托盘/主题/配对） | **MANUAL** | 由 T7 `Gui.Tests`（125 passed）+ 手工清单覆盖；本机未做 GUI 自动化（`scripts/gui-theme-shots.ps1` 为文档化 stub） |
| 部署文档可照做 | **PARTIAL** | DEPLOY/DEV 已交付；AstrBot WebUI 重载与真实模型调用属于用户操作（见 §5） |

被测二进制（final + D-1 修复后重建，T4 2026-10-31 19:28:44 构建；本轮门禁/E2E 均在此二进制上运行）：

```text
path        : windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe
exe sha256  : 0fc09867d2ca01d9fcf22474fdf492336af3dd1f489ebca23773064fbdeb6333   (原生 apphost stub，跨构建不变)
dll sha256  : 6b2de869fa24c4384d226d397eb3b57612f4b6fc5cb5095735d44e6be9dcbeea   (D-1 修复后；旧值 faf81255…)
dll mtime   : 2026-10-31T19:28:44+08:00
exe mtime   : 2026-10-31T19:28:44+08:00
SDK/runtime : .NET SDK 10.0.401 / WindowsDesktop 10.0.12；Windows 11 交互会话 session=1
fixtures    : windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe
```

> 证据以 **DLL sha256** 为准：apphost `WhaleGo.Executor.exe` 是原生启动 stub，重建不改其字节；逻辑代码在 `WhaleGo.Executor.dll`。

---

## 1. 环境事实

- 容器：`AstrBot` Up（`m.daocloud.io/docker.io/soulter/astrbot:latest`），bind mount `C:\Users\23500\data → /AstrBot/data`；容器内 `astrbot 4.29.3`、`mcp 1.30.0`。
- 宿主：Windows 11，当前显示 DPI **120（125%）**；交互会话 `session=1`，未锁屏；UIA 可用；进程 DPI 感知 `per_monitor_v2`。
- 工具链：dotnet SDK **10.0.401**；`uv 0.13.6`；`mcp 1.30.0`（uv 管理的 Python 3.14.3；容器内 Python 3.12 + mcp 1.30.0）。
- 构建包：`ModelContextProtocol.AspNetCore 2.3.0`、`FlaUI.UIA3 5.0.0`（NU1701 已 NoWarn）。
- C# MCP ↔ Python mcp 1.30 往返（text + PNG image + custom header）证据见 [container-env.md](container-env.md)。

---

## 2. 契约门禁（`scripts/check-contract.ps1 -CheckHealth`）—— PASS

被测对象：真实 headless 执行器（临时 config/data/端口，`WHALEGO_DATA_DIR` 隔离），用**真实 mcp==1.30.0** 客户端拉 `tools/list` 与 `contracts/whalego.tools.v1.json` 归一化 diff。

命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
```

实际输出（节选，完整日志 `.tmp/tg-docs-scripts/final-contract-gate.log`）：

```text
  /healthz  : OK
  /mcp no token : 401 (expected)
[PASS] contract_version == 1.2
[PASS] protocol_version == 1
[PASS] tool names exactly match the 7 contract tools
[PASS] all tool descriptions match the contract
[PASS] all inputSchemas match the contract (x-internal stripped, order-insensitive)
[PASS] no internal params (session_id/request_id/scope) in model-facing schemas
[PASS] go_health.protocol_version == 1
[PASS] go_health.accepting_actions present
RESULT: PASS
CONTRACT CHECK PASSED
```

- 7 个工具：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`。
- 归一化规则：剥离 contracts 中 `x-internal: true` 的参数（当前为 `go_stop.scope`）；键序不敏感；结构完全相等。
- 负例：`POST /mcp` 无 token → HTTP 401（`WWW-Authenticate: Bearer`）。

---

## 3. 端到端 E2E（`scripts/e2e.ps1`）—— PASS 86/0/0

命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1
```

编排（两个**顺序**阶段：交互会话内 headless 执行器共享单实例互斥，不能并发；T4 的 per-data-root 互斥已实现，脚本仍保持顺序以保证确定性）：

1. **normal 阶段**：临时 config/data + 空闲端口起 headless 执行器（`--headless --no-hotkey --config ...`，`WHALEGO_DATA_DIR` 隔离；config 预置 token sha256 + `secrets.json` 明文，执行器不轮换）→ 真实 mcp 1.30 客户端（Bearer + 隐藏内部参数 `session_id="wg-8f21cd04f00dbeef"`，16 hex）跑全部正常用例。
2. 停 normal → 等进程退出释放互斥 → **readonly 阶段**：`policy.readonly=true` 的执行器（另一临时数据根/端口）跑只读负例。

真实结果：

```text
RESULT: 72 passed, 0 failed, 0 skipped     (normal phase)
RESULT: 14 passed, 0 failed, 0 skipped     (readonly phase)
TOTAL: 86 passed, 0 failed, 0 skipped
E2E PASSED
```

### 3.1 normal 阶段覆盖的用例（72）

| 组 | 用例（断言） |
|---|---|
| HTTP | `/healthz` 200 免认证；`ok=true`；`protocol_version=1`；`accepting_actions` 为 bool；含 `version/uptime_s/paused/stopped`；`POST /mcp` 无 token → 401；错误 token → 401 |
| tools/list | 恰好 7 个契约工具名 |
| go_health | 首行 `OK:`；`whalego-health-v1`；`protocol_version=1`；`accepting_actions=true`；`paused=false`；`stopped=false`；`capabilities.actions` 非空；`observation` 含 `uia_tree`；`launch_allowlist` 含 `fixture` |
| go_apps / go_windows | 首行 `OK:`；schema `whalego-apps-v1` / `whalego-windows-v1`；列表结构正确 |
| go_launch（allowlist） | `OK:`；`whalego-launch-v1`；`launched=true`；返回 `pid` + `window_id`；窗口随后出现在 `go_windows`（标题 `WhaleGo Fixture`） |
| go_observe（截图） | `OK:`；**ImageContent 1 张，mime=image/png，47 KB，PNG 魔数合法**；`whalego-observe-v1`；`state_id=st-XXXXXXXX`；`frame.actionable=true`；frame 含 image/client 像素映射；树含夹具控件（txtInput/btnIncrement/btnClickTarget/lblCounter） |
| go_observe（无截图） | 首行 `OK:`；**无图片**（默认不自动附图）；仍返回新 `state_id` |
| go_act set_value | 成功=isError false + 首行 `OK:`；`action_receipt.dispatch_status="accepted"`；`state_available=false`（state 立即失效）；`verification.status="verified"` |
| STALE_STATE | 用旧 `state_id` 再动作 → `ERROR[STALE_STATE]`，`retry=reobserve` |
| 读回 | 重新 observe：`txtInput` 值包含 `e2e-set-value` |
| go_act type | 成功 accepted；重新 observe 值包含 `e2e-type-42` |
| D7 读全文 | `set_value` 写入 **500 字符**；`max_value_chars=1000` observe 读回 `len=500` 且包含第 100 字符后的标记 `read-after-100` |
| 元素 click（Invoke） | `btnIncrement` click accepted；`lblCounter 0→1`；`btnClickTarget` click accepted；`lblClickCount target=0→1` |
| **坐标 click（frame 映射）** | 带截图 observe 的 frame → 用元素 `bounds_screen_diag` 中心换算为**图片像素**（`[left,top,right,bottom]` DWM 边框推导客户端原点）→ `go_act op=click target={kind:coordinate}` accepted；`pnlClick panel 0→1`（125% DPI 下真实命中） |
| go_stop（模型调用） | `OK:`；`whalego-stop-v1`；`stopped=true scope=session`；停止后 `go_observe` **仍可用**；`go_act` → `ERROR[SESSION_STOPPED]`（`retry=never`）；`go_health.session_stopped=true`、`accepting_actions=false` |
| CONTROLLER_BUSY（双会话） | 会话 A 动作成功持有租约；会话 B 立即动作 → `ERROR[CONTROLLER_BUSY]`，`retry=retry`，`retry_after_ms=5000`（未使 state 失效） |

### 3.2 readonly 阶段覆盖的用例（14）

| 组 | 用例 |
|---|---|
| HTTP | `/healthz` 200 / payload / protocol / 字段 / 无 token 401 / 错误 token 401（只读执行器同样） |
| 只读语义 | `go_health` 可达；`go_act` → `ERROR[READONLY_MODE]`、`retry=never`；`go_launch` → `ERROR[READONLY_MODE]`（写动作同样被拒） |

> 双阶段设计原因：Windows 交互会话内 headless 执行器共享单实例互斥（默认数据根）；脚本严格顺序执行并等待退出，避免"第二个 executor 退出码 2"。T4 已增加 per-data-root 互斥名（见 DEV.md §3.3），测试数据根隔离后可并发，但 E2E 保持顺序以确保结果确定。

---

### 3.3 审计断言（`scripts/e2e.ps1` 内置，D-1 回归，9 项）

normal 阶段结束后，E2E 直接读取临时数据根的 `logs/audit-YYYYMMDD.jsonl` 并断言（不依赖执行器自述）：

```text
PASS  audit: audit-*.jsonl exists  [audit-20261031.jsonl]
PASS  audit: every line is valid JSON  [lines=24 parseErrors=0]
PASS  audit: action records present  [actions=8]
PASS  audit: no input plaintext in raw audit file  [plaintextsChecked=3]
PASS  audit: no token / Authorization material
PASS  audit: every type/set_value action has text_length>0 + 8-hex text_sha256_prefix  [records=3]
PASS  audit: type record length=11 sha_prefix=9c06f98a
PASS  audit: set_value record length=13 sha_prefix=b130461d
PASS  audit: set_value record length=500 sha_prefix=6d29dfe2
AUDIT ASSERTIONS: 9 passed, 0 failed
```

即：`type("e2e-type-42")` → length=11、sha256[:8]=`9c06f98a`；`set_value("e2e-set-value")` → 13/`b130461d`；
D7 的 500 字 set_value → 500/`6d29dfe2`；审计原文不含这些明文与 token。

## 4. DESIGN §11「验收夹具与 oracle」逐条

| # | 验收点 | 前置/夹具 | oracle / 预期 | 实际 | 结论 |
|---|---|---|---|---|---|
| 1 | 首启配对 | 空 %APPDATA% + 新容器 | 5 步完成，`/whalego health` OK | 本机未走 GUI 向导（GUI MANUAL，见 §5）；headless 首启生成 config/secrets 的路径由 E2E 临时数据根验证（预置 token 不轮换） | **MANUAL** |
| 2 | 截图默认可用（Q4） | 夹具 + 默认配置 | include_screenshot=true 返回图片；defaultInclude=false 默认不自动附图 | E2E：true → 1 张 47KB PNG；false → 0 张图片 | **PASS** |
| 3 | 暂停可见 | GUI 点暂停 | `PAUSED` + `health.paused=true/accepting_actions=false` | 模型面无 pause 工具；pause 语义由 Executor 单测覆盖（62 passed），E2E 未注入 GUI 暂停 | **MANUAL**（单测 PARTIAL） |
| 4 | 热键 3 秒停止 | 动作中按 Ctrl+Alt+Shift+F12 | ≤3s `stopped`，后续 `SESSION_STOPPED` | headless 不注册热键；需桌面手工 | **MANUAL** |
| 5 | 关闭窗口仍在托盘 | 关设置窗 | 进程/托盘在、MCP 仍可调 | GUI 手工清单（T7） | **MANUAL** |
| 6 | 读 500 字正文 | 500 字 + max_value_chars=1000 | 能读回第 100 字之后内容 | E2E D7：写 500 字（夹具 txtInput），读回 len=500 含 `read-after-100` | **PASS** |
| 7 | 保存两支 | 有/无保存控件夹具 | 有控件可点则成功；无控件明确失败 | 未自动化（notepad 保存对话框场景） | **MANUAL** |
| 8 | 焦点/两记事本不串窗 | 两窗口 + 切焦点 | 只改目标窗口 | 未自动化 | **MANUAL** |
| 9 | 坐标换算 100/125/200% DPI | 同夹具三档 DPI | 误差 ≤1px；非客户区拒绝 | 本机 **125%** 下坐标点击 pnlClick 命中（panel 0→1）；100%/200% 未跑（改系统 DPI 需人工）；非客户区拒绝未自动化 | **PARTIAL** |
| 10 | 双会话租约 | 两个 MCP 会话并发动作 | 非持有者 `CONTROLLER_BUSY` + `retry_after_ms` | E2E：A 持租约，B → CONTROLLER_BUSY/retry/5000ms | **PASS** |
| 11 | 插件超时 DISPATCH_UNCERTAIN | 插件 timeout 调小 + 动作夹具 | 固定文案"可能已发送，禁止重试" | 插件 convert 层由 pytest 150 覆盖；真实超时注入未跑 | **PARTIAL** |
| 12 | 审计只读目录负例 | 审计目录只读 | `AUDIT_UNAVAILABLE/not_sent` | 未注入只读目录；审计写入与 fail-closed 由 Executor 单测覆盖 | **MANUAL**（单测 PARTIAL） |
| 13 | 锁屏 SESSION_LOCKED | 锁屏 | 动作 `SESSION_LOCKED`，树仍可读 | 未锁屏验证 | **MANUAL** |
| 14 | 高完整性夹具 | `WhaleGo.Fixtures.Elevated`（管理员，P1 手工） | `UIPI_BLOCKED/not_sent` 零发送 | 夹具+步骤已交付（README），本轮未执行 UAC 手工用例 | **MANUAL** |
| 15 | 非客户区坐标拒绝 | 标题栏/边框坐标 | `UNSUPPORTED_ACTION` / not_sent | 未自动化 | **MANUAL**（单测 PARTIAL） |

### 4.1 DESIGN §11 验收标准 1–8

| # | 验收标准 | 实际 | 结论 |
|---|---|---|---|
| 1 | 插件在 AstrBot 4.29.3 容器内加载、7 工具可见可调用 | 插件 pytest 150 passed；容器内 mcp 1.30.0 与宿主往返已验证；**未**在本机 AstrBot WebUI 重载真实插件（会改用户容器数据，属用户操作） | **PARTIAL** |
| 2 | `go_health/apps/windows` JSON 正确 | E2E 全部 PASS（schema/字段/oracle） | **PASS** |
| 3 | `go_observe` OK 摘要 + 元数据 + 树 + 可选截图 | E2E PASS（含 47KB PNG、frame actionable、树 33 元素） | **PASS** |
| 4 | 夹具元素 click/set_value/type/坐标 click + 读回 + 动作后 STALE | E2E PASS（含 125% DPI 坐标命中、STALE_STATE、verification=verified） | **PASS** |
| 5 | 失败语义：401 / STALE / READONLY / UNSUPPORTED_ACTION / 文本前缀 | 401、STALE_STATE、READONLY_MODE、SESSION_STOPPED、CONTROLLER_BUSY 均 PASS；`UNSUPPORTED_ACTION` 无法由契约内 schema 合法构造（组合键/右键在 v1 schema 里不可表达），由 schema+Executor 单测覆盖 | **PARTIAL→PASS(可表达子集)** |
| 6 | 审计产生且不含明文/截图字节；动作审计返回前落盘 | audit JSONL 24 条（8 条 action）：输入明文 **0 命中**、token **0 命中**、executor 日志亦 0 命中；`type/set_value` 的 `text_length`/`text_sha256_prefix` 已写入且与独立计算一致（D-1 已修复，E2E 审计断言 9/9 PASS） | **PASS** |
| 7 | 多会话 CONTROLLER_BUSY；托盘/热键可停止 | E2E CONTROLLER_BUSY PASS；托盘/热键 MANUAL（见 §5） | **PARTIAL** |
| 8 | 文档齐全、可 30 分钟照做部署 | [DEPLOY.md](../DEPLOY.md) + [DEV.md](../DEV.md) 已交付并含本机验证命令；WebUI 重载为文档化用户操作 | **PARTIAL（文档 PASS，容器侧人工项）** |

---

## 5. 未能自动化 / 手工验证项（如实）

### 5.1 手工项（需要人工或桌面状态）

1. **GUI**（T7 范围）：首启 5 步配对向导、托盘 5 态、关闭窗口仍在托盘、暂停/恢复、紧急停止二次确认、浅/深主题切换与截图对比（`scripts/gui-theme-shots.ps1` 目前是**文档化 stub**，T7 GUI 就绪后实现 PrintWindow 截图）。
2. **全局热键** Ctrl+Alt+Shift+F12 的 ≤3s 停止：headless 不注册热键，需 GUI 模式手工。
3. **锁屏 `SESSION_LOCKED`**：需锁屏/解锁操作。
4. **UIPI_BLOCKED**：需启动 `WhaleGo.Fixtures.Elevated`（UAC 提权）后人工执行，步骤见夹具 README。
5. **100% / 200% DPI 坐标换算**：本机为 125%，改系统 DPI 需人工；125% 已 PASS。
6. **焦点/双记事本不串窗、保存对话框两支**：需要 notepad 交互场景。
7. **非客户区坐标拒绝**：需要点击标题栏/边框的专门夹具用例（未加入 E2E）。
8. **插件侧超时 → DISPATCH_UNCERTAIN 固定文案**：需要插件 + 延迟注入；插件 pytest 已覆盖转换层。
9. **AstrBot 容器内真实加载 + 模型调用**：本轮没有修改用户 AstrBot 数据/配置（`install-plugin.ps1` 只安装、不重启；WebUI 重载由用户操作）。容器→宿主网络链路已用 `docker exec AstrBot curl` PASS。
10. **审计目录只读负例 / 审计 fail-closed**：未注入只读目录（需 ACL 操作）；由 Executor 单测覆盖。
11. **provider 无 image 桩 / READONLY 插件侧开关**：需要 AstrBot provider 配置环境。

### 5.2 实现偏差（如实，已/待处理）

| ID | 偏差 | 影响 | 状态 |
|---|---|---|---|
| D-1 | `go_act type/set_value` 的审计记录 `text_length` / `text_sha256_prefix` 曾为 `null`（Contracts `AuditEvent` 有字段，Executor `ActionService` 未填充） | 审计无法证明"输入文本只记长度+哈希"这一 DESIGN §5.7/ARCH §3.8/PROTOCOL §11.1 要求 | **FIXED（T4，19:28:44 构建）**：`type` 取 args.text、`set_value` 取 args.value，写 RuneCount + sha256(UTF-8)[:8]；真实平台审计验证 3 条记录：type 11/`9c06f98a`、set_value 13/`b130461d`、set_value 500/`6d29dfe2`，无明文；E2E 审计断言 9/9 PASS；Executor.Tests 62/62（含新增审计用例） |
| D-2 | E2E 无法构造 `UNSUPPORTED_ACTION`：v1 schema 已把组合键/右键/双击/拖拽表达排除（模型面 `click.button` 仅 `left`、`count` 仅 `1`、`modifiers` 仅 `""`） | 该错误码只能由非法内部调用/未来扩展触发；由 schema 本身+单测覆盖 | 已知，设计如此 |
| D-3 | `docs/PROTOCOL.md`/`DESIGN.md`/`NAMING.md` 的 `session_id` 示例曾为 8 hex（`wg-8f21cd04`）而规范/实现为 `[:16]`（16 hex） | 插件/测试如照抄示例会被 resolver 拒绝 | **已修**（Lead 批准，examples→`wg-8f21cd04f00dbeef`，state_id 示例保持 8 hex；历史评审文档不改） |
| D-4 | 发现并修复的产品 bug：`TokenService.Verify` 旧实现比较 64 字节 ASCII hex 与 32 字节 hash → 任何 token 均 401 | 阻塞全部认证/契约门禁/E2E | **已修（T4）**，本轮 all-green |
| D-5 | 发现并修复的产品 bug：config 键被 lower-case 导致 camelCase 键（`actRatePerMinute` 等）被忽略 | 配置静默失效 | **已修（T4）** |
| D-6 | 夹具 DPI：WinForms 分析器 WFO0003 禁止 manifest DPI + API 双声明；夹具在 csproj 精确 `NoWarn` 并注释（manifest 保留 PerMonitorV2，Program 同时调 API） | 仅夹具项目，不影响产品 | 已知，已注释说明 |
| D-7 | GUI 主题截图脚本为 stub（T7 GUI 未定稿前不伪造截图） | 双主题视觉证据待 T7 后补 | 已知（P1 可选） |

### 5.3 本轮 E2E 夹具/harness 修复（过程记录）

1. `go_launch.wait_ms` 超契约上限（15000 → 10000）。
2. D7 500 字构造长度错误（414 → 500：100A + `read-after-100` + 386B）。
3. readonly 执行器并发启动被单实例互斥拒绝 → 改为**顺序两阶段**，并在停止后等待进程退出（后 T4 提供 per-data-root 互斥）。
4. 坐标映射错误：`go_windows/go_observe` 的 `bounds` 是 `[left,top,right,bottom]`（不是 x/y/w/h）→ 按 DWM 边框推导客户端原点后，125% DPI 坐标点击命中 pnlClick。
5. Windows PowerShell 5.1 兼容：`RandomNumberGenerator.Fill`、`Path.GetFullPath(path, base)` 不可用 → 改用 .NET Framework 可行 API。
6. E2E 阶段函数返回值被 PowerShell 管道污染 → 结果判定改为解析阶段 `RESULT:` 行聚合（`TOTAL: 86 passed`）。
7. `run-executor.ps1 -Port` 改用执行器 `--port` CLI（不再用 cmd.exe 包装，避免孤儿进程锁 DLL）。
8. 隐私提示断言修正：隐私提示由**插件**加在 OK 摘要行最前（PROTOCOL §11.5），直连执行器的 E2E 只断言 `OK:` + 图片语义。
9. 新增 D-1 审计断言时，首次把 `e2e-set-value` 长度写成 14（实为 13）导致 1 项 FAIL；修正后 9/9 PASS（审计值本身正确）。

---

## 6. 审计与隐私证据（真实数据）

来源：`scripts/e2e.ps1 -KeepTemp` 保留的 normal 执行器临时数据根（`normal/logs/`）。

```text
audit JSONL  : 24 行（8 条 action：go_act + go_launch；其余 observe）
plaintext    : grep 'e2e-type-42|e2e-set-value|read-after-100' -> 0
token        : grep 'wg_[A-Za-z0-9_-]{10,}|Bearer ' -> 0
executor log : 同样 0 命中（输入明文/token）
```

样本（动作审计；D-1 修复后，含 text_length/text_sha256_prefix，仍无明文）：

```json
{"ts_ms":1793446272721,"kind":"action","session_id":"wg-8f21cd04f00dbeef","tool":"go_act","op":"set_value",
 "window_id":"w-000001AE032A","target_kind":"element","action_sent":true,"dispatch_status":"accepted",
 "method":"uia","promoted":false,"verification":"verified","elapsed_ms":35,"event":"act",
 "text_length":13,"text_sha256_prefix":"b130461d","image_bytes":null}
{"ts_ms":1793446273032,"kind":"action","op":"type","tool":"go_act","dispatch_status":"accepted",
 "text_length":11,"text_sha256_prefix":"9c06f98a","event":"act"}
```

→ 明文/截图/token 均未入库；**D-1 已修复**：动作审计现在写入输入长度 + SHA-256 前缀（与独立计算一致），仍然不含明文。

---

## 7. 复现命令（本机已验证）

### 7.0 构建、测试、工具链

```powershell
dotnet --version                                   # 10.0.401
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
#   -> BUILD OK (Release, 3.7 s); 0 warnings / 0 errors
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
#   -> Contracts 82 / Platform 111 / Executor 62 / Gui 125 passed (380 total, 0 failed)
#   -> plugin pytest 150 passed -> TEST OK
cd astrbot/astrbot_plugin_whalego
uv run --no-project --with ruff==0.14.4 ruff check .
uv run --no-project --with ruff==0.14.4 ruff format --check .
```

### 7.1 契约门禁

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
#   -> RESULT: PASS / CONTRACT CHECK PASSED
```

### 7.2 E2E

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1
#   -> RESULT: 72 passed, 0 failed, 0 skipped   (normal)
#   -> AUDIT ASSERTIONS: 9 passed, 0 failed      (D-1 regression: length+sha256 prefix, no plaintext)
#   -> RESULT: 14 passed, 0 failed, 0 skipped   (readonly)
#   -> TOTAL: 86 passed, 0 failed, 0 skipped / E2E PASSED
# 保留现场（审计/日志/配置）用于排查：
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1 -KeepTemp
```

### 7.3 自检（干净临时数据根，非运行中端口）

```powershell
# 由 scripts/lib/WhaleGo.TestEnv.psm1 建临时 config/data/secrets 后：
WhaleGo.Executor.exe --self-check --json --config <tmp>\config.json   # WHALEGO_DATA_DIR=<tmp>\data
#   -> exit 0; ok=true; 12 items (config/data_dir/audit/port/uia/dpi/interactive_session/
#      screen_lock/token/contract = ok; hotkey/theme = warn(headless)); ASCII-only; 不含 token
```

### 7.4 容器 → 宿主链路（不改 AstrBot 配置）

```powershell
# 执行器临时配置 listen.address=0.0.0.0 + 空闲端口 + 临时数据根（WHALEGO_DATA_DIR），启动后：
docker exec AstrBot sh -c "curl -s -o /dev/null -w '%{http_code}' http://host.docker.internal:<port>/healthz"
#   -> 200
docker exec AstrBot sh -c "curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer <token>' http://host.docker.internal:<port>/healthz"
#   -> 200
# 容器内 POST /mcp（Bearer, initialize JSON）：到达 MCP 端点（空 body -> 400 解析错误，而非 401）
```

### 7.5 夹具与 UIA

```powershell
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
& windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe --help   # exit 0
# UIA 实测（FlaUI UIA3 5.0.0，120 DPI）：AutomationId = Control.Name；btnIncrement Invoke -> lblCounter=1；
# txtInput SetValue/type 读回；txtSecret IsPassword=true 且值不暴露；pnlClick 真实鼠标点击 -> panel=1；
# 坐标：pnlClick 300x175（125%）；ready-file 含 pid/dpi/client/hwnd。
```

---

## 8. 结论

- **P0/P1 主链路在本机真实通过**：构建、380 个 .NET 测试、150 个插件测试、契约门禁、86 条 E2E 用例（含 125% DPI 坐标点击、截图、STALE/回执语义、READONLY、CONTROLLER_BUSY、SESSION_STOPPED）、自检、容器→宿主网络链路。
- **产品 bug 2 个已由本轮门禁发现并修复**（token 验证、config 键大小写）；**审计元数据偏差 D-1 已修复并加入 E2E 回归断言**；1 个文档不一致已修（session_id 示例）。
- **剩余未自动化项**集中在 GUI/托盘/热键/锁屏/UIPI/多 DPI/焦点串窗与插件真实容器调用（见 §5.1），需要在 GUI 就绪后按 DEPLOY/DEV + GUI.md 手工清单补跑；D-1 已修复并由 E2E 内置审计断言持续回归（9/9 PASS）。
- 本文件后续如有补跑（GUI 双主题截图、100%/200% DPI、UIPI 手工、容器内 AstrBot 真实调用），在 §4 对应行追加"实际/结论"并注明日期。
