# WhaleGo Windows 端设置 GUI 设计（v0.3，用户拍板定稿）

> 状态：**v0.3 —— 用户拍板定稿**。GUI 六问的结果见 `docs/reviews/design-review-02-response.md` §6 D21（本文 §13 同步）。
> 本文是 GUI 实现规格：**WPF 设置窗口（浅/深两套主题）+ WinForms `NotifyIcon` 托盘，同进程**；D21 引起的跨文档改动清单见 §11.2（本文只改自身）。
> 基线：设计/架构 **v0.5**、协议 v1.2、contracts 1.2（2026-10-31）；执行器 = C#/.NET 10 WinExe，必须跑在用户交互会话。
> 命名：一律 WhaleGo / `go_*` / `WhaleGo.*`；历史评审里的 PCCtrl / pc_* 是旧名，本稿不沿用。
> 标记约定：**已裁决** = 用户/Lead 已拍板；**已落地** = 主文档（DESIGN/ARCHITECTURE/NAMING/README v0.5、PROTOCOL v1.2）已同步；**新增** = 现有文档没有的键/端口。

**一句话方案**：在 `WhaleGo.Executor` 单进程内，用 **WPF** 做「WhaleGo 设置」窗口（浅/深主题、圆角卡片、清爽排版、150ms 过渡），
用 **WinForms `NotifyIcon`** 做系统托盘（同进程 `UseWPF`+`UseWindowsForms`，零第三方 NuGet）；托盘常驻、双击打开设置，
设置只做四件事：**首次配对、随时止血、看状态、改配置**。**不做开机自启（手动启动），不做 WebUI / WebView。**

## 1. 结论先行：方案与关键取舍

一句话方案见文首。展开成 10 条取舍（都写代价，不写“没有缺点”的方案）：

| # | 取舍 | 选择 | 代价 / 不选的后果 |
|---|---|---|---|
| 1 | GUI 技术栈（已裁决 D21） | **WPF 设置窗口 + WinForms NotifyIcon 托盘**（同进程，零第三方 NuGet） | XAML/样式/动画人力约 2×；但这是“要好看 + 深色”的必然成本 |
| 2 | 主题 | **浅/深两套 ResourceDictionary + 跟随系统/手动切换，热生效** | 需自建 token 体系、双主题回归与对比度验收（§10.2 / §10.7） |
| 3 | 进程模型 | **单进程**（托盘 GUI 内嵌执行器内核） | UI 未捕获异常可能拖垮内核 → async 调用 + 全局异常兜底 + 崩溃日志 |
| 4 | 关闭窗口 | **隐藏到托盘**；真正退出只在托盘菜单 | 用户可能忘了它还在跑 → 托盘状态醒目（§5）+ 退出确认（§4.3） |
| 5 | 止血 | **三态：只读 / 暂停 / 停止**（GUI 两个按钮：暂停、紧急停止） | 协议已有 `PAUSED`/`SESSION_STOPPED`；恢复一律仅本机（§9.1） |
| 6 | token 明文 | `%LOCALAPPDATA%\WhaleGo\secrets.json`（仅当前用户 ACL），`config.json` 只存 sha256 | 已裁决 D13，主文档已同步；GUI 只做掩码/复制/轮换（§7.2） |
| 7 | 监听面 | 保留 `0.0.0.0:8765` 默认（容器必须能连） | 暴露面风险 → 常驻风险横幅 + 防火墙指引；「仅本机」明确“容器连不上”（§9.6） |
| 8 | 启动方式（已裁决 Q5） | **手动启动**：不写启动文件夹、不建计划任务、GUI 无自启开关 | 重启电脑后需手动点开，启动前模型不可用（用户已知情选择，§4.4） |
| 9 | 截图（已裁决 Q4） | **默认开启**，但 `defaultInclude=false`：默认不自动附图，模型按需 `include_screenshot=true` | 隐私面扩大 → 敏感/自身窗口抑制 + 不可转发提示 + 用户可关（§9.8） |
| 10 | Web 技术 | **不做 WebUI、不嵌 WebView**（原生 WPF 实现参考视觉） | 放弃“用网页快速做界面”的省事路线（额外运行时/更新链/安全边界） |

三个“看起来该做但不做”：**不做开机自启**、**不做 WebUI/WebView/网页版**、**不引入第三方 UI 组件库/主题包**（MahApps、MaterialDesign、HandyControl 等一律不用，样式全部自写）。

> 定位关系：DESIGN §5.10（宿主形态）与 ARCHITECTURE §2.1 L7 已定义“单进程托盘宿主 + GUI 只引用 Contracts”；本稿把实现规格落到 WPF/主题/seam 级别，
> 并把 D21 的 WPF、无自启、截图默认开、`ui.theme` 的变化列进 §11.2 交给 Lead 同步主文档。

## 2. 目标与非目标

### 2.1 本期目标（P1 + GUI v1，已按 D21 更新）

1. 双击 `WhaleGo.Executor.exe`（**手动启动**）→ 托盘出现 WhaleGo 图标；首次运行弹配对向导，5 步内完成 Windows 端 + AstrBot 插件配对。
2. 两个动作永远可达：**双击托盘打开设置**、**Ctrl+Alt+Shift+F12 紧急停止**（热键注册失败必须显式告警，不静默）。
3. **外观达标**：浅色/深色两套主题（跟随系统 / 手动切换，热生效、无需重启），圆角卡片、克制排版、hover/focus/pressed 态与 150ms 过渡；6 页在两套主题、100%/200% DPI 下均可用。
4. 「WhaleGo 设置」能改 §6 的配置（含 `ui.theme`）并写回 `%APPDATA%\WhaleGo\config.json`；非法值拒绝保存并就地提示；标明热生效/需重启。
5. 诊断页回答三个问题：**现在能不能被模型操作？**（只读/暂停/停止/异常）、**谁在操作？**（租约 + 最近请求）、**连不上卡在哪？**（自检 + 打开日志/审计目录）。
6. `--headless` 能在 CI/无桌面跑；GUI 的“逻辑”（自检、配置校验、暂停状态机、主题决策、presenter）都能在无窗口下测试（§12.2）。

### 2.2 非目标（本期明确不做）

- **不做 WebUI / WebView / 网页版设置**（用户拍板）；**不做开机自启**（不写启动文件夹、不建计划任务、无 GUI 开关）。
- **不引入第三方 UI 组件库/主题包/托盘库**：WPF 样式、圆角、动效、`IWindowTheme` 全部自写或走 Contracts 端口。
- **不做自定义窗口边框/自绘标题栏**：用 Windows 原生标题栏（Win11 原生圆角 + DWM 暗色标题栏），避免 Snap Layouts/无障碍/高对比度回归。
- **多语言**（文案集中到常量类，P2 再抽资源）、**自动更新/安装器/代码签名**（DESIGN §1.3 已有）、**UAC 自我提权**、**崩溃看护 watchdog**、**GUI 的 UI 自动化测试**（§12.3）。
- **在 GUI 里看截图 / 远程看桌面 / 回放操作**（那是远程桌面）；**修改容器侧插件配置**（GUI 只生成可粘贴片段）。

## 3. 形态与技术选型

### 3.1 WPF / WinForms / Avalonia 对比（已按 D21 重估）

| 维度 | **WPF（选中：设置窗口）** | WinForms | Avalonia 11 |
|---|---|---|---|
| 依赖/体积 | WindowsDesktop 共享框架，**零额外 NuGet**（本机已装：DESIGN 附录 A#1） | 同为共享框架，零额外 NuGet | 额外 NuGet（Avalonia 全家桶，数 MB）+ 自带渲染栈 |
| 主题 / 深色 | **ResourceDictionary + `DynamicResource`：换肤只是换字典**，浅/深/高对比度各一套 | 深色需全自绘（只有标题栏/菜单有部分系统 API），成本高且回归面大 | 主题开箱即用，但多一套依赖 |
| 圆角 / 卡片 / 阴影 / 过渡 | 原生 `CornerRadius`、`DropShadowEffect`、Style/Trigger、`VisualStateManager` + 动画，**足以复刻参考视觉** | 圆角需 Region/自绘；过渡要手写定时器 | 支持良好 |
| 托盘支持 | 无内置 → **用 WinForms `NotifyIcon`（同进程，零第三方）** | `NotifyIcon` 内置 | 内置 `TrayIcon`（API 不同） |
| DPI / 缩放 | 布局单位 DIP + manifest PerMonitorV2，跨屏拖动自动重排；文字渲染清晰 | 支持，但控件/字体缩放历史坑多 | 自有缩放，但与 Win32 坐标两套 |
| 开发成本 | 中高：XAML + MVVM + 样式/动画；**“好看+深色”必须付这笔钱** | 低，但达不到观感要求 | 中，且团队要新学一套、多一层依赖 |
| 与现有 WinForms 夹具一致性 | 不要求：`WhaleGo.Fixtures.WinForms`/`Elevated` 是**被自动化的目标**，不是 GUI 技术选型依据（旧稿按此选型已随 Q6 否决） | 同左 | 同左 |
| .NET 10 兼容 | 一等公民（`net10.0-windows`、`UseWPF`） | 一等公民（`UseWindowsForms`） | 支持 net8+，可跑 net10，属跟随者 |
| 结论 | ✅ **选它（设置窗口）** | ⚠️ **只用于托盘**（保留 NotifyIcon 能力） | ✗ 不选：多一套依赖/渲染栈，且无跨平台需求 |

### 3.2 推荐：WPF 做窗口 + WinForms 做托盘（同一进程、同一项目）

- **组合式选型**：`WhaleGo.Gui` 项目 `UseWPF=true` + `UseWindowsForms=true`；设置窗口/页面/主题用 WPF，托盘与热键消息窗用 WinForms 控件。
  这是“既满足观感与深色、又不引第三方包”的唯一组合（WPF 无内置托盘；第三方托盘库 `Hardcodet.NotifyIcon.Wpf` 属于新依赖，违反零 NuGet 目标）。
- **WinForms 全量 GUI 明确否决**：深色/圆角/主题化需大面积自绘且回归面大，达不到 Q6 的“好看”要求；WinForms 只保留 `NotifyIcon` 托盘能力（这是它唯一不可替代的部分）。
- **不用 WinUI 3**：需 Windows App SDK 额外依赖与打包/部署链，与“单 exe + 共享框架”冲突。
- **不用第三方主题库**：样式全部自写（控件模板 + token 字典），避免主题升级/许可/兼容三重风险；代价是控件模板要自己维护（§10.5）。
- **不复制参考站点资产**：只借用其“留白 + 中性色 + 单一蓝色强调 + 圆角卡片 + 克制动效”的视觉语言，不搬页面结构、文案或图形素材（§10.1）。

### 3.3 单进程（托盘宿主内嵌执行器）vs 双进程

| 维度 | **单进程（推荐）** | 双进程（GUI ↔ 执行器，需 IPC） |
|---|---|---|
| 组件/启动 | 1 个进程、1 次手动启动、1 份配置 | 2 个进程、2 次手动启动，还要处理“谁先起” |
| 通信 | 进程内调用 `IExecutorControl` | 命名管道/本地 HTTP：要设计 ACL、握手、版本字段 |
| 故障隔离 | **差**：UI 未处理异常可能拖垮内核 | 好：一侧崩另一侧还在 |
| 状态/主题一致性 | 天然一致（同一个 SessionManager、同一份主题状态） | 要同步/竞态（暂停/主题到底谁说了算） |
| 成本 | 低（1~2 天接线） | +2~3 天，且长期多一套 IPC 兼容 |
| 安全 | 与执行器同权限（用户中完整性）——本来就是用户自己的桌面 | GUI 可降权，但内核仍必须是用户权限，收益有限 |
| headless | `--headless` 跳过 UI 初始化即可 | GUI 可缺席，但 CI 要多起一个进程 |

