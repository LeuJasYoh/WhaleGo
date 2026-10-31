# WhaleGo 开发指南（v0.1，新增实现文档）

> 定位：**怎么在本仓库开发/构建/测试/调试/改契约**。部署与运维见 [docs/DEPLOY.md](DEPLOY.md)。
> 上位文档：`DESIGN.md`（v0.5，为什么）、`ARCHITECTURE.md`（v0.5，分层/接口/测试 seam/T1–T7）、`PROTOCOL.md`（v1.2，工具面唯一事实源）、`contracts/whalego.tools.v1.json`（`contract_version="1.2"`）、`GUI.md`（v0.3，GUI 宿主）。
> 本文为**新增文档**，不改变既有文档版本号（DESIGN/ARCH/README v0.5、PROTOCOL v1.2、GUI v0.3）。
> 标注约定：**[已验证]** = 本机实际执行过；**[按设计]** = 依契约/架构实现后应如此。

---

## 1. 仓库结构（实现阶段，实际）

```text
D:\Projects\PCCtrl
├── README.md / AGENTS.md            # 入口、硬规则
├── docs/                            # 设计/协议/GUI/评审/证据 + 本文件 + DEPLOY.md
│   ├── DESIGN.md ARCHITECTURE.md PROTOCOL.md GUI.md NAMING.md
│   ├── DEPLOY.md DEV.md             # 本文档族（新增实现文档 v0.1）
│   ├── evidence/container-env.md    # 环境证据（容器/宿主工具链）
│   └── evidence/implementation-e2e.md  # 实现验收结果（DESIGN §11 oracle 表）
├── contracts/
│   ├── whalego.tools.v1.json        # 工具面机器可读契约（单一事实源，contract_version=1.2）
│   └── README.md                    # 如何改契约/跑一致性检查
├── windows/                         # C#/.NET 10 执行器解决方案
│   ├── WhaleGo.sln / global.json（SDK 10.0.401）/ Directory.Build.props（warnings-as-errors 等）
│   ├── src/
│   │   ├── WhaleGo.Contracts/       # net10.0：DTO + 接口 + Domain 纯规则 + WireJson（无平台依赖）
│   │   ├── WhaleGo.Platform/        # net10.0-windows：Win32/UIA/截图/输入/launch（唯一碰原生 API 的层）
│   │   ├── WhaleGo.Executor/        # net10.0-windows：唯一组合根；MCP/认证/会话/租约/审计/控制/单实例/headless
│   │   └── WhaleGo.Gui/             # net10.0-windows：WPF 设置窗口 + WinForms NotifyIcon 托盘；只引用 Contracts
│   └── tests/
│       ├── WhaleGo.Contracts.Tests/
│       ├── WhaleGo.Platform.Tests/  # 需要 Windows + 交互桌面（用夹具窗口）
│       ├── WhaleGo.Executor.Tests/
│       ├── WhaleGo.Gui.Tests/
│       ├── WhaleGo.Fixtures.WinForms/   # UIA/输入测试夹具（WinExe，自包含）
│       └── WhaleGo.Fixtures.Elevated/   # 高完整性 UIPI 手工夹具（requireAdministrator，不进 CI）
├── astrbot/
│   └── astrbot_plugin_whalego/      # AstrBot 插件（metadata/_conf_schema/main.py/whalego/*/prompts/tests）
├── scripts/                         # PowerShell 构建/运行/安装/防火墙/契约/E2E + Python 驱动
│   ├── build.ps1 test.ps1 run-executor.ps1 self-check.ps1
│   ├── install-plugin.ps1 firewall.ps1 check-contract.ps1 e2e.ps1 gui-theme-shots.ps1
│   ├── lib/WhaleGo.TestEnv.psm1     # 测试脚本共用（临时数据目录/端口/token/健康等待）
│   ├── check-contract/contract_check.py
│   └── e2e/mcp_e2e.py
├── research/ 、ref/ 、ZCode-main/    # 只读参考；不要修改（ZCode-main 不入库）
└── .tmp/                            # 本机临时目录（gitignored；不要提交）
```

### 1.1 项目矩阵（同 ARCHITECTURE §3.1）

