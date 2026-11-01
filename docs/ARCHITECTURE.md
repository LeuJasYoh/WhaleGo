# WhaleGo 工程架构与分层封装设计（v0.5）

> 配套文档：`DESIGN.md`（v0.5，决策/选型/总体架构）、`PROTOCOL.md`（v1.3，MCP 工具面与 JSON 契约）、`docs/GUI.md`（v0.3，GUI 宿主）。
> 本文回答三个问题：**功能怎么分层、边界怎么封装、代码怎么组织**。先设计后实现；本文不写业务代码。
> 已确认的决策（2026-10-31）：Windows 执行器 = C#/.NET 10；AstrBot = Python 插件（MCP 客户端）；
> 网络 = 监听 0.0.0.0 + Bearer Token 认证；动作策略 = 全自动 + 审计；GUI = WPF 设置窗口 + WinForms NotifyIcon 托盘（同进程，手动启动、不自启）。

---

## 1. 设计原则与不变量

### 1.1 六条设计原则

| # | 原则 | 含义 | 落地方式 |
|---|---|---|---|
| P1 | **协议先于实现** | 工具契约（名称/参数/回执/错误）独立于传输和语言；执行器可被任意 MCP 客户端复用 | `PROTOCOL.md` 为单一事实源；C#/Python 两端各自映射，不互相复制业务规则 |
| P2 | **唯一事实源** | UI 观察状态只在执行器侧；插件和模型都不保存"UI 真相" | `SessionManager` 持有 state/frame；插件只缓存提示性 state_id |
| P3 | **窄接口 + 依赖倒置** | 业务层只依赖 Contracts 接口；平台细节被隔离、可替换、可 fake | `WhaleGo.Contracts` 无 Windows 依赖；`WhaleGo.Platform` 实现接口 |
| P4 | **失败关闭（fail-closed）** | 新鲜度/身份/权限任一不满足就拒绝；不自动补偿、不静默降级 | `STALE_STATE`/`WINDOW_CHANGED`/`UIPI_BLOCKED` 等错误码，统一 `retry` 语义 |
| P5 | **回执而非承诺** | `accepted` ≠ 生效；`possibly_sent` 不可重放 | 三态 `dispatch_status` + `retry_action`；域模型强制 |
| P6 | **薄接口、厚规则** | MCP 工具/命令层只做校验与转发；规则集中在 Domain/Application | 每个工具 handler ≤ 30 行；Domain 纯逻辑 100% 单测覆盖 |

### 1.2 十条硬不变量（实现时用测试/代码评审守护）

1. 任何**原生输入调用**前，目标必须来自当前会话最新观察（元素：`state_id + index`；坐标：会话内部 frame + 像素），
   否则返回 `STALE_STATE`/`ELEMENT_UNAVAILABLE`，**不自动补观察**。
2. 观察不进入输入 lane；输入的动作派发严格串行（每会话），观察可并发。
3. **Platform 层不产生用户/模型可见文案**（只回结构化结果与内部错误码）；**Executor/Tool 层不直接调用 Win32/UIA**。
4. 插件层**不缓存、不推断** UI 状态；只保存配置、连接、session 映射、最近 state_id 提示。
5. 错误必须携带 `retry` 语义；`dispatch_status=possibly_sent` 时 **`retry_action` 必须为 false**。
6. Token 只出现在 HTTP 头与执行器配置/凭据中；**不出现在工具参数、返回值、日志、审计**。
7. 模型可见文本不出现内部派发术语（`PostMessage`/`Invoke`/升级路径）；这些只进审计/诊断（receipt 模型面只留 `escalated`）。
8. 所有工具调用有**有界超时**：派发前可取消（→ not_sent）；一旦进入原生派发则不可撤销，走完并回
   `possibly_sent`。不承诺"已派发的动作可取消"。
9. 每个写动作必须**在返回前**完成审计落盘（fail-closed：审计失败 → `AUDIT_UNAVAILABLE` 且 not_sent），
   审计不含输入明文；观察审计可异步、丢计数进 health。
10. 执行器**不主动向网络外发数据**（无 telemetry/无远程上报）；只响应入站请求。
11. **GUI 不得绕过 Contracts/Facade**：`WhaleGo.Gui` 只引用 Contracts，仅经 `IExecutorControl` 等端口操作内核；禁止直接引用
    `WhaleGo.Platform`/`Microsoft.AspNetCore.*`，禁止直接碰 HWND/UIA/SendInput/审计写（架构测试守引用集，D15）。

### 1.3 封装边界总原则

- **DTO 过界，对象不过界**：跨层只传不可变记录（record）与原始值；平台句柄（HWND/UIA COM 对象）不出 Platform 层。
- **状态集中，句柄分散**：业务状态在 Domain/Session；平台句柄缓存只在 Platform 内部（按 state_id 索引，TTL+身份校验）。
- **接口朝内，实现朝外**：接口定义在 Contracts（被业务引用），实现放在 Platform（外层），依赖方向永远从外指向内。
- **一次性映射**：DTO ↔ MCP JSON ↔ 模型文本的转换各只有一处（`ResultShaper` / `convert.py`），禁止多处重复拼装。

---

## 2. 系统分层总览

### 2.1 横向分层（Windows 执行器）

```text
┌─ L7 UI（WinForms 托盘 + WPF 设置窗口）─────────────────────────────────┐
│ WinForms NotifyIcon 托盘 + WPF 设置窗口 + HotkeyWindow（同进程 UseWPF+UseWindowsForms；--headless 跳过）│
├─ L6 Transport/Interface ───────────────────────────────────────────────┐
│ MCP Streamable HTTP 端点、Bearer 认证、限流、负载预算、版本协商、/healthz │
├─ L5 Tool ──────────────────────────────────────────────────────────────┤
│ 7 个 MCP 工具（含 go_launch）schema/校验/调用 Application/结果塑形      │
├─ L4 Application/Services ──────────────────────────────────────────────┤
│ ObservationService / ActionService / WindowService / SessionManager /   │
│ HealthService / AuditService / ControlService / StatusService / SelfCheck│
├─ L3 Domain ────────────────────────────────────────────────────────────┤
│ 快照模型、Element 索引规则、新鲜度规则、receipt 状态机、策略（只读/限流） │
├─ L2 Platform Adapter ──────────────────────────────────────────────────┤
│ WindowCatalog / UiaEngine+UiaActions / Capture(PrintWindow→WGC) /       │
│ Input(PostMessage/SendInput/ForegroundGuard/PhysicalLane) / DPI / Clipboard│
├─ L1 Native Interop ────────────────────────────────────────────────────┤
│ P/Invoke 声明、COM 包装、结构体、句柄工具（internal only）               │
└────────────────────────────────────────────────────────────────────────┘
依赖方向：L7 → Contracts；L6 → L5 → L4 → L3 → Contracts ← L2（实现注入）；L1 只被 L2 引用；L7 不依赖 L5/L4。
```

**各层职责/输入/输出/禁止**

| 层 | 职责 | 输入 | 输出 | 禁止 |
|---|---|---|---|---|
| L7 UI | WPF 设置页/主题（浅/深热切换）、WinForms 托盘状态、止血/配对/自检呈现 | `IExecutorControl` 调用 | UI/命令 | 直连 Platform/AspNetCore、Win32、审计写 |
| L6 Transport | MCP 会话、认证、限流、payload 上限、协议版本 | HTTP 请求 | JSON-RPC 响应 | 业务状态、Win32、审计业务字段拼装 |
| L5 Tool | 工具 schema、参数校验（JSON Schema + 语义）、错误→MCP 结果 | JSON 参数 | `CallToolResult` | 直接访问 Platform、跨工具共享可变状态 |
| L4 Application | 编排用例、事务边界、会话/速率/只读、审计、缓存 | DTO/参数 | DTO/Domain 结果 | 原生调用、MCP 细节、文本渲染 |
| L3 Domain | 纯规则：索引/新鲜度/回执/错误映射/预算裁剪 | DTO | DTO/领域结果 | IO、时间（注入 IClock）、平台调用 |
| L2 Platform | 原生能力实现：窗口/UIA/截图/输入/DPI/剪贴板 | Contracts 接口调用 | 平台结果 DTO | 业务规则、模型文案、审计写入（只回事件） |
| L1 Native | P/Invoke/COM/结构体 | — | — | 被 L3/L4/L5/L6 直接引用 |