**结论：单进程。** GUI 只经 `IExecutorControl` 等 Contracts 端口访问内核（§4.6）；未来真要拆双进程，只换端口实现，视图/主题代码不动。
风险缓解（必须做）：GUI 每次调内核都 async + 超时（≤1s）+ try/catch，UI 线程不做 IO、不等锁；三处全局异常兜底；Kernel 先 ready 再建 UI，UI 建失败不影响 MCP 服务（等价降级到 `--headless`）。

## 4. 进程与模块模型

### 4.1 进程拓扑与新项目

```text
WhaleGo.Executor.exe（WinExe，用户交互会话 Session 1；UseWPF + UseWindowsForms）
  Program.cs（唯一组合根；WPF Application，ShutdownMode=OnExplicitShutdown）
   ├─ --headless ? 控制台宿主 : WPF App（App.xaml，无 StartupUri，由组合根建 MainWindow）
   ├─ Kernel（ASP.NET Core + MCP + Application 服务）→ 实现 IExecutorControl 等端口
   ├─ WhaleGo.Gui
   │    ├─ TrayShell（WinForms NotifyIcon + ContextMenuStrip + 5 态映射）
   │    ├─ MainWindow + 6 个 Page（WPF View/ViewModel，浅/深主题资源字典）
   │    └─ ThemeService（ui.theme + 系统主题跟随 → 热切换 DynamicResource）
   └─ Contracts 端口（由 Platform 实现）：IWindowTheme（DWM 暗色标题栏）、IHotkeyRegistrar（RegisterHotKey）
```

- 新项目 `windows/src/WhaleGo.Gui/`（`net10.0-windows`、`UseWPF=true`、`UseWindowsForms=true`）；引用方向：`WhaleGo.Executor`（组合根）→ `WhaleGo.Gui` → `WhaleGo.Contracts`。
- WPF 启动关键点：`ShutdownMode=OnExplicitShutdown`（否则关设置窗口会退出进程）；主窗口先 `Hide()` 启动，托盘常驻；全部隐藏时进程不退出。
- MVVM 不引第三方：自写 `ObservableObject`（`INotifyPropertyChanged`）与 `RelayCommand`，避免 CommunityToolkit.Mvvm 之类的新 NuGet。

### 4.2 单实例

- 互斥体 `Local\WhaleGo.Executor.SingleInstance`（`Local\` 而非 `Global\`：本 App 是“每交互会话一个”）。
- 第二实例：等互斥 3s → 仍失败则 `EventWaitHandle.Set("Local\WhaleGo.Executor.ShowSettings")` → 退出码 0；第一实例后台线程收到后经 `Dispatcher.Invoke` 显示并置前窗口。
- `--headless` 与 GUI 共用同一互斥体（同一份配置/同一端口，不能并存）：冲突时 stderr + **退出码 2**（脚本可判）。

### 4.3 窗口生命周期：关闭 ≠ 退出（含退出确认与崩溃）

| 操作 | 语义 |
|---|---|
| 点设置窗口 X / Esc | **隐藏到托盘**（`Hide()`，不 `Close()`）；不改进程状态（`OnExplicitShutdown` 保证） |
| 托盘双击 / 菜单「打开设置」 | `Show()` + `WindowState=Normal` + `Activate()`；若已被其它窗口遮挡则 `Topmost` 闪一次 |
| 托盘菜单「退出」 | **唯一真正退出路径** → 确认框 → 优雅停机（≤3s）→ 释放托盘/热键/互斥 → `Application.Shutdown(0)` |
| `SessionEnding`（注销/关机） | best-effort `Stop()`（≤2s）+ 写停机标记，不弹任何窗口 |
| 资源管理器重启（`TaskbarCreated`） | WinForms NotifyIcon 自动恢复；再 `NIM_ADD` 兜底，状态重算，图标不能“消失” |

退出确认文案：`退出后，模型将立刻无法操作你的桌面（正在进行的动作可能已发出，不会自动回滚）。` 有租约时补 `当前有会话正在操作（wg-xxxx，剩 12s）。`；
按钮 `[取消]（默认）` / `[退出]`，Enter 不默认确认（安全默认）；流程：停监听 → 等在途请求（≤3s）→ 保存 control-state → 释放资源 → 退出。
崩溃与重启：进程没了就没有被控能力（fail-closed），所以只做“别静默 + 能复盘”——三处全局异常钩子写 `%EXEDIR%\logs\crash-*.log`（与可执行程序同目录；不含 token/明文）；
`run.lock`（PID+时间）检测上次异常退出；崩溃不自动恢复 control-state（崩前是 stopped，重启仍 stopped）；「立即重启」= 起新实例（不带 `--headless`）+ 本实例 `Shutdown()`，新实例等互斥 ≤10s。

### 4.4 不做开机自启（已裁决 Q5）

- 用户拍板：**手动启动**。因此：不写启动文件夹快捷方式、不创建任务计划、GUI 不提供自启开关、`IExecutorControl` 不含 `SetStartupShortcut`，配置删除 `startup.*` 组。
- 启动方式（写进「关于/限制」页与 README）：双击 `WhaleGo.Executor.exe`（可自行固定到任务栏/开始菜单）；无参数即“托盘 + 内核”，`--headless` 为无头。
- 影响必须如实说：**重启电脑后，用户不点开 WhaleGo，模型就无法操作桌面**（fail-safe，但用户要知道）；托盘/热键只在程序运行时有效。
- 已落地（v0.5）：DESIGN §5.10 标题改“宿主形态”、§9.1 改手动启动、§12.3 删自启；README 已写明“手动启动（不自启）”。

### 4.5 `--headless`（CI / 无 GUI 测试）

| 参数 | 行为 |
|---|---|
| `--headless`（或 `WHALEGO_HEADLESS=1`） | 不建 WPF 窗口/托盘/全局热键；日志走 stdout；互斥与端口照常 |
| `--config <path>` | 用指定配置（CI 用临时目录，不踩真实 `%APPDATA%`） |
| `--self-check --json` | 跑 §8.4 自检，JSON 打到 stdout，**退出码 0=全 OK / 1=有 FAIL**；不启 MCP 监听（临时端口测绑定） |
| `--no-hotkey` / `--diagnostics-dump` | 显式不注册热键 / 打印 `go_health` 等价 JSON + 最近请求后退出（E2E 用） |

设计要点：GUI 只是渲染层，**自检/配置校验/暂停状态机/主题决策全在 Application 与 Gui 的非视图层**，headless 跑的是同一份逻辑（§12.2 的 seam）。

### 4.6 依赖方向（GUI 不得绕过 Domain/Application；WPF 项目也不碰 Win32）

```text
WhaleGo.Executor（组合根/宿主）
   ├─ 引用 → WhaleGo.Gui        （组合根 new MainWindow(control, theme)，注入端口）
   ├─ 引用 → WhaleGo.Platform   （仅 DI 注册，ARCHITECTURE §3.1 已有约束）
   └─ 引用 → WhaleGo.Contracts
