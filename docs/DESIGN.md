# WhaleGo 架构设计（v0.5）

> 状态：**v0.5 —— D21 用户拍板（GUI 六问）已落地**（见 §12 决策记录）。当前阶段先交付"分层/封装/契约"设计，
> 对应 `ARCHITECTURE.md`（v0.5，工程分层与封装）、`PROTOCOL.md`（v1.2，MCP 工具面契约）与 `docs/GUI.md`（v0.3，GUI 宿主）；
> 评审通过后再进入实现。
> 依据：本工作区前期研究 `research/zcode/`（ZCode CUA 实现研究，~2500 行主报告 + 6 篇分题笔记）、
> `research/astrbot/`（AstrBot 插件约束与能力边界），以及本次在本机完成的可行性实测（附录 A）。
> 日期：2026-10-31。
>
> **文档地图**（本阶段三份设计交付物）：
> - `DESIGN.md`（本文）：为什么这样做——背景/研究结论/方案对比/选型/总体架构/安全/部署/路线图。
> - `ARCHITECTURE.md`：怎么做——功能分层、封装边界、Contracts 接口、状态机、目录结构、测试 seam、任务分解。
> - `PROTOCOL.md`：跨端契约——MCP 工具面 v1（参数/返回值/回执三态/错误码/坐标与新鲜度规则）。

---

## 0. 结论先行

一句话：**Windows 侧做一个常驻的 MCP（Streamable HTTP）服务端，用 C#/.NET 10 实现观察与输入；
AstrBot 侧写一个 Python 插件作为 MCP 客户端，把 `go_*` 工具注册给模型；两者之间用 Bearer Token 认证，
容器用 `host.docker.internal` 访问宿主。**

关键判断：

1. **Windows 执行器选 C#（.NET 10）**：UIA COM、Win32 P/Invoke、SendInput、PrintWindow/WGC 都是本机一等公民；
   官方 MCP C# SDK（2.3.0）与 ASP.NET Core 已经能直接提供 Streamable HTTP MCP 服务；本机已实测可跑（附录 A）。
   Rust 可行但开发成本高；Node 的原生输入/UIA 绑定是额外风险；Python（pywinauto）最快但分发、线程模型、
   性能与长期维护都更弱。**不要为了"和容器同语言"而选 Python/Node。**
2. **AstrBot 侧选插件做 MCP 客户端（而不是只配内置 mcpServers）**：插件能拿到 `FunctionTool` 的完全控制权
   （显式 JSON Schema、图片回传、错误语义、会话绑定、确认流程、审计、只读开关），也符合"AstrBot 插件"的诉求；
   内置 MCP 客户端保留为"零代码备选"（同一服务端两用）。容器实测 `pip list`：`mcp 1.30.0`（AstrBot 自带，
按 3 元组解包）；插件通过它工作，`requirements.txt` 固定 `mcp>=1.30,<2`（宿主环境的 mcp 2.x 会破坏 AstrBot 客户端，见评审 M9）。
3. **协议照抄 ZCode 的三条硬契约**（不是照抄它的工具形态）：
   观察与动作分离、`action_sent/dispatch_status` 三态回执、`state_id` + 内部 frame 绑定 + 动作后立即失效（fail-closed）。
   这能避免一大类"点错目标/重复点击/坐标漂移"事故。
4. **安全必须 day-1 设计**：跨容器到宿主的链路没有"同机 peer 校验"可依赖，token + 监听面收敛 + 动作分级
   + 审计 + kill switch 是底线；截图默认开启但只截目标窗口客户区、按需附图、限额。

---

## 1. 背景与目标

### 1.1 场景

- 用户 Windows 主机（本机）运行两个 Docker 容器：机器人账号（SnowLuma）与 AstrBot（4.29.3，插件宿主/模型编排）。
- 目标：模型在 AstrBot 内调用一个工具 → 工具请求到达 Windows 上的执行器 → 执行器用原生 API
  "看屏幕（读 UI 树/截图）、点鼠标、敲键盘" → 结果回到模型。
- 最终产物：**AstrBot 插件**（容器侧） + **Windows 执行器**（宿主侧）两部分，仓库内可构建、可测试、可部署。

### 1.2 硬约束

| 约束 | 事实 / 影响 |
|---|---|
| 执行器必须在**交互式用户会话**（Session 1） | Windows 服务/Session 0 没有桌面；不能做成普通 Windows Service。 |
| 容器与 Windows 是**跨机器链路** | 不能依赖同机 peer 校验/命名管道 ACL；认证、授权、审计必须自己设计。 |
| AstrBot 插件是**宿主进程内 Python 代码**、全异步单事件循环 | 不能阻塞；不能 `requests`/`time.sleep`；handler 无框架级超时（工具调用受 `tool_call_timeout` 约束）。 |
| AstrBot 4.29.3 已内置 `mcp 1.30.0` | 插件可直接用官方 Python MCP 客户端；`requirements.txt` 只需声明，无需真正安装。 |
| 开发约束 | 需要虚拟环境时在工作区用 `uv`；Windows/Node/Go/Rust/JDK21/Python/.NET8/10 均可用。 |

### 1.3 非目标（本期不做）

- 不做浏览器内操控（ZCode 的 Browser Use 仅作工程参考）、不做多租户/多用户云端、不做代码签名安装器、
  不做完整 ZCode 级宿主矩阵（WPF/Chromium/UWP/GTK 全覆盖）——先做"少而稳"的主路径，矩阵逐步扩。
- 不做语音/视频流、远程桌面、屏幕直播。

---

## 2. 前期研究结论（只列会直接影响架构的）

### 2.1 ZCode CUA 的六条可迁移事实

1. **架构是"本机桌面 Agent"**：CUA 只在 desktop-local Host 可用，Helper（签名进程）持有 OS 权限；
   SSH/WSL/Docker/远程 workspace **明确不支持**。WhaleGo 的"容器→Windows"在 ZCode 中**没有对应实现**，
   因此网络链路与认证必须自研（研究主报告 §2、§7.6）。
2. **模型面收敛为"1 个入口 + SDK"** 是它拥有 Node Agent 运行时的产物；对 WhaleGo **值得抄契约、不值得抄形态**：
   WhaleGo 用"少而稳的粗粒度 MCP 工具"（≈7 个），而不是 30 个或"单 js 工具"（主报告 §3.4、§11.1-11.3）。
3. **观察以 AX/UIA 树为第一优先，截图坐标只作兜底**；模型只提交"最新 raster 的整数像素"，
   `state_id` 由服务端签发、模型回传；`frame_id` 完全内部化（模型不传），过期/跨会话/不可动作一律 fail-closed（§4）。
4. **"动作被接受"≠"操作生效"**：回执三态 `action_sent / dispatch_status(accepted|possibly_sent|not_sent) / retry_action`，
   `possibly_sent` 时**禁止自动重放**非幂等动作（§5.4）。
5. **Windows 输入是"后台优先的自动升级链"**：UIA → PostMessage → 前台 SendInput；UIPI 预检先于一切；
   物理动作进程内串行；焦点防守/键盘排水/逐字符节拍是真实工程资产（§6、notes/04）。
6. **Browser Use 是唯一完整可读的真实自动化实现**：对象模型 + 单一命令通道 + snapshot/ref + 结构化结果/错误，
   是本项目的工程范本（§10）。

### 2.2 AstrBot 插件侧的关键事实（研究 + 源码复核）

- 插件 = 宿主进程内 Python；无沙箱；handler 无框架超时；**必须全异步**。
- 工具注册推荐官方子类方式：`@dataclass class XTool(FunctionTool[AstrAgentContext]) + async call(context, **kwargs)`，
  `handler=None`；`@filter.llm_tool` 只解析 docstring（schema 表达力有限）不采用。
  **禁止 `FunctionTool(handler=插件绑定方法)`**：加载期会被重绑定为 `partial(raw, star_cls)` → TypeError，
  且 `handler_module_path` 解析错（评审 B1，源码已核实）。
- `ToolExecResult = str | mcp.types.CallToolResult`；返回 `CallToolResult` 时可带 `ImageContent`，
  AstrBot 4.29.3 会把图片落盘缓存并在下一步注入模型（`tool_loop_agent_runner.py:1294-1142`）——
  但**仅当 provider 配置 `modalities` 为空或含 image** 时才真正可见；且 **AstrBot 不读 `isError`**（全树零处理）。
- 工具调用超时来自 AstrBot 配置 `tool_call_timeout`（默认 120s，`asyncio.wait_for`）；事件 handler 无此超时（两类要区分）。
- **非内置工具的默认权限是 `member`**（`func_tool_manager.py:453-458`），不是 admin；插件必须自己做管理员兜底。
- AstrBot 内置 MCP 客户端会把工具名 sanitize 成 `[A-Za-z0-9_-]` 且**不加 server 前缀**；多 server 时工具名要自己保证唯一。
- 容器数据目录挂载：`C:\\Users\\23500/data → /AstrBot/data`；插件装到 `data/plugins/<name>/`，WebUI 可重载。

### 2.3 对 WhaleGo 的直接含义

| 结论 | 设计落点 |
|---|---|
| 跨机器、无同机信任 | L1 Bearer Token + 监听面收敛 + 审计 + 可选 TLS；token 不进工具参数/日志 |
| 工具面成本 | 固定 7 个粗粒度工具（含 launch/stop），名字稳定、snake_case、无歧义 |
| 观察/动作强分离 | `go_observe` 唯一事实源；`go_act` 只回 receipt；新鲜度状态在服务端 |
| Windows 输入复杂 | Platform 层窄接口 + 升级链 + 回执三态；先覆盖主路径，矩阵逐步扩 |
| 模型可能被提示注入 | 只读开关、速率限制、`go_stop`、审计（确认流程 P2） |