### 2.2 横向分层（AstrBot 插件）

```text
┌─ L4 Star Adapter（main.py）────────────────────────────────────────────┐
│ Star 子类、生命周期（initialize/terminate）、工具注册、/whalego 命令、   │
│ AstrBot 事件/配置/权限适配                                              │
├─ L3 Tool Facade（tools.py）────────────────────────────────────────────┤
│ 7 个 FunctionTool 子类（显式 schema + call()）、参数预处理、结果返回     │
├─ L2 Application（client.py / session.py / policy.py / convert.py）─────┤
│ 连接与调用编排、umo→session、只读/限流、MCP 结果→AstrBot 结果、错误映射  │
├─ L1 Protocol Adapter（mcp_client.py / compat.py）──────────────────────┤
│ mcp SDK 版本兼容、streamable HTTP、超时、重连、CallToolResult 读写       │
├─ L0 Foundation（config.py / errors.py / logging.py）───────────────────┤
│ 配置校验、错误类型、日志（不 import astrbot）                            │
└────────────────────────────────────────────────────────────────────────┘
依赖方向：L4 → L3 → L2 → L1 → L0。
> 修订：L0-L2 **不 import astrbot**（可用 uv+pytest 脱离宿主测试）；L3（tools.py）**允许** import
> `astrbot.core.agent.tool`（FunctionTool 基类）、`astrbot.core.agent.run_context`（ContextWrapper）、
> `astrbot.core.astr_agent_context`（AstrAgentContext）与消息类型，但**不允许**碰生命周期/事件总线；L4 负责接线。
```

> 这条"L4 之外不 import astrbot"的规则，是为了让 90% 的插件逻辑可以在 `uv` venv + pytest 里测试，
> 而不需要启动整个 AstrBot。

---

## 3. Windows 执行器：分层与封装

### 3.1 项目与依赖矩阵

```text
WhaleGo.Contracts (net10.0)        ← 无平台依赖；DTO + 接口 + 错误码 + 枚举
WhaleGo.Platform  (net10.0-windows) → 引用 Contracts；FlaUI.UIA3、P/Invoke
WhaleGo.Executor  (net10.0-windows) → 引用 Contracts + Platform + Gui；ASP.NET Core + MCP C# SDK + WPF/WinForms 组合根
   （TFM 必须是 windows：net10.0 项目无法引用 net10.0-windows 的 Platform；单测也只在 Windows 跑）
WhaleGo.Gui       (net10.0-windows) → 只引用 Contracts；WPF 设置窗口 + WinForms NotifyIcon 托盘（UseWPF+UseWindowsForms）；禁引 Platform/AspNetCore
tests/...                         → 各自引用对应项目
```

| 引用关系 | Contracts | Platform | Executor |
|---|---|---|---|
| Contracts | — | 被引用 | 被引用 |
| Platform | ✔ | — | ✘（Executor 不直接引用 Platform 类型） |
| Executor | ✔ | ✔（仅 DI 注册/组合根） | — |

组合根（`Program.cs`）是唯一允许同时 `using WhaleGo.Platform` 与 Executor 的地方；
L5/L4/L3 代码只依赖 Contracts 接口。Gui 只被组合根引用，且只允许 `using WhaleGo.Contracts`（架构测试守引用集）。

### 3.2 Contracts：数据模型（关键 DTO）

```csharp
namespace WhaleGo.Contracts.Model;

public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

// ── 身份/窗口 ────────────────────────────────────────────────
public sealed record AppInfo(
    string AppId,                 // "win-exe:<sha1(exe 路径)[:12]>" 或 "win-aumid:<aumid>"（无 #N 后缀）
    string Name,
    int? Pid,
    string? ExecutablePath,
    string? AumId,
    bool IsPackaged,              // UWP/WinUI3 packaged
    bool IsForeground,
    IReadOnlyList<WindowInfo> Windows);

public sealed record WindowInfo(
    string WindowId,              // 稳定标识："0x" + HWND(64bit) + 可选 generation
    string AppId,
    string Title,
    string ClassName,
    int Pid,                      // 顶层窗口 owner pid（UWP 可能是 ApplicationFrameHost）
    bool IsOnscreen,
    bool IsMinimized,
    bool IsForeground,
    bool IsEnabled,
    RectI Bounds,                 // DWM extended frame bounds（屏幕物理像素）
    string GeometrySource,        // "dwm" | "win32"
    uint Dpi,                     // GetDpiForWindow
    long CapturedAtMs);

// ── 观察 ────────────────────────────────────────────────────
public sealed record ObservationSnapshot(
    string SchemaVersion,         // "whalego-observe-v1"
    string SessionId,
    string StateId,               // "st-xxxxxxxx"
    AppInfo App,
    WindowInfo Window,
    WindowResolution Resolution,  // Exact | MainFallback（禁止静默降级）
    IReadOnlyList<ElementNode> Elements,
    OmissionInfo Omitted,
    FrameInfo? Frame,
    IReadOnlyList<string> Advisories,
    long CapturedAtMs);

public sealed record ElementNode(
    int Index,                    // 观察后分配的稳定索引（文本树与 elements 共用）
    string Kind,                  // button/edit/text/listitem/...（ControlType 归一化）
    string? Title,                // Name
    string? Value,                // ValuePattern/TextPattern 提取（截断）
    string? AutomationId,
    IReadOnlyList<string> Actions, // 可用动作名（invoke/toggle/select/expand/set_value/scroll）
    bool Enabled,
    bool Selected,
    RectI BoundsScreenDiag,       // wire bounds_screen_diag；诊断用，禁止当坐标
    int Depth);

public sealed record OmissionInfo(
    int OmittedCount,
    IReadOnlyDictionary<string, int> ByKind,
    string Reason,                // "budget" | "depth" | "none"
    bool SparseIndices);

public sealed record FrameInfo(
    string FrameId,               // "fr-xxxxxxxx"
    int Width, int Height,
    double Dpr,                   // 物理像素 / 逻辑像素
    bool Actionable,              // 是否可作为坐标动作目标
    string CaptureMethod,         // "printwindow" | "wgc"
    long CapturedAtMs);

// ── 动作 ────────────────────────────────────────────────────
public enum DispatchStatus { NotSent = 0, Accepted = 1, PossiblySent = 2 }

public enum TargetKind { Element = 0, Coordinate = 1 }

public sealed record ActionTarget(
    TargetKind Kind,
    int? ElementIndex = null,
    int? X = null, int? Y = null,
    string? StateId = null,
    string? FrameId = null);

public sealed record ActionRequest(
    string SchemaVersion,         // "whalego-act-v1"
    string SessionId,
    string Op,                    // v1: click|set_value|type|key|hotkey|scroll（P2 扩展）
    ActionTarget Target,          // 必填：click/set_value/type/key/scroll 均须 target（D6）
    IReadOnlyDictionary<string, object?> Args,
    string Strategy,              // "auto" | "event"（本期忽略 a11y，保留字段）
    string? RequestId);

public sealed record ActionReceipt(
    string SchemaVersion,
    string SessionId,
    string Op,
    bool ActionSent,
    DispatchStatus DispatchStatus,
    bool RetryAction,
    DispatchInfo? Dispatch,       // 内部元数据（模型侧可简化展示）
    VerificationInfo Verification,
    bool StateAvailable);         // 本期恒 false：要状态就再 observe

public sealed record DispatchInfo(string Method, bool Escalated, string? WindowId, int? ElementIndex);
public sealed record VerificationInfo(string Status /* verified|unverified|mismatched */, string? Detail);

// ── 错误 ────────────────────────────────────────────────────
public enum RetryHint { Never = 0, Retry = 1, Reobserve = 2 }

public sealed record GoError(
    string SchemaVersion,         // "whalego-error-v1"
    string Code,
    string Message,               // 面向模型的一句话（可操作，不含内部术语）
    RetryHint Retry,
    bool ActionSent,
    DispatchStatus DispatchStatus,
    IReadOnlyDictionary<string, object?>? Details,
    IReadOnlyDictionary<string, object?>? Diagnostic);

// ── 控制/GUI（Contracts；GUI 只依赖这些 + IExecutorControl）──────────────
public enum ControlState { Running = 0, Paused = 1, Stopped = 2, Faulted = 3 }
public sealed record StatusSnapshot(ControlState State, bool AcceptingActions, bool Locked, long UptimeMs,
    string? Version, string? ProtocolVersion);
public sealed record RecentRequest(long Ts, string Tool, string? Op, string Result, int ElapsedMs, string? ErrorCode);
public sealed record SelfCheckReport(bool Ok, IReadOnlyList<SelfCheckItem> Items);
public sealed record ConfigPatch(IReadOnlyDictionary<string, object?> Values);   // 本地 camelCase 键
```