WhaleGo.Gui  ──只引用──→  WhaleGo.Contracts（IExecutorControl / IWindowTheme / IHotkeyRegistrar 等端口）
```

- **硬规则**：`WhaleGo.Gui` 的 `using` 不允许出现 `WhaleGo.Platform`、`System.Windows.Automation`、user32 P/Invoke、`Microsoft.AspNetCore.*`；用架构测试守住引用集（CI 失败即违规）。
- GUI 只能通过 `IExecutorControl` 做事：`GetStatus` / `GetRecentRequests` / `GetToolCounters` / `Pause` / `Resume` / `Stop` / `ResetStop` / `GetConfig` / `SetConfig` / `RotateToken` / `RevealTokenForCopy` / `RunSelfCheck` / `ReadAuditTail` / `OpenDataFolder` / `RequestRestart`（**已删 `SetStartupShortcut`**）。
- 两个视觉/系统相关动作也必须走 Contracts 端口（**已裁决 D22，已落地 DESIGN §5.3 / ARCHITECTURE §3.3**）：`IWindowTheme.TryApplyDarkTitleBar(hwnd, dark)`（DWM 暗色标题栏，Platform 用 `DwmSetWindowAttribute` 实现）；`IHotkeyRegistrar.TryRegister(gesture, callback)`（热键注册/注销，Platform 用 `RegisterHotKey` 实现）。GUI 只通过 `WindowInteropHelper(window).Handle` 拿 HWND 转交，不写 P/Invoke。
- GUI 不碰：UIA COM、SendInput、会话/租约内部对象、审计文件写（只读 tail）、config 文件直接读写（走 `IConfigStore`）。

## 5. 托盘、图标与通知

### 5.1 图标状态（5 态；颜色 + 形状 + 文字三重编码）

| 态 | 图标 | 判定 | 菜单首行 |
|---|---|---|---|
| 运行中 · 已连接 | 蓝色鲸尾 | 监听正常 且 最近 5 分钟内有 MCP 请求 或 存在活跃会话 | `● 运行中 · 已连接` |
| 运行中 · 无连接 | 灰色鲸尾 | 监听正常 但 5 分钟无请求、无会话 | `○ 运行中 · 暂无客户端` |
| 已暂停 | 琥珀鲸尾（角标 II） | `paused=true` | `⏸ 已暂停（15:32 起）` |
| 已停止（kill switch） | 红色鲸尾（角标 ■） | `stopped=true`（全局）/ `session_stopped=true` 会话态另计 | `■ 已停止 · 需本机恢复` |
| 异常 | 红色鲸尾（角标 !） | 端口绑定失败／审计目录不可写／非交互会话／热键注册失败（另加气泡） | `! 异常：<一句话原因>` |

- **浅/深任务栏双版本**：每状态两套 `.ico`（深色图形供浅色任务栏、浅色图形供深色任务栏），按 `HKCU\...\Themes\Personalize\AppsUseLightTheme` + `SystemParameters.HighContrast` 选择；`.ico` 内多尺寸（16/20/24/32/48/256），**不在运行时用 GDI+ 拼图**。
- 状态判定在 Application `StatusService`，托盘只做映射；WinForms `NotifyIcon.Icon` 换图标即换态（不支持矢量）。
- tooltip：单行 ≤63 字符，格式 `WhaleGo · 运行中 / 0.0.0.0:8765 / 1 会话`；**禁止**出现 token、会话 id 全量、窗口标题、截图信息（肩窥风险）。

### 5.2 托盘菜单与鼠标

```text
● 运行中 · 0.0.0.0:8765            （首行禁用：状态）
────────────────────────────
打开设置...                  (双击同效)
暂停                  ← 运行中可用；暂停后变「恢复」
紧急停止(S)           ← 停止所有动作；恢复需二次确认
复制插件配置          ← 含 url + token，剪贴板 60s 自动清
打开日志目录 / 打开审计目录
────────────────────────────
关于 WhaleGo
退出(X)               ← 唯一真正退出
```

- **删除“开机自启”项**（Q5）；菜单只剩状态、设置、止血、复制、目录、关于、退出。
- 暂停/恢复是**同一位置切换**（文案随状态变）；紧急停止与暂停分开，避免“想暂停却停了”；「复制插件配置」在 token 不可读时置灰。
- 菜单用 WinForms `ContextMenuStrip`：**跟随系统的浅/深色渲染**（Win11 原生），不跟随应用 `ui.theme`（应用内深色 + 浅色托盘菜单是可接受的系统一致性差异；不为此自绘菜单）。
- **双击**=打开设置（主路径），**单击不响应**（避免误触弹窗打断用户操作），右键=菜单，中键=无动作；菜单项全部可用键盘操作。

### 5.3 气泡通知策略（宁可少、不可吵）

| 触发 | 级别 | 文案示例 |
|---|---|---|
| 热键/托盘触发紧急停止 | 重要 | `WhaleGo 已紧急停止，模型无法再操作桌面。恢复请在托盘点「恢复」。` |
| 模型调用 `go_stop` | 重要 | `模型已停止本会话的桌面操作（原因：…）。` |
| 启动失败（端口占用/审计不可写/非交互会话） | 重要 | `WhaleGo 启动失败：端口 8765 被占用。点此打开诊断。` |
| 审计 fail-closed 触发（`AUDIT_UNAVAILABLE`） | 重要 | `审计写入失败，动作已被拒绝。请检查磁盘/审计目录。` |
| 认证失败聚合（60s 内 ≥5 次 401） | 一般 | `有客户端认证失败，请检查 AstrBot 插件里的 token。` |
| 上次异常退出 | 一般 | `WhaleGo 上次未正常退出，已重新启动。` |

- 冷却：同类 30s 一次、全局 ≤3 条/分钟；超限合并或只改托盘状态；内容 ≤140 字，**不含** token/窗口标题/截图/输入文本；点气泡 → 打开设置对应页。
- 气泡由系统渲染（`NotifyIcon.ShowBalloonTip`），**不随应用主题**；用户可关：`ui.notificationsLevel = off / important / all`（DESIGN §5.8 已有），关掉后托盘状态仍会变。

## 6. 设置项目录

### 6.1 页面结构（6 页）

1. **概览**：状态卡片、两个止血按钮、配对向导入口、复制插件配置；**右上角主题切换（跟随系统/浅色/深色）**，首屏即见（Q1 拍板）。
2. **连接与配对**：`listen.*`、token 列表（掩码/复制/轮换）、连接测试、防火墙提示、0.0.0.0 风险横幅。
3. **执行策略**：`policy.*`、`limits.*`、`screenshot.*`（默认开 + “不自动附图”说明）、只读开关、`launch.allowlist`。
4. **隐私与审计**：`privacy.*`、`audit.*`（含 fail-closed 危险项，默认锁定）。
5. **诊断与日志**：状态详情、租约、最近请求、工具计数、审计 tail、一键自检、打开目录。
6. **关于/限制**：版本、协议版本、主题说明、**“启动方式：手动启动”**、限制（UIPI/高完整性、锁屏、DPI、Chromium 坐标降级）、免责声明。

### 6.2 配置项（来自 DESIGN §5.8 v0.5 已落地键；默认值已按 D21 修正）

> 本地 `config.json` 是 **camelCase**、wire 协议是 **snake_case**（DESIGN §5.8、ARCHITECTURE §3.8）；GUI 只写本地 camelCase，永不直接编辑 wire JSON。

| 分组 | 配置键 | 类型 | 默认值 | 校验 / 范围 | 生效 | 文档章节 |
|---|---|---|---|---|---|---|
| 连接 | `listen.address` | string | `0.0.0.0` | 允许 `0.0.0.0` / `127.0.0.1` / 具体网卡 IP；非法 IP 拒绝保存；改 `127.0.0.1` 时强警告“容器连不上” | 需重启 | DESIGN §5.8、§8.2 |
| 连接 | `listen.port` | int | `8765` | 1–65535；<1024 提示需管理员/易占用；保存时探测占用 | 需重启 | DESIGN §5.8、§8.2 |
| 连接 | `listen.healthzRequireToken` | bool | `false` | healthz 是否要求 Bearer（高级区） | 热 | PROTOCOL §1、DESIGN §5.8 |
| 安全 | `auth.tokens[].name` | string | `astrbot` | 1–32 字符 `[A-Za-z0-9_-]`，唯一 | 热 | DESIGN §5.8 |
| 安全 | `auth.tokens[].sha256` / `createdAt` | string（只读） | 首启生成 | sha256 64 位 hex + 创建时间；仅轮换时写入 | 热 | DESIGN §5.8、§8.2 |
| 策略 | `policy.readonly` | bool | `false` | 执行器侧只读（与插件 `readonly` 双保险，任一为只读即拒绝动作） | 热 | DESIGN §5.8、§8.3 |
| 策略 | `policy.actRatePerMinute` | int | `120` | 1–600 | 热 | DESIGN §5.8 |
| 策略 | `policy.controllerWaitMs` | int | `2000` | 0–10000；只影响等租约的等待，不改租约 TTL（协议固定 60s） | 热 | DESIGN §5.8、PROTOCOL §5.3 |
| 策略 | `policy.stateTtlSeconds` | int | `120` | 10–600（过大易引发 STALE_STATE 风暴） | 热 | DESIGN §5.8、PROTOCOL §5.2 |
| 策略 | `policy.frameTtlSeconds` | int | `60` | 10–300 | 热 | DESIGN §5.8、PROTOCOL §5.2 |
| 预算 | `limits.maxObserveElements` | int | `300` | 50–2000（协议上限 2000） | 热 | DESIGN §5.8、PROTOCOL §3.6 |
| 预算 | `limits.maxObserveChars` | int | `20000` | 2000–200000 | 热 | DESIGN §5.8 |
| 预算 | `limits.maxImageWidth` | int | `1024` | 320–4096 | 热 | DESIGN §5.8、§5.4 |
| 预算 | `limits.maxImageBytes` | int | `200000` | 20000–2000000 | 热 | DESIGN §5.8、§5.4 |
| 预算 | `limits.toolTimeoutSeconds` | int | `40` | 5–40；必须 ≤ 插件 `timeout`-5s（插件默认 45s，D12） | 热 | DESIGN §5.8、ARCHITECTURE §3.11 |
| 截图 | `screenshot.enabled` | bool | **`true`**（D21/Q4，**已落地**） | 执行器总开关；**默认开**，但仍需模型 `include_screenshot=true` 或 `defaultInclude` 才附图（三开关 AND） | 热 | DESIGN §5.8、PROTOCOL §11 |
| 截图 | `screenshot.mode` | enum | `printwindow` | `printwindow` / `wgc`（wgc 为 P2 占位，选中则回退并提示） | 需重启 | DESIGN §5.8、§5.4 |
| 截图 | `screenshot.defaultInclude` | bool | `false` | **默认不自动附图**；模型按需 `include_screenshot=true`（设为 true 才会每轮自动带图，谨慎） | 热 | DESIGN §5.8、PROTOCOL §11 |
| 隐私 | `privacy.maskPasswordFields` | bool | `true` | 改 false 需确认（违反 day-1 隐私规则） | 热 | DESIGN §5.8、PROTOCOL §11 |
| 隐私 | `privacy.sensitiveWindows` | string[] | `["*password*","*bank*"]` | glob 通配，每项 1–64 字符；命中时截图被抑制（`screenshot_suppressed`） | 热 | DESIGN §5.8、PROTOCOL §11 |
| 审计 | `audit.enabled` | bool | `true` | 关闭需二次确认 | 热 | DESIGN §5.8、§5.7 |
| 审计 | `audit.dir` | string | `%EXEDIR%\logs`（可执行程序同目录） | 可写目录；不可写 → 保存失败并解释 fail-closed 后果 | 需重启 | DESIGN §5.7、§5.8 |
| 审计 | `audit.requireDurableForActions` | bool | `true` | 高级危险项：改 false 需输入确认（§9.7） | 热 | DESIGN §5.8、§5.7 |
| 热键 | `hotkeys.enabled` | bool | `true` | headless 时默认 false | 热 | DESIGN §5.8 |
| 热键 | `hotkeys.emergencyStop` | string | `"Ctrl+Alt+Shift+F12"` | RegisterHotKey 语法（修饰键 + 单键）；冲突/非法 → 告警并标红 | 热 | DESIGN §5.8 |
| 热键 | `hotkeys.openSettings` | string | `null`（不启用） | 未拍板：保持关闭；如需再加（§13 保留项） | 热 | DESIGN §5.8 |
| 通知 | `ui.notificationsLevel` | enum | `important` | `off` / `important` / `all` | 热 | DESIGN §5.8 |
| 外观 | `ui.theme` | enum | `system` | **新增（D21）**：`system` / `light` / `dark`；跟随系统或强制；**热切换**（§10.7） | 热 | DESIGN §5.8 v0.5（已落地） |
| 日志 | `logging.level` | enum | `information` | trace/debug/information/warning/error | 热 | DESIGN §5.8 |
| 日志 | `logging.dir` | string | `%EXEDIR%\logs`（可执行程序同目录） | 可写目录 | 需重启 | DESIGN §5.7、§5.8 |
| 启动 | `launch.allowlist` | list[{name,path}] | notepad/calc/mspaint/explorer | 模型只能按 name 启动；path 必须绝对路径且存在；GUI 增删/校验 | 热 | PROTOCOL §3.8、DESIGN §5.8 |

### 6.3 D21 带来的配置增删（已裁决，**已落地 DESIGN §5.8 v0.5**）

| 变更 | 键 | 说明 |
|---|---|---|
| **新增** | `ui.theme`（enum：`system` / `light` / `dark`，默认 `system`，热切换） | 用户要求“深色模式要做”；默认跟随系统，手动可覆盖；只影响 GUI，不影响协议 |
| **删除** | `startup.autoStart`（含 `startup` 组、环境变量 `WHALEGO_STARTUP__*`、DESIGN §5.10/§9.1/§12.3 与 README 的自启描述） | Q5 明确“不做开机自启（手动启动）” |
| **改默认** | `screenshot.enabled`：`false` → **`true`** | Q4“截图默认开”；**`defaultInclude` 保持 `false`**（默认不自动附图，避免每轮传图），另在插件片段里 `allow_screenshot=true` |
| 保持不变 | `hotkeys.openSettings = null` | 该问题未拍板（原 Q6 已被“外观”占用）；保持不启用，「关于」页注明“暂不支持设置热键，可加” |

### 6.4 配置来源、写回与生效（已裁决 D14）

- **来源优先级**：`appsettings.json` < `%APPDATA%\WhaleGo\config.json` < 环境变量 `WHALEGO_*` < 命令行；越靠后优先级越高。
  GUI 每项显示来源徽标（默认 / 配置文件 / 环境变量 `WHALEGO_xxx` / 命令行）；**被环境变量或命令行覆盖的项禁止在 GUI 编辑**，提示“改 GUI 不生效”。
- 写回：只写用户点「应用」的当前页字段（patch 语义），原子写（临时文件 + `File.Replace`），保留未知键（`JsonExtensionData`），整页校验通过才写。
- 保存前比对文件哈希：外部改动 → 提示“配置被其它程序修改，是否重新加载”；热生效项走 `IOptionsMonitor`，重启项打「重启生效」徽标 + 应用后问「立即重启/稍后」。
- **主题特例**：`ui.theme` 是“热生效 + 立即视觉反馈”的项——应用即写配置并切主题（不等待整页保存）；若保存失败，主题回滚到上次值并提示。

## 7. 首次运行与容器配对

### 7.1 向导（5 步）

判定：`%APPDATA%\WhaleGo\config.json` 不存在，或 `secrets.json` 缺失/无可用 token。可「跳过，稍后配置」（托盘进入“异常：未完成配对”，设置页常驻引导条）。

1. **欢迎与风险**：WhaleGo 能做什么（看屏幕/点鼠标/敲键盘）、必须在本机跑、**截图默认可用但默认不自动附图**（模型按需请求；敏感窗口自动抑制；截图不得转发）、随时可热键停止；一个「我明白」勾选。
2. **监听与 token**：显示 `listen.address/port`（默认 0.0.0.0:8765，可改端口）、生成 token、掩码显示 + 「复制」。
3. **复制插件配置片段**：见 §7.3，只读 JSON 框 + 「复制」；同时给“粘到 AstrBot WebUI 哪里”的 3 行指引。
4. **连接测试**：本机自测（§7.4）+ 「等待容器连接」进度（出现第一条外部请求即绿）。
5. **完成**：防火墙提示（复制命令）、日志/审计目录位置、**“启动方式：手动启动”**、下一步（AstrBot WebUI 保存并重载插件）。

> 向导页与设置页共用控件与校验逻辑（两套代码必然漂移）；向导只是“必须完成的顺序 + 更少分支”，外观与主题与设置页完全一致。

### 7.2 token 的生成、存放、展示、复制、轮换（已裁决 D13）

- 生成：32 字节 CSPRNG → base64url，前缀 `wg_`；`config.json` 只存 `{name, sha256, createdAt}`，明文存 `%LOCALAPPDATA%\WhaleGo\secrets.json`（仅当前用户 ACL；DPAPI 可选 P2）。
- 展示：一律掩码 `wg_ab12••••••••9f3c`（首 5 后 4）；**不提供“显示明文”按钮**（肩窥/录屏风险），要看明文只能“轮换并复制新 token”。
- 复制：写剪贴板 + 60s 定时器，到时**仅当剪贴板内容仍等于所复制值**才清空；复制/轮换写应用日志（`token_copied` / `token_rotated`，不含值）。
- 轮换：确认框（“旧 token 立即失效，AstrBot 插件需同步修改”）→ 生成 → 写 sha256 + secrets → 弹「只显示这一次」+ 复制 + 「复制新插件配置」；旧 token 立刻 401。
- secrets 丢失/损坏：显示“token 不可读”→ 只能轮换；不影响 MCP 服务运行（认证用哈希）。

### 7.3 一键复制“插件配置片段”

> 需求里说的 `server_url` 在插件 schema 里实际叫 `url`（DESIGN §6.6、NAMING §4.2）——片段按文档用 `url`，不新造键名。

```json
{
  "url": "http://host.docker.internal:8765/mcp",
  "token": "<这里替换为复制的 token>",
  "timeout": 45,
  "readonly": false,
  "admin_only": true,
  "allow_screenshot": true,
  "allowed_tools": ["go_health","go_apps","go_windows","go_observe","go_act","go_launch","go_stop"]
}
```

- `allow_screenshot: true` 是 Q4 的配套（插件侧开关；执行器 `screenshot.enabled` 已默认 true）；**仍受三开关 AND**：执行器 enabled 且（模型 `include_screenshot=true` 或 `defaultInclude=true`）且插件 `allow_screenshot=true`。
- `url` 随 `listen.port` 动态生成；若 `listen.address=127.0.0.1` → 片段顶部加 `⚠ 当前仅本机监听，容器连不上` 并禁用“完成”态；「复制」时把 token 明文直接替换进去（一次复制完成）。

### 7.4 连接测试 + 容器侧在哪填

| 测试 | 做法 | 结果展示 |
|---|---|---|
| 本机认证 | 进程内 `HttpClient` → `GET http://127.0.0.1:<port>/healthz`：带 token 期望 200，不带期望 401 | ✅/❌ + 延迟 ms + 失败原因 |
| 等待容器连接 | 监听“最近一次外部请求”（来源 IP、时间、工具名），出现即“容器已连上” | 状态灯 + 最近请求时间 |
| 容器侧排查 | 生成可复制命令（不自动执行）：`docker exec AstrBot curl -fsS -H "Authorization: Bearer <token>" http://host.docker.internal:8765/healthz` | 复制按钮 + 说明 |
| 插件侧确认 | 指引：AstrBot WebUI → 插件管理 → WhaleGo → 配置保存 → 重载 → 日志出现已连接；或对机器人发 `/whalego health` | 步骤清单 |