---

## 3. 方案对比与选型

### 3.1 集成方式：A/B/C 三案

| 方案 | 组成 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A. 纯 MCP 配置 | AstrBot 内置 `mcpServers` 直接连 Windows MCP | 零代码、最快打通 | 无插件级控制（确认/只读/审计/会话绑定）、工具名无前缀、错误提示不可定制 | 作为**兼容退路**保留 |
| **B. 插件 MCP 客户端（选定）** | 插件用 Python MCP SDK 连 Windows MCP，注册 `FunctionTool` | 完全控制工具面/结果转换/图片/错误/确认/审计；仍是标准 MCP 协议，服务端可被其他客户端复用 | 要维护插件；AstrBot 插件机制有硬约束 | **主路径** |
| C. 自定义 HTTP/JSON-RPC | 不走 MCP，普通 REST | 协议最简单 | 失去 MCP 生态与 AstrBot 内置能力，语义要自造 | 不选 |

### 3.2 Windows 执行器语言选型

| 维度 | **C# / .NET 10（选定）** | Rust | Node/TS | Python |
|---|---|---|---|---|
| UIA COM | FlaUI（MIT，本机实测可用）/ 原生 interop | windows crate，手写量大 | 需 FFI/edge-js，风险高 | pywinauto，可用但性能/线程弱 |
| Win32 输入 | P/Invoke 一等公民 | windows crate 好 | koffi/ffi 额外层 | ctypes 可行 |
| MCP 服务端 | **官方 MCP C# SDK 2.3.0 + ASP.NET Core**（实测跑通） | rmcp 可用 | 官方 TS SDK 好 | 官方 Python SDK 好 |
| 部署 | 单文件/框架依赖均可，WindowsDesktop 运行时已装 | 单 exe 无依赖 | 需 Node | 需 Python+依赖 |
| 开发效率 | 高（本机 sdk 10.0.401） | 中 | 中 | 高（但长期维护弱） |
| 结论 | ✅ | 备选 | 不推荐（原生层风险） | 不推荐（分发/长期） |

> 机器上已有 .NET SDK 10.0.401（运行时 8/10 齐全，含 WindowsDesktop），无需新装 SDK。

### 3.3 AstrBot 插件技术栈

- Python 3.12（容器自带），纯异步 `asyncio`；网络用 `mcp` SDK 内部 httpx，**不引入额外依赖**；`requirements.txt` 固定 `mcp>=1.30,<2`。
  容器实测：`pip list` 为 `mcp 1.30.0`（3 元组 legacy 客户端）；宿主 `.spike` venv 曾验到 mcp 2.x 的 2 元组/httpx2 差异，
  该 venv 已删除——实现阶段在 `docs/evidence/` 复验并补记录；若 AstrBot 升级到 mcp 2.x 需同步适配（评审 M9/D20）。
- 兼容容器内 `mcp 1.30` 的 `streamablehttp_client` 与新版 `streamable_http_client`（实测两者 API/返回值不同，需兼容层）。

### 3.4 传输与认证

- **传输：MCP Streamable HTTP**（`POST/GET /mcp`）。理由：跨容器可用、AstrBot 与官方 SDK 均原生支持、
  比 stdio 合理、比 SSE/WebSocket 简单。SSE 兼容**未验证**，仅作 P3+ 路线记录（v1 不承诺）。
- **认证：L1 Bearer Token**（HTTP `Authorization` 头），插件侧配置；服务端只存哈希/常量时间比较；可轮换。
- **TLS**：MVP 不做（容器↔宿主同机网络）；文档保留升级路线（自签证书 + 固定指纹 / 反向代理 / mTLS）。
- L2 每调用能力令牌（ZCode 式）本期不做，用"会话 + 审计 + 只读开关"替代；标记为 P4。

### 3.5 选型结论表

| 决策点 | 结论 |
|---|---|
| AstrBot 集成 | 插件 + MCP 客户端（B），保留纯 MCP 配置（A）作为兼容 |
| Windows 执行器 | C# / .NET 10 / ASP.NET Core / 官方 MCP C# SDK / FlaUI UIA3 |
| 通信 | Streamable HTTP + Bearer Token；默认端口 8765；监听地址可配 |
| 工具面 | 7 个必需工具（含 go_launch/go_stop；无 go_session；单一动作通道 `go_act`） |
| 观察 | UIA 树文本 + elements 表 + 可选单窗截图；state_id 对外、frame 内部绑定 |
| 安全 | 只读开关 + 速率限制 + 审计 JSONL + controller lease + `go_stop`（确认流程 P2） |

---

## 4. 总体架构

### 4.1 拓扑

```text
┌──────────────────────── Docker Desktop (WSL2) ────────────────────────┐
│  AstrBot 容器 (4.29.3)                                                │
│   ├─ 模型编排 / Agent loop                                            │
│   ├─ astrbot_plugin_whalego（本项目）                                  │
│   │    ├─ ToolRegistry: go_health/go_apps/go_windows/go_observe/…     │
│   │    ├─ McpClient: mcp SDK streamable HTTP，Bearer Token            │
│   │    ├─ SessionMap: umo → wg-<sha16>（内部参数，模型不可见）          │
│   │    └─ Policy: 只读/确认/限流/错误映射                              │
│   └─ 配置: url=http://host.docker.internal:8765/mcp                   │
└──────────────────────────────────┬───────────────────────────────────┘
                                   │ HTTP + Bearer Token (只走内网/宿主)
┌──────────────────────────────────▼───────────────────────────────────┐
│ Windows 主机（用户交互会话 Session 1）                                 │
│  WhaleGo.Executor（常驻进程，本项目）                                   │
│   1) Transport: ASP.NET Core + MCP SDK（/mcp, /healthz）+ Token 中间件 │
│   2) Tools: 7 个 MCP 工具（薄层：schema 校验/结果转换）                 │
│   3) SessionManager: session_id → {last state/frame, lease, audit}     │
│   4) ObservationService: 窗口枚举/身份 + UIA 树 + 截图 + 预算/裁剪     │
│   5) ActionService: 校验/升级链/回执组装/防重放                        │
│   6) Platform（窄接口）: Win32/UIA/SendInput/PrintWindow(→WGC)         │
│   7) Audit: JSONL 审计/脱敏；Health/Stop                               │
└───────────────────────────────────────────────────────────────────────┘
```

### 4.2 一次"看一眼 + 点一下"的时序

1. 用户对机器人说"帮我点一下计算器上的 7"。
2. 模型调用 `go_apps` / `go_windows` 找目标 → `go_observe(window_id=...)`。
3. 执行器：窗口身份校验 → UIA 树遍历（预算裁剪）→ 生成 `state_id`，可选截图生成内部 frame；
   返回**文本树（带 index）+ elements 元数据 (+ image)**。
4. 模型调用 `go_act(op="click", target={kind:"element", index:6, state_id:"st-..."})`。
5. 执行器：校验 state 新鲜度/窗口身份 → UIA 模式梯 Invoke → 若 absent 才按矩阵升级 →
   返回 receipt（`action_sent`、`dispatch_status`、`verification`、`state_available:false`）。
6. 模型需要确认效果 → 再次 `go_observe`（读回/二次观察），而不是相信"工具返回 ok"。
7. 插件把 receipt / 错误转成模型能懂的文字（不暴露内部术语如 PostMessage/升级路径）。

### 4.3 状态所有权（重要）

| 状态 | 所有者 | 说明 |
|---|---|---|
| UIA 树 / 截图 / frame 坐标 | **执行器**（ObservationService） | 模型只拿 index/像素；不传 frame_id 细节 |
| `state_id` + 内部 frame 生命周期 | **执行器**（SessionManager） | 新观察顶替；动作后立即失效；窗口身份变化/过期即失效 |
| 当前会话/租约/审计 | **执行器** | 单会话串行输入；服务端唯一事实源 |
| umo → session_id 映射、只读/管理员策略 | **插件** | 插件是"会话人格"，执行器是"动作执行者" |
| 工具 schema/描述、错误文案 | **插件**（面向模型） | 执行器只保证结构化语义 |

---

## 5. Windows 执行器设计（C#）

### 5.1 分层与依赖方向

```text
WhaleGo.Contracts  (net10.0, 无 Windows 依赖)
    ← 被引用：WhaleGo.Platform（实现平台接口）、WhaleGo.Executor（使用 DTO/接口）
WhaleGo.Platform   (net10.0-windows, FlaUI + P/Invoke) —— 唯一碰原生 API 的地方
WhaleGo.Executor   (net10.0-windows, ASP.NET Core + MCP SDK + DI) —— 业务/协议/会话/审计
WhaleGo.Gui        (net10.0-windows, WPF + WinForms) —— WPF 设置窗口 + WinForms NotifyIcon 托盘（同进程 UseWPF+UseWindowsForms，零第三方依赖）；只引用 Contracts（禁引 Platform/AspNetCore）
（必须 windows TFM 才能引用 Platform；单测同理）
```

依赖规则（照抄 ZCode/Browser Use 的解耦范式）：

- Tool 层只做 schema 校验、调用 Service、组装结果，**不直接调 Win32**。
- ActionService 只依赖 `IPlatform` 窄接口（UIA/输入/窗口/截图分开），便于注入 fake 测试、未来换实现。
- ObservationService 是**唯一 UI 事实来源**；动作层不"猜"目标，必须拿观察产生的 state/frame。
- 传输层只做认证/协议/限流/大小预算，不含业务状态。
- Contracts 只放 DTO + 接口 + 错误码，不依赖任何平台库。

### 5.2 目录结构