| 项目 | TFM | 引用 | 说明 |
|---|---|---|---|
| WhaleGo.Contracts | net10.0 | — | DTO/接口/错误码/枚举/**Domain 纯规则**（新鲜度、回执、裁剪、坐标映射）；无平台依赖 |
| WhaleGo.Platform | net10.0-windows | Contracts | FlaUI.UIA3 5.0.0 + P/Invoke；唯一允许调 Win32/UIA/截图/输入/launch 的层 |
| WhaleGo.Executor | net10.0-windows | Contracts + Platform + Gui | 唯一组合根；ASP.NET Core + ModelContextProtocol.AspNetCore 2.3.0 |
| WhaleGo.Gui | net10.0-windows | **仅 Contracts** | WPF 设置窗口 + WinForms 托盘；禁引 Platform/AspNetCore；不写 P/Invoke |
| *.Tests | net10.0-windows | 对应项目 | xunit；Platform/Gui 测试需要 Windows（部分需交互桌面） |
| Fixtures.* | net10.0-windows | 无（自包含） | 测试/E2E 目标窗口；Elevated 独立清单 |

**硬规则**（违反即视为设计违约）：
- Domain 规则只能放 Contracts（纯逻辑、可 100% 单测）；Platform/Executor 不重写新鲜度/回执/裁剪规则。
- Platform 之外（Executor/Gui/Tool 层）**不得**出现 user32/UIA/COM P/Invoke；原生调用只经 Contracts 接口。
- Gui **只引用 Contracts**；系统级动作走 Contracts 端口 `IWindowTheme` / `IHotkeyRegistrar`（D22），不写 P/Invoke。
- 模型面 schema **不得**出现 `session_id`/`request_id`/`scope`（插件注入的内部参数，contracts 标 `x-internal: true`）。
- UI = WPF 设置窗口 + WinForms `NotifyIcon` 托盘；**不做 WebUI/WebView**；启动方式 = **手动启动**（不做开机自启）。
- 自身进程窗口（`WhaleGo.*`）永不枚举/观察/截图。
- 不改语义：`possibly_sent` 禁止重试；动作后 state/frame 失效；恢复止血仅限本机；截图默认开但 `defaultInclude=false`。

---

## 2. 构建与测试

### 2.1 构建 [已验证]

```powershell
# 仓库根目录
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
# 等价：
dotnet build windows/WhaleGo.sln -c Release
# Debug / 不还原：
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Debug -NoRestore
# 夹具（已在 solution 中，可单独构建）：
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
dotnet build windows/tests/WhaleGo.Fixtures.Elevated -c Release
```

- 本机 [已验证]：`BUILD OK`（0 warnings / 0 errors）；执行器产物 `windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe`。
- `Directory.Build.props` 统一：`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors=true`、`AnalysisLevel=latest`、`Version=0.1.0`。测试项目在各自 csproj 显式 `TreatWarningsAsErrors=false`。
- `global.json` 固定 SDK **10.0.401**（rollForward=latestFeature）。SDK 不在 10.0.x 时先装 SDK，不要改 global.json（Lead 维护）。

### 2.2 测试 [已验证]

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
# 等价 dotnet 部分：
dotnet test windows/WhaleGo.sln -c Release
# 只看某个项目：
dotnet test windows/tests/WhaleGo.Contracts.Tests -c Release
```

- `scripts/test.ps1` 还会用 uv 临时环境跑插件 pytest：`uv run --no-project --with pytest --with pytest-asyncio --with mcp==1.30.0 python -m pytest -q`（不依赖插件自身 .venv，与容器 mcp 版本一致）；`uv` 缺失时跳过并警告，不阻塞 dotnet。
- 本机最终实测（final 二进制）：`Contracts 82 / Platform 111 / Executor 62 / Gui 125 = 380 passed, 0 failed, 0 skipped`；插件 `150 passed`（T5 final，命令见 §2.3）；`TEST OK`。
- Platform/Gui 的部分测试需要 Windows 桌面/交互会话；CI 里无法满足时应跳过（测试内用条件跳过，不要静默通过）。
- **测试执行顺序（必须）**：`scripts/test.ps1` 先 `dotnet build`，再按 Contracts → Platform → Executor → Gui **串行**逐项目 `dotnet test --no-build`，最后插件 pytest。原因：Platform 输入测试会抢占/恢复前台窗口、Gui 的 STA 测试会创建窗口，多个 test host 并行会互相干扰（实测偶发 110/111，重跑即过）。
- 测试分层与 seam 见 ARCHITECTURE §3.14：
  | 层 | seam | 测法 |
  |---|---|---|
  | Domain（Contracts） | 纯函数 | 单测（新鲜度/回执/裁剪/坐标/错误映射） |
  | Application（Executor） | 注入 fake I*（IObservationEngine/IDispatcher/IClock/IAuditSink） | 单测矩阵 |
  | Platform | 真实 Win32/UIA + 夹具窗口 | Windows-only 集成测试 |
  | Transport/Tool | WebApplicationFactory + MCP 客户端 | token/schema/错误路径 |
  | Plugin | L0–L2 不 import astrbot；L3 官方子类 | uv + pytest（+ 容器内真实加载冒烟） |
  | 全链路 | headless 执行器 + 真实 mcp 客户端 | `scripts/e2e.ps1` |

### 2.3 插件测试 [按设计]

```powershell
cd astrbot/astrbot_plugin_whalego
uv sync
uv run pytest            # 或：powershell -File ../../scripts/test.ps1
# ruff（若配置在 pyproject）
uv run ruff check .
```

- 插件分层规则：L0–L2（config/errors/mcp_client/convert/policy/session 等）**不 import astrbot**，可脱离宿主 pytest；
  L3（tools.py）允许 import `astrbot.core.agent.tool` / `agent.run_context` / `astr_agent_context`；L4（main.py）负责接线。
- 工具注册用官方子类方式（`handler=None`，不触发加载期重绑定）；工具 schema 从包内 contracts 文件构造，启动时可选与执行器 `tools/list` 对比（漂移只告警）。
- 容器内冒烟（不重启容器）：`docker exec AstrBot python -c "import ..."` / 插件加载后 WebUI 重载由用户操作。

---

## 3. 运行执行器（开发/无头）

### 3.1 GUI 模式（手动启动）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1
# 或直接：
windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe
```

默认读 `%APPDATA%\WhaleGo\config.json`；首启生成 config/secrets/control-state 与日志目录；托盘 + WPF 设置窗口；单实例互斥（第二实例退出码 2）。

### 3.2 Headless + 临时配置/数据（开发与 CI 推荐）

**隔离原则：绝不碰真实用户数据。** 用临时目录 + `--config` + `WHALEGO_DATA_DIR`：

```powershell
$tmp = Join-Path $env:TEMP ("whalego-dev-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $tmp 'data'), (Join-Path $tmp 'logs') | Out-Null
# config.json 可参考 scripts/lib/WhaleGo.TestEnv.psm1 的 New-WhaleGoTestDataDir（端口/token 哈希/审计目录/allowlist）
$env:WHALEGO_DATA_DIR = Join-Path $tmp 'data'
windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --headless --no-hotkey --config (Join-Path $tmp 'config.json')
# 另一个窗口：
Invoke-RestMethod http://127.0.0.1:8765/healthz | ConvertTo-Json -Depth 5
```

现成脚本：`scripts/run-executor.ps1 -Headless -NoHotkey -WaitReadySec 30`、`scripts/self-check.ps1`、`scripts/check-contract.ps1`、`scripts/e2e.ps1`（都走临时目录隔离）。

### 3.3 无头参数速查

| 参数 | 说明 |
|---|---|
| `--headless` | 不建 WPF/托盘/热键；日志走 stdout；MCP 照常 |
| `--no-hotkey` | 不注册全局热键 |
| `--config <path>` | 指定 config.json（CI 临时目录） |
| `--port <int>` | 覆盖 listen.port（0..65535；测试/多实例用；CI 脚本用它避免改配置） |
| `--self-check --json` | 自检 JSON（退出码 0/非 0），不起 MCP 监听 |
| `--diagnostics-dump` | 打印 go_health 等价 JSON + 最近请求后退出 |

环境变量：`WHALEGO_*`（双下划线分段，如 `WHALEGO_LISTEN__PORT`、`WHALEGO_UI__THEME`）；`WHALEGO_DATA_DIR` 覆盖本地数据根（secrets/control-state/logs/ui-state）。
优先级：appsettings.json < config.json < 环境变量 < 命令行。

**单实例互斥名（per-data-root，T4 实现）**：默认数据根（%LOCALAPPDATA%\WhaleGo）使用
`Local\WhaleGo.Executor.SingleInstance` 与 `Local\WhaleGo.Executor.ShowSettings`；
当设置 `WHALEGO_DATA_DIR` 或 `--config`（数据根被覆盖）时，名字追加 `.` + `sha256(规范化绝对数据根小写)[0..8]`，
即 `Local\WhaleGo.Executor.SingleInstance.<8hex>` / `Local\WhaleGo.Executor.ShowSettings.<8hex>`。
这样 CI/E2E 的临时数据根实例可以与用户真实实例（或其他测试实例）**共存**；同一数据根的第二个 headless 实例仍以退出码 2 退出。

**CLI 输出编码**：`--self-check --json` / `--diagnostics-dump` 输出 ASCII-only JSON（非 ASCII 用 \uXXXX），
避免 Windows 控制台代码页问题；程序启动时设置 Console.OutputEncoding=UTF-8；headless 就绪行固定以 `LISTEN ` 开头。

---

## 4. 夹具（Windows 测试窗口）

### 4.1 WhaleGo.Fixtures.WinForms（T2/T3/T4 共用）

```powershell
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
$exe = "windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe"
Start-Process $exe -ArgumentList '--title','dev','--ready-file',"$env:TEMP\fixture-dev.ready",'--auto-close-ms','60000'
& $exe --help
```

- 详细控件/automation id 契约见 [windows/tests/WhaleGo.Fixtures.WinForms/README.md](../windows/tests/WhaleGo.Fixtures.WinForms/README.md)：
  `btnIncrement`（Increment）、`lblCounter`、`lblStatus`（`counter=…;check=…;text=…;clicks=…`，200ms 刷新）、
  `txtInput`、`btnSetText`（Set）、`txtSecret`（密码）、`chkToggle`、`lstItems`(A/B/C)、`cboChoice`(X/Y/Z)、
  `pnlClick`（240×140，无子控件，Click 计数）、`btnClickTarget`、`lblClickCount`、`btnDisabled`（Enabled=false）。
- **UIA 事实**（FlaUI UIA3 实测）：`AutomationId = 控件 Name`；`UIA Name = 可读文本/当前值`（如 btnIncrement Name="Increment"，lblCounter Name="0"，lblStatus Name="counter=0;…"）。Label 的值只在 UIA `Name`（无 ValuePattern），测试别当 TextBox 读。
- 进程 **PerMonitorV2**（app.manifest + `Application.SetHighDpiMode`）；逻辑尺寸 900×650 / 点击面板 240×140 会在 Shown 后按 `DeviceDpi/96` 显式缩放（本机 120 DPI → 客户区 1125×813、面板 300×175）。
- **测试宿主 DPI 注意**：测试进程若不是 DPI-aware，UIA 的 `BoundingRectangle` 会被 DWM 虚拟化（125% 下约 ×0.8）。断言尺寸/坐标前把测试宿主设为 PerMonitorV2，或使用执行器返回的 frame 像素坐标。
- 多实例并行：用 `--title <suffix>` 区分窗口 + 独立 ready/click-log 路径；teardown 用 `--auto-close-ms` 兜底。

### 4.2 WhaleGo.Fixtures.Elevated（UIPI 手工用例，不进 CI）

```powershell
dotnet build windows/tests/WhaleGo.Fixtures.Elevated -c Release
Start-Process windows/tests/WhaleGo.Fixtures.Elevated/bin/Release/net10.0-windows/WhaleGo.Fixtures.Elevated.exe -ArgumentList '--title','uipi'
```

`requireAdministrator`（触发 UAC）；验证执行器对高完整性窗口返回 `UIPI_BLOCKED`（`retry=never`、`dispatch_status=not_sent`、零发送、state/frame 保持有效）。步骤见 [windows/tests/WhaleGo.Fixtures.Elevated/README.md](../windows/tests/WhaleGo.Fixtures.Elevated/README.md)。

---

## 5. 契约同步工作流（本仓库最重要的一条流水线）

**单一事实源 = `docs/PROTOCOL.md` v1.2；机器可读镜像 = `contracts/whalego.tools.v1.json`；两端（C# Executor / Python 插件）都从契约为准。**

修改流程（顺序不能变）：
1. 先改 `docs/PROTOCOL.md`（语义/参数/错误码/返回；升版本规则见 PROTOCOL §9）。
2. 再改 `contracts/whalego.tools.v1.json`（`contract_version` 按 PROTOCOL 同步；模型面不出现 internal 参数；internal 参数标 `"x-internal": true`）。
3. 同步两端实现：Executor 的 Tool 层/handlers + 插件 schema/转换层；插件启动时对 `tools/list` 做漂移告警。
4. 跑契约门禁：

```powershell
# 起临时 headless 执行器 → 真实 mcp 1.30 客户端 tools/list → 与契约 diff（含 401 负例）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
# 依赖 uv：uv run --no-project --with mcp==1.30.0 python scripts/check-contract/contract_check.py ...
```

门禁断言（CI 必须全绿）：7 个工具名精确一致；描述一致；inputSchema 结构一致（**剥离 x-internal 属性后**、键序不敏感）；模型面 schema 无 `session_id`/`request_id`/`scope`；`contract_version="1.2"`；`protocol_version=1`；`/mcp` 无 token 401。

规则：
- 新增可选字段/枚举值 → 不升主版本（会检查）；删除字段/改语义/改工具名 → `protocol_version + 1`（需 Lead 决策并同步全部文档）。
- 模型可见文本 = 简体中文 + 固定英文前缀（`OK:` / `ERROR[<code>]:`），错误码英文；success/失败判据见 PROTOCOL §2。
- 契约改动的证据与结果写入 [docs/evidence/implementation-e2e.md](evidence/implementation-e2e.md)。

---

## 6. E2E（真实执行器 + 真实 mcp 客户端）

```powershell
# 前置：构建执行器与 WinForms 夹具；uv 可用；Windows 交互桌面；无其它夹具实例在跑
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1
# 保留下手排查：-KeepTemp；指定端口/路径：-Port/-ReadonlyPort/-ExecutorPath/-FixturePath
```

脚本编排（`scripts/e2e.ps1` + `scripts/e2e/mcp_e2e.py`）：
1. **顺序两阶段**（交互会话内 headless 执行器共享单实例互斥；T4 已提供 per-data-root 互斥名，脚本仍顺序以保证确定性）：
   normal 执行器 → 全部正常用例；停进程并等待退出 → `policy.readonly=true` 执行器 → 只读负例；
2. 真实 mcp 1.30.0 客户端（Bearer token；隐藏内部参数 `session_id`，与插件行为一致）；
3. 覆盖用例：`/healthz 200`、`/mcp` 无 token/错 token 401、7 工具、`go_health.protocol_version=1 + accepting_actions`、
   `go_apps`/`go_windows`、`go_launch` allowlist 夹具、`go_observe` 树+PNG 截图、无截图 observe 不图、
   `go_act` `set_value`/`type`/`click` + 读回 oracle、D7 500 字读回、125% DPI 坐标点击（frame 映射）、
   审计断言（type/set_value 的 text_length + sha256 前缀、无明文/token；D-1 回归）、旧 state_id → `STALE_STATE`、
   `go_stop` → `SESSION_STOPPED`、停止后观察仍可用、只读执行器 `READONLY_MODE`、双会话 `CONTROLLER_BUSY`；
4. 逐条打印 `PASS/FAIL/SKIP`，末尾汇总；任何 FAIL 退出码 1（setup 错误退出码 2）；
5. 结束时清理执行器/夹具进程与临时目录（`-KeepTemp` 保留现场）。

**本机最终实测结果**（final 二进制，2026-10-31）：
```text
RESULT: 72 passed, 0 failed, 0 skipped   (normal phase)
AUDIT ASSERTIONS: 9 passed, 0 failed       (D-1 regression: text_length + sha256 prefix, no plaintext)
RESULT: 14 passed, 0 failed, 0 skipped   (readonly phase)
TOTAL: 86 passed, 0 failed, 0 skipped    -> E2E PASSED
```

已知边界（E2E 仍不覆盖，如实记录）：
- **坐标点击已验证**（125% DPI、frame 映射、元素 `bounds_screen_diag` 中心 → 图片像素）：本机 PASS；
  100%/200% DPI 与"非客户区拒绝"仍需人工/参数化环境。
- **CONTROLLER_BUSY 已验证**：本机实现保留租约，双会话用例 PASS（retry=retry, retry_after_ms=5000）。
- **UIPI_BLOCKED**：需要管理员窗口，手工（`WhaleGo.Fixtures.Elevated`），不进 CI。
- **锁屏 SESSION_LOCKED / 审计目录只读 / provider 无 image / 焦点切走 / 保存对话框两支**：需要人工或环境注入，
  记录在 [docs/evidence/implementation-e2e.md](evidence/implementation-e2e.md) 的“未能自动化”清单。
- **E2E 里不能构造 UNSUPPORTED_ACTION**：v1 模型面 schema 已排除组合键/右键/双击等表达（设计如此），由 schema+单测覆盖。

结果写入 [docs/evidence/implementation-e2e.md](evidence/implementation-e2e.md)（每条：前置/夹具/oracle/预期/实际/PASS-FAIL/证据命令）。

---

## 7. 脚本清单

| 脚本 | 用途 | 关键参数 |
|---|---|---|
| `scripts/build.ps1` | 构建 solution（可附带夹具） | `-Configuration` `-NoRestore` `-IncludeFixtures` |
| `scripts/test.ps1` | dotnet test + 插件 pytest（有 venv 时） | `-Configuration` `-NoBuild` `-SkipPlugin` |
| `scripts/run-executor.ps1` | 启动执行器（GUI/headless，后台友好） | `-Headless` `-Port` `-Config` `-NoHotkey` `-WaitReadySec` |
| `scripts/self-check.ps1` | 跑 `--self-check --json` 并透传退出码 | `-Config` `-Raw` |
| `scripts/install-plugin.ps1` | 安全安装插件到 AstrBot data（**不重启容器**） | `-Mode Auto|Copy|Docker` `-Container` `-DataRoot` `-WhatIf` `-Force` |
| `scripts/firewall.ps1` | 管理 `WhaleGo-MCP` 入站规则（限 Docker/WSL 网段） | `-Action add|remove|show` `-Port` `-RemoteAddress` |
| `scripts/check-contract.ps1` | 契约门禁（tools/list vs contracts） | `-CheckHealth` `-KeepTemp` `-ExecutorPath` |
| `scripts/e2e.ps1` | 端到端验收（顺序两阶段 headless 执行器 + mcp 客户端 + 审计断言） | `-Port` `-ReadonlyPort` `-KeepTemp` `-PythonTimeoutSec` |
| `scripts/gui-theme-shots.ps1` | GUI 浅/深主题截图（P1 可选；T7 就绪前为**文档化 stub**，打印计划并 SKIP） | `-OutputDir` |
| `scripts/lib/WhaleGo.TestEnv.psm1` | 测试脚本共用函数（临时数据目录/端口/token/健康等待/HTTP 状态） | 供脚本 import |
| `scripts/check-contract/contract_check.py` | MCP tools/list 与契约归一化 diff（Python，mcp 1.30） | `--url/--token/--contracts/--check-health` |
| `scripts/e2e/mcp_e2e.py` | E2E 用例驱动（PASS/FAIL/SKIP） | `--url/--token/--readonly-url/--fixture/--contracts` |

约定：脚本用 **ASCII 消息**（Windows PowerShell 5.1 无 BOM 时中文易乱码；中文只进文档）；统一 `powershell -NoProfile -ExecutionPolicy Bypass -File ...` 调用；测试脚本必须使用临时目录与 `WHALEGO_DATA_DIR` 隔离，绝不碰用户真实数据。

---

## 8. 代码规范

C#（见 ARCHITECTURE §6）：
- TFM 按矩阵（Contracts `net10.0`；其他 `net10.0-windows`）；`Nullable=enable`；`TreatWarningsAsErrors`（测试项目除外）；`AnalysisLevel=latest`。
- 公共类型 `public`、内部实现 `internal`；跨层异常只用 `WhaleGoToolException(GoError)`（不要裸抛/吞异常）。
- wire JSON 一律 `snake_case`（序列化策略）；本地 config 是 camelCase，映射集中一处（ARCHITECTURE §3.8），禁止散落手写映射。
- 平台调用只在 `WhaleGo.Platform`；Contracts 不引用 Windows 专用包（保持 `net10.0` 可测）。
- 格式化：`dotnet format`（提交前）；不要手工改 `WhaleGo.sln` / `Directory.Build.props` / `global.json`（Lead 维护）。

Python（插件，见 ARCHITECTURE §4）：
- `ruff` 格式化/检查；类型注解全覆盖；`from __future__ import annotations`；async-only。
- L0–L2 不 import astrbot；L3 仅允许官方 FunctionTool 子类相关 import；L4 负责生命周期接线。
- 工具注册 `handler=None` + `add_llm_tools`；schema 来自 contracts，不手抄。
- token/输入文本/截图字节不进日志；插件侧错误码映射集中在 `convert.py`。

文档：中文（代码/字段英文）；模型可见文本 = 简体中文 + `OK:`/`ERROR[...]` 前缀；改既有文档先改 PROTOCOL/裁决；**不要改 `research/`、`ref/`、`ZCode-main/`**。

---

## 9. 常见构建/环境问题

| 症状 | 原因 | 处理 |
|---|---|---|
| NU1701（FlaUI 包目标框架较老） | FlaUI.UIA3 5.0.0 的 NuGet 目标框架与 net10.0-windows 不完全匹配，仅告警 | Platform/Executor csproj 已 `<NoWarn>$(NoWarn);NU1701</NoWarn>`；新增引用 FlaUI 的项目同样加，或在 Directory.Build.props 统一（Lead 决定） |
| WFO0003（夹具 app.manifest 与 DPI API 重复声明） | WinForms 分析器要求 DPI 只走 API/属性，但夹具需要 manifest 级 PerMonitorV2 | 夹具 csproj 显式 `NoWarn WFO0003` 并有注释说明（见 Fixtures.WinForms.csproj）；不要全局压制 |
| error NETSDK1136 / 无法引用 net10.0-windows | `net10.0` 项目引用了 windows TFM 项目 | 按矩阵：Contracts=net10.0 只能被引用；Platform/Executor/Gui/Tests 用 net10.0-windows |
| `dotnet --version` 不是 10.0.401 | 本机 SDK 多版本 / PATH 问题；`global.json` 只在 windows/ 目录下生效 | 从 `windows/` 或仓库根运行（根目录无 global.json，用默认最新 SDK）；需要严格固定时在 windows/ 下执行 |
| 构建被 CA/IDE 分析器卡住 | `TreatWarningsAsErrors=true` + AnalysisLevel latest | 修代码/加精确 NoWarn（需注释原因），不要整体关闭分析器 |
| 测试需要交互桌面失败 | Platform/Gui 测试起真实窗口/UIA | 在登录用户桌面运行；CI 中按条件跳过并如实报告 |
| PowerShell 中文乱码 | Windows PowerShell 5.1 按 ANSI 读无 BOM UTF-8 脚本 | 脚本消息保持 ASCII；文档用 UTF-8（.md 由编辑器/GitHub 渲染） |
| GUI 子进程看不到 --help 输出 | WinExe 无控制台 | 夹具/执行器内部 AttachConsole 到父进程；或重定向 stdout（Start-Process -RedirectStandardOutput） |
| `docker exec` 找不到 python/插件 | 容器名/路径不对 | `docker ps` 确认容器名；数据目录以 `docker inspect` 的 Mounts 为准 |
| 端口 8765 被占 / 第二个实例退出码 2 | 已有执行器（单实例互斥）；同一数据根 headless 只允许一个实例 | `netstat -ano | findstr 8765`；退出旧实例；测试请用独立 `WHALEGO_DATA_DIR`/`--config`（per-data-root 互斥名）与 `--port` |

---

## 10. 实现偏差与说明（如实记录，供评审追溯）

> 记录实现阶段与设计文档不完全一致、或设计未细化的取舍；每条给出状态与影响。

1. **夹具 DPI 实现方式**：设计未规定夹具 DPI；实现用 manifest 声明 PerMonitorV2 + `Application.SetHighDpiMode`，并在 Shown 后按 DeviceDpi 显式缩放（900×650/240×140 逻辑尺寸）。因 WinForms 分析器 WFO0003 与 manifest 声明冲突，夹具 csproj 精确 `NoWarn` 并注释原因。**状态：已实现并实测（120 DPI）**。
2. **夹具 AccessibleName**：为让 UIA 的 `Name` 保留可读文本/当前值（Label 值可读），控件只设 `Name=automation id`，不覆盖 `AccessibleName`（个别如 `txtInput` 设固定 "Input" 避免 WinForms 自动借用动态 Label 文本）。AutomationId=控件 Name 已用 FlaUI 实测。**状态：已实现**。
3. **契约门禁依赖 uv + PyPI**：`check-contract.ps1`/`e2e.ps1` 用 `uv run --no-project --with mcp==1.30.0`（与容器 mcp 版本一致的真实客户端）；离线环境需预热 uv 缓存，脚本无 stdlib 回退。**状态：有意选择（验证真实客户端兼容性）**。
4. **坐标点击全链路（已覆盖）**：E2E 通过 `go_observe(target.window.bounds = [left,top,right,bottom])` + frame 的 image/client 尺寸推导客户端原点，
   把元素 `bounds_screen_diag` 中心换算成图片像素后执行 `go_act coordinate`；本机 125% DPI 下 pnlClick panel 0→1 PASS。
   未验证：100%/200% DPI、非客户区拒绝（人工/参数化环境）。**状态：主要路径已验证**。
5. **CONTROLLER_BUSY（已验证）**：本机实现保留输入租约，E2E 双会话用例 PASS（session A 持租约 → session B `CONTROLLER_BUSY`，`retry=retry`、`retry_after_ms=5000`）。**状态：已确认并纳入 E2E**。
6. **UIPI 手工**：`WhaleGo.Fixtures.Elevated` 需管理员/UAC，不进 CI；仅手工 P1 验收。**状态：已交付夹具 + 步骤**。
7. **GUI 主题截图**：`gui-theme-shots.ps1` 在 T7 GuiShell 仍为 scaffold 时打印计划并 SKIP（不伪造截图）；T7 完成后按脚本内计划实现 PrintWindow 截图。**状态：stub（P1 可选）**。
8. **run-executor.ps1 的 -Port**：使用执行器正式 CLI `--port <int>`（不再用 cmd.exe 环境包装），打印的 PID 即执行器 PID，便于调用方停止。**状态：已实现并验证**。
9. **夹具不在最初的 sln 中**：由 Lead 加入 `windows/WhaleGo.sln`（T6 不编辑 sln）；当前 solution 已包含两个夹具项目。**状态：已解决**。
10. **文档版本**：DEPLOY/DEV 为新增实现文档 v0.1；既有文档版本不变（DESIGN/ARCH/README v0.5、PROTOCOL v1.2、GUI v0.3、contract_version 1.2）。**状态：符合约束**。
11. **证据基线（已补齐）**：宿主/容器环境与 C#↔Python MCP 1.30 往返证据在 `docs/evidence/container-env.md`；
    实现验收（380 .NET 测试 / 150 插件测试 / 契约门禁 8 项 / E2E 86 用例 + 9 项审计断言 / 自检 / 容器链路 / 审计隐私）
    在 `docs/evidence/implementation-e2e.md`，未能自动化的项如实列“MANUAL/PARTIAL”。**状态：已记录（2026-10-31）**。
12. **审计输入元数据（已修复）**：`go_act type/set_value` 的审计记录现在写入 `text_length`（Unicode rune 数）+
    `text_sha256_prefix`（SHA-256 hex 前 8 位），仍无明文。修复前为 null（曾报 T4）；D-1 修复后由 `scripts/e2e.ps1`
    的 9 项审计断言持续回归：type 11/`9c06f98a`、set_value 13/`b130461d`、set_value 500/`6d29dfe2`，0 明文 0 token。
    **状态：closed（T4 19:28:44 构建，E2E 9/9 PASS）**。
13. **本轮门禁发现的产品 bug（已修）**：`TokenService.Verify` 64 字节 hex vs 32 字节 hash 比较导致所有 token 401；
    config 键被 lower-case 导致 camelCase 键静默失效。两者均由 T4 修复并复验。**状态：closed**。

---

## 11. 证据与验收

- 环境证据：[docs/evidence/container-env.md](evidence/container-env.md)（dotnet SDK/运行时、NuGet 包、Python/uv、容器 mcp 版本、C# MCP ↔ Python mcp 1.30 往返）。
- 实现验收：[docs/evidence/implementation-e2e.md](evidence/implementation-e2e.md)（DESIGN §11 oracle 表逐条：前置/夹具/oracle/预期/实际/PASS-FAIL/证据命令）。
- 复现入口：`scripts/build.ps1 → scripts/test.ps1 → scripts/check-contract.ps1 -CheckHealth → scripts/e2e.ps1`。
- 提交前检查（本地）：构建 0 warning、dotnet test 全绿、contract gate 通过、E2E 无 FAIL（SKIP 需在证据文档解释）；不提交 .tmp/、bin/、obj/、secrets。