容器侧步骤：① AstrBot WebUI →「插件管理」→「已安装插件」→ **WhaleGo**（`astrbot_plugin_whalego`）→「配置」；② 粘入 §7.3 片段（`url` + `token` 必备，`admin_only` 保持 true）；
③ 保存 →「重载插件」→ 看到 `OK:`；④ 备选（不推荐）：内置 `mcpServers` 直连（PROTOCOL §10：不读 isError、工具名无前缀、默认 member 权限）。

## 8. 状态与诊断页

### 8.1 状态卡片（顶部，只读）

| 字段 | 来源 | 展示 |
|---|---|---|
| 运行状态 | `StatusService` | 运行中 / 已暂停 / 已停止（全局/本会话）/ 异常（附一句话原因） |
| 实际监听 | Kestrel `IServerAddressesFeature`（**不是配置值**） | `0.0.0.0:8765`；配置与实际不一致时显示“配置 X / 实际 Y” |
| 版本 / 协议 | 程序集 FileVersion + `go_health.version` / `protocol_version` + `mcp_protocol_version` | `0.1.0（build abc1234）` · `v1 / 2025-06-18` |
| 会话 / 租约 | `go_health.sessions` + `controller{held,mine,expires_in_ms}` | `2 个会话 · 租约：其它会话持有（剩 43s）` / `本会话持有` / `无` |
| 只读 / 截图 / 审计 | 配置 + 运行态 | `只读：关 · 截图：开（不自动附图）· 审计：开（fail-closed）` |

### 8.2 最近请求与工具计数

- 最近请求（内存环形缓冲 ≤50 条）：时间｜工具｜会话（尾 6 位）｜耗时 ms｜结果（`OK` / `ERROR[CODE]`）｜备注（元素数 / 有无截图 / 租约等待）。
- 隐私红线：**不记录**参数、输入文本、截图字节、窗口标题，只留元数据；页面注明“仅本机内存，重启即清空”。数据由 Application `StatusService` 记录（不从日志反解）。
- 工具计数：每工具一行 `go_observe：总 42 / 成功 40 / 失败 2（STALE_STATE 1, FRAME_NOT_ACTIONABLE 1）` + 当前分钟速率（对比 `policy.actRatePerMinute`）；从进程启动开始、不落盘、重启清零。

### 8.3 审计 tail 与打开目录

- 读 `audit.dir` 下最新 `audit-YYYYMMDD.jsonl` **末 200 行**，只读、等宽字体、可复制；`FileShare.ReadWrite | Delete` 容忍执行器正在写；单行截断 4KB；文件不存在 → “今日暂无审计”。
- 顶部说明：审计不含输入明文 / 剪贴板 / 截图字节 / token（DESIGN §5.7、PROTOCOL §11）；下方「打开审计目录」「打开日志目录」用 `explorer.exe <path>`，目录不存在 → 提示 + “创建”按钮。

### 8.4 一键自检（可复制报告）