### 3.3 Contracts：接口（封装缝）

```csharp
namespace WhaleGo.Contracts.Abstractions;

// ── 平台能力（由 WhaleGo.Platform 实现；业务层只依赖这些）──────────────
public interface IWindowCatalog
{
    IReadOnlyList<AppInfo> ListApps(ListAppsOptions options);
    IReadOnlyList<WindowInfo> ListWindows(string? appId);
    WindowInfo? TryGetWindow(string windowId);
    bool IsForeground(string windowId);
}

public interface IObservationEngine
{
    // 采集纯数据快照；内部把平台句柄缓存登记到 stateId 下（TTL + 窗口身份校验）
    ObservationSnapshot Observe(ObserveOptions options);
}

public interface IElementActionDispatcher
{
    // 用 (stateId, index) 解析平台句柄并执行；找不到/过期 → ElementUnavailable/StateUnknown
    Task<PlatformActionOutcome> ExecuteAsync(
        string stateId, int elementIndex, string action,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct);
}

public interface ICoordinateActionDispatcher
{
    // 用 (frameId, x, y) 执行；frame 过期/窗口几何变化 → StaleFrame
    Task<PlatformActionOutcome> ExecuteAsync(
        string frameId, int x, int y, string action,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct);
}

public interface IScreenCapture
{
    FrameInfo? Capture(string windowId, CaptureOptions options);
}

public interface IUiHitTester          // 坐标动作的派发路径分类输入
{
    HitTestResult HitTest(string windowId, int screenX, int screenY);
}

public interface IAppLauncher          // go_launch
{
    Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct);
}

public interface IInputLane            // 物理输入串行化（SendInput/激活/剪贴板）
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

public interface ISystemProbe         // 会话/完整性/锁屏/DPI/交互会话/互斥等环境事实（health 暴露 locked 与 os.windows_session_id）
{
    SystemFacts GetFacts();
}

public interface IPlatformClock { long NowMs { get; } }

// ── GUI/控制端口（GUI 只经这些端口；实现见 §3.5）────────────────────────────
public interface IExecutorControl
{
    StatusSnapshot GetStatus();  IReadOnlyList<RecentRequest> GetRecentRequests(int limit);
    IReadOnlyDictionary<string, long> GetToolCounters();
    void Pause(string reason);  void Resume();  void Stop();
    ConfigPatch GetConfig();  void SetConfig(ConfigPatch patch);
    string RotateToken(string name);  string RevealTokenForCopy();  SelfCheckReport RunSelfCheck();
    IReadOnlyList<string> ReadAuditTail(int lines);  void OpenDataFolder();
    void RequestRestart();
}
// GUI 不写 P/Invoke：由 Platform 实现、组合根注入（D22）
public interface IWindowTheme { bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark); }
public interface IHotkeyRegistrar { bool TryRegister(string id, string gesture, Action onPressed); void Unregister(string id); }
public interface IConfigStore { ConfigPatch Current { get; }  void Save(ConfigPatch patch);  string? SourceOf(string key); }
public interface ITokenService { string Generate();  string Rotate();  bool Verify(string presented); }
public interface ISelfCheck { SelfCheckReport Run(); }

public interface IAuditSink
{
    ValueTask WriteAsync(AuditEvent e, CancellationToken ct);
}

// ── 平台动作结果（Platform → Application，不含文案）────────────────
public sealed record PlatformActionOutcome(
    bool Sent,                    // 是否进入原生调用
    bool Uncertain,               // 调用后失败/超时 → possibly_sent
    string Method,                // uia | post_message | send_input | none
    bool Escalated,
    string? FailureCode,          // UIPI_BLOCKED / FOREGROUND_REQUIRED / ELEMENT_UNAVAILABLE / ...
    string? FailureDetail);
```

> **关键封装决策**：平台层在 `Observe` 时把 UIA 元素句柄缓存在自己的 `StateHandleStore` 中（`stateId → {windowId, pid, geometry, dpi, elements[]}`）。
> 业务层只持有字符串 `stateId`；动作时把 `(stateId, index)` 交给平台，平台做二次身份/几何校验并解析句柄。
> 这样既避免 COM 对象跨层泄漏，又避免"重新遍历树找第 N 个元素"的不确定性。

### 3.4 Domain 规则（纯逻辑，可 100% 单测）

```text
Domain/
  Observation/
    ObservationShaper.cs      # 裁剪（按 kind/深度/交互性）+ 分配稳定 index + 渲染文本树
    ObservationBudget.cs      # max elements / max chars / max image bytes
    TextTreeRenderer.cs       # 模型可读文本（唯一渲染点）
  Action/
    ActionValidator.cs        # op/参数/目标/策略校验
    ReceiptFactory.cs         # PlatformOutcome → ActionReceipt（含三态规则）
    RetryPolicy.cs            # 错误码 → RetryHint（possibly_sent 强制 Never）
  Session/
    SessionState.cs           # last state/frame 指针 + 新鲜度判定（纯函数）
    Freshness.cs              # 时间/窗口身份/几何/DPI/owner 变化规则
  Policy/
    ReadOnlyPolicy.cs / RateLimitPolicy.cs
  Errors/
    GoErrorFactory.cs         # 内部原因 → 稳定错误码 + 模型可读 message
```

**动作状态机**（Domain 层强制）：

```text
[Received]
  → Validate(schema/op/args)            失败 → INVALID_ARGUMENT (Never, NotSent)
  → CheckSession                        失败 → SESSION_* (Never, NotSent)
  → CheckControl/ReadOnly/RateLimit     失败 → PAUSED/SESSION_STOPPED/READONLY_MODE/RATE_LIMITED (Never, NotSent)
  → ResolveTarget
       element: CheckFreshness(stateId)  失败 → STALE_STATE (Reobserve, NotSent)
       coord:   CheckFrame(frameId)      失败 → STALE_STATE (Reobserve, NotSent)
  → [Dispatch]
       platform out: Sent=false           → 失败映射（Never/Retry/Reobserve, NotSent）
                     Sent=true, ok        → Accepted (unverified, retry=false)
                     Sent=true, Uncertain → PossiblySent (Never, retry=false)
  → Audit + Receipt
```

**观察状态机**：

```text
ObserveRequest → 解析窗口（app_id/window_id/前台主窗）
  → 窗口身份/可见性校验（失败：WINDOW_NOT_FOUND/WINDOW_CHANGED）
  → UIA 遍历（硬上限 2000 节点/2s 预算，超限截断 + sparse indices）
  → 可选截图（只截客户区；预算缩放/压缩；失败不影响树，advisories 标注）
  → Domain 裁剪 + 渲染 + 分配 index → 生成 stateId（新观察顶替旧 state；无截图不清 frame，D8）
  → SessionManager.Capture(snapshot) + Audit(observe)
  → 返回（文本树 + elements JSON + 可选 image）
```

### 3.5 Application 服务