```text
windows/
  WhaleGo.sln
  Directory.Build.props                  # 统一 LangVersion/Nullable/TreatWarningsAsErrors
  src/
    WhaleGo.Contracts/
      Model/Observation.cs               # ObservationSnapshot/ElementNode/FrameInfo/WindowRef/AppRef
      Model/Action.cs                    # ActionRequest/ActionReceipt/DispatchStatus/Verification
      Model/GoError.cs                   # GoError：错误码/retry 语义/错误 DTO
      Model/Control.cs                   # ControlState/StatusSnapshot/RecentRequest/SelfCheckReport/ConfigPatch
      Abstractions/                      # IWindowCatalog/IObservationEngine/IActionDispatcher/IScreenCapture/IAuditSink
                                         #   + IExecutorControl/IConfigStore/ITokenService/ISelfCheck
    WhaleGo.Platform/
      Native/NativeMethods.cs            # user32/kernel32/dwmapi/shcore P/Invoke
      Native/Dpi.cs                      # Per-Monitor V2、坐标换算
      Windows/WindowCatalog.cs           # EnumWindows、窗口身份、AUMID、前台/最小化
      Uia/UiaTreeWalker.cs               # UIA 遍历、预算、渲染文本（FlaUI UIA3）
      Uia/UiaActions.cs                  # 模式梯 Invoke/Toggle/Select/Expand/Value/Scroll
      Capture/PrintWindowCapture.cs      # MVP 截图（PrintWindow + BitBlt 兜底）
      Capture/WgcCapture.cs              # P2：Windows.Graphics.Capture（单窗、Chromium 友好）
      Input/SendInputDispatcher.cs       # 物理 lane、SendInput 鼠标/键盘/Unicode
      Input/PostMessageDispatcher.cs     # PostMessage 定点派发（含子 HWND）
      Input/Clipboard.cs                 # P2：paste（快照→写→粘贴→读回→还原）
      Input/ForegroundGuard.cs           # 焦点防守/排水/条件恢复
      PlatformFacade.cs                  # 组合实现 Contracts 的接口
    WhaleGo.Executor/                    # MCP 内核/Application 服务/审计/控制端口（组合根 Program.cs）
    WhaleGo.Gui/                         # WPF 设置窗口 + WinForms NotifyIcon 托盘/热键消息窗（同进程，只引用 Contracts）
  tests/
    WhaleGo.Contracts.Tests/              # 纯逻辑：预算裁剪/receipt/错误映射/坐标同源校验
    WhaleGo.Platform.Tests/               # 只跑在 Windows：窗口枚举/UIA/截图/输入（可跳过交互断言）
    WhaleGo.Fixtures.WinForms/            # 受控测试夹具（文本框/按钮/计数/状态）
    WhaleGo.Executor.Tests/               # MCP 工具层：schema/回执/会话新鲜度（WebApplicationFactory）
    WhaleGo.Gui.Tests/                    # GUI 逻辑 seam：状态映射/配置校验/自检/主题解析（--headless，无窗口）
```

### 5.3 关键契约（接口草案）

```csharp
// Contracts —— 平台无关
public interface IWindowCatalog {
    IReadOnlyList<AppInfo> ListApps();
    IReadOnlyList<WindowInfo> ListWindows(string? appId = null);
    WindowInfo? Resolve(string windowId);
}

public interface IObservationEngine {
    // 采集一次观察：树（已裁剪/渲染）+ 可选帧
    Task<ObservationSnapshot> ObserveAsync(ObserveRequest request, CancellationToken ct);
}

public interface IActionDispatcher {
    // 单次派发：校验目标/策略 → 平台动作 → 回执（possibly_sent 语义在实现内保证）
    Task<ActionReceipt> DispatchAsync(ActionRequest request, ObservationSnapshot? boundState, CancellationToken ct);
}

public interface IScreenCapture {
    Task<FrameInfo> CaptureWindowAsync(string windowId, CaptureOptions options, CancellationToken ct);
}

public interface IAuditSink { void Write(AuditEntry entry); }

// Contracts —— GUI 宿主端口（GUI 只能经此操作内核；完整方法集见 ARCHITECTURE §3.3）
public interface IExecutorControl {
    StatusSnapshot GetStatus();
    void Pause(string reason);  void Resume();  void Stop();   // 恢复仅本机 + 二次确认
    ConfigPatch GetConfig();  void SetConfig(ConfigPatch patch);
    // 其余：GetRecentRequests/GetToolCounters/RotateToken/RevealTokenForCopy/RunSelfCheck/
    //       ReadAuditTail/OpenDataFolder/RequestRestart
}

// GUI 不写 P/Invoke：这两个窄端口由 Platform 实现，经组合根注入 GUI（D22）
public interface IWindowTheme { bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark); }
public interface IHotkeyRegistrar { bool TryRegister(string id, string gesture, Action onPressed); void Unregister(string id); }
```

```csharp
// 执行器内
sealed class SessionManager {                      // session_id → GoSession
    GoSession GetOrCreate(string sessionId);
}
sealed class GoSession {                            // 唯一状态所有者（当前窗口 + 当前 state）
    string SessionId { get; }
    string? CurrentWindowId { get; }
    ObservationSnapshot? State { get; }             // 当前 state（元素索引/元数据）
    FrameBinding? Frame { get; }                    // 内部 frame（frame_id 不暴露给模型）
    long ObservedAtMs { get; }
    SemaphoreSlim InputGate { get; }                // 单会话输入串行
    void Capture(ObservationSnapshot s);            // 顶替当前窗口/state/frame
    void InvalidateAfterAct();                      // 动作进入派发 → 立即失效
    bool IsStateFresh(string stateId, long nowMs);  // TTL/会话/窗口身份
    bool IsFrameFresh(long nowMs);                  // TTL/几何/DPI
    bool IsFrameActionable(long nowMs);             // 非黑帧/几何一致/未最小化
}
```

### 5.4 观察引擎

1. **窗口/应用枚举**：`EnumWindows` + 进程名/exe 路径 + UWP AUMID（`ApplicationFrameHost` 特例，PID 分裂处理）
   + 窗口类名 + 标题 + 屏幕矩形 + DPI + 前台/最小化；`app_id`：普通程序 `win-exe:<sha1(规范化 exe 路径小写)[:12]>`、
   打包应用 `win-aumid:<aumid>`，**删除 `#2/#3` 后缀**，同名多窗口由 `window_id` 区分（评审 M3/D16）；标题只供人读。
   **自身窗口过滤**：永不枚举/观察/截图 `WhaleGo.*` 进程的窗口（自检与 GUI 白名单例外）；`go_windows` 前台置前、
   `go_apps` 给"推荐下一步（window_id）"（D9/D10）。
2. **UIA 树**：FlaUI UIA3 遍历；节点 → `{index, kind, title, value, value_truncated, actions[], enabled, selected, bounds_screen_diag}`；
   **密码控件（IsPassword）value 一律 `***`**；**文本渲染**给模型（缩进 + index + role + name + value），
   文本树固定截 48；`max_value_chars`（1..4000，默认 48）控 `elements[].value` 单值上限，总预算受 `limits.maxObserveChars`（默认 20000）约束，
   超限截断 + `tree_budget_exceeded`；稀疏索引注记，OK 行固定含 `elements=… sparse=yes|no`（评审 M5/D7）。
3. **截图**：MVP 用 `PrintWindow(PW_RENDERFULLCONTENT)` + BitBlt 兜底（实测 WinForms/普通窗口 17-30ms）；
   P2 换 WGC；**只截客户区**，缩放 ≤1024 宽、base64 ≤200KiB；frame 带 `image_width/image_height/client_width/client_height/scale_x/scale_y/capture_region:"client"`；
   三开关 AND（执行器 `screenshot.enabled` &&（模型 `include_screenshot`||`screenshot.defaultInclude`）&& 插件 `allow_screenshot`）；
   默认 `enabled=true`、`allow_screenshot=true`、`defaultInclude=false`（默认不自动附图，模型按需 `include_screenshot=true`）；
   敏感窗口只回树 + `screenshot_suppressed`，截图禁用时 `screenshot_disabled`（不报错）（D8/D9/Q4）。
   **CaptureValidator** 做非空/非纯黑/几何一致校验；未验证宿主（Chromium/GPU）→ `actionable=false` + `capture_degraded`（评审 M2）。
4. **坐标换算**：模型坐标 = 截图**图片像素**（左上原点）；内部 `client_x = image_x*client_width/image_width`（四舍五入），y 同理；`bounds_screen_diag` 仅诊断不可当坐标；
   窗口移动/缩放/DPI 变化 → 旧 frame 失效（元素 state 不因几何变化失效）。
5. **state/帧绑定**：服务端生成 `state_id`（如 `st-<8hex>`），内部 frame 对象（`fr-*` 不暴露给模型）绑定
   `{session_id, window_id, owner_pid, geometry, dpi, captured_at, capture_method, actionable}`；
   **任何动作进入派发后 state/frame 立即失效**；无截图 observe **不清理**同窗口既有 frame（仍校验 TTL/几何/DPI/actionable），
   切换窗口或 act 进入派发 → frame 失效；`go_act` 校验失败 → `STALE_STATE`（不自动补观察）。

### 5.5 动作派发（升级链）