| 检查 | 方法 | 通过判据 | 失败建议 |
|---|---|---|---|
| 端口 | 临时 socket 绑定 + 现监听比对 | 可绑定，或已由本进程监听 | 换端口 / 给出占用进程 PID + 名称 |
| 防火墙 | `netsh advfirewall firewall show rule name=WhaleGo-MCP`（只查询，不提权） | 规则存在且 Enabled | 复制“以管理员运行”的建规则命令（DESIGN §8.2） |
| UIA 可用性 | 起 FlaUI Automation，取桌面根元素 | 根元素可读 | “UIA 服务异常 / 被安全软件拦截” |
| DPI | `GetDpiForSystem` + 进程 DPI 感知 | PerMonitorV2 且 DPI 合法 | “manifest 未生效，检查构建产物” |
| 交互会话 | `ProcessIdToSessionId` + `OpenInputDesktop` | Session 1 且能打开输入桌面 | “请用登录用户直接运行，不要用服务/后台会话” |
| 审计可写 / 热键 / 认证 | 建 temp 文件 + 删 / 真实组合注册再注销 / 见 §7.4 | 均成功 | 换目录、改热键、检查 token 与 secrets |
| **主题资源** | 加载 Light/Dark 两套 `ResourceDictionary`，检查 token 键齐全、按 WCAG 抽查关键前景/背景对比度 | 两套均可加载 + 对比度达标 | “主题资源缺失/对比度不足，检查 Themes/*.xaml” |

结果每项 `✅ / ⚠️ / ❌` + 一句话；底部「复制自检报告」（纯文本，不含 token）。

## 9. 止血与安全

### 9.1 三档控制状态与按钮（已裁决 D2）

| 态 | 进入方式 | `go_act` / `go_launch` | 观察 | 退出方式 | 重启后 |
|---|---|---|---|---|---|
| 运行中 | 默认 / 恢复 | 正常 | 正常 | — | 运行中 |
| **只读** `readonly` | 执行器或插件配置 | 拒绝，`READONLY_MODE` | 允许 | 改配置（双开关任一关闭即解除） | 跟配置 |
| **已暂停** `paused` | 设置页/托盘「暂停」（`reason=user_button`） | 拒绝，`PAUSED` | 允许（只读不改桌面） | 本机一键「恢复」 | **不持久化**（重启回运行中） |
| **已停止** `stopped` | 热键/托盘「紧急停止」/管理员命令；模型 `go_stop` 只停**本会话** | 全局停止 → `SESSION_STOPPED`；会话停止 → 仅该会话 `SESSION_STOPPED` | 允许 | **仅本机**托盘/设置页「恢复」需**二次确认**（不提供远程解除） | 全局停止**持久化**（`%LOCALAPPDATA%\WhaleGo\control-state.json`） |

- 暂停与停止都**立即释放 controller lease、取消排队动作**（在途派发不撤销，走 `possibly_sent`）；暂停不持久化、全局停止持久化。
- 模型永远只能“停”不能“恢复”（评审 M4、DESIGN §5.6）：热键只停不恢复，托盘恢复要二次确认。每次状态变化写应用日志 + 一条审计（`tool=control, op=pause|resume|stop`）+ 重要级气泡。
- 概览页大按钮：`[暂停]`（暂停后变绿色 `[恢复]`）、`[紧急停止]`（红色，需确认）+ 一行状态说明；托盘菜单同一套（§5.2）。“可能被模型操作”的任何时候托盘图标都不能隐藏。

### 9.2 全局热键

- 默认 `Ctrl+Alt+Shift+F12`（DESIGN §5.8 `hotkeys.emergencyStop`）；行为：**紧急停止**（全局停止态，不是暂停），立即生效、不弹确认，再按一次也**不恢复**（防误解除）。
- 注册走 Contracts `IHotkeyRegistrar`（Platform 用 `RegisterHotKey` 实现，GUI 不 P/Invoke）；注册失败（被占用/非法）：设置页红字 + 托盘气泡 + 诊断页一条，**不静默、不自动换键**。
- 只在当前交互会话有效：锁屏 / UAC 安全桌面下收不到（写进「关于/限制」）。`hotkeys.openSettings` 保持 `null`（未拍板，不启用；§13 保留项）。

### 9.3 `go_health` 状态字段（已落地 PROTOCOL v1.2 §3.2）

模型必须**能看见**止血状态，不能只靠 `go_act` 报错。PROTOCOL 已落地 6 个顶层可选字段（GUI 只读展示，不新增协议）：

```jsonc
{
  "schema_version": "whalego-health-v1",
  "ok": true,                    // 语义不变：服务本身健康（暂停≠故障）
  "paused": false,               // 暂停态（GUI/托盘一键恢复）
  "pause_reason": null,          // "user_button" | "startup" | null
  "paused_since_ms": null,
  "stopped": false,              // 全局 kill switch（热键/托盘/管理员命令）
  "session_stopped": false,      // 本会话被模型 go_stop 停止
  "accepting_actions": true,     // = !paused && !stopped && !session_stopped（只读是另一维度）
  "...": "其余字段不变"
}
```

GUI 状态卡片直接映射：`只读 / 暂停 / 停止（全局/本会话）/ 运行中`；诊断页展示 `controller{held,mine,expires_in_ms}`（D1 后不含会话 id 全量）。

### 9.4 错误码 `PAUSED`（已裁决 D2，已落地 PROTOCOL §8 / contracts）

| 码 | 场景 | retry | dispatch_status |
|---|---|---|---|
| `PAUSED` | 用户在本机暂停（GUI/托盘按钮） | `never` | `not_sent` |
| `SESSION_STOPPED` | 全局停止，或本会话被模型 `go_stop` 停止 | `never` | `not_sent` |
| `READONLY_MODE` | 只读配置生效 | `never` | `not_sent` |

- 插件侧 `errors.py` 实现时同步文案：`用户已在本机暂停 WhaleGo，暂停期间不会执行动作；不要重试，等待用户恢复后再 go_observe。`；GUI 不做错误文案（与插件/协议解耦）。
- GUI 只展示审计里的错误码统计（诊断页工具计数），不解释协议语义。

### 9.5 token 掩码 / 复制 / 轮换

见 §7.2。安全要点：掩码显示（首 5 后 4），**永不**在 UI 常驻明文；不写日志/审计；不进 tooltip；复制后 60s 条件清空剪贴板；
轮换即时生效（旧 token 立刻 401）；secrets 文件 ACL 仅当前用户，检测到 Everyone/Users 可读 → 设置页黄条 + 一键修复（拒绝继承 + 仅当前用户）。

### 9.6 监听面风险（0.0.0.0 vs 仅本机）

- 默认 `0.0.0.0:8765`（DESIGN §3.4/§8.2 已定，容器必须能连）；「连接与配对」页顶部常驻横幅：`⚠ 当前监听所有网卡：局域网内任何能访问 TCP 8765 的设备都可能尝试连接（仍需 token）。建议用防火墙规则限定 WSL/Docker 网段。`
- 「仅本机」（127.0.0.1）保留，但必须显示准确后果：`host.docker.internal 走宿主虚拟网卡，不是回环；改成仅本机后容器将无法连接`（常见直觉误区）。只适合“宿主上跑 MCP 客户端做测试”。
- 更推荐：绑定 Docker/WSL 虚拟网卡的宿主 IP（GUI 下拉列本机网卡地址），配合防火墙规则——值仍是 `listen.address`，不新增键。
- 防火墙：GUI 不自动提权；提供「复制建规则命令」（`scripts/firewall.ps1` 实现阶段提供）+ 自检查询（§8.4）。

### 9.7 UIPI / 高完整性窗口限制 + 审计 fail-closed 危险项

- WhaleGo 以用户中完整性运行：UIA 可能读得到高完整性窗口，但**动作会被 UIPI 拒绝**（`UIPI_BLOCKED`，PROTOCOL §8）；**不建议**“以管理员身份运行 WhaleGo”（改变输入/焦点语义、触发 UAC、与“用户日常桌面”不一致），高完整性目标请人工操作。
- 「关于/限制」+ 诊断页展示当前完整性级别；同页如实写：锁屏/无人值守、多屏负坐标、Chromium/GPU 坐标降级、WGC P2——这些不是 GUI 能修的。
- `audit.requireDurableForActions` 默认 **true**（DESIGN §5.8、ARCHITECTURE §1.2 #9）；设为 false = “磁盘写不进去也照做动作”，事故无法复盘。UI：放在「隐私与审计 → 高级（不推荐）」折叠区，默认**只读 + 锁图标**；
  解锁后 ① 红字 3 行讲后果 ② 必须输入 `我了解风险` 才能保存 ③ 保存后托盘黄标“审计：非 fail-closed”，直到改回。`audit.enabled=false` 同样二次确认。

### 9.8 截图默认开启（Q4）的隐私边界

- 默认 `screenshot.enabled=true`，但 `defaultInclude=false`：**默认不自动附图**；模型显式 `include_screenshot=true` 才请求（三开关 AND，见 §7.3）。
- 敏感窗口命中 → 只返回树 + `advisories=["screenshot_suppressed"]`（PROTOCOL §11）；自身进程（`WhaleGo.*`，含设置/诊断窗口）**永不观察、不截图**（D9）。
- 图片存在时插件在 OK 摘要行最前固定隐私提示（`未经用户明确要求不得转发`）；GUI 内在「执行策略」页用一行说明“截图默认开启但不自动附图”，并可一键关闭（热生效）。
- 关掉后：模型请求截图 → 返回树 + `screenshot_disabled` advisory，不报错；诊断页状态卡显示“截图：关”。

## 10. 视觉与交互规范（参考 deepseek.com 的克制视觉语言，原生 WPF 实现）

### 10.1 设计语言（只借“感觉”，不搬资产）

- 关键词：**克制、留白、低饱和中性色、单一蓝色强调、圆角卡片、细边框、轻阴影、短动效**；信息密度中等，不堆控件。
- 只用三种视觉层级：页面背景（canvas）→ 卡片（surface）→ 卡片内分组；**不用渐变、不用玻璃拟态、不用多彩图标**；状态色只用于状态点/徽标与危险按钮。
- 图形与图标：优先 `Segoe Fluent Icons`（Win10/11 自带）或自绘矢量 `Path`；不引入图片主题包；Logo 用 WhaleGo 品牌图（NAMING §4.4）。
- 明确“不做”：不复制参考站点的页面结构/文案/图形资产；不做网页式滚动长页；不做营销/落地页。

### 10.2 颜色 token（浅 / 深两套，全部 `DynamicResource`）

| Token | 浅色 | 深色 | 用途 |
|---|---|---|---|
| `Color.Canvas` | `#FFFFFF` | `#0F1115` | 页面背景 |
| `Color.Surface` | `#F6F7FB` | `#161A22` | 卡片/分组背景 |
| `Color.SurfaceAlt` | `#EEF1F6` | `#1D222C` | 输入框、悬停、次级按钮 |
| `Color.Sidebar` | `#FAFBFD` | `#12151B` | 左侧导航背景 |
| `Color.Text.Primary` | `#1A1D24` | `#E8EAF0` | 主文字 |
| `Color.Text.Secondary` | `#5A6270` | `#9AA3B2` | 说明/标签 |
| `Color.Text.Tertiary` | `#8A93A3` | `#6E7686` | 占位/禁用 |
| `Color.Accent` | `#4D6BFE` | `#6D86FF` | 主按钮、选中态、链接 |
| `Color.AccentHover` / `Pressed` | `#3F5BEA` / `#3450DC` | `#7E94FF` / `#8FA3FF` | 悬停/按下 |
| `Color.AccentSubtle` | `#EEF1FF` | `#1C2340` | 选中导航底、浅色徽标底 |
| `Color.Border` | `#E4E7EE` | `#242A36` | 卡片/输入边框 |
| `Color.BorderStrong` | `#CFD6E4` | `#333B4A` | 分隔线、悬停边框 |
| `Color.FocusRing` | `#4D6BFE` 40% | `#6D86FF` 55% | 键盘焦点环（2px 外扩） |
| `Color.Success` | `#2E9E6B` | `#45C08A` | 运行中/成功 |
| `Color.Warning` | `#C9820A` | `#E8B45A` | 暂停/警告 |
| `Color.Danger` | `#D93B3B` | `#FF6B6B` | 停止/异常/危险按钮 |
| `Color.Idle` | `#8A93A3` | `#6E7686` | 无连接/禁用 |
| `Color.Shadow` | `#0F1115` 8% | `#000000` 45% | 卡片/浮层阴影 |

- 语义色（成功/警告/危险）在深色下**提亮**，保证对比度；状态徽标用“色点 + 文字”，不只靠颜色（色盲友好）。
- 正文（14 DIP）与背景对比度 ≥ **4.5:1**，大字/图标 ≥ 3:1；两套主题都要过自检（§8.4）与手工抽查。

### 10.3 字体与排版

- 字体栈（WPF `FontFamily` 支持逗号回退）：`Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei`；界面语言中文为主，数字/英文用同一栈。
- 字号/行高（DIP）：页面标题 20/28、卡片标题 15/22（Semibold）、正文 13/20、说明 12/18、徽标 11/16；等宽（token/日志/JSON）`Cascadia Mono, Consolas` 12/18。
- 中文排版：全角标点；正文一行 ≤ 40 汉字；按钮 2–4 字；数字与单位间不空格；避免两端对齐造成的字距不均（WPF 默认左对齐即可）。

### 10.4 尺寸与布局（880×620，左导航 192）

- 主窗口：默认 **880×620**（DIP），最小 760×520；原生标题栏（Win11 原生圆角 + DWM 暗色标题栏，§10.7）；状态栏 28。
- 左导航 **192px**：顶部 Logo + 产品名（WhaleGo），6 项导航（行高 40、圆角 8、选中 `AccentSubtle` 底 + `Accent` 文字/左指示条），底部版本号。
- 内容区：`Padding 24`；页头（标题 20 + 一句说明）高约 56；卡片间距 16；表单行高 34、行间距 12；页面底部固定操作条（`[恢复默认] [应用]` + 状态文字）。

```text
┌──────────────────────────────────────────────────────────────┐
│ WhaleGo 设置                                       [_][□][X] │
├──────────────┬───────────────────────────────────────────────┤
│ 鲸 WhaleGo   │ 状态        ● 运行中        主题 [跟随系统 ▾] │
│ ● 概览       │ 监听        0.0.0.0:8765（实际绑定）          │
│   连接与配对 │ 会话/租约   2 个会话 · 无租约                 │
│   执行策略   │ 版本        WhaleGo 0.1.0 · protocol v1       │
│   隐私与审计 │ ┌───────────────────────────────────────────┐ │
│   诊断与日志 │ │ [暂停]  [紧急停止]  Ctrl+Alt+Shift+F12    │ │
│   关于/限制  │ └───────────────────────────────────────────┘ │
│   v0.1.0     │ [打开配对向导]  [复制插件配置]                │
├──────────────┴───────────────────────────────────────────────┤
│ ● 运行中 · 0.0.0.0:8765 · 截图：开（不自动附图）             │
└──────────────────────────────────────────────────────────────┘
```

### 10.5 组件规范（自写 Style，不引第三方库）

| 组件 | 规格 |
|---|---|
| 卡片 `Card` | `Surface` 底 + 1px `Border` + 圆角 12 + 内边距 20；深色下不加投影（用边框分层），浅色可用 8% 阴影 |
| 主按钮 `Button.Primary` | 高 34、圆角 8、`Accent` 底白字；hover `AccentHover`、pressed `AccentPressed`；禁用降透明度 40% |
| 次按钮 `Button.Secondary` | `SurfaceAlt` 底 + `Border`；危险操作 `Button.Danger`（`Danger` 底白字，仅紧急停止/轮换/退出） |
| 输入框 | 高 34、圆角 8、`SurfaceAlt` 底 + `Border`；聚焦边框 `Accent` + 焦点环；非法 `Danger` 边框 + 下方红色说明 |
| 开关 `ToggleSwitch` | 40×22、圆角 11；开 `Accent`、关 `BorderStrong`；空格切换，焦点环可见 |
| 分段控件 `SegmentedControl` | 用于主题切换（跟随系统/浅色/深色）：高 28、圆角 8、选中 `Surface` + `Border` |
| 状态徽标 `StatusPill` | 圆角 999、高 22；色点 6px + 文字（运行/暂停/停止/异常）；底色用对应语义色 12% 透明 |
| 表格（诊断） | 行高 36、表头 32、无斑马纹；悬停 `SurfaceAlt`、选中 `AccentSubtle`；窄屏横向滚动而非压缩列 |
| 导航项 `NavItem` | 行高 40、圆角 8；选中 `AccentSubtle` 底 + `Accent` 文字 + 左侧 3px 指示条；hover `SurfaceAlt` |
| 提示条 `InlineBanner` | 圆角 8、内边距 12；info（`SurfaceAlt`）/ warning（`Warning` 12% 底）/ danger（`Danger` 12% 底）+ 图标 |
| 对话框 | 系统原生标题栏 + 居中卡片样式；危险确认默认焦点在“取消”；遮罩 `#000` 35%（浅）/55%（深） |

- 所有颜色/圆角/间距只从 `Themes/*.xaml` 的 token 取；**XAML 视图里禁止出现 `#RRGGBB` 字面量**（用一条静态检查测试守住，§12.2）。
- 焦点/悬停/按下/禁用四态必须成对定义（不能只做 hover）；禁用态同时改变文字色与不透明度（不只靠灰度）。

### 10.6 动效与交互反馈

- hover：**120ms** `ColorAnimation`/透明度过渡；focus：**立即**出现 2px 焦点环（不做动画，键盘用户不应等）；pressed：**80ms** 下沉 1px 或加深底色。
- 主题切换：**150ms 交叉淡入**（旧字典→新字典由 `ThemeService` 在两帧间切换 `MergedDictionaries`，不闪烁、不重建窗口）；页面切换：120ms 内容淡入（不做滑动）。
- 页面/卡片出现、行悬停等**全部 ≤150ms**；不做弹跳、缩放、旋转；**遵循系统“显示动画”设置**：`SystemParameters.ClientAreaAnimation=false` 时全部过渡改为 0ms。
- 关键操作（暂停/紧急停止/退出）**不做动画**，立即出结果；危险确认框不做淡入（避免拖慢反应）。

### 10.7 主题机制（`ui.theme`，热切换、无需重启）

- 资源组织：`Themes/Shared.xaml`（字号/间距/圆角/控件 Style）+ `Themes/Light.xaml` + `Themes/Dark.xaml` + `Themes/HighContrast.xaml`（映射 `SystemColors`）；运行时替换 `MergedDictionaries` 中的一个槽位。
- `ThemeService`：读 `ui.theme`（`system` 时跟随 Windows：监听 `SystemEvents.UserPreferenceChanged` + `SystemParameters.StaticPropertyChanged`，读 `AppsUseLightTheme`）；切换 = 换字典 + 通知 `IWindowTheme` 更新标题栏；失败回滚。
- 标题栏暗色：GUI 通过 `IWindowTheme.TryApplyDarkTitleBar(hwnd, dark)`（Contracts 端口，Platform 用 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` 实现）——**GUI 自身不写 P/Invoke**；Win10 不支持时回退“浅色标题栏 + 深色内容”，在「关于」页注明。
- 高对比度：`SystemParameters.HighContrast=true` 时切 `HighContrast.xaml`（系统色优先），保证可读性优先于“好看”；系统主题/对比度变化 1s 内跟随。
- 持久化：只写 `ui.theme` 到 `config.json`（不进 `ui-state.json`）；窗口位置/大小/最后页签等纯 UI 状态写 `%LOCALAPPDATA%\WhaleGo\ui-state.json`（与业务配置分离）。

### 10.8 DPI 与渲染

- `app.manifest`：`<dpiAware>true</dpiAware>` + `<dpiAwareness>PerMonitorV2</dpiAwareness>`（进程级；Platform 坐标换算同样依赖它，DESIGN §5.4）。
- WPF 布局天然使用 DIP；窗口 `UseLayoutRounding=True`、`SnapsToDevicePixels=True`（边框/1px 线清晰）；图标用矢量或 `DrawingImage`，不用位图缩放。
- 验收：100%/125%/150%/200% 与双屏混合 DPI 拖动，6 页 × 2 主题不得错位/裁切/模糊；文字不截断。

### 10.9 键盘可达性与无障碍

| 键 | 行为 |
|---|---|
| Tab / Shift+Tab | 按视觉顺序移动（显式 `TabIndex`）；焦点环始终可见 |
| Alt+字母 | 标签助记符（`_` 标记，WPF `AccessText`） |
| Ctrl+S | 应用当前页；Esc 隐藏窗口；F5 刷新诊断；Enter 触发默认按钮（危险对话框默认“取消”） |
| 方向键 / 空格 | 表格/导航内移动；开关与分段控件用方向键切换 |

- `AutomationProperties.Name/HelpText` 覆盖所有交互控件与状态徽标（屏幕阅读器读“已暂停”而不是只有颜色）；自绘状态点提供 `AccessibleRole`。
- 高对比度模式：品牌色退化为系统色；图标/文字双编码保证不用颜色也能区分状态；禁用态不只靠颜色。

## 11. 与现有文档的修订清单（已全部落地）

### 11.1 已裁决并落地的部分（核对基准 v0.5 / v1.2 / contracts 1.2；2026-10-31 已全部同步）

| 决策 | 落点 | 状态 |
|---|---|---|
| D2 三态止血 + 恢复仅本机 | PROTOCOL §3.2/§8/§9；contracts `control_states` | ✅ 已落地 |
| D3 `go_health` 6 字段 + healthz 最小面 | PROTOCOL §1/§3.2/§9；contracts | ✅ 已落地 |
| D9 敏感窗口/自身窗口排除 + advisory 闭集 | PROTOCOL §11/§3.6 | ✅ 已落地（DESIGN §8.1 已随 D21 改为“截图默认开启、可一键关闭”） |
| D13 token/secrets | DESIGN §5.8/§9.2；PROTOCOL §11；NAMING §4.4 | ✅ 已落地 |
| D14 配置来源优先级 + 新增组 | DESIGN §5.8；ARCHITECTURE §3.8 | ✅ 已落地（D21/Q5 已全删 `startup.*`） |
| D15 单进程宿主/L7/`IExecutorControl`/`--headless` seam | DESIGN §5.10；ARCHITECTURE §2.1/§3.1/§3.3/§3.9/§6/§7 | ✅ 已落地（D21 已改 WPF 设置窗口 + WinForms 托盘） |
| **D21 Q4 截图默认开** | PROTOCOL §11（三开关语义）；contracts `privacy.screenshot_default=true`；DESIGN §5.8 `enabled=true` | ✅ 已落地（v0.5） |
| **D21 Q1/Q2/Q3** | 与本稿 v0.2 设计一致（概览首屏/隐藏托盘/两档止血） | ✅ 无需改 |

### 11.2 D21 落地记录（主文档 v0.5 已全部同步；本表保留为变更溯源）

| 文档 | 章节·原句 | 改成 |
|---|---|---|
| DESIGN | 页眉版本行、§5.10 末尾 `docs/GUI.md` v0.2 | `docs/GUI.md` **v0.3**；DESIGN 升 **v0.5**（或 v0.4 小版本，见 §11.3 第 4 条） |
| DESIGN | §5.10 “内嵌 kernel + 托盘 + WinForms 设置窗口” | “内嵌 kernel + WinForms 托盘 + **WPF 设置窗口**（`UseWPF`+`UseWindowsForms`，零第三方）” |
| DESIGN | §5.10 标题“宿主形态与自启”与“自启 = 启动文件夹快捷方式…” | 标题改“宿主形态”；**删除自启整句**，改为“手动启动（不做开机自启，D21/Q5）” |
| DESIGN | §5.8 JSON：`"screenshot": { "enabled": false, … }`、`"startup": { "autoStart": false }`、`"ui": { "notificationsLevel": "important" }` | `"enabled": true`；保留 `"defaultInclude": false` 并注明“默认不自动附图，模型按需 include_screenshot”；**删除 `"startup"` 组**；`"ui"` 组加 `"theme": "system"` |
| DESIGN | §8.1 威胁表“截图三开关默认关”、§8.3 “截图默认关闭” | 改为“截图默认开启但**默认不自动附图**；敏感/自身窗口抑制；可一键关闭”（D21/Q4，隐私权衡） |
| DESIGN | §9.1 “自启 = 启动文件夹快捷方式（GUI 复选框）” | 删除；改“手动启动”（Q5）；§9.3 排障表可加一行“界面异常 → 检查 Themes 资源/`--headless --self-check`” |
| DESIGN | §10.3 P1 行“托盘 GUI v1”；P2 行“计划任务/崩溃重启” | P1 改“托盘 GUI v1（WPF 浅/深主题）”；P2 删除“计划任务自启”“深色模式”，改“自定义主题/更丰富外观、崩溃看护、UI 自动化” |
| DESIGN | §11 验收 | 加 4 条：浅/深主题热切换无需重启；6 页 × 2 主题 × 100/200% DPI 无错位；深色正文对比度 ≥4.5:1；`screenshot.enabled=true` 且 `defaultInclude=false` 时 `include_screenshot=false` 不得附图 |
| DESIGN | §12.1 决策记录第 6 条“WinForms 设置 GUI”；§12.3 第 1 条自启 | 第 6 条改“**WPF 设置 GUI（WinForms NotifyIcon 托盘）**”；新增 D21 行（Q1–Q6 拍板）；§12.3 第 1 条删自启，改“手动启动” |
| ARCHITECTURE | §2.1 L7 图与表 | `NotifyIcon 托盘 + SettingsForm` → “**WinForms NotifyIcon 托盘 + WPF SettingsWindow**（同进程 `UseWPF`+`UseWindowsForms`）”；禁止列保持 |
| ARCHITECTURE | §3.1 依赖矩阵 `WhaleGo.Gui` 行 | “WinForms 托盘/设置窗口” → “**WPF 设置窗口 + WinForms 托盘**（`UseWPF`+`UseWindowsForms`，零第三方）；只引用 Contracts” |
| ARCHITECTURE | §3.3 `IExecutorControl`：`SetStartupShortcut` | **删除**该方法（已落地）；新增 `IWindowTheme` / `IHotkeyRegistrar` 两个窄端口（**已裁决 D22，已落地**，GUI 不 P/Invoke） |
| ARCHITECTURE | §3.5 `ControlService` 描述、§3.9 启动顺序、§3.14 seam | ControlService 删自启；启动顺序加“ThemeService 初始化（读 ui.theme，不阻塞 ready）”；seam 加“WPF presenter 单测 + STA 构造冒烟 + 主题资源完整性/禁用色值扫描” |
| ARCHITECTURE | §6 目录树 `WhaleGo.Gui/` 注释、§7 T7 验收 | 注释改“WPF 设置窗口 + WinForms 托盘/热键消息窗”；T7 验收改“5 步配对/6 页/三态止血/`--headless` 自检/**浅深主题热切换与 DPI 100·200% 全绿**/只引用 Contracts” |
| NAMING | §0 与 §4.4：`WhaleGo.Gui`（WinForms 库）、“启动快捷方式 `WhaleGo.lnk`” | 改“**WPF+WinForms 混合 GUI 库**”；**删除** `WhaleGo.lnk`/自启措辞；可加主题字典名（`Themes/Light.xaml` / `Dark.xaml` / `HighContrast.xaml`） |
| PROTOCOL | 页眉版本、§0/§11/§12 | 核对：§11 截图默认开已落地；若升版再补 §0/§12 的“用户拍板已裁决”说明（协议行为未变，`protocol_version` 仍为 1） |
| contracts | `privacy.screenshot_default` | 已为 `true` ✅ 无需改；如加 GUI 相关键不在契约内（GUI 不产生 wire 字段） |
| README | 状态行、一页架构 L7 行、v1 已知限制、路线图 | 状态行 GUI v0.3；L7 改“WinForms NotifyIcon 托盘 + WPF 设置/配对”；**已知限制删除“深色模式”**，新增“无开机自启（手动启动）”“不做 WebUI”；路线图 P1 加“WPF 浅/深主题” |