| 服务 | 职责 | 依赖 | 关键方法 |
|---|---|---|---|
| `SessionManager` | 会话生命周期、最新快照、controller lease、限流计数、kill switch | IClock、IAuditSink | `GetOrCreate`、`Capture`、`CheckFresh`、`AcquireControllerLeaseAsync`、`Stop` |
| `AppLaunchService` | go_launch：启动/激活 + 有界等待窗口 | IAppLauncher、IWindowCatalog、ObservationService | `LaunchAsync`（启动/激活） |
| `WindowService` | apps/windows 查询 + 窗口解析（app_id→窗口） | IWindowCatalog | `ListApps`、`ListWindows`、`ResolveTarget` |
| `ObservationService` | 观察编排（窗口解析→平台采集→Domain 裁剪→会话登记→文本树） | IWindowCatalog、IObservationEngine、IScreenCapture、SessionManager | `ObserveAsync` |
| `ActionService` | 动作编排（校验→派发→回执→审计） | SessionManager、IElementActionDispatcher、ICoordinateActionDispatcher、IInputLane | `ActAsync` |
| `HealthService` | 版本/会话/能力/完整性/锁屏/controller/audit 丢弃计数 | ISystemProbe、SessionManager、AuditService | `GetHealth` |
| `AuditService` | 审计组装/脱敏/落盘 | IAuditSink | `RecordAction`、`RecordObserve` |
| `PolicyService` | 只读/速率/危险 op 策略 | Options | `Check` |
| `ControlService` | GUI 端口实现：状态/最近请求/工具计数/暂停/恢复/停止/自检/配置（含 `ui.theme`）（IExecutorControl） | SessionManager、StatusService、ConfigStore、TokenService、ISelfCheck | `Pause`/`Resume`/`Stop`/`RunSelfCheck` |
| `StatusService` | 托盘 5 态 + health 派生（运行/暂停/停止/异常、锁屏、租约、审计丢弃） | SessionManager、ISystemProbe | `Snapshot` |
| `SelfCheckService` | 自检（启动/配对/端口/审计目录/热键）→ `--self-check --json` | ISelfCheck、IConfigStore | `Run` |
| `ConfigStore`/`TokenService` | 配置合并/来源追踪/校验/持久化（camelCase）；`wg_` token 生成/轮换/哈希校验/secrets | IConfigStore、ITokenService | `Current`/`Save`/`Rotate`/`Verify` |

**ActionService 伪代码**（体现边界）：

```csharp
public async Task<ActionReceipt> ActAsync(ActionRequest req, CancellationToken ct)
{
    var session = sessions.GetOrCreate(req.SessionId);
    var plan = validator.Validate(req, session, options, facts);       // Domain，纯逻辑
    await using var lease = await session.AcquireInputAsync(ct);       // 全局输入租约（跨会话唯一）
    var outcome = plan.Target.Kind == TargetKind.Element
        ? await elementDispatcher.ExecuteAsync(plan.StateId, plan.Index, plan.Op, plan.Args, ct)
        : await coordinateDispatcher.ExecuteAsync(plan.FrameId, plan.X, plan.Y, plan.Op, plan.Args, ct);
    var receipt = ReceiptFactory.From(plan, outcome, clocks.NowMs);    // Domain
    audit.RecordAction(plan, outcome, receipt);                        // 异步、失败不影响主流程
    return receipt;
}
```

### 3.6 Platform 层实现要点（封装在 L2，内部可自由拆分）

```text
Platform/
  Windows/
    WindowCatalog.cs         EnumWindows + 进程 exe + AUMID + UWP 三重 pid + DWM bounds + DPI + 前台
    WindowResolver.cs        app_id→候选窗口（main_fallback 需显式标记，禁止静默）；自身窗口（WhaleGo.*）过滤（D9）
    WindowActivator.cs       激活/置前（AttachThreadInput + SetForegroundWindow 的受限实现）
  Apps/
    AppLauncher.cs           启动 exe / 激活已有应用；有界等待主窗口出现（go_launch）
  Uia/
    UiaEngine.cs             FlaUI UIA3：根元素、遍历、属性安全读取（ValueOrDefault+try）
    UiaTreeBuilder.cs        节点→UiaNode（kind/title/value/actions/bounds/id/depth）+ 硬上限
    UiaActionExecutor.cs     模式梯：Invoke→Toggle→SelectionItem→ExpandCollapse→Value/Scroll/TextRange
    UiaHitTester.cs          ElementFromPoint + 窗口域枚举（坐标动作的派发路径分类）
    StateHandleStore.cs      stateId → {windowId,pid,geometry,dpi,elements[]}；TTL+身份校验
  Capture/
    CaptureCoordinator.cs    策略选择：printwindow → (P2) wgc；缩放/编码/预算
    PrintWindowCapture.cs
    CaptureValidator.cs      非空/非纯黑/几何一致/最小化检查 → frame.actionable 判据
    FrameCoordinateMapper.cs image 像素 → client 像素换算（比例/四舍五入）+ frame 字段组装（D8）
    WgcCapture.cs            (P2)
  Privacy/
    Redactor.cs              UIA IsPassword → "***"；敏感窗口匹配 → 截图抑制（screenshot_suppressed）
  Input/
    InputLane.cs             进程内串行队列（SemaphoreSlim(1) + 优先级：物理动作）
    SendInputDispatcher.cs   SendInput 鼠标/键盘/Unicode；光标定位；失败映射
    PostMessageDispatcher.cs 子 HWND 定向消息（点击/移动/WM_CHAR/WM_KEYDOWN）；返回 SendResult
    ForegroundGuard.cs       EnableWindow shield / NoActivateGuard / 条件恢复 / 键盘排水
    KeyChordParser.cs         "ctrl+c"/"enter" → 键序列
  System/
    SystemProbe.cs           会话/完整性级别/锁屏/多屏/DPI/进程信息
    DpiAwareness.cs          Per-Monitor V2 manifest + 坐标换算
  PlatformFacade.cs          组装上述实现 → Contracts 接口（DI 注册）
```

**输入升级链（MVP）**：

```text
element click:
  actions 包含 invoke → UIA Invoke
  else toggle/select/expand → 对应 pattern
  else → ELEMENT_UNAVAILABLE（不猜测）
coordinate click:
  ① UIA hit-test（ElementFromPoint 主级 + 窗口域枚举次级）→ 得到宿主分类
  ② 宿主分类（class name 前缀/进程名）→
       deliverable/unknown: PostMessage(childHwnd, WM_LBUTTON*, client coords) → 若明确失败且 not_sent → 升级
       droppable（如 Chromium 顶层）: 直接前台 sendinput（需目标已前台；否则 FOREGROUND_REQUIRED）
  ③ 前台 SendInput：激活 + 光标定位 + down/up + 条件恢复 + 键盘排水
type(element, text):
  元素路径优先 WM_CHAR/ValuePattern；需前台但无法安全激活 → FOREGROUND_REQUIRED 零发送（D6）
key(element, 单键):
  元素 target 定向 PostMessage(WM_KEYDOWN/UP) / 必要时前台 SendInput；不向"当前焦点窗口"盲发；
hotkey(element, keys):
  组合键（ctrl/alt/shift/win + 字母/数字/F 键/命名键）；前台 SendInput 一次提交；
  回执 unverified；无黑白名单；win/alt+f4 等全部放开（v1.3）
scroll:
  元素 → UIA ScrollPattern；坐标 → WPF/WinUI3 SB 码族后台 / 其余前台滚轮
```

> 注意：升级/宿主矩阵是**实现细节**，写在 Platform 内部；对外只暴露 `Method/Escalated` 元数据。
> MVP 先实现"小集合"（WinForms/WPF/记事本/Edge）并在审计里记录路径；矩阵逐步扩（对齐研究 §6.2）。

### 3.7 Transport 与 Tool 层

- **MCP 端点**：`/mcp`（Streamable HTTP，官方 C# SDK），`/healthz`（GET，免认证最小面，`listen.healthzRequireToken` 可要求 token）；
  payload `{ok,version,protocol_version,uptime_s,paused,stopped,accepting_actions}`（D3）。
- **认证**：自定义 ASP.NET Core 中间件（在 `MapMcp` 之前）校验 `Authorization: Bearer`；常数时间比较 SHA-256；
  401 时返回 `WWW-Authenticate: Bearer`；不记录 token。
- **限流**：`RateLimiter` 中间件按 token 名 + IP 计数（默认观察 60/min、动作 120/min）；超限 429 + Retry-After。
  同时 Domain 的 `RateLimitPolicy` 做动作级限流（防单会话刷）。