```text
go_act(op, target, args)
  → 校验：会话/只读/速率/参数/能力（v1 子集以外的动作 → UNSUPPORTED_ACTION）
  → 获取 controller lease（全局唯一输入控制权；非持有者 CONTROLLER_BUSY）
  → 目标解析：element index+state_id（动作后立即 dirty）或 coordinate x,y（用会话最近带截图观察的 frame）
  → UIPI 预检（目标完整性 > 本进程 → refused, not_sent, 零发送）
  → 物理 lane 排队（SendInput/激活/剪贴板才入队；后台 UIA/PostMessage 不入队）
  → 元素动作：UIA 模式梯（Invoke → Toggle → SelectionItem.Select → ExpandCollapse → Value/Scroll/TextRange）
  → 坐标动作：① UIA ElementFromPoint hit-test → ② 宿主分类/丢弃矩阵 → ③ PostMessage → ④ 前台 SendInput
  → 回执：action_sent/dispatch_status/retry_action/verification（method 只进审计，模型面只暴露 escalated）
```

**红线（照抄 ZCode）**：
- 动作一旦进入原生调用，任何失败都按 `possibly_sent` 处理，**不自动重放**；只有 `not_sent` 才允许升级/重试。
- **verification 闭集** `{verified,mismatched,unverified}`（每 op 规则以 PROTOCOL §3.7 为准）：`set_value` 元素读回 == 请求值、
  `type` 元素读回包含请求文本 → verified，否则 mismatched；`click/key/scroll` → unverified。mismatched 只允许 reobserve，
  不得自动重放、不改变 `dispatch_status=accepted`（D5）。
- 成功路径不向模型披露内部升级细节；`method`/升级只进审计与 receipt 元数据（模型面只留 `escalated`）。
- **取消语义**：派发前可取消（→ not_sent）；进入原生调用后不可撤销，超时/异常 → possibly_sent（评审 M7）。

**v1 动作面（收窄）**：`click(element|coordinate, left, count=1)`、`set_value(element, value)`、
`type(element, text)`、`key(element, 单键)`、`scroll(element|coordinate, up|down, pages)` —— **所有 op 必须带 `target`**，
`type`/`key` 只接受 element target（无 target 前台盲发路径已删除；需前台但无法安全激活 → `FOREGROUND_REQUIRED` 零发送）。
`type.text` ≤1500；组合键/双击/右键/拖拽/粘贴 → `UNSUPPORTED_ACTION`（P2 再扩，评审 M10/D6）。
**`go_launch`（写动作）**：只接受 allowlist 的 `app` 或 `app_id`（激活已有主窗），二选一，无 PATH/arguments；成功 = `isError=false` 且首行 `OK:`，
审计 fail-closed 返回前落盘；`LAUNCH_TIMEOUT` → `retry=never`（"进程可能已启动，禁止重试，请用 go_apps 确认"）（D4）。

### 5.6 会话、租约与停止

- 会话身份：`session_id` 由插件注入（内部参数，`"wg-"+sha256(umo)[:16]`）；**模型永远看不到也传不了** `session_id`/`request_id`/`scope`（D1）。
- `GoSession` 持有：当前窗口、state/frame、输入锁（`SemaphoreSlim`）、速率计数、审计上下文。
- **三态止血（恢复仅本机）**：只读 `readonly`（配置，拒绝 `READONLY_MODE`）；暂停 `paused`（本机 GUI/托盘，拒绝 **`PAUSED`**，不持久化、
  重启回运行）；停止 `stopped`（本机热键/托盘/管理员命令；模型 `go_stop` 只停本会话 → 该会话 `SESSION_STOPPED`；全局停止持久化到
  `%LOCALAPPDATA%\WhaleGo\control-state.json`）。暂停/停止立即释放租约、取消排队动作，在途派发走 `possibly_sent`；恢复一律本机二次确认。
- **controller lease（全局输入租约，跨会话唯一）**：进入派发前获取、动作续期（TTL 60s），等待上限 `policy.controllerWaitMs`（默认 2000）；
  非持有者 → `CONTROLLER_BUSY`（`retry=retry` + `retry_after_ms=min(剩余租约,5000)`，不使 state/frame 失效）；`go_health.controller={held,mine,expires_in_ms}`（D11）。
- 空闲超时（默认 10 分钟）清理 session；MCP 会话结束时释放会话与租约。

### 5.7 审计与日志

- `%LOCALAPPDATA%\WhaleGo\logs\audit-YYYYMMDD.jsonl`：`{ts, session_id, tool, op, window_id, app_id, target_kind,
  action_sent, dispatch_status, method, promoted, verification, elapsed_ms, error_code}`；
  **control 事件**（pause/resume/stop/token 轮换/配置修改/主题切换）单独记 `{ts, event, reason, actor=local|admin}`（D15）。
- **不记录**：输入文本明文（只记长度/哈希）、剪贴板内容、截图字节（只记尺寸/预算）、token。
- **写动作审计在返回前落盘**（fail-closed：队列满/磁盘不可写 → `AUDIT_UNAVAILABLE` 且 not_sent）；
  观察审计可异步，丢弃计数进 `go_health.audit_dropped`（评审 M6）。
- 结构化诊断日志（Serilog/内置 ILogger）分级别；`go_health` 顶层新增 `paused/pause_reason/paused_since_ms/stopped/session_stopped/accepting_actions` +
  `capabilities.launch_allowlist`（`capabilities.observation` 仅在截图启用时含 `screenshot_printwindow`），原 `os.session_id` 改名
  `os.windows_session_id`；`/healthz` 免认证最小面返回 `{ok,version,protocol_version,uptime_s,paused,stopped,accepting_actions}`
  （`listen.healthzRequireToken` 可要求 token，不含 token/会话 id，D1/D3）。

### 5.8 配置

`%APPDATA%\WhaleGo\config.json`（本地 camelCase；**wire 协议一律 snake_case**，映射表见 ARCHITECTURE §3.8）。来源优先级：
`appsettings.json` < `%APPDATA%\WhaleGo\config.json` < 环境变量 `WHALEGO_*` < 命令行；GUI 显示来源徽标、环境变量覆盖项只读（D14）。
token 明文**不进** `config.json`（只存 `auth.tokens[].{name,sha256,created_at}`），明文在 `%LOCALAPPDATA%\WhaleGo\secrets.json`
（仅当前用户 ACL）；`wg_` 前缀 32 字节 CSPRNG；GUI 掩码显示（首 5 后 4）+ 一键复制插件配置（剪贴板 60s 自动清空）；
token 不进工具参数/返回值/日志/审计/托盘 tooltip（D13）。

```json
{
  "listen": { "address": "0.0.0.0", "port": 8765, "healthzRequireToken": false },
  "auth": { "tokens": [ { "name": "astrbot", "sha256": "…", "createdAt": "2026-10-31T00:00:00+08:00" } ] },
  "policy": { "readonly": false, "actRatePerMinute": 120, "controllerWaitMs": 2000,
              "stateTtlSeconds": 120, "frameTtlSeconds": 60 },
  "limits": { "maxObserveElements": 300, "maxObserveChars": 20000, "maxImageWidth": 1024, "maxImageBytes": 200000,
              "toolTimeoutSeconds": 40 },
  "screenshot": { "enabled": true, "mode": "printwindow", "defaultInclude": false },
  "privacy": { "maskPasswordFields": true, "sensitiveWindows": ["*password*", "*bank*"] },
  "audit": { "enabled": true, "dir": "%LOCALAPPDATA%\\WhaleGo\\logs", "requireDurableForActions": true },
  "hotkeys": { "enabled": true, "emergencyStop": "Ctrl+Alt+Shift+F12", "openSettings": null },
  "ui": { "notificationsLevel": "important", "theme": "system" },
  "logging": { "level": "information", "dir": "%LOCALAPPDATA%\\WhaleGo\\logs" },
  "launch": { "allowlist": [ { "name": "notepad", "path": "C:\\Windows\\System32\\notepad.exe" } ] }
}
```

控制状态单独存 `%LOCALAPPDATA%\WhaleGo\control-state.json`（全局 stopped 持久化，D2）。截图默认开（Q4）：`screenshot.enabled=true`、
插件 `allow_screenshot=true`，`defaultInclude` 保持 `false`（默认不自动附图，模型按需 `include_screenshot=true`，仍受三开关 AND）；
`ui.theme` ∈ {`system`,`light`,`dark`}（默认 `system`，跟随系统；手动切换热生效、不重启，Q6）；启动方式为手动启动（不自启，Q5）。

### 5.9 错误码与重试语义

**唯一事实源 = `PROTOCOL.md §8`**（避免两文档漂移，评审 m6）。要点：

- 成功判定按工具类别（动作类/查询类/`go_launch`，见下）；其余一律 `isError=true` + 错误对象（动作类内嵌 receipt）。
- `retry=retry` 仅允许 `not_sent`；`possibly_sent` 一律 `retry=reobserve` 且 `retry_action=false`。
- v1 新增：`UNSUPPORTED_ACTION`、`CONTROLLER_BUSY`、`AUDIT_UNAVAILABLE`、`APP_NOT_FOUND`、`AMBIGUOUS_APP`、
  `LAUNCH_FAILED`、`LAUNCH_TIMEOUT`（`retry=never`）、`FRAME_NOT_ACTIONABLE`、`SESSION_STOPPED`、**`PAUSED`**、
  **`SESSION_LOCKED`**（锁屏；`ok=false` 时树仍可观察）、`INTERNAL`。
- **动作类**（go_act）成功 = `isError=false && dispatch_status="accepted"`；**查询类**（health/apps/windows/observe/stop）与
  **go_launch**（写动作、无 dispatch_status）成功 = `isError=false` 且首行 `OK:`（详见 PROTOCOL §2/§3）。

### 5.10 宿主形态