### 11.3 D21 引出的裁决项（**已全部拍板：采纳**，D22）

1. **Contracts 新增两个窄端口**：`IWindowTheme`（DWM 暗色标题栏）与 `IHotkeyRegistrar`（RegisterHotKey）——否则 GUI 要么写 Win32 P/Invoke（违反分层），要么无法做暗色标题栏/热键。**已采纳并落地** DESIGN §5.3 / ARCHITECTURE §3.3。
2. **`ui.theme` 键位与默认**：建议 `ui.theme: "system"`（新增到 DESIGN §5.8 `ui` 组）；环境变量覆盖名 `WHALEGO_UI__THEME`。
3. **`startup.*` 删除范围**：配置组、环境变量前缀、`SetStartupShortcut` 端口、DESIGN/NAMING/README 措辞、T4/T6 脚本（`startup-shortcut.ps1`）、测试用例一并删除。
4. **文档版本**：DESIGN/ARCHITECTURE → **v0.5**（已落地）；GUI → **v0.3**；README → **v0.5**；contracts `contract_version` 不变（无 wire 变化）。

### 11.4 已知冲突 / 风险（如实写，不回避）

- **无自启的后果**：重启后不点开 WhaleGo，模型不可用；托盘/热键也不存在。这是 Q5 的直接代价，README/关于页必须写明。
- **Win10 暗色标题栏**：DWM `DWMWA_USE_IMMERSIVE_DARK_MODE` 仅 Win11 支持；Win10 回退“浅色标题栏 + 深色内容”，外观不完全一致（无 API 可解，不自绘标题栏）。
- **系统渲染的组件不跟随应用主题**：托盘菜单（`ContextMenuStrip`）、文件选择框、气泡通知由系统渲染，跟随 Windows 而非 `ui.theme`；这是“不引第三方/不自绘”的取舍。
- **WPF PerMonitorV2 历史坑**：跨屏拖动偶有 1px 抖动/字体回退；验收用 100/125/150/200% + 混合 DPI 双屏覆盖，必要时对窗口设置 `TextOptions.TextFormattingMode=Display`。
- **无第三方 UI 库**：控件模板全部自写，长尾控件（NumberBox/日期选择）缺失时用原生控件 + 简洁样式，不临时引包。