- **Payload 预算**：观察结果文本 ≤ `maxObserveChars`；截图 base64 ≤ `maxImageBytes`；工具结果超限截断并标注。
- **7 个工具**：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`（无可选工具）。
- **单失败通道**：动作类成功 = `isError=false && dispatch_status="accepted"`；查询类与 `go_launch` 成功 = 首行 `OK:`；`not_sent`/`possibly_sent` 一律
  `isError=true` + 错误对象内嵌 `action_receipt`；文本首行强制前缀 `OK:`/`ERROR[<code>]:`（AstrBot 不读 isError，
  必须靠文本前缀让模型识别失败）。
- **图片位置**：ImageContent 放 content 数组最前（部分网关按位置读图）。
- **Tool 层示例**（`go_observe`，薄层）：

```csharp
[McpServerToolType]
public sealed class GoTools(ObservationService observation, ActionService actions, ...)
{
    [McpServerTool(Name = "go_observe"), Description("...")]
    public async Task<CallToolResult> Observe(
        [Description("...")] string? windowId,
        [Description("...")] string? appId,
        [Description("...")] bool? includeScreenshot,
        [Description("...")] int? maxElements,
        [Description("...")] int? maxValueChars,
        CancellationToken ct)   // session_id 由插件的 MCP 连接身份注入（模型面不出现）
    {
        var result = await observation.ObserveAsync(new ObserveOptions(...), ct);
        return ResultShaper.ToCallToolResult(result);   // 文本树 + JSON + 可选 ImageContentBlock
    }
}
```

- **错误塑形**：Application 抛 `WhaleGoToolException(GoError)` → Tool 层统一转
  `CallToolResult { IsError = true, Content=[TextContent("ERROR[code]: ..."), TextContent(json)] }`；
  **业务失败不用 HTTP 5xx**；`possibly_sent` 也走此通道（错误对象内嵌 receipt）。
- **会话身份**：内部参数 `session_id`（插件注入 umo 派生值；模型面 schema 不出现）→ MCP 会话身份 → `"default"`；解析顺序与清理规则见 §3.11。
- **版本协商**：`go_health.protocol_version = 1`（整数）+ `mcp_protocol_version`（SDK 运行时协商值，如 `2025-06-18`，D20）；
  服务端拒绝未知大版本（`UNSUPPORTED_PROTOCOL`）；请求本身不携带 schema_version。

### 3.8 配置 / 审计 / 日志

- 配置加载（低→高）：`appsettings.json` < `%APPDATA%\WhaleGo\config.json` < 环境变量 `WHALEGO_*` < 命令行；GUI 显示来源徽标、
  环境变量覆盖项只读；绑定 `IOptions<WhaleGoOptions>`（不可变）。敏感分离：`config.json` 只存 `auth.tokens[].{name,sha256,created_at}`，
  明文 token 在 `%LOCALAPPDATA%\WhaleGo\secrets.json`（用户 ACL）；全局停机态在 `%LOCALAPPDATA%\WhaleGo\control-state.json`（D13/D14）。
- 审计：`JsonlAuditSink`（`Channel<AuditEvent>` 后台消费）；
  **写动作审计在返回前落盘（fail-closed）**：队列满/磁盘不可写 → `AUDIT_UNAVAILABLE` 且 not_sent；
  观察审计可异步、丢弃计数进 `go_health.audit_dropped`。字段见 `DESIGN.md §5.7`；
  **输入文本只记长度 + SHA-256 前 8 位**。
- 日志：`Microsoft.Extensions.Logging`，可选 `Serilog` 文件 sink；级别/文件轮转可配；健康检查不含敏感信息。
- wire/本地映射（单点转换，禁止散落）：`window_id↔windowId`、`app_id↔appId`、`state_id↔stateId`、`session_id↔sessionId`、
  `max_value_chars↔maxValueChars`、`image_width↔imageWidth`、`retry_after_ms↔retryAfterMs`、`bounds_screen_diag↔boundsScreenDiag`；
  `healthzRequireToken`/`actRatePerMinute` 等本地键即 camelCase（wire 无对应则不加映射）。
- UI/隐私键（本地，非 wire，D21）：`ui.theme`（`system|light|dark`，默认 `system`，热切换）、`ui.notificationsLevel`；`screenshot.enabled=true`、
  `screenshot.defaultInclude=false`（默认不自动附图）、插件 `allow_screenshot=true`（Q4）；自启配置组已删除（手动启动，Q5）。

### 3.9 并发与生命周期

| 资源 | 并发规则 | 实现 |
|---|---|---|
| 观察 | 允许并发（不同窗口），受全局 semaphore(4) 约束 | ObservationService |
| 动作 | **每会话串行**（输入不能交错）；跨会话通过**全局 controller lease** 串行 | `GoSession.InputGate` + `IInputLane` + `ControllerLease` |
| 控制权 | 同一时刻仅一个会话持有输入租约（TTL 60s，动作续期）；他人 → `CONTROLLER_BUSY` | `ControllerLease`（Application） |
| 平台句柄缓存 | 按 stateId 索引，LRU + TTL(10min) | StateHandleStore |
| 审计 | 单写者后台队列；**动作类写前排队、返回前落盘** | JsonlAuditSink |
| MCP 连接 | 每连接独立会话；断线清理会话与租约 | SDK 会话生命周期 + SessionManager |
| kill switch | 全局/会话原子标志；取消排队动作并清租约；模型 `go_stop` 只停本会话（`scope=all` 仅内部/管理员/GUI） | SessionManager.Stop |

执行器启动顺序：单实例互斥 → 加载配置（解析来源）→ 生成/读取 token（首启写 secrets）→ 内核绑定端口/注册路由 → 初始化 WinForms 托盘/WPF 设置窗口
（应用 `ui.theme`；`--headless` 跳过）→ 恢复 `control-state`（全局 stopped）→ 注册热键（失败显式告警）→ ready（快速 ready，重活懒加载，避免 AstrBot MCP 初始化超时）。

### 3.10 契约同步（单一事实源）

工具契约不能"两边各写一份然后祈祷不漂移"。设计一个机器可读的契约文件作为单一事实源：

```text
contracts/
  whalego.tools.v1.json      # 7 工具名字/描述/JSON Schema；contract_version="1.3"；internal 参数断言（D1/D17）
  README.md                 # 如何修改契约、如何跑一致性检查
```

| 使用方 | 用法 |
|---|---|
| 执行器 | Tool 层代码声明 schema；**集成测试**用 `tools/list` 结果与契约文件做归一化比对（CI 失败即漂移） |
| 插件 | 启动时从包内 `contracts/whalego.tools.v1.json` 构造 `FunctionTool`（不手抄 schema）；连接成功后拉一次 `tools/list` 对比，漂移只告警不阻塞（兼容旧/新执行器） |
| 脚本 | `scripts/check-contract.ps1`：起执行器 → `tools/list` → 与契约文件 diff；提交前/CI 跑 |

为什么不"完全动态"（插件直接消费服务端 `tools/list`）：AstrBot 要求插件加载时就有工具；执行器离线时动态注册会失败。
静态 schema（来自契约文件）+ 启动时一致性检查，兼顾"离线可用"与"不漂移"。**internal 参数断言**：`session_id`/`request_id`/`scope`
必须标 `"internal": true` 且不得出现在模型面 `tools/list` schema（CI 失败即违规）；contract 与 PROTOCOL v1.3 做 diff。

### 3.11 会话身份与请求生命周期

**会话键解析顺序**：内部参数 `session_id`（插件注入，`"wg-"+sha256(umo)[:16]`，contracts 标 `"internal": true`）→ MCP 会话身份
（C# SDK 的 `McpServer.SessionId`；工具方法通过注入 `McpServer`/`RequestContext` 获取；实现时以 SDK 2.3.0 实际注入方式为准）→ `"default"`；
**模型不可见/不可传**。MCP 连接关闭时释放该连接绑定的所有业务会话与 controller lease；重连递增会话 **epoch**，旧 state 一律 `STALE_STATE`（D1/D12）。

**控制权**：**全局输入租约（跨会话唯一）**在进入派发前获取、动作续期（TTL 60s，等待 `policy.controllerWaitMs`）；非持有者 →
`CONTROLLER_BUSY`（`retry=retry` + `retry_after_ms=min(剩余,5000)`，不使 state/frame 失效）；暂停/停止/退出释放租约（D11）。

**原子性**：一个工具调用 = 一个 Application 方法 = 一个事务边界（审计/状态变更在同一方法内完成）。

**请求生命周期**（所有工具）：

```text
HTTP 请求 → 认证 → MCP 反序列化 → Tool(L5) → Application(L4) → Domain(L3) 校验
  → 平台调用（可取消点：遍历/等待/前置检查；一旦进入原生派发则忽略取消，走完并回 possibly_sent）
  → 结果塑形 → MCP 响应