- **单进程托盘宿主 + WPF 设置窗口**：`WhaleGo.Executor.exe`（WinExe，唯一组合根）内嵌 kernel + WinForms `NotifyIcon` 托盘 +
  WPF 设置窗口（同进程 `UseWPF`+`UseWindowsForms`，零第三方依赖）；GUI 项目 `windows/src/WhaleGo.Gui/`（只引用 Contracts，
  禁引 Platform/AspNetCore）；单实例 `Local\WhaleGo.Executor.SingleInstance` 互斥 + `ShowSettings` 事件（`--headless` 共用，冲突退出码 2）；
  关闭设置窗口 = 隐藏托盘，唯一退出在托盘菜单（确认框含租约警告）；托盘 5 态、设置 6 页。
- **外观（Q6）**：浅色/深色主题（跟随系统 + 手动切换、热切换不重启，`ui.theme`）；视觉参考 deepseek.com 的清爽排版/蓝白主色/圆角卡片；
  **不嵌 WebView、不做 WebUI/网页设置页**。
- **P1 止血**：托盘暂停/恢复 + 全局热键默认 `Ctrl+Alt+Shift+F12`（只触发紧急停止、不恢复、不弹确认；恢复一律本机二次确认）；
  启动方式 = **手动启动**（不自启，Q5）。
- 无头 seam：`--headless` / `--self-check --json` / `--config <path>` / `--no-hotkey` / `--diagnostics-dump`（见 `docs/GUI.md` v0.3）；不做 Windows Service（Session 0 无桌面）。

---

## 6. AstrBot 插件设计

### 6.1 目录与分层

```text
astrbot/astrbot_plugin_whalego/
  metadata.yaml                  # name=astrbot_plugin_whalego（合法标识符）; version; astrbot_version ">=4.29"
  main.py                        # Star 子类：__init__/initialize/terminate + 工具实例化注册 + 命令
  _conf_schema.json              # url/token/timeout/readonly/admin_only/allow_screenshot/inject_usage_prompt
  requirements.txt               # mcp>=1.30,<2（容器实测内置 1.30.0；上限防 2.x 破坏 AstrBot 客户端）
  README.md
  whalego/
    __init__.py
    config.py                    # 读取/校验配置（防御性，schema 升级兼容）
    compat.py                    # mcp 1.30 与新版客户端 API/字段差异适配
    mcp_client.py                # 连接/重连/调用/超时/取消/并发
    tools.py                     # 7 个 FunctionTool 子类（显式 schema + call()，官方推荐方式）
    convert.py                   # MCP 结果 → AstrBot CallToolResult（文本预算/图片保留/错误映射）
    session.py                   # umo → session_id、串行锁、最近 state_id 提示（不缓存 UI 状态）
    policy.py                    # 只读/管理员校验/限流
    errors.py                    # 错误码 → 模型友好文案
```

### 6.2 工具注册方式

```python
# tools.py（L3）—— 官方推荐子类方式；handler=None，不会触发加载期重绑定（评审 B1）
@dataclass
class GoObserveTool(FunctionTool[AstrAgentContext]):
    name: str = "go_observe"
    description: str = OBSERVE_DESC
    parameters: dict = Field(default_factory=lambda: OBSERVE_SCHEMA)
    client: Any = None
    sessions: Any = None
    allow_screenshot: bool = True    # 注入：配置（Q4 默认开）

    async def call(self, context: ContextWrapper[AstrAgentContext], **kwargs) -> ToolExecResult:
        event = context.context.event
        sid = self.sessions.for_umo(event.unified_msg_origin)
        outcome = await self.client.observe(
            sid, window_id=kwargs.get("window_id"), app_id=kwargs.get("app_id"),
            include_screenshot=kwargs.get("include_screenshot", False) and self.allow_screenshot,
            settle_ms=kwargs.get("settle_ms"), max_elements=kwargs.get("max_elements"))
        return convert.to_astrbot_result(outcome)     # mcp.types.CallToolResult

# main.py（L4）：self.context.add_llm_tools(GoObserveTool(client=..., sessions=..., allow_screenshot=...), ...)
```

- **禁止** `FunctionTool(handler=<插件实例绑定方法>)`：AstrBot 会在加载期把 `handler.__module__ == 插件模块` 的工具
  重绑定为 `partial(raw, star_cls)`，绑定方法会错位成 `(star_cls, event)` → TypeError；且 `handler_module_path` 解析错，
  插件禁用后工具仍 active（评审 B1；源码 `ref/AstrBot/star/star_manager.py:1281-1311`、`star/context.py:109-122`）。
- handler/call 返回 `mcp.types.CallToolResult`：文本 + 可选 `ImageContent`（AstrBot 缓存后作为图片注入下一步）。
- 工具总数固定 7 个，避免每轮工具定义成本膨胀。

### 6.3 连接管理

- `initialize()` 校验配置 → 建立 MCP 连接（`streamablehttp_client`，header 带 Bearer）→ 失败只告警不阻塞 AstrBot 启动，后台指数退避重连（1s→30s）。
- 每次工具调用：`ClientSession.call_tool(name, args)`，`asyncio.wait_for(timeout)`；`timeout` 默认 **45s**，clamp 到
  AstrBot `tool_call_timeout-5s` 且必须 > 执行器整体预算（≤40s）5s 以上；动作类超时/断线按 `DISPATCH_UNCERTAIN` 呈现；
  只读类可重连重试一次，再失败返回 `EXECUTOR_TIMEOUT`；401/协议不匹配不重试（D12）。
  超时后**不重发动作**、把连接标记为 unclean、下次调用前重连。
- 异常分类：连接断开 → 重连一次（幂等只读工具）→ 仍失败返回友好错误；动作类**绝不自动重发**。
- 并发：同一 `umo` 串行（`asyncio.Lock`），不同会话可并发；全局信号量限制并发调用数（默认 2）。
- `terminate()`：取消重连任务、关闭 session/连接，不阻塞。

### 6.4 umo → whalego session

- `session_id` 由插件注入（内部参数）= `"wg-" + sha256(umo)[:16]`（如 `wg-8f21cd04f00dbeef`）；**模型不可见/不可传**；observation 状态绑定在该 session 上。
- 插件只记录最近一次 observe 的 `state_id/window_id` 作为**提示**（不缓存 UI 状态、不代为复用）；
  缺失/过期由执行器判定并返回 `STALE_STATE`，插件提示模型"先 observe"。
- 插件重启/会话切换 → 新 session 自动创建；旧 session 执行器侧空闲超时清理。

### 6.5 结果转换与错误映射

- 文本预算：执行器已裁剪；插件再做截断保护，对齐 AstrBot 预览阈值（≈7000 tokens，超限会落文件只给路径，评审 m8）。
- 图片：`CallToolResult.content` 原样保留 `ImageContent`（不转文字）；**仅当 provider modalities 支持 image 时**才真正可见，
  插件在请求截图前检查当前 provider，不支持则文本降级并提示（评审 M13）。
- **isError 不可依赖**：AstrBot 源码全树零处理 `isError`（已核实），插件必须保证失败文本以 `ERROR[CODE]:` 开头、
  成功以 `OK:` 开头（评审 B3/M13）。
- 截图结果固定附加隐私提示："桌面截图含用户隐私，未经用户明确要求不得转发"（评审 M5）。
- 永不把执行器内部术语（PostMessage/Invoke/升级）写进模型可见文本；细节进日志。
- **插件侧错误码**（执行器契约之外，PROTOCOL §8.1）：`EXECUTOR_UNREACHABLE/EXECUTOR_TIMEOUT/AUTH_FAILED/PROTOCOL_MISMATCH/CONNECTION_LOST`；
  动作类超时/断线按 `DISPATCH_UNCERTAIN`；文本按 token 预算裁剪（JSON 不可截断）；`message` 用中文、错误码保持英文（D12）。
- **隐私提示位置**：截图存在时放 **OK 摘要行最前**，避免与 AstrBot"可发图"提示冲突；**模型可见规则由插件注入**：`inject_usage_prompt`
  （默认 true）经 `on_llm_request` 注入 ≤15 行说明（`prompts/usage.md`，单测锁定，P1）（D10）。

### 6.6 配置 schema（初稿）

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `url` | string | `http://host.docker.internal:8765/mcp` | 执行器 MCP 地址 |
| `token` | string (secret) | "" | L1 Bearer Token（不加密存储，仅遮罩） |
| `timeout` | int | 45 | 单次调用超时秒（自动 clamp 到 AstrBot `tool_call_timeout`-5s 内） |
| `readonly` | bool | false | 只读模式：禁用 go_act/go_launch |
| `admin_only` | bool | true | 工具级管理员校验（`event.is_admin()` 兜底；默认 AstrBot 非内置工具权限是 member） |
| `allow_screenshot` | bool | true | 是否允许 go_observe 返回截图（Q4 默认开；执行器 `screenshot.enabled=true`、`defaultInclude=false` 不自动附图） |
| `inject_usage_prompt` | bool | true | 经 `on_llm_request` 注入 ≤15 行使用说明（P1；内容 `prompts/usage.md`） |
| `allowed_tools` | list[string] | 全量 | 白名单 |

### 6.7 命令

- `/whalego status`：连接状态/版本/会话/最近观察。
- `/whalego health`：调用 `go_health`。
- `/whalego stop`：kill switch。
- 权限以 `NAMING §4.3` 为唯一事实源：`status/health/stop/help` = **成员**（止血对成员开放）；`readonly on|off` = **管理员**；别名 `/wg`。
- **`resume` 不是远程命令**：`/whalego resume` 只返回"请到 Windows 托盘/设置窗口点恢复"的提示（恢复仅本机、二次确认，D2/D18）；
  `stop` 的模型面只停本会话（`scope=all` 仅管理员/本机 GUI 内部使用）。
- 命令用 `@filter.command`，不掺和 LLM 工具。