## 12. 测试计划

### 12.1 手工清单（每 PR 至少 P0 段；发版全跑）

1. 首启：删 `%APPDATA%\WhaleGo` 与 `%LOCALAPPDATA%\WhaleGo` → 手动双击 → 向导 5 步（含“截图默认可用但默认不自动附图”说明）→ 复制片段 → 完成；向导未结束时再双击 exe → 不崩、不抢端口、设置窗口被唤起。
2. 关闭窗口：X 与 Esc → 进程仍在、托盘在、MCP 仍可调（`go_health` OK）；托盘双击打开、右键菜单项状态正确（**无“开机自启”项**；暂停/恢复随状态变；token 不可读时“复制插件配置”置灰）。
3. 暂停：点暂停 → 托盘琥珀 → 模型 `go_act` 得 `ERROR[PAUSED]`、`go_health.paused=true` → 点恢复 → 动作恢复。
4. 紧急停止：按热键 → 3 秒内托盘变红 + 气泡 → 动作得 `SESSION_STOPPED` → 托盘点恢复需二次确认 → 再按热键不恢复；停止状态下杀进程再启动 → 仍停止。
5. 热键冲突：先用其它程序占用该组合 → 启动后托盘/设置页有明确告警。
6. 截图（Q4）：默认 `enabled=true` 时 `include_screenshot=false` **无图**、`true` **有图**；`screenshot.enabled=false` 时请求截图 → 树 + `screenshot_disabled`；敏感窗口 → `screenshot_suppressed`；自身设置窗口不出现在 `go_apps/go_windows`。
7. token：复制 → 粘进插件配置（`allow_screenshot=true`）→ 连接成功；轮换 → 旧 token 立即 401、新 token 生效；`--headless --self-check --json` 退出码正确且报告不含 token。
8. 配置：改端口 8766 → 提示重启 → 立即重启 → 实际监听从配置/自检确认；非法端口 → 拒绝保存并红字；外部改 config.json → 保存时提示冲突可重新加载；环境变量覆盖项只读 + 来源徽标正确。
9. **主题**：跟随系统（在 Windows 设置里切浅/深，1s 内跟随）→ 手动浅色/深色 → 重启后保持 `ui.theme`；切换**无需重启、无闪烁、无残留旧色**；6 页 × 2 主题截图对比无错位。
10. **深色可读性**：正文对比度 ≥4.5:1、图标/大字 ≥3:1；焦点环在浅/深背景都清晰；禁用态可辨认；高对比度模式自动回退系统色。
11. **视觉态**：hover/pressed/focus/disabled 四态齐全；过渡 ≤150ms；系统关闭“显示动画”后无过渡；危险按钮/确认框立即响应。
12. **托盘图标**：浅色与深色任务栏下都清晰可辨（双版本）；tooltip ≤63 字符且不含 token/会话 id。
13. DPI/字体：100/125/150/200% + 双屏混合 DPI 拖动；中文字体无截断；圆角/1px 边框不模糊。
14. 键盘/无障碍：只用键盘完成首启向导、主题切换、暂停/恢复；屏幕阅读器读到状态徽标与所有输入控件。
15. 退出：有租约时退出 → 警告文案正确；退出后热键失效、托盘消失、端口释放；注销/关机不弹窗、不卡住（≤3s）。
16. `--headless`：CI/无桌面可启动（无 WPF 窗口/托盘），MCP 可调用，自检 JSON 正常。

### 12.2 自动化 seam（必须有）