并发护栏：全局 semaphore（观察 4 / 动作 2）+ 每会话输入锁 + 审计异步队列
```

**超时预算**（默认值，可配）：

| 环节 | 预算 | 超时行为 |
|---|---|---|
| 观察-树遍历 | 2s（硬上限，超时截断 + sparse） | 返回部分树，`advisories=["tree_budget_exceeded"]` |
| 观察-截图 | 3s | 无图返回（树仍返回），`advisories=["screenshot_failed"]` |
| 动作-元素 | 5s | 未派发 → `ACTION_TIMEOUT/not_sent`；已派发 → `possibly_sent` |
| 动作-键入 | 5s + 20ms/字符 | 同上；中途失败 → `possibly_sent` |
| 工具整体 | 执行器 **≤40s**；插件 `timeout` 45s（clamp 到 `tool_call_timeout-5s`） | 取消请求；插件超时后**不重发**；动作类按 `DISPATCH_UNCERTAIN` 呈现（D12） |

### 3.12 验证与读回语义

`verification` 只描述"服务端能自证的迹象"，**永不改变 dispatch_status**；闭集 `{verified,mismatched,unverified}` 与每 op 规则以
**PROTOCOL §3.7 为唯一事实源**（下表为摘要，D5）：

| op | 读回方式 | verified 条件 | 失败时 |
|---|---|---|---|
| `set_value` | ValuePattern.Value 读回 | == 请求值 | `mismatched`（仍 accepted） |
| `type`（元素） | ValuePattern/TextPattern 读回 | 包含请求文本 | `mismatched`（仍 accepted，只允许 reobserve） |
| `key`/`click`/`scroll` | 默认无 | — | 恒 `unverified` |

`mismatched` 只是"写后读不一致"的事实，调用方应重新观察；**不得**据此自动重放（可能已写入另一形态）。

### 3.13 扩展点

| 想扩展 | 改哪里 | 不需要改 |
|---|---|---|
| 新增动作 op | Domain `ActionValidator` + Platform `UiaActionExecutor`/`SendInputDispatcher` + PROTOCOL.md | Transport/Tool schema 仅加枚举 |
| 新增宿主特例（如 GTK） | Platform `HostClassifier` + dispatch 矩阵 | Domain/Plugin |
| 换截图实现（WGC） | 实现 `IScreenCapture`，DI 切换 | 其他所有层 |
| 换传输（stdio/SSE） | Transport 层注册 | Tool/Domain/Platform |
| 新增会话策略（确认） | Application `PolicyService` + Plugin policy | Platform |
| 多执行器/多机器 | 无需改执行器；插件侧多连接配置 | — |

### 3.14 测试 seam 与测试分层

| 层 | seam | 测试方式 |
|---|---|---|
| Domain | 纯函数/无 IO | 单测覆盖索引/新鲜度/receipt/错误映射/预算 |
| Application | 注入 fake `IObservationEngine`/`I*Dispatcher`/`IClock`/`IAuditSink` | 单测用例矩阵（正常/过期/只读/限流/possibly_sent） |
| Platform | 真实 Win32/UIA，但用例用夹具窗口 + 条件断言 | 集成测试（Windows only），失败可跳过非交互环境 |
| Transport/Tool | `WebApplicationFactory` + MCP 客户端 | 端到端工具调用（token/正负例/schema/错误路径） |
| Plugin | L0-L2 不 import astrbot（mock MCP client）；L3 子类可 import `astrbot.core.agent.tool/run_context/astr_agent_context` | pytest（uv venv）+ 容器内真实加载/调用冒烟 |
| GUI/headless | WPF 设置窗口 + WinForms 托盘；`--headless` + 注入 fake `IExecutorControl`；设置页/主题解析不依赖窗口句柄 | 单测状态映射/配置校验/自检/主题切换（system/light/dark）；GUI 冒烟（Windows only） |
| 全链路 | 起执行器 + 容器/宿主 Python MCP 客户端 | E2E 脚本（health→apps→observe→act→observe 验证） |

---

## 4. AstrBot 插件：分层与封装

### 4.1 模块与依赖规则

```text
astrbot/astrbot_plugin_whalego/
  metadata.yaml
  main.py                 # L4：Star 子类 + 工具实例化/注册 + 命令 + 生命周期
  _conf_schema.json
  requirements.txt        # mcp>=1.30,<2（AstrBot 4.29.3 按 3 元组解包，2.x 会破坏；见评审 M9）
  whalego/
    __init__.py
    config.py             # L0：dataclass 配置 + 校验（不 import astrbot）
    errors.py             # L0：PluginError/ToolError 类型 + 模型文案
    compat.py             # L1：mcp SDK 版本/字段兼容（streamablehttp_client vs streamable_http_client；snake/camel）
    mcp_client.py         # L1：连接、ClientSession、call_tool、超时、重连、关闭
    client.py             # L2：WhaleGoClient —— 对 7 个工具的强类型封装 + 结果转换编排
    session.py            # L2：umo→session_id、每 umo 锁、最近 state_id 提示（不缓存 UI 状态）
    policy.py             # L2：只读/限流/工具白名单
    convert.py            # L2：MCP CallToolResult → AstrBot CallToolResult（文本/图片/错误）
    tools.py              # L3：7 个 FunctionTool 子类（显式 schema + call()；允许 import agent.tool/run_context/astr_agent_context）
```

规则：
- `main.py` 只做接线：构造 `WhaleGoClient`、**实例化工具子类并调用 `context.add_llm_tools(...)`**、注册命令、生命周期；≤200 行。
- `tools.py` 的 `call()` 只做：参数组装 → client 调用 → `convert.to_astrbot_result`；≤40 行/工具。
- **工具注册必须用官方推荐的子类方式**（见 §4.2）：`@dataclass class GoObserveTool(FunctionTool[AstrAgentContext])`
  + `async def call(self, context, **kwargs)`，`handler=None`。**禁止** `FunctionTool(handler=插件绑定方法)`：
  AstrBot 加载期会把 `handler.__module__ == 插件模块` 的工具重绑定为 `partial(raw, star_cls)`，绑定方法会被
  错误错位成 `(star_cls, event)` → TypeError；且 `handler_module_path` 解析错导致插件禁用后工具仍 active
  （评审 B1，证据 `ref/AstrBot/star/star_manager.py:1281-1311`、`star/context.py:109-122`）。
- `client.py` 是插件唯一"懂协议"的模块；其他模块不解析 MCP JSON 结构。
- `compat.py` 是唯一处理 mcp 版本差异的地方：容器 1.30 的 `streamablehttp_client(headers=...) → 3 元组`；
  新版 `streamable_http_client(http_client=httpx2...) → 2 元组`；字段 camel/snake 差异。

### 4.2 关键类与流程

```python
# mcp_client.py（L1）
class McpConnection:
    async def connect(self) -> None            # 建立 streamable HTTP + ClientSession
    async def call(self, tool: str, args: dict, timeout: float) -> mcp.types.CallToolResult
    async def close(self) -> None
    @property
    def state(self) -> ConnectionState          # Disconnected/Connecting/Ready/Backoff

class ReconnectLoop:  # 指数退避（1s→2s→…→30s），可取消；连接成功回调工具状态
```

```python
# client.py（L2）
class WhaleGoClient:
    async def health(self) -> dict
    async def apps(self, include_windows: bool = False) -> dict
    async def windows(self, app_id: str | None = None) -> dict
    async def observe(self, session_id, *, window_id=None, app_id=None, include_screenshot=False,
                      max_elements=None, max_value_chars=None) -> ToolCallOutcome
    async def act(self, session_id, op, *, target, args=None) -> ToolCallOutcome     # target 必填
    async def launch(self, session_id, *, app=None, app_id=None) -> ToolCallOutcome  # 无白名单，app 或 app_id 二选一
    async def stop(self, session_id, reason=None) -> ToolCallOutcome                 # 只停本会话

@dataclass
class ToolCallOutcome:
    ok: bool
    text: str                    # 已塑形、模型可读
    images: list[ImageContent]   # 原样保留
    error: GoError | None
```

```python
# tools.py（L3）—— 官方推荐子类方式（关键：handler=None，不触发加载期重绑定）
from pydantic import Field
from pydantic.dataclasses import dataclass
from astrbot.core.agent.run_context import ContextWrapper
from astrbot.core.agent.tool import FunctionTool, ToolExecResult
from astrbot.core.astr_agent_context import AstrAgentContext