### 6.8 AstrBot 硬约束对策

| 约束 | 对策 |
|---|---|
| 全异步单事件循环 | 只用 async；MCP SDK 内部 httpx async；无同步 IO |
| 工具调用有超时（`tool_call_timeout`，默认 120s）、事件 handler 无超时 | 每次调用 `wait_for` 并 clamp；区分两类 handler |
| **不读 isError** | 失败文本强制 `ERROR[code]:` 前缀；成功 `OK:` |
| **非内置工具默认权限 = member** | `admin_only` 默认开启 + `event.is_admin()` 兜底 + README 配置指引 |
| **图片仅 modality 支持时注入** | 截图前检查 provider；不支持则文本降级 |
| 工具名 sanitize | 全小写下划线，无冲突 |
| 会话并发 | `asyncio.Lock`(umo) + 全局 semaphore |
| 配置 secret 不加密 | 不日志/不回显；README 明示风险 |
| 插件重装覆盖目录 | 数据写 `data/plugin_data/`（本项目基本无状态） |
| 单一事件循环阻塞 | 结果转换/大文本处理轻量；重活在执行器 |

---

## 7. 协议契约 v1（工具面）

> **完整契约以 `PROTOCOL.md` v1.2 为唯一事实源**（本节的摘要若与 PROTOCOL 冲突，以 PROTOCOL 为准）。
> 传输：MCP Streamable HTTP `POST /mcp`；所有工具返回 `CallToolResult`；
> **动作类成功 = `isError=false && dispatch_status="accepted"`；查询类与 `go_launch` 成功 = 首行 `OK:`**；失败/未发送/可能已发送一律 `isError=true` + 错误 JSON
> （动作类内嵌 receipt）；文本首行 `OK:`/`ERROR[CODE]:`。请求不携带 `schema_version`，版本由 `go_health.protocol_version=1` 协商。

### 7.1 工具清单（7 个必需，无 go_session）

| 工具 | 用途 | 关键参数 |
|---|---|---|
| `go_health` | 健康/能力/版本/协商 | — |
| `go_apps` | 列出应用 | `include_windows?` |
| `go_windows` | 列出窗口 | `app_id?` |
| `go_observe` | 唯一观察入口 | `window_id?`、`app_id?`、`include_screenshot?`、`settle_ms?`、`max_elements?`、`max_value_chars?` |
| `go_act` | 唯一动作通道 | `op`、**`target`（必填）**、`args?` |
| `go_launch` | 启动/激活应用（**写动作**；唯一启动方式 = allowlist `app`） | `app?`、`app_id?`（二选一；无 PATH/arguments） |
| `go_stop` | kill switch（**只停本会话**；恢复仅本机） | `reason?`；`scope` 仅内部/管理员 |

### 7.2 go_act 的 op（v1 收窄）

`click`（element/coordinate；left 单击）、`set_value`（element）、`type`（element, text）、`key`（element, 单键）、
`scroll`（element/coordinate；up/down）；**所有 op 必须有 `target`**，`type`/`key` 只接受 element target（D6）。
组合键/双击/右键/拖拽/粘贴/P2 动作 → `UNSUPPORTED_ACTION`；模型**不传** `frame_id`/`strategy`，坐标动作使用会话最近一次带截图观察的内部 frame。

### 7.3 观察返回（详见 PROTOCOL §3.6/§4）

返回 `content` 固定顺序：`[(可选) ImageContent, OK 摘要, observe 元数据 JSON, 文本树]`（图片最前，兼容按位置读图的网关）；
元数据含 `state_id`、`frame{image_width,image_height,client_width,client_height,scale_x,scale_y,capture_region:"client",actionable,capture_method}`
（**frame_id 仅内部，不下发模型**）、`elements[]`（含 `value_truncated`）、`omitted`、`advisories`（闭集）。示例见 `PROTOCOL.md §3.6`。

文本树（单独 TextContent，给模型读）示例：

```text
OK: 观察完成 window=w-00000000006AF2 state_id=st-8f21cd04 elements=123 screenshot=no
[window w-00000000006AF2] "计算器" app=win-exe:ab12cd34ef56 dpi=120 onscreen=true
  [6] button "7" actions=[invoke]
  [12] text "0" value="0"
… (omitted 4 elements: container=4)
```

### 7.4 动作回执与失败通道（详见 PROTOCOL §2/§3.7）

成功（唯一）：`isError=false` + receipt.action_receipt.dispatch_status=`accepted`，文本首行 `OK: 动作已发送...`。
失败：`isError=true` + `whalego-error-v1`（`not_sent` 或 `possibly_sent` 时内嵌完整 `action_receipt`），
文本首行 `ERROR[CODE]: ...`。三态语义：

| dispatch_status | 含义 | 调用方允许行为 |
|---|---|---|
| `not_sent` | 确定未进入原生派发 | 可按 `retry` 规则重试/升级 |
| `accepted` | 原生调用被接受（≠生效） | 不允许自动重放；可再观察验证 |
| `possibly_sent` | 可能已进入原生派发（超时/崩溃/中断） | **禁止自动重放**，必须重新观察 |

### 7.5 错误返回（详见 PROTOCOL §2.3/§8）

错误对象字段：`code/message/retry/retry_after_ms/details/action_receipt?(动作类)/diagnostic`；
**无 `is_error` 字段**（isError 是 MCP 层标志）。完整示例见 `PROTOCOL.md §2.3`。

---

## 8. 安全设计

### 8.1 威胁模型

| 威胁 | 影响 | 对策 |
|---|---|---|
| 容器/镜像内 token 泄漏 | 任何人可控桌面 | token 只存配置；最小权限账号运行；审计；可轮换；只读开关；`go_stop` |
| 网络暴露到 LAN/公网 | 被扫描利用 | 默认监听可配（建议 0.0.0.0 + 防火墙限定 docker/WSL 网段）；文档写清不要公网裸奔 |
| 提示注入驱动危险动作 | 误删/误发/误购 | 只读模式/确认策略/速率限制/动作白名单/kill switch |
| 截图/输入数据进第三方模型 | 隐私泄露 | day-1：只截目标窗口客户区、IsPassword 脱敏、敏感窗口默认不附图、自身窗口排除、截图默认开启可一键关闭、`defaultInclude=false` 不自动附图、预算压缩、日志不记明文（Q4） |
| 重放/重复动作 | 重复发送消息等 | possibly_sent 不自动重放；receipt 语义；幂等 op 才允许重试 |
| 模型/群员滥用（工具默认 member） | 桌面被他人驱动 | 插件 `admin_only` + `event.is_admin()` + WebUI 工具权限设 admin |
| 截图被模型转发到聊天 | 隐私外泄 | 插件固定隐私提示 + 敏感窗口默认不附图 + 截图默认开启可一键关闭（D9/Q4） |
| 自身/GUI 窗口被当作操作目标 | 误操作设置窗口/自检 | 执行器永不枚举/观察/截图 `WhaleGo.*` 窗口（D9） |
| 模型启动任意可执行文件 | 任意代码执行 | `launch.allowlist` 服务端白名单，只接受 `app`/`app_id`（D4） |
| 模型/容器解除止血 | 安全防线失效 | 暂停/停止恢复仅本机 GUI/托盘（二次确认），`resume` 不是远程命令（D2/D18） |
| 审计丢失 | 事故不可复盘 | 动作审计 fail-closed（返回前落盘）；观察审计丢计数告警 |

### 8.2 认证与网络

- L1 Token：32 字节 CSPRNG → base64url 前缀 `wg_`，服务端存 SHA-256（常数时间比较）；明文只在 `%LOCALAPPDATA%\WhaleGo\secrets.json`；轮换后旧 token 立即 401（D13）。
- 仅 `/mcp` 与 `/healthz`（healthz 也可要求 token）暴露；无其他路由。
- 容器访问：`host.docker.internal`（Docker Desktop 默认可用；Linux Docker 需 `--add-host`）。
- 防火墙：若连接被阻断，添加"仅允许 WSL/Docker 网段访问 TCP 8765"的入站规则（脚本提供，需管理员）。
- 升级路线（P3+）：TLS 自签名 + 指纹固定 / 反向代理 mTLS / L2 per-call token。

### 8.3 动作策略（插件侧为主）

- `readonly` 开关：禁用 `go_act`（执行器侧也支持只读配置，双保险）。
- **确认流程（P2，可选）**：命中配置的危险 op 时，用 AstrBot 按钮（`Comp.Button`）或 `session_waiter` 向用户确认；
  v1 用"只读开关 + 速率限制 + kill switch + 托盘/热键"兜底（用户已选全自动 + 审计）。
- 速率：执行器 `actRatePerMinute`（默认 120）+ 插件全局 semaphore(2)。
- `go_stop` 只能由模型"停"（**只停本会话**）；**恢复仅本机**：Windows 托盘/设置窗口二次确认（热键 `Ctrl+Alt+Shift+F12` 只停），
  模型与容器都不能解除止血（评审 M4/D2/D18）；状态落审计；自身窗口永不作为操作目标（D9）。
- **隐私**：UIA `IsPassword` 值脱敏、敏感窗口默认不附图、自身窗口排除、截图默认开启可一键关闭（`defaultInclude=false` 不自动附图）、结果附"不得转发"提示（D9/M5/Q4）。

---

## 9. 部署与运维

### 9.1 Windows 执行器

```powershell
# 构建（开发者）
cd windows; dotnet build -c Release
# 运行（当前用户交互会话）
src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe   # 默认托盘 + 首次配对向导；无头加 --headless
# 首启生成 config.json + secrets.json（明文 token）+ control-state.json + 审计目录；自检：--self-check --json
```