| 层 | seam | 测什么 |
|---|---|---|
| Application | `IExecutorControl` 纯逻辑 | 三态迁移、control-state 持久化、启动时恢复停止态 |
| Application | `ConfigStore` | 校验规则全表（类型/范围/枚举/glob）、未知键保留、原子写、来源优先级、冲突检测、`ui.theme` 热生效标记 |
| Application | `SelfCheckService` | fake probes 输出各项结果；`--json` 结构稳定 |
| Gui 非视图层 | `ThemeService` + fake `IWindowTheme`/`SystemEvents` | `system/light/dark` 决策矩阵、系统主题变化跟随、失败回滚、HighContrast 切换 |
| Gui presenter | `SettingsViewModel` + fake `IExecutorControl` | 脏标记、应用/回滚、重启徽标、掩码/复制按钮可用性（不 new 真控件） |
| Gui 构造冒烟 | `new MainWindow(fake)` + `CreateControl()`，跑在 STA 线程 + `Dispatcher` | 无异常、Tab 顺序非空、控件有 `AutomationProperties.Name`（不显示窗口） |
| Gui 静态检查 | XAML 扫描（Views/*.xaml） | **禁止颜色字面量** `#RRGGBB`；禁止第三方 UI 包引用；禁止 `WhaleGo.Platform` 引用 |
| E2E | `--headless --self-check --json` + MCP 客户端 | 首启生成 token、认证 200/401、暂停态可见 |

### 12.3 UI 自动化：本期不做（P2 再评估）

理由：FlaUI 对 WPF 可用，但 UI 自动化维护成本 > 收益（控件定位脆弱、CI 需交互桌面、主题/DPI 组合爆炸）；本期用“presenter 单测 + STA 构造冒烟 + XAML 静态检查 + 手工清单（含双主题截图对比）”覆盖。
若 P2 回归成本变高，再引 FlaUI 做 3 条主路径（首启向导 / 暂停恢复 / 主题切换），复用 `WhaleGo.Platform.Tests` 的 Windows-only 运行条件。

## 13. 用户拍板结果（2026-10-31，D21）

| # | 问题 | 用户选择 | 落地 |
|---|---|---|---|
| Q1 | 设置首屏 | **A 概览**（状态 + 两个止血按钮 + 配对入口） | §6.1 / §10.4；首屏右上角放主题切换 |
| Q2 | 点窗口 × | **A 隐藏到托盘** | §4.3（`ShutdownMode=OnExplicitShutdown`；唯一退出在托盘菜单） |
| Q3 | 止血档位 | **A 暂停 + 紧急停止两档**（暂停一键恢复；紧急停止需二次确认） | §9.1；热键只触发紧急停止 |
| Q4 | 截图默认 | **B 默认开** | `screenshot.enabled=true`（已落地 DESIGN §5.8 v0.5）；`defaultInclude=false`（默认不自动附图）；插件片段 `allow_screenshot=true`；敏感/自身窗口抑制不变（§9.8） |
| Q5 | 开机自启 | **不做**（手动启动） | §4.4：删除启动文件夹/任务计划/`startup.*`/`SetStartupShortcut`/相关测试与文档 |
| Q6 | UI 外观 | **要好看 + 深色模式**（视觉参考 deepseek.com，**不做 WebUI**） | §3：WPF 设置窗口 + WinForms NotifyIcon 托盘；§10：浅/深双主题 token、圆角卡片、150ms 过渡；主题跟随系统 + 手动切换、热生效 |

拍板后不再是问题的项：GUI 技术栈（WPF）、深色模式（做）、开机自启（不做）、截图默认（开）。
**保留的可选项**（不阻塞实现，后续可加）：中文名「鲸跃/鲸渡/鲸行」（NAMING §6）、Logo 图形（NAMING §6）、`hotkeys.openSettings`（保持 `null`，如要再加）、P2 自定义主题色/更丰富外观、P2 自定义标题栏（当前用原生标题栏）。

## 14. 与 ARCHITECTURE §7 任务分解的映射（T1–T6 + T7，已按 D21 更新）

| 任务 | 现有范围 | GUI 相关增量 | 依赖 |
|---|---|---|---|
| T1 Contracts+Domain | DTO / 接口 / 状态机 / 契约文件 | 端口 `IExecutorControl`（**删 SetStartupShortcut**）/ `IStatusQuery` / `IConfigStore` / `ITokenService` / `ISelfCheck`；**新增 `IWindowTheme` / `IHotkeyRegistrar`（已裁决 D22，已落地 DESIGN §5.3/ARCH §3.3）**；DTO（`ControlState`/`StatusSnapshot`/`RecentRequest`/`SelfCheckReport`/`ConfigPatch`）；`PAUSED` 已落地 | — |
| T2 Platform-观察 | 窗口 / UIA / 截图 / state 缓存 | 自检只读探针：UIA/DPI/交互会话（扩 `ISystemProbe`）；端口/防火墙探测 | T1 |
| T3 Platform-输入 | 输入 / hit-test | **两个小实现**（放 Platform/System）：`IWindowTheme`（DWM 暗色标题栏）、`IHotkeyRegistrar`（RegisterHotKey）——GUI 不 P/Invoke | T1 |
| T4 Executor+守护 | MCP / 认证 / 会话 / 租约 / 审计 + 托盘 / 热键 | 单实例互斥与唤出事件；`ControlService`（三态+持久化，**无自启**）；`ConfigStore`/`TokenService`/`secrets.json`；`StatusService`/`SelfCheckService`；`--headless`/`--self-check`；托盘 5 态；热键失败告警；**启动顺序加 ThemeService 但不阻塞 ready** | T1（可先 fake） |
| T5 AstrBot 插件 | 7 工具 / 权限 / 图片 / 错误 | `PAUSED`/`SESSION_STOPPED` 文案；`go_health` 三态展示；README 更新 `allow_screenshot=true` 与“截图默认开但不自动附图” | PROTOCOL |
| T6 文档/脚本/E2E | docs / scripts / E2E | 维护 `docs/GUI.md` v0.3；`scripts/`：**删除 `startup-shortcut.ps1`**，保留 `self-check.ps1`，新增 `gui-theme-shots.ps1`（双主题截图对比）；E2E 用 `--headless --self-check --json`；手工清单进发版检查表 | T1,T4 |
| **T7 GUI（新增）** | — | `windows/src/WhaleGo.Gui/**`：`App.xaml`/`MainWindow`/6 页 Views+ViewModels/`ThemeService`/`Themes/{Shared,Light,Dark,HighContrast}.xaml`/`TrayShell`（WinForms NotifyIcon）/向导/诊断页/多尺寸双版本图标；`windows/tests/WhaleGo.Gui.Tests/**`：presenter、STA 构造冒烟、主题资源完整性、XAML 无颜色字面量 | T1（端口/DTO）、T4（宿主接线）；T2/T3 并行 |

**排期建议**：T1 → T4（kernel + 单实例 + 托盘骨架）→ T7（WPF 框架/主题/6 页），T2/T3/T5/T6 并行；**主题 token 体系要在 T7 第一周定稿**（之后所有页面都依赖它，返工成本最高）。
**写 scope 约定**：T7 只写 `WhaleGo.Gui/**` 与 `WhaleGo.Gui.Tests/**` 与 `docs/GUI.md`；`WhaleGo.sln` / `Directory.Build.props` / 主文档由 Lead 统一改。

---

> 本文档 v0.3 为**拍板定稿**：§13 是用户结论，§11.2 是交给 Lead 的主文档同步清单，§10 是可实现的视觉规格。
> 实现中若发现 WPF 能力与规格冲突，以“可访问性/安全 > 观感 > 动效”的顺序取舍，并在 §11.4 追加记录。
---

## 15. 实现说明（v0.3 落地记录；T7，2026-10-31）

> 本节只记录**已实现内容、与规格的偏差、以及与 T4 的集成契约**，不改变 §1–§14 的规格结论，也不改变本文版本号（仍为 **v0.3**）。
> 代码：`windows/src/WhaleGo.Gui/`（只引用 `WhaleGo.Contracts`）；测试：`windows/tests/WhaleGo.Gui.Tests/`。

### 15.1 已交付

| 交付物 | 落点 | 说明 |
|---|---|---|
| 主题 token / 控件样式 | `Themes/Shared.xaml`、`Themes/Light.xaml`、`Dark.xaml`、`HighContrast.xaml` | §10.2 的 19 个 `Color.*` token 全量、每套字典一套（静态测试逐文件校验）；`Brush.*` 随主题字典整体替换（见 §15.2-3）；圆角/字号/间距/字体栈与 `Button/Card/ToggleSwitch/SegmentedControl/NavItem/InlineBanner/Table/ToolTip/ScrollBar` 全部自写样式 |
| WPF 外壳 | `App.xaml(.cs)`、`Views/MainWindow.xaml(.cs)` | `ShutdownMode=OnExplicitShutdown`、无 StartupUri；左导航 192、默认 880×620（最小 760×520）、状态栏 28、右上角主题分段控件；关窗=隐藏托盘；Esc=隐藏、F5=刷新、Ctrl+S=应用当前页 |
| 主题服务 | `Theming/ThemeService.cs`、`ThemeCoordinator.cs`、`LiveSystemThemeProvider.cs`、`WpfThemeResourceHost.cs` | `system/light/dark` 决策矩阵 + 跟随系统 + 高对比度强制覆盖 + 换字典热切换 + `IWindowTheme` 标题栏通知 + 应用失败回滚；`ui.theme` 立即生效并 `SetConfig` 持久化，保存失败回滚视觉 |
| 托盘 | `Tray/TrayShell.cs`、`TrayState.cs`、`TrayIconProvider.cs`、`BalloonPolicy.cs`、`Assets/Icons/*.ico` | NotifyIcon + 5 态 × 浅/深任务栏共 10 个多尺寸 `.ico`（16/20/24/32/48/256）；菜单按 §5.2（无自启项）；双击=打开设置、单击无动作；tooltip ≤63 字符且不含 token/会话 id；气泡 30s 同类冷却 + ≤3 条/分钟 + `ui.notificationsLevel` 过滤；退出=唯一真实退出路径（默认取消 + 租约警告） |
| 6 个设置页 | `Views/*Page.xaml` + `ViewModels/*ViewModel.cs` | 概览 / 连接与配对 / 执行策略 / 隐私与审计 / 诊断与日志 / 关于·限制；所有内核调用经 `Infrastructure/SafeControl.cs`（后台线程 + 1s 超时 + 全异常捕获），UI 线程不阻塞 |
| 配置页机制 | `ViewModels/ConfigPageViewModel.cs`、`ViewModels/Fields/**`、`Validation/ConfigRules.cs` | 逐页脏标记、只 `SetConfig` 当前页改动键（patch 语义）、来源徽标（默认/配置文件/环境变量/命令行）且环境变量/命令行覆盖项只读、逐字段校验就地报错、重启生效徽标 + 应用后「重启执行器」提示、`恢复` 回滚到上次加载值 |
| 配对向导 | `ViewModels/PairingWizardViewModel.cs`、`Views/Dialogs/PairingWizardWindow.xaml` | 5 步：风险确认 / 监听+token（掩码、重新生成、复制）/ 插件配置片段（url 随端口、复制时替换 token 明文、127.0.0.1 警告）/ 本机连接测试 + 等待容器 + 一键自检摘要 / 完成（防火墙命令、目录、手动启动、AstrBot 侧步骤） |
| GuiShell | `GuiShell.cs`（公共面不变）、`Internal/GuiHost.cs` | 在独立 STA 线程内创建 WPF Application + 窗口 + 托盘 + 全局热键；对外只暴露既有构造/属性/方法/事件；`ExitRequested` 从后台线程触发；`Dispose` 注销热键（id=`emergency-stop`）、销毁托盘、关窗口、`Application.Shutdown()` 并 join ≤5s |
| 测试 | `WhaleGo.Gui.Tests`（125 个用例，0 失败） | 主题决策矩阵/fake 回滚/标题栏、配置校验表、逐页 apply/回滚/来源只读/重启徽标/危险项口语确认、托盘 5 态映射/tooltip/气泡策略、诊断表映射、XAML 静态检查（无颜色字面量/无第三方包/GUI 只引用 Contracts/图标多尺寸/主题 token 完整性）、STA 构造冒烟 + `AutomationProperties.Name` 全覆盖、GuiShell 公共面与 headless 行为 |

### 15.2 与规格的偏差（如实记录）

1. **系统主题跟随不引 `Microsoft.Win32.SystemEvents`**：为保持“零第三方 NuGet”，`LiveSystemThemeProvider` 用
   `SystemParameters.StaticPropertyChanged` + 1 秒一次的注册表轮询（`AppsUseLightTheme`）与 `SystemParameters.HighContrast` 比对，
   仍在 §10.7 要求的“1 秒内跟随”范围内；未使用 §10.7 提及的 `SystemEvents.UserPreferenceChanged`。
2. **微动效与系统“显示动画”开关**：主题切换（150ms 淡入）与页面切换（120ms 淡入）在
   `SystemParameters.ClientAreaAnimation=false` 时降为 0ms；但控件级 80–120ms 的 hover/pressed/开关滑块过渡属于状态反馈，未做全局关闭
   （WPF 无全局动画开关，逐 Storyboard 判断代价过高）。按 §10 末尾“可访问性/安全 > 观感 > 动效”的优先级取舍。
3. **`Brush.*` 定义在每套主题字典内（而非 Shared.xaml 一处派生）**：WPF 的 `Freezable`（`SolidColorBrush`）不适合持有跨字典的
   `DynamicResource` 表达式（实测换字典后不重新解析），因此每套主题字典各自给出 19 个 `Brush.*`；字面颜色只出现在
   `Themes/*.xaml`（静态测试仍保证视图零颜色字面量、19 个 `Color.*` token 每套齐全、视图引用的 `Brush.*` 一定存在且为 `DynamicResource`）。
4. **托盘“审计非 fail-closed 黄标”**：§9.7 要求“托盘黄标”，5 态图标集不新增第 6 态；实现为托盘菜单中的黄色告警行
   （“⚠ 审计：非 fail-closed（事故无法复盘）”）+ tooltip 追加“审计非 fail-closed”+ 保存后一次气泡，状态仍可用键盘/读屏获取。
5. **连接检测口径**：`IExecutorControl` 没有“外部请求来源/等待容器连接”专用 API，向导第 4 步与托盘“已连接”判定使用
   `StatusSnapshot.Sessions > 0` 或最近 5 分钟内的 `GetRecentRequests`（与 §5.1 判定一致），容器侧探测命令以“可复制、不自动执行”呈现。
6. **工具计数粒度**：`GetToolCounters()` 只给每工具总数（无成功/失败/错误码细分）；诊断页展示总数 + 由两次采样计算的每分钟速率，
   并借“最近请求（≤50）”展示错误码分布；不伪造细分数据。
7. **打开日志/审计目录**：Contracts 只有 `OpenDataFolder()`，按 §8.3 的明确要求用 `explorer.exe <path>`（纯 shell 激活、不 P/Invoke、
   不提权）；数据目录仍走内核端口。
8. **本机连接测试**：按 §7.4 用进程内 `HttpClient` 请求 `http://127.0.0.1:<port>/healthz`（带/不带 token），不新增 HTTP 路由；
   不替代内核自检。
9. **`screenshot.mode=wgc`**：按 §6.2 作为 P2 占位保留可选项并显示“会回退 printsummary/printwindow”的提示；内核行为由 T4 决定。
10. **高对比度**：`Brush.*` 映射系统 `SystemColors`（高对比度下品牌色退化），`Color.*` token 保留文档键集以维持工具/测试契约。

### 15.3 与 T4（Executor 组合根）的集成契约

- **公共 API 不变**：`GuiOptions(bool Headless, bool NoHotkey, string Version, string ProtocolVersion, string ExecutablePath, string ConfigPath, string LogsDirectory)`；
  `GuiShell(IExecutorControl, IWindowTheme, IHotkeyRegistrar, GuiOptions)` + `WindowTheme/Hotkeys/Options/Control` + `ExitRequested` +
  `ShowSettings()/HideToTray()/ShowBalloon(string,string,bool)/RefreshStatus()/RequestExit()/Dispose()`（有反射契约测试守护）。
- **GUI 自己拥有 UI 线程与 WPF Application**（`Internal/GuiHost.cs` 独立 STA 线程；`--headless` 时完全不建 UI）。T4 **不要**再创建
  `Application`/窗口；非 headless 只需构造 `GuiShell`。
- **退出语义**：托盘「退出」→ 确认框 → `ExitRequested`（在线程池线程触发）。T4 在事件里停内核、保存状态，然后调用 `Dispose()`；
  `Dispose()` 会注销热键、销毁托盘、关窗口并 `Shutdown()` WPF，最多等待 UI 线程 5s，不阻塞内核停机。
- **热键**：`IHotkeyRegistrar.TryRegister("emergency-stop", <hotkeys.emergencyStop>, callback)`，`NoHotkey` 或 `hotkeys.enabled=false` 时不注册；
  回调可能来自 Platform 线程，GUI 内部转后台 `Stop("hotkey")`，不阻塞回调；注册失败会托盘气泡 + 设置页/诊断页红字，不静默。
- **标题栏**：`IWindowTheme.TryApplyDarkTitleBar(hwnd, dark)` 在窗口创建与每次主题变化时调用；返回 `false` 时按 Win10 回退处理（关于页注明）。
- **配置值形状（写入 `ConfigPatch.Values`，键一律 camelCase `ConfigKeys.*`）**：`bool` / `int`（或 `long`）/ `string` /
  `privacy.sensitiveWindows` 用 `string[]` / `launch.allowlist` 用 `List<LaunchAllowlistEntry>`；
  读取侧兼容 `JsonElement`、`long`、`string`、allowlist 字典等形状（`Validation/ConfigValue.cs`），T4 侧无需为 GUI 改类型。
- **`GetConfigSources()`** 期望返回 `ConfigSourceKinds.*` 字符串；GUI 把 `environment`/`commandLine` 视为只读并显示覆盖变量名。
- **首启判定**：`ConfigPath` 不存在或 `GetTokens()` 无可用 token → 启动后显示设置窗口并弹出 5 步向导（可跳过）。
- **状态轮询**：可见时每 2s 一次 `GetStatus`（1s 超时），托盘状态随 `StatusSnapshot` 更新；`RefreshStatus()` 可立即触发。
- `GuiShell` 使用的 `IExecutorControl` 方法全集：`GetStatus / GetRecentRequests / GetToolCounters / GetConfig / GetConfigSources / SetConfig /
  GetTokens / RotateToken / RevealTokenForCopy / RunSelfCheck / ReadAuditTail / Pause / Resume / Stop / ResetStop / OpenDataFolder / RequestRestart`。

### 15.4 待 Lead 决策的 Contracts 缺口（本任务未改 Contracts）

1. `GetToolCounters()` 返回 `IReadOnlyDictionary<string,long>`：无成功/失败/错误码细分，诊断页只能“总数 + 最近请求”近似；
   如需 §8.2 的完整表格，建议后续在 Contracts 增补只读诊断 DTO。
2. 无“外部请求/最后连接时间”端口：向导步骤 4 与托盘“已连接”只能用 `Sessions` + 最近请求推断（口径已与 §5.1 对齐）。
3. 无按目录打开的端口（只有 `OpenDataFolder()`）：日志/审计目录按 §8.3 用 `explorer.exe` 打开。
4. `ConfigPatch` 为弱类型字典：GUI 已在读取侧做兼容，但“写入形状”目前靠双方约定（见 §15.3）；后续若要强约束，建议 Contracts 增补
   `ConfigSnapshot`/类型化 getter（会牵动 T4，属契约变更，需 Lead 决策）。