@dataclass
class GoObserveTool(FunctionTool[AstrAgentContext]):
    name: str = "go_observe"
    description: str = OBSERVE_DESC
    parameters: dict = Field(default_factory=lambda: OBSERVE_SCHEMA)
    client: Any = None                      # 注入：WhaleGoClient
    sessions: Any = None                    # 注入：SessionMap
    allow_screenshot: bool = True           # 注入：配置（Q4 默认开）

    async def call(self, context: ContextWrapper[AstrAgentContext], **kwargs) -> ToolExecResult:
        event = context.context.event
        sid = self.sessions.for_umo(event.unified_msg_origin)
        outcome = await self.client.observe(
            sid, window_id=kwargs.get("window_id"), app_id=kwargs.get("app_id"),
            include_screenshot=kwargs.get("include_screenshot", False) and self.allow_screenshot,
            settle_ms=kwargs.get("settle_ms"), max_elements=kwargs.get("max_elements"),
        )
        return convert.to_astrbot_result(outcome)   # mcp.types.CallToolResult
# main.py（L4）：self.context.add_llm_tools(GoObserveTool(client=..., sessions=..., allow_screenshot=...), ...)
```

> `call()` 的签名是 `(context: ContextWrapper, **kwargs)`；AstrBot 的本地执行器在 `method_name == "call"` 时按
> `handler(context, **kwargs)` 调用（`ref/AstrBot/astr_agent_tool_exec.py:813-818`）。

### 4.3 连接与调用流程

```text
插件加载 initialize()
  → 校验配置 → McpConnection.connect()（失败：记录并启动 ReconnectLoop，不阻塞 AstrBot 启动）
工具调用
  → policy.check(tool)（白名单/只读/限流）
  → session.lock(umo) 获取串行锁
  → client.call → mcp_client.call(tool,args,timeout=45s)
       ├─ 成功 → convert（文本预算/图片/错误）→ CallToolResult
       ├─ 连接错 → 触发重连（只读工具可重试一次；动作工具不重发）
       └─ 超时 → 动作类按 DISPATCH_UNCERTAIN（"可能已发送，禁止重试"）；只读类可重试一次（D12）
  → audit/log（不含 token/明文）
```

### 4.4 配置与命令

- 配置 schema 见 `DESIGN.md §6.6`；启动时 `Config.from_astrbot_config()` 做类型/范围校验，非法值回退默认并告警。
- 命令：`/whalego status|health|stop|help`（成员）、`readonly on|off`（管理员）、`resume`（**只返回"请到本机托盘恢复"提示**）；
  `@filter.command`，权限以 `NAMING §4.3` 为唯一事实源；不设"统一 `permission_type(ADMIN)`"兜底（D18）。
- 工具权限（已核实）：AstrBot 对**非内置工具默认权限是 `member`**（`ref/AstrBot/provider/func_tool_manager.py:453-458`），
  不是 admin。插件必须：① 配置 `admin_only: true`（默认），在 `call()` 内 `if not event.is_admin(): return 权限错误`；
  ② README 指导管理员在 WebUI → 扩展 → 组件里把 7 个工具权限设为 admin；③ `metadata` 不依赖 `support_platforms`。
- 内置 MCP 备选路径下 AstrBot 不读 `isError`、工具名无前缀、默认成员可用——README 必须写明风险。

### 4.5 错误与结果转换（convert.py 唯一入口）

| 执行器返回 | 插件转换 | 模型看到 |
|---|---|---|
| 正常 receipt（accepted） | 文本 JSON + 一句"动作已发送，是否生效需再观察" | 可理解 + 可继续 observe |
| `isError/stale` | "观察状态已过期，请重新 go_observe" + JSON | 可操作 |
| `isError/readonly` | "当前为只读模式" | 明确原因 |
| `isError/uipi` | "目标窗口权限更高，无法操作" | 不暴露内部术语 |
| 连接失败 | "执行器不可达（已重连 N 次），请检查 Windows 端" | 用户可排查 |
| 图片 content | `ImageContent` 原样透传（不转文字） | AstrBot 缓存并注入（仅 provider 支持 image 时；`tool_loop_agent_runner.py:1107-1142`） |
| **失败文本前缀** | 统一以 `ERROR[CODE]:` 开头 + 结构化 JSON | AstrBot 不读 isError，模型靠前缀识别失败 |
| **成功文本前缀** | 统一以 `OK:` 开头 | 明确成功语义 |
| **隐私提示** | 截图结果固定附加"未经用户要求不得转发"（放 OK 摘要行最前） | 抵消 AstrBot 的"可发图"提示 |
| 插件侧：连接失败 | `EXECUTOR_UNREACHABLE`（已重连 N 次，检查 Windows 端） | 用户可排查 |
| 插件侧：动作超时/断线 | `DISPATCH_UNCERTAIN`："可能已发送，禁止重试，先 go_observe" | 不重发、不误导 |
| 插件侧：只读超时/认证 | 只读重连重试一次→`EXECUTOR_TIMEOUT`；401/协议不匹配→`AUTH_FAILED`/`PROTOCOL_MISMATCH` 不重试 | 可操作 |

### 4.6 并发与生命周期

- 每 umo 一个 `asyncio.Lock`（弱引用字典，空闲清理）；全局 `Semaphore(2)` 限制并发调用。
- `initialize()` 里 `asyncio.create_task(reconnect_loop)`；`terminate()` 取消任务 + `await connection.close()`。
- handler 内不创建任务、不 sleep；所有等待受 `asyncio.wait_for` 约束。

### 4.7 测试 seam

- L0-L2 不依赖 astrbot：pytest 直接构造 `WhaleGoClient`（mock `McpConnection`）、`convert`、`policy`、`session`；
  L3 工具子类用最小 astrbot stub 或容器内冒烟测试。
- `mcp_client` 的版本兼容：对 fake transport 跑两种响应形状（camel/snake、2/3 元组）。
- E2E：宿主机起执行器，venv 里跑真实 MCP 客户端脚本（不需要 AstrBot）。
- **容器内真实冒烟（必须）**：把插件装进 AstrBot 容器 → 加载无异常 → `tools/list` 可见 7 个工具 →
  用真实模型/或直接调工具 → 验证注册/重绑定/权限/图片链路（mock 不足，B1 教训）。

---

## 5. 跨端契约（摘要）

完整版见 `PROTOCOL.md`。核心约定：

- 工具名固定 7 个：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`。
- 所有工具返回 `CallToolResult`；**动作类**成功 = `isError=false && dispatch_status="accepted"`；**查询类**与 `go_launch`（写动作、
  无 dispatch_status）成功 = `isError=false` 且首行 `OK:`；失败/未发送/可能已发送一律 `isError=true` + `whalego-error-v1`
  （动作错误内嵌 `action_receipt`）；文本首行 `OK:` / `ERROR[CODE]:`（AstrBot 不读 isError；唯一事实源 PROTOCOL §2/§8）。
- 观察返回：摘要 + JSON 元数据 + 文本树 + 可选图片（ImageContent 放 content 最前）。
- 动作只返回回执；要状态就再 observe；`state_available=false`；动作后该会话 state/frame 立即失效。
- `session_id` 是插件注入的内部参数（umo 派生 `wg-<sha16>`，模型不可见/不可传）；缺省回退 MCP 会话身份；跨会话输入由全局输入租约串行。
- `state_id` 由服务端生成、模型在 `target` 回传；**frame 绑定内部化，模型不传 frame_id**。

---

## 6. 目录结构与工程规范

```text
WhaleGo/
  README.md
  docs/
    DESIGN.md              # 决策/选型/总体架构
    ARCHITECTURE.md        # 本文（分层/封装/接口/状态机/测试 seam）
    PROTOCOL.md            # 工具面与 JSON 契约 v1.3（唯一事实源）
    GUI.md                 # 托盘 + WPF 设置窗口宿主契约（v0.3；浅/深主题）
    DEPLOY.md              # 部署/网络/防火墙/排障
    DEV.md                 # 构建/运行/测试/贡献
  windows/
    WhaleGo.sln
    Directory.Build.props
    src/WhaleGo.Contracts/  src/WhaleGo.Platform/  src/WhaleGo.Executor/  src/WhaleGo.Gui/
    tests/WhaleGo.Contracts.Tests/  tests/WhaleGo.Platform.Tests/
          WhaleGo.Executor.Tests/   WhaleGo.Gui.Tests/   WhaleGo.Fixtures.WinForms/   WhaleGo.Fixtures.Elevated/
  astrbot/astrbot_plugin_whalego/
  contracts/               # 机器可读工具契约（单一事实源，见 §3.10）
  scripts/                 # build.ps1 / run-executor.ps1 / install-plugin.ps1 / e2e.ps1 / firewall.ps1 / check-contract.ps1
  research/                # 前期研究（只读）
```