- 配对：托盘 → 设置 → "连接与配对" 5 步向导，一键复制插件配置（含 token）；启动方式 = **手动启动**（不自启，Q5）。
- 端口冲突/防火墙/权限/单实例冲突（退出码 2）→ `/healthz`（免认证最小面）与日志排障。

### 9.2 容器侧

1. 把插件目录复制到 `C:\\Users\\23500\\data\\plugins\\astrbot_plugin_whalego\\`（或 WebUI 上传 zip 安装）。
2. AstrBot WebUI → 插件管理 → 重载；配置里填 `url`（默认 `http://host.docker.internal:8765/mcp`）与 `token`（设置窗口「复制插件配置」获取，D13）。
   配对片段：`{ "url": "http://host.docker.internal:8765/mcp", "token": "wg_…", "allow_screenshot": true }`（Q4 默认已开）。
3. 验证：`/whalego health`，再让模型 `go_apps` / `go_observe`。
4. Docker Desktop 默认支持 `host.docker.internal`；如为 Linux Docker，需 `--add-host=host.docker.internal:host-gateway`。

### 9.3 排障清单

| 症状 | 排查 |
|---|---|
| 插件连接失败 | 容器内 `curl http://host.docker.internal:8765/healthz`；执行器是否在跑；防火墙；token |
| 工具调用超时 | 执行器日志/审计；`tool_call_timeout`；是否 observe 过大窗口 |
| 点击无效果 | 用 `go_observe` 验证；查看 receipt `dispatch_status`/`escalated`；目标 UIPI/前台；检查 frame 是否 `capture_degraded` |
| 动作返回 `PAUSED`/`SESSION_STOPPED` | 执行器被本机暂停/停止：到 Windows 托盘/设置窗口恢复（远程不能恢复） |
| 动作返回 `SESSION_LOCKED` | 桌面锁屏；解锁后重新 `go_observe`（`ok=false` 时树仍可读） |
| 图片看不到 | 确认 AstrBot 版本 ≥4.29.1（图片缓存支持）；插件返回 `CallToolResult` 而非纯文本 |

---

## 10. 开发与测试

### 10.1 仓库结构（最终）

```text
WhaleGo/
  README.md                  # 项目介绍/快速开始
  docs/                      # 本设计、协议、部署、开发
  windows/                   # C# 执行器 + WinForms 托盘/WPF 设置窗口（solution + src + tests）
  astrbot/                   # AstrBot 插件
  contracts/                 # 机器可读工具契约（单一事实源）
  scripts/                   # 构建/安装/E2E/防火墙脚本
  research/                  # 前期研究（只读）
  ref/                       # 从容器提取的 AstrBot 参考源码（只读，可删）
```

### 10.2 测试策略

1. **Contracts 单测**（纯逻辑）：树渲染/预算裁剪、坐标换算、receipt 状态机、错误码映射、会话新鲜度。
2. **Platform 单测**（Windows）：窗口枚举、UIA 遍历、截图非空、SendInput 参数构造（用夹具或 mock）。
3. **Executor 集成测试**：`WebApplicationFactory` 起 MCP 服务，用 MCP 客户端调工具（token 正/负、schema、错误路径）。
4. **E2E 夹具**：`WhaleGo.Fixtures.WinForms`（文本框/按钮/计数/状态），验证 UIA Invoke、PostMessage、SendInput、
   读回验证；断言"动作-观察-验证"闭环（对应研究报告 §11.8 的 first-green 用例，先做小集合）。
5. **AstrBot 插件单测**：mcp 兼容层/结果转换/错误映射/配置校验（`uv` venv + pytest，mock MCP 客户端）。
6. **端到端**：脚本起执行器 + 容器侧 Python MCP 客户端跑一遍 7 工具（不依赖 AstrBot 重启）。

### 10.3 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P0** | 协议骨架 + 认证 + health/apps/windows/observe/launch/stop（UIA 树 + PrintWindow 截图 + state 绑定 + kill switch） | 插件能连、列窗口、观察树与截图、launch 记事本（allowlist）；token 负例 401；go_stop 只停本会话 |
| **P1** | go_act 收窄子集（click-left/set_value/type/key 单键/scroll）+ 坐标 hit-test 链 + 回执三态 + 控制器租约 + 审计 fail-closed + WPF 设置窗口 v1（浅/深主题）/全局热键 | 夹具上 UIA/PostMessage/SendInput 三路径 + 计数 oracle；**另加 WPF + Edge + 高完整性窗口 + 用户中途切走**用例；STALE_STATE/CONTROLLER_BUSY/UNSUPPORTED_ACTION 负例 |
| **P2** | drag/paste、组合键/双击/右键、WGC 截图、UWP/WPF/Chromium 特例、确认流程、崩溃重启 | 宿主矩阵小集合通过 |
| **P3** | TLS/mTLS、L2 token、审计 UI、脱敏增强、多执行器 | 安全评审 |

### 10.4 实现阶段交付物（评审通过后）

- `windows/`：可构建可运行的执行器（P0+P1 工具面 + 托盘/热键 + WPF 设置窗口 v1（浅/深主题）+ `--headless`）、单测、WinForms 夹具与高完整性夹具。
- `contracts/whalego.tools.v1.json`：机器可读契约（两端共用/一致性检查）。
- `astrbot/astrbot_plugin_whalego/`：可安装插件（MCP 客户端 + 7 工具 + 配置/命令）、单测、README。
- `scripts/`：构建/运行/安装/E2E/防火墙脚本。
- `docs/`：DESIGN（本文）、ARCHITECTURE、PROTOCOL、NAMING、GUI、DEPLOY、DEV；根 README。
- 端到端验证记录：本机 + 容器内实测（附录 A 已覆盖关键链路，实现后再跑一遍）。

---

## 11. 实现阶段范围与验收（设计评审通过后）

- 范围：P0 + P1（见 §10.3）。

**验收夹具与 oracle（D19）**：每条验收必须写明 前置/夹具/oracle/预期输出，否则不算通过。

| 验收点 | 前置/夹具 | oracle / 预期 |
|---|---|---|
| 首启配对 | 空 `%APPDATA%\WhaleGo` + 新容器 | 5 步完成，`/whalego health` OK |
| 截图默认可用（Q4） | provider 支持 image + 默认配置（`screenshot.enabled=true`/`allow_screenshot=true`） | `include_screenshot=true` 返回图片；`defaultInclude=false` 时默认不自动附图 |
| 暂停可见 | GUI 点暂停 | `PAUSED` + `health.paused=true/accepting_actions=false` |
| 热键 3 秒停止 | 动作中按 Ctrl+Alt+Shift+F12 | ≤3s 内 `stopped`，后续调用 `SESSION_STOPPED` |
| 关闭窗口仍在托盘 | 关设置窗 | 进程/托盘在、模型仍可调、health OK |
| 读 500 字正文 | 记事本 500 字 + `max_value_chars=1000` | 能读回第 100 字之后内容 |
| 保存两支 | 有/无保存控件两条夹具路径 | 有控件可点则成功；无控件明确失败（不编造） |
| 焦点/两记事本不串窗 | 两窗口 + 切焦点 | 只改目标窗口 |
| 坐标换算 100/125/200% DPI | 同夹具三档 DPI | 换算误差 ≤1px；非客户区坐标拒绝（`not_sent`/`FRAME_NOT_ACTIONABLE`） |
| 双会话租约 | 两个 MCP 连接并发动作 | 非持有者 `CONTROLLER_BUSY` + `retry_after_ms` |
| 插件超时 DISPATCH_UNCERTAIN | 插件 timeout 调小 + 动作夹具 | 固定文案"可能已发送，禁止重试" |
| 审计只读目录负例 | 审计目录只读 | `AUDIT_UNAVAILABLE/not_sent` |
| 锁屏 SESSION_LOCKED | 锁屏 | 动作 `SESSION_LOCKED`；树仍可读 |
| 高完整性夹具 | `WhaleGo.Fixtures.Elevated`（管理员运行，P1 手工，不进 CI 主链路） | `UIPI_BLOCKED/not_sent` 零发送 |

- 验收标准：
  1. 插件在 AstrBot 4.29.3 容器内加载成功（无重绑定错误），7 个工具可见且可调用（容器内真实冒烟）；
  2. `go_health`/`go_apps`/`go_windows` 返回正确 JSON；`go_launch` 能启动记事本并返回窗口；
  3. `go_observe` 返回 `OK:` 摘要 + 元数据 + 文本树 + 可选截图（provider 支持 image 时模型可见）；
  4. 夹具上：元素 click（UIA Invoke）计数 +1、坐标 click（PostMessage/SendInput）计数 +1、set_value/type 读回一致；
     动作后旧 state 必须失效（`STALE_STATE` 负例）；
  5. 失败语义：无 token → 401；stale → `ERROR[STALE_STATE]`；只读 → `READONLY_MODE`；
     未支持动作 → `UNSUPPORTED_ACTION`；模型可见文本首行 `OK:`/`ERROR[...]`；
  6. 审计日志产生且不含明文输入/截图字节；动作审计在返回前落盘；
  7. 多会话：非持有者动作返回 `CONTROLLER_BUSY`；托盘/热键可停止；
  8. 文档齐全，可在 30 分钟内按文档部署到本机+容器。

---

## 12. 决策记录与遗留问题

### 12.1 已确认决策（2026-10-31 用户拍板；第 10–15 行为晚间 D21 GUI 六问补充）

| # | 决策 | 用户选择 | 说明 |
|---|---|---|---|
| 1 | Windows 执行器技术栈 | **C# / .NET 10** | 官方 MCP C# SDK + ASP.NET Core + FlaUI UIA3 + P/Invoke；本机可行性已实测（附录 A） |
| 2 | AstrBot 集成方式 | **插件做 MCP 客户端** | 插件注册 `go_*` 工具、控制图片/错误/只读/审计；内置 MCP 配置保留为兼容退路 |
| 3 | 网络与鉴权 | **监听 0.0.0.0 + Bearer Token 验证身份** | 不引入 TLS/隧道；token 只存配置、不进日志/工具参数；防火墙限定网段（部署文档） |
| 4 | 动作策略 | **全自动 + 审计** | 不做逐动作确认；提供只读开关、速率限制、`go_stop`、JSONL 审计兜底 |
| 5 | 工作节奏 | **先交付分层/封装/架构设计，不急着写代码** | 本文 + `ARCHITECTURE.md` + `PROTOCOL.md` 为设计交付物；用户评审后再实现 |
| 6 | 宿主形态（D15/D21 Q6） | **单进程托盘宿主 + WPF 设置窗口** | WinForms `NotifyIcon` 托盘 + WPF 设置窗口（同进程 UseWPF+UseWindowsForms）；`WhaleGo.Gui` 只引用 Contracts；`--headless` 无头 seam（§5.10） |
| 7 | 止血（D2） | **只读/暂停/停止三态；恢复仅本机** | 新增 `PAUSED`；全局停止持久化 `control-state.json` |
| 8 | 模型面权限（D1） | **会话由插件注入，模型不可见 session_id** | 内部参数标 `"internal": true` |
| 9 | token 与启动（D13/D4） | **明文 secrets.json + config 存哈希；launch allowlist 唯一启动方式** | 不接受 PATH/arguments（P2 再扩） |
| 10 | GUI 设置首屏（D21 Q1） | **A 概览**：状态 + 两个止血按钮 + 配对入口 | 用户 2026-10-31 晚拍板 |
| 11 | 关闭设置窗口（D21 Q2） | **A 隐藏到托盘** | 维持单进程/托盘常驻 |
| 12 | 止血档位（D21 Q3） | **A 两档**：暂停一键恢复 / 紧急停止二次确认 | 与 D2 三态止血一致 |
| 13 | 截图默认（D21 Q4） | **B 默认开** | `screenshot.enabled=true`、`allow_screenshot=true`；`defaultInclude=false`（默认不自动附图） |
| 14 | 自启（D21 Q5） | **不做**（手动启动） | 删除自启配置与启动项说明、GUI 自启端口及相关测试 |
| 15 | UI 外观（D21 Q6） | **WPF + 深色主题**，视觉参考 deepseek.com；**不做 WebUI/WebView** | 浅/深主题跟随系统 + 手动热切换；WinForms `NotifyIcon` 托盘 |

### 12.2 设计评审与修订

- 独立评审：`docs/reviews/design-review-01.md`（3 blocker + 13 major + 9 minor），结论"需修订后实现"。
- 修订记录：`docs/reviews/design-review-01-response.md`（逐条处理）。
- 复审结论：`docs/reviews/design-review-01-verdict.md`（"可进入实现"，4 项开工前小修已全部处理）。
- 命名：2026-10-31 项目定名 **WhaleGo**、工具前缀 **`go_`**（见 `docs/NAMING.md`）。评审文档中的 `PCCtrl`/`pc_*` 为历史名，未改动。
- 主要修订：插件工具注册改官方子类方式（B1）；Executor TFM 改 `net10.0-windows`（B2）；
  单失败通道 + 文本前缀（B3）；新鲜度/坐标 hit-test/frame.actionable（M1/M2）；新增 `go_launch`、收窄 v1 动作（M3/M10/M11/M12）；
  安全三件套（M4/M5/M6）；取消/租约/版本链（M7/M8/M9）；AstrBot isError/modality 兜底（M13）；m1-m9 清理。
- 第二轮审计：`docs/reviews/design-review-02.md`（0 blocker / 9 major / 10 minor）+ 场景走查 `workflow-trace-01.md`（5 场景）+ GUI 讨论稿
  `docs/GUI.md`；裁决 D1–D20 见 `design-review-02-response.md`。本轮：会话身份/三态止血/失败面/GUI 宿主/verification/观察可用性/验收夹具；
  版本 DESIGN v0.5 / ARCHITECTURE v0.5 / PROTOCOL v1.2 / GUI v0.3 / README v0.5（contract_version=1.2；D21 用户拍板见 §12.1）。

### 12.3 遗留问题（不阻塞设计评审，用户可后续拍板）

1. **托盘/设置窗口**：托盘 + WPF 设置窗口为 P1；启动方式 = **手动启动**（不自启，Q5）。
2. **只读默认值**：默认 `readonly=false`；暂停/停止恢复仅本机（D2）；如需默认只读改配置即可。
3. **目标应用优先级**：用于排宿主矩阵验收顺序（浏览器/Office/微信/游戏？）。
4. **确认交互**（P2）：AstrBot 按钮 `Comp.Button` vs `session_waiter` 文本确认，取决于目标平台能力。
5. **组合键/拖拽**：v1 明确 `UNSUPPORTED_ACTION`，P2 按 ZCode 真机矩阵逐项放开。
6. **Chromium/GPU 坐标**：v1 走元素路径，坐标按设计降级；WGC 单窗采集 P2 再验证（D8/D20）。
7. **SSE / 宿主 mcp 2.x 证据**：SSE 未验证（P3+）；`.spike` venv 已删，实现阶段复验并补 `docs/evidence/`（D20）。

---

## 附录 A. 可行性验证记录（2026-10-31 本机实测）

| # | 验证项 | 结果 |
|---|---|---|
| 1 | .NET SDK/运行时 | SDK 10.0.302/401；运行时 8.0.33、10.0.10/12；含 WindowsDesktop ✅ |
| 2 | NuGet 可达性 | api.nuget.org → nuget.azure.cn 镜像可用；MCP C# SDK 2.3.0、FlaUI 5.0.0 还原成功 ✅ |
| 3 | MCP C# 服务端 | ASP.NET Core + `ModelContextProtocol.AspNetCore 2.3.0`，`/mcp` 起 Streamable HTTP，工具 list/call 正常 ✅ |
| 4 | Bearer 中间件 | 错误 token 401；正确 token 通过 ✅ |
| 5 | 容器→宿主链路 | AstrBot 容器内 `mcp 1.30.0` 客户端经 `host.docker.internal:18765` 调通；text + image content 往返一致 ✅ |
| 6 | 容器 mcp API | 容器内有 `streamablehttp_client`（legacy，返回 3 元组）；新版为 `streamable_http_client`（2 元组）→ 插件需兼容层 ✅ |
| 7 | FlaUI UIA3 on .NET 10 | 枚举 WinForms 夹具：Edit/Button/CheckBox/Text、automation id、patterns(Value/Invoke/Toggle) 均可读 ✅ |
| 8 | UIA SetValue | ValuePattern 写入后读回一致 ✅ |
| 9 | UIA Invoke | 按钮触发点击，业务计数 +1 ✅ |
| 10 | PostMessage 点击 | 投递到按钮子 HWND，计数 +1 ✅ |
| 11 | SendInput 点击 | 物理点击，计数 +1（需前台） ✅ |
| 12 | SendInput 键入 | Unicode 逐字符输入，文本框读回 "SendInput-typed" ✅ |
| 13 | PrintWindow 截图 | 窗口 PNG 保存成功，~17-30ms ✅ |
| 14 | DPI | 本机 125%（GetDpiForWindow=120）；UIA bounds/PrintWindow rect 在样例窗口一致，按"物理像素"处理并在实现中用夹具验证 ✅ |
| 15 | AstrBot 工具返回图片 | 源码确认 `CallToolResult(ImageContent)` → 图片缓存并注入模型（4.29.3）✅ |
| 16 | AstrBot `add_llm_tools` | 自动解析 `handler_module_path`；普通 FunctionTool 实例需子类方式注册 ✅ |
| 17 | AstrBot 源码事实 | `isError` 全树零处理；非内置工具默认权限 = member；`tool_call_timeout` 默认 120s；图片仅 modality 支持时注入 ✅ |
| 18 | AstrBot 加载期重绑定 | `star_manager.py:1281-1311` 会重绑定 handler 所属插件工具 → 绑定方法注册会 TypeError（评审 B1）✅ |
| 19 | 容器 mcp 版本 | `docker exec AstrBot pip list`：`mcp 1.30.0`（3 元组 legacy 客户端）；原始输出见 `docs/evidence/container-env.md` ✅ |
| 20 | 设计独立评审 | 3 blocker + 13 major + 9 minor，已全部修订（`docs/reviews/`）✅ |

> 证据链：宿主 `.spike` venv 已删除，mcp 2.x 差异与 MCP C# SDK 2.3.0 待实现阶段复验并补 `docs/evidence/`；`mcp_protocol_version` 一律示例为"运行时协商值（如 `2025-06-18`）"（D20）。
> 未验证/风险：真实 Chromium/WPF/UWP 宿主矩阵、多屏负坐标、UIPI 高完整性窗口、长文本前台焦点恢复、
> 执行器崩溃恢复——按研究主报告 §11.8/§12.4 保留为 P2/P3 专项。

## 附录 B. 研究文档索引

- `research/zcode/ZCode电脑操控实现研究.md`（主报告，§0 速览/§2 架构/§3 工具面/§6 Windows/§9 MCP/§11 落地建议）
- `research/zcode/notes/01~06`（分题证据；实现 Windows 输入先读 04）
- `research/astrbot/AstrBot插件开发约束与能力边界.md`（插件硬约束与能力边界）
- `research/zcode/00-研究简报与协作约定.md`（研究背景/证据标准）