规范：
- C#：`net10.0`/`net10.0-windows`；`Nullable=enable`；`TreatWarningsAsErrors`（测试除外）；
  `Directory.Build.props` 统一 `LangVersion=latest`、`AnalysisLevel`；公共类型 `public`，内部实现 `internal`；
  `using` 顺序/格式化用 `dotnet format`；异常只用 `WhaleGoToolException(GoError)` 跨层。
- Python：`ruff` 格式化/检查；类型注解全覆盖；`from __future__ import annotations`；async-only。
- 命名：工具名/字段 snake_case（对模型）；C# 类型 PascalCase、字段 camelCase（JSON 序列化用 `snake_case` 策略）；
  Python 模块 snake_case。
- 提交：小步提交，每个 PR 只动一个分层（Contracts/Platform/Executor/Plugin/Docs），便于回滚。

---

## 7. 实现任务分解（后续 Agent Teams 分工草案）

| 任务 | 范围（write scope） | 依赖 | 验收 |
|---|---|---|---|
| T1 Contracts+Domain | `windows/src/WhaleGo.Contracts/**`、`windows/tests/WhaleGo.Contracts.Tests/**`、`contracts/**` | — | DTO/接口/状态机/裁剪渲染单测全绿；契约文件 `contract_version="1.3"` 定稿；测试项目 `net10.0-windows` |
| T2 Platform-观察 | `windows/src/WhaleGo.Platform/{Windows,Uia,Capture,System}/**` | T1 | 夹具窗口枚举/UIA 树/截图/state 缓存 |
| T3 Platform-输入 | `windows/src/WhaleGo.Platform/{Input,Uia/UiaHitTester,Apps,Privacy}/**` | T1,T2 | UIA/PostMessage/SendInput 三路径 + hit-test 夹具通过 |
| T4 Executor+守护 | `windows/src/WhaleGo.Executor/**`（含托盘/全局热键/P1 止血 + 组合根注入 IExecutorControl） | T1（可先 fake） | MCP 工具/认证/会话/租约/审计/E2E 脚本 |
| T5 AstrBot 插件 | `astrbot/**` | PROTOCOL v1.3 | 容器内真实加载 + 7 工具 + 权限/图片/错误冒烟 |
| T6 文档/脚本/E2E | `docs/**`、`scripts/**`、`windows/tests/**` | T1,T4 并行 | 文档可照做部署；E2E 全绿；D19 夹具（Fixtures.Elevated/焦点 watcher/双 MCP 连接/审计只读注入/provider 无 image 桩） |
| T7 GUI（WinForms 托盘+WPF 设置窗口） | `windows/src/WhaleGo.Gui/**`、`windows/tests/WhaleGo.Gui.Tests/**`、`docs/GUI.md` | T1,T4 | WPF 设置窗口（浅/深主题热切换、参考 deepseek 视觉）/WinForms 托盘/5 步配对/6 页设置/止血 5 态/`--headless` 自检全绿；不做 WebUI；GUI 只引用 Contracts（架构测试） |

> 实现阶段由 Lead 维护契约与最终合并；T2/T3 与 T4 通过 Contracts 解耦，可并行；写 scope 互不重叠。

---

## 8. 风险与待验证项（设计层面）

| # | 风险/待验证 | 影响 | 缓解 |
|---|---|---|---|
| 1 | PrintWindow 对 Chromium/GPU 窗口可能黑屏 | 观察质量 | P2 切 WGC（Windows.Graphics.Capture）；失败时 advisories 标注 |
| 2 | UIA 树遍历大窗口慢/超预算 | 响应时间/超时 | 硬上限 + 2s 预算 + 深度/类型裁剪 + 缓存同 state 的句柄 |
| 3 | PostMessage 在部分宿主静默失败（droppable 矩阵） | 点击无效 | 宿主分类 + 读回 oracle + 升级链 + 审计记录路径；矩阵先小集合验证 |
| 4 | UWP/ApplicationFrameHost 的 PID 分裂 | 身份错误 | 用 HWND 做 owner 校验；UIA root pid 只做参考（研究 §6.5） |
| 5 | 高 DPI/多显示器坐标 | 点偏 | Per-Monitor V2 + 全链路物理像素 + frame 同源 + 夹具测试 125%/200% |
| 6 | AstrBot 工具超时/取消语义 | 动作中断/possibly_sent | 插件 wait_for 超时后**不重发**；执行器动作超时返回 possibly_sent |
| 7 | AstrBot 版本差异（图片/工具注册） | 插件不可用 | 目标锁定 4.29.3（当前容器）；启动时版本检查 + 兼容层 |
| 8 | MCP Python SDK 新旧 API 差异 | 插件启动失败 | `compat.py` 适配两种客户端签名/字段（本机已实测差异） |
| 9 | 容器→宿主防火墙/网络 | 连不上 | 文档 + 脚本；`/healthz` 自检；Docker Desktop 默认可用（已实测） |
| 10 | 动作确认缺失导致误操作 | 安全 | 全自动+审计为默认（已确认），并保留 readonly 开关/速率/kill switch；P2 加确认 |
| 11 | 单进程混合 UI（WPF 设置窗口 + WinForms 托盘）异常拖垮内核 | 被控能力中断 | UI 调用 async+超时+try/catch、三处全局异常兜底、Kernel 先 ready 再建 UI、崩溃日志（D15/Q6） |
| 12 | 高完整性/UIPI 夹具难自动化 | 验收缺口 | `WhaleGo.Fixtures.Elevated` 管理员手工 P1，不进 CI 主链路（D19） |
| 13 | AstrBot 不读 `isError`（已核实零处理） | 模型把失败当成功 | 文本首行强制 `OK:`/`ERROR[code]:`（PROTOCOL §2）；插件转换层兜底 |
| 14 | 非内置工具默认权限 = member（已核实） | 群员可驱动桌面 | 插件 `admin_only` 默认 + `event.is_admin()` 兜底 + README 指导配置 |
| 15 | AstrBot 提供 `send_message` 工具，可外发截图 | 隐私 | 插件隐私提示 + 敏感窗口默认不附图 + 截图默认开启可一键关闭 + README 风险声明 |
| 16 | MCP Python SDK 版本漂移（容器 1.30 vs 宿主 2.x） | 插件连不上 | `requirements.txt` 固定 `mcp>=1.30,<2`；`compat.py` 双路径；容器内 E2E |
| 17 | 多会话抢同一桌面 | 动作交错 | controller lease（TTL 60s）+ `CONTROLLER_BUSY` + 单机单用户说明 |
| 18 | 审计丢失与"必有审计"矛盾 | 无法复盘 | 动作审计 fail-closed（返回前落盘）；观察审计可丢并计数告警 |

---

## 附录：一张图记住分层

```text
        模型 (AstrBot)
             │ go_* 工具
      ┌──────▼──────┐   MCP/Streamable HTTP + Bearer Token
      │ AstrBot 插件 │   L4 main / L3 tools / L2 client / L1 mcp SDK
      └──────┬──────┘
             │
   ══════════▼══════════ 网络 ═══════════
             │
      ┌──────▼───────────┐
      │ WhaleGo.Executor  │   L7 UI / L6 transport / L5 tools / L4 services / L3 domain
      │   （业务+协议）    │
      └──────┬───────────┘
             │ Contracts 接口（依赖倒置）
      ┌──────▼───────────┐
      │ WhaleGo.Platform  │   L2 窗口/UIA/截图/输入/DPI
      └──────┬───────────┘
             │ P/Invoke / COM
      ┌──────▼───────────┐
      │ Windows 原生 API  │   user32/kernel32/dwmapi/UIAutomationCore/GDI+/WGC
      └──────────────────┘
```
