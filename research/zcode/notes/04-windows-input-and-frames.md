# Windows 输入派发、App Frame/身份与运行打包

> **研究范围**：ZCode Computer Use 在 Windows 上的输入派发（14 工具底层：点击/文本/键盘/滚动/拖拽）、窗口 frame 采集与坐标/身份合同、透明拉起与 app identity、Helper 运行/传输/打包、操作指示器、进程 containment 与清理。不含：broker 权限模型、node_repl/工具面封装的完整分析（见 03/05 笔记）、截图与 AX 树裁剪细节（只在影响坐标/派发处引用）。
> **阅读材料**：`ZCode-main/docs/cua/2026-10-13-windows-input-delivery-spec.md`（1025 行，总纲，精读）、S3/S4 计划与 S4.0 并行基线、scroll/electron 两份探针报告、`2026-10-23-windows-app-frame-surface-spec.md`（883 行）、`2026-09-14-windows-cua-indicator-repair-spec.md`、`windows-product-runtime.md`、`windows-source-development.md`、`producer/windows-app-identity-and-transparent-launch.md`、`producer/windows-helper-transport-ready.md`、`docs/windows-mcp-job-object-containment.md`、`docs/windows-agent-process-cleanup-state-machine.md`，以及真实源码 `packages/services/src/cua-permission-broker/windowsCua*.ts`、`packages/desktop/src/main/windowsCuaOperationIndicator*.ts`、`apps/zcode-cli/packages/zcode-cua-plugin/scripts/computer-use-client.mjs`。
> **核实基线**：ZCode-main 开源快照（2026-10 检出；docs 最新条目 2026-10-28 D8 裁决）。**关键事实**：producer 原生实现（`ax_win.cc`、`windowsInputDispatch.ts`、`electronInputHandlers.ts`、`screen_capture_win.cc`、`win_window_geometry.cc`）**不在本仓库**（全仓 glob 零命中），因此除 `windowsCua*.ts` / `windowsCuaOperationIndicator.ts` / `computer-use-client.mjs` 外，本文 Windows 输入/窗口机制均来自【规格】文档，不能当作可运行代码。
> **证据标注**：【代码】= 本仓库可编译/可运行源码；【规格】= docs 下的设计规格/事实账本（真实产品文档，非本快照可执行）；【契约】= 占位包类型定义；【推断】= 显式推论。
> **环境事实**（frame spec 探针轮）：Windows 11 25H2 build 26200.9711、单屏 3200×2000、DPI 192（200%）（`2026-10-23-windows-app-frame-surface-spec.md:534`）。

## 0. 结论速览（可直接进主报告）

1. **Windows 输入架构 = 「后台优先的自动升级链」**：`UIA → PostMessage → 前台 SendInput`，外加预检（身份 + UIPI）与物理 lane 排队；升级是驱动侧自主决策、模型无感，只有「确定未发送且身份/权限仍有效」才允许换路径（`2026-10-13-windows-input-delivery-spec.md:27-29, 112-127`）【规格】。
2. **14 工具签名冻结，改动只在 producer/native/broker**；模型可见回执形状不变、**成功后不披露升级路径**，`delivery {path,effect,attempts[],escalated}` 只进 broker 元数据（同文件 `:21-23, 209-235`）【规格】。路径只有三值：`ax` / `post_message` / `foreground_send_input`。
3. **点击链已打穿并专项验证：53/53（47 生效 + 6 明确拒绝），点击集合零焦点违规**；全量矩阵后来落到 133 动作：100 delivered、21 noop（15 诊断 + 5 S4 锚点 + 1-2 轮次抖动）、3 refused、9 unresolved（GTK3 结构性）（S3 计划 `:310, 344-359`；总纲 `:955`）【规格】。
4. **派发分类是「常量表 + 类名匹配」，unknown 按 deliverable 尝试**：自绘窗口（WPS/godot）跑 Win32 消息泵，PostMessage 与真实输入不可区分，是最优路径；Chromium/WPF 键盘/GTK 单击等被判必丢（丢弃矩阵，总纲 `:129-197`）【规格】。
5. **UIPI 必须先预检**：目标完整性级别高于普通 helper 时 PostMessage/SendInput 都受限，直接 `refused` + `action_sent:false` + attempts 记 `uipi_blocked`，**不得激活目标后再试 SendInput**；本批不新增提权/UIAccess（总纲 `:199-207`）【规格】。
6. **焦点防守是三件套**：EnableWindow shield（XAML/Chromium，动作线程 RAII 恢复）+ NoActivateGuard（PostMessage burst）+ 条件式前台恢复（仅当前台仍==目标根窗才恢复，用户切第三窗不抢回）；UIA 作业 4s 只是等待预算，不是取消/恢复期限（总纲 `:237-302`）【规格】。
7. **恢复前必须键盘排水**：SendInput 只把事件注入系统队列即返回，立即 restore 会丢键（真机 H3/H8/H9/J2 全灭 + E7 丢 12 字）→ finally 先等 120ms 再恢复；长文本前台化改「逐字符对注入 + 4ms 键间」，排水 `max(120, n×20)ms`，>50 UTF-16 单位且已发送则**不恢复原前台**（总纲 `:304-367`）【规格】。
8. **PostMessage 后台可达面有明确边界**：点击——Chromium 直接宿主（2026-10-20 探针 bg/occl/fg 全 9/9 消费）、多数 Win32 泵宿主可投；滚轮——5 宿主 × 2 目标全败，后台滚动只剩 WPF/WinUI3 的 SB 码族（投根窗）；组合键——LPARAM 无 CTRL/SHIFT 位且 posted 修饰键不更新 `GetKeyState`，所有多 token 和弦结构性不可投，直接短路前台（scroll/electron 探针报告；总纲 `:650-699`）【规格】。
9. **type/set_value 用「写前快照→写→读回」裁决**：替换路径 `after==requested` 才 confirmed；插入路径 changed+contains；verify 五档 + effect 四档两层正交；Chromium UIA echo 降级为 `unverifiable`；读回失败从不报 error（总纲 `:551-600`）【规格】。
10. **UWP 是身份拓扑最特殊的目标**：身份 pid=ApplicationFrameHost（多个 UWP 共享）、真实渲染 pid、UIA 元素 ProcessId 三者分裂；owner 校验只能比 HWND，不能比元素 ProcessId；XAML 宿主三处谓词必须共用 `IsXamlHostHwnd`（总纲 `:395-463`）【规格】。
11. **运行形态与安全边界**：Helper = 安装包内 Electron（`ELECTRON_RUN_AS_NODE=1`）fork 的 Node 进程，加载 `ax_native.node`，经 `\\.\pipe\zcode-cua-<random>` named pipe 暴露 broker；两阶段就绪 `transport_ready → ready`；产品资源固定在 `resources/tools/cua-helper`，manifest 带 SHA-256 + arch/Electron 版本校验（`windowsCuaDevHelperHost.ts:309-474`、`windows-product-runtime.md:44-61`）【代码+规格】；**安全边界的意外事实**：Windows 上 broker 的 native peer-identity 门「临时放开」（producer 常量 `WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN`），**本机任何进程都能连这条 named pipe**，只剩 stderr 告警（`windows-source-development.md:93-101`）【规格】——本项目若自研 Windows 执行器，鉴权不能照抄此状态。
12. **窗口/坐标合同**：截图/投影/probe/派发共用 `ReadWindowFrameBounds`（优先 `DWMWA_EXTENDED_FRAME_BOUNDS`，DWM 失败同次回退 `GetWindowRect` 并标记 `geometry_source`）；来源或几何变化即拒绝重观察；frame-projection 纯投影不加 inset；Helper 重启使旧 `frame_id` 失效（S3 计划 `:134-154`；总纲 `:473-479`）【规格】；**app frame surface（2026-10-23/28）是「观察侧」的大修**：菜单/弹层/面板有独立 HWND 时按 `window/owned_popup/menu_popup/child_window` 四 kind 归属；根 WGC **不开** `IncludeSecondaryWindows`，弹层逐窗独立 WGC + 预乘 alpha 按 z 序合成（限根边界）；坐标权限只来自 `capture_surfaces` 的像素属主，不来自实时 `WindowFromPoint`（frame spec `:14-27, 281-314, 345-378`）【规格】。
13. **候选顺序建议**（详见 §7）：混合升级链是对的；对 PCCtrl 这种「容器内模型 → Windows 本机执行器」的场景，先做「UIA 元素动作 + PostMessage 坐标点击（Win32 泵宿主）+ SendInput 前台兜底与节拍」，把读回裁决、防重放、UIPI/焦点/DPI 四类边界作为 day-1 设计项。

## 1. 当前实现（Windows）：入口与实际路径（问题 A）

### 1.1 调用链与进程边界

- 【规格/代码】调用链：模型 → 单一 `js` 工具（`mcp__node_repl__js`）→ Worker 内 `agent.computerUse` SDK（`computer-use-client.mjs`）→ 一次性认证本地 bridge → shared-host `CuaControlPort` → Helper 内 broker（named pipe、line-JSON/NDJSON）→ `ax_native.node`（UIA/PostMessage/SendInput/WGC）。

- 【规格】模型只看到 shared `node_repl` 的 `js`；CUA 不再注册独立 MCP，broker socket/token 只恢复给可信 node_repl 子进程（`docs/cua-permission-broker/zcode-cua-permission-broker.md:5-12`；简报 `:26-28`）。
- 【代码】SDK 侧 `COMPUTER_METHOD_NAMES` 就是 14 个方法名：`list_apps/list_windows/get_app_state/left_click/scroll/left_click_drag/type/set_value/select_text/key/perform_action/paste/request_access/stop_computer_control`（`computer-use-client.mjs:30-45`）。
- 【代码】SDK 的 `setupComputerUseRuntime({globals})` 从宿主注入的 bridge 取 `call`，失败即抛「Computer Use runtime bridge is unavailable」（同文件 `:892-899`）。
- 【规格】整条 Windows 链在源码侧是 **desktop-local only**：SSH/WSL/Docker/远程 workspace/手机 `/remote` 不创建 Helper（`windows-source-development.md:3`；`windows-product-runtime.md:63-66`）。【推断】这正是 PCCtrl 场景的镜像：执行器必须跑在用户交互桌面 Session 上，容器侧只能通过一个受控通道调用它。
### 1.2 14 工具底层派发总表（当前实现，总纲 §2.1 + S4 计划）

| 工具/能力 | Windows 当前路径 | 证据 |
|---|---|---|
| `left_click` 元素目标 | UIA 模式梯：Invoke → Toggle → SelectionItem.Select → ExpandCollapse.Expand →（四模式全 absent 才）Legacy；仅 absent 推进 | 总纲 `:42, 481-486`【规格】 |
| `left_click` 坐标目标 | UIA hit-test（`ElementFromPoint` 主级 + 窗口域枚举次级）→ 丢弃矩阵 → PostMessage → 前台升级 | 总纲 `:44, 489-515`【规格】 |
| `left_click` Chromium 直接宿主左键单击 | 绕过 UIA 直投已验证 PostMessage（元素中心）；双击/右键/修饰仍前台升级 | 总纲 `:45, 516-532`【规格】 |
| `left_click` WinUI3 元素双击 | 同一真实作业内两次 Invoke、间隔 40ms（语义双 Invoke，不承诺原生 DoubleTapped） | 总纲 `:43`；S3 `:468-476`【规格】 |
| `left_click` WinUI3 坐标双击/右/中键、WPF 内嵌 WebView2 右键 | 已确认后台不可达（v3 口径是「跳过该级直接前台升级」；旧 S3 口径是明确拒绝，见 §6 文档张力） | 总纲 `:46, 99-100`【规格】 |
| 前台点击 | 精确 HWND/PID/几何校验 → 同步定位光标 → 每击单批 move/down/up → 条件恢复 | 总纲 `:47`【规格】 |
| `type`/`set_value` 元素替换 | `element_set_value` → UIA ValuePattern 整值替换 + 三段读回 | 总纲 `:566-600`【规格】 |
| `type` event 插入 | PostMessage `WM_CHAR` 中间级（focused descendant）+ changed/contains 读回 | S4 计划 `:36-64`【规格】 |
| `key`/`pressKey` | 单键：PostMessage `WM_KEYDOWN/UP` 焦点子窗；组合键：短路前台 SendInput；XAML：UIA AcceleratorKey→Invoke | S4 计划 `:79-98`；总纲 `:662-699`【规格】 |
| `scroll` | 坐标目标：WPF/WinUI3 走 SB 码族后台，其余前台滚轮 SendInput；元素目标：UIA ScrollPattern 首选 | 总纲 `:650-661`；scroll 探针 `:63-74`【规格】 |
| `left_click_drag` | PostMessage 节拍序列（MOVE 预热→DOWN→35ms→12×(MOVE+25ms)→UP）或前台锁步注入 | 总纲 `:602-648`【规格】 |
| `paste` | provided paste：快照→Empty→延迟渲染占位→markDispatched→投粘贴键→awaitRead→还原；键部分是前台 chord | 总纲 `:726-827`【规格】 |
| `perform_action` | 元素 pattern 调用（非坐标，同 UIA 作业族） | S3 计划 `:86-96`【规格】 |

**结论**：底层只有三类原语——UIAutomation（元素语义 + hit-test）、PostMessage（后台定向消息）、SendInput（物理注入），没有合成 pen/touch（明确 defer，总纲 `:1015-1018`）。**前台/后台分界**：`strategy=event` 或带修饰键直接进物理 lane；`auto` 按宿主能力自动选后台或升级；XAML 元素单击保持后台 UIA 不先激活（总纲 `:120, 239-247`）。
### 1.3 点击状态链（总纲 §2.3 原文语义）

```text
请求取得派发租约 → 验证目标与策略 → 适用 UIA 或宿主特例
  → sent/possibly_sent：停止，返回真实回执
  → busy/身份/权限/明确不支持：拒绝，不重放
  → 允许升级且确定 not_sent：PostMessage 或前台级
  → 真实作业完成后释放引用、guard 与门闩
```
（总纲 `:59-75`；v3 已把「租约」改为物理 lane：「后到者等待前一个物理动作完成，不因队列占用而拒绝（`input_dispatch_busy` / `uia_job_in_flight` 两类模型可见错误随之废除）」，总纲 `:85-127`）【规格】

**两条不可越的红线**：
- 「动作一旦调用，失败也按 possibly_sent 终止；S_OK 只证明调用被接受，不证明业务生效」（总纲 `:484-486`）——所以元素路径**点击后不读回**，`verified:false / unverifiable`，no-op 防守靠分类 + 回执 + 升级兜底（同文件 `:533-534`）。
- 「超时不清空 native single-flight，不取消已发送 COM」；外层可先返回，真实作业仍持资源（总纲 `:250, 928-942`）。
### 1.4 v3 物理 lane：只串行「物理动作」，后台不受闸

- 物理 lane = 进程内 promise 链：激活、SendInput、条件恢复、scroll、drag、全局键盘注入、`dispatchWindowsForegroundKeyed` 全部排队，**无 busy 拒绝**；后台 UIA/后台 PostMessage 不进 lane（多 session 并行区）（总纲 `:907-916`）【规格】。
- paste 另有进程内有界串行（防 begin/finish 交错），单向嵌套 paste → lane，无死锁环（同文件 `:914-915`）。
- 2026-10-19 拆锁决策：`g_inputSequenceMutex` → `g_sendInputMutex` + **per-root-HWND 队列**（同根 burst 排队从不拒绝、不同窗口并行）；键盘/文本/滚动 PostMessage 原语接同一队列（S4 计划 `:120-149`）【规格】。
- S4.0 实测修正：三轮全量 parallel 实验 busyTotal=0——两臂几乎同刻发起点才可能撞锁，调度抖动足以错开；拆锁收益从「消除高频 busy」修正为「消除偶发撞锁面 + 同窗 burst 交错的语义正确性」（`2026-10-19-windows-input-s4-parallel-baseline.md:9-41`）【规格】。

## 2. 目标架构：后台优先升级链（问题 B）

### 2.1 自动升级条件与模型参数边界

| 问题 | 结论 |
|---|---|
| 谁能表达前台意图 | 只有 `strategy=event`（显式）与带 `modifiers` 的点击/拖拽（自动走物理 lane） |
| `strategy` 取值 | `auto | a11y | event`；`event` 发全局输入且要求目标已在前台，否则 `FOREGROUND_REQUIRED` 且零发送（`computer-use.md:198-202`）【规格】 |
| 自动升级触发 | 仅「允许升级 + 确定未发送（`dispatch_status=not_sent`）+ 身份与权限仍有效」；busy/timeout/unavailable/已调用失败一律不重放（总纲 `:122-127`） |
| 不再自动前台重试 | 已确认不支持的后台手势（旧口径 `background_unavailable`） |
| 模型可见变化 | 零新增参数/工具；不要求模型加重试参数（总纲 `:120, 235-236`） |

**文档张力（供 Lead 决策）**：总纲 §3.1 第一段仍写「已确认不支持的后台手势返回 `background_unavailable`，不自动前台重试」（`:83`），但同节 v3 契约与 §2.1 表把 WinUI3 等必丢格改为「跳过本级直接进 4（自动升级，不再预登记拒绝）」（`:99-100`），S3 计划 §8.4 的「明确拒绝」已被 v3 头部标记为 superseded（S3 `:3-8`）。【推断】现行契约应以 v3（自动升级）为准，`background_unavailable` 是上一版残留文案；主报告建议明确写 v3 口径。
### 2.2 派发分类与丢弃矩阵（native `classify_delivery(hwnd, event_kind) → droppable|deliverable|unknown`）

`✕` = 判定 PostMessage 必丢；`·` = 按可投递尝试（总纲 `:129-146`，逐字矩阵）：

| 窗口类别（判定依据） | 点击 | 移动 | 滚轮 | 单键 | 组合键 | 文本 |
|---|---|---|---|---|---|---|
| Chromium 顶层（`Chrome_WidgetWin_`/`CefBrowser` 前缀） | ✕ | ✕ | ✕ | ✕ | ✕ | ✕ |
| 内嵌 Chromium（WebView2/Tauri，`EnumChildWindows`） | · | ✕ | ✕ | · | ✕ | · |
| WPF（`HwndWrapper…`） | ✕ | ✕ | ·(注1) | ✕(注2) | ✕(注2) | ✕(注6) |
| Tk（`TkTopLevel`） | · | · | · | ✕ | ✕ | ✕ |
| GTK（`gdkWindow/gdkSurfaceToplevel`） | ✕(注3) | · | · | · | · | · |
| VCL（`SAL*`，LibreOffice 族） | · | ✕(注8) | · | ✕ | ✕ | ·(注4) |
| WinUI3 | 刻意豁免(注5) | · | · | · | · | · |

关键注解（总纲 `:147-184`）：
1. **WPF 滚轮不标记**：scroll 工具把 `WM_VSCROLL/HSCROLL` 直投顶层 HWND，WPF hook 可消费；WPF 指针丢弃的真实机制是「仅当真实系统光标悬于窗口上才处理投递的指针消息」。
2. **WPF 键盘前台依赖**：仅当目标**非前台**（`target_is_foreground`）时标键盘丢；前台态 PostMessage 键盘可投（但 `GetKeyState` 类加速键仍不生效，S4.3 实测 `accel_fired=0`）。
3. **GTK 单击广泛标记**：整窗单 HWND 无法区分 button（丢）与 drawing-area（可收），保守标记；画布拖拽实践中可用。
4. **VCL 文本可用**（WM_CHAR 端到端验证过），但单键/组合键因 `TranslateAccelerator` 读 `GetKeyState` 而丢。
5. **WinUI3 豁免**：普通单击用后台 UIA Invoke；坐标双击/右击/拖拽明确拒绝（v3：升级）。
6. **WPF 文本改标必丢（S4.2 矩阵实测）**：WPF 控件无独立 HWND，WM_CHAR 只能投根窗，`InputManager` 键盘焦点门控使后台窗口不消费（四宿主矩阵 mirror 恒不变）。
7. **Chromium 单击特例路由**：整类标记但普通单击绕过 UIA 直投 PostMessage，由探针验证可达性；对 Chromium 的 UIA「成功」结构性不采信（遮挡时 S_OK 但 DOM 无事件）。
8. **VCL 移动列改标必丢（2026-10-27 bench）**：VCL 静默丢弃整段 PostMessage 拖拽序列——消息入队成功、原语回执 ok、实为 no-op；drag 派发层按 `host=vcl` 拦截升级前台，回执 `reason=droppable_vcl`。
9. **WinForms TrackBar 是控件级丢弃（2026-10-27 真机+探针）**：`msctls_trackbar32` thumb 按下进入模态拖拽循环，不消费 posted 序列，中间级回执 sent 但值恒 0；`classify` 接口只回 host 字符串、不回窗口类名，**控件级拦截需要新的 native 接口**，当前以 noop 契约格钉死现状。

规则：unknown 按 deliverable 尝试（自绘窗口正是适用对象）；Qt6 初始不标记（原生消息泵）；矩阵是常量表 + 类名匹配，**不做进程名/heuristic 判断**，新增类别必须先有 fixture 证据；UIPI 预检先于矩阵（总纲 `:185-197`）。
### 2.3 UIPI 预检

- 依据：目标完整性级别更高时 `PostMessageW` 与 `SendInput` 都受 UIPI 限制；预检比较双方完整性级别，不可达 → `refused`、`action_sent:false`、attempts 记 `uipi_blocked`，**不得激活目标后再试 SendInput**；本批不新增提权或 UIAccess 通道（总纲 `:199-207`）【规格】。
- 失败可观测性不对称：PostMessage 被 UIPI 阻断返回失败、`GetLastError=5`（不是静默成功）；SendInput 的失败返回**不能单独识别** UIPI 原因，所以必须预检（同节）。
- 高完整性目标的真机 UIPI 验证仍列未验证项（S3 `:400-401`）。
### 2.4 回执契约：按受众分三通道

| 受众 | 内容 | 约束 |
|---|---|---|
| 模型（SDK 文本） | 保持现状形状；确定未发送=如实拒绝原因；已发送/可能已发送=`possibly_sent` 语义，说明效果未确认、需重新观察，**不描述成「未生效」、不直接建议重试**；成功路径不披露升级，`method` 投影为中性值（如 `click`） | 不追加 JSON 行；错误文本不出现 `PostMessage`/`double Invoke`/`winui3_exempt` 等内部术语（总纲 `:209-226`） |
| broker 响应元数据 | 可选 `delivery { path, effect, attempts[], escalated }`；协议**不 bump 版本**（新增可选字段） | 矩阵 harness/评测从这里取数（总纲 `:222-226`） |
| 运行时取证 | service logger `debug` 级落盘（与动作流同数量级） | 同节 `:226` |

- `effect` 四档：`confirmed / unverifiable / suspected_noop / refused`；Windows 不生产 `partial`。`suspected_noop` 仅表怀疑，**不等于确定未发送、不授予重放权**（`:228-235`）。
- 每个 attempt 另记 `dispatch_status: not_sent | sent | possibly_sent`：仅 `not_sent` 可升级；`effect` 与派发状态分开记录，避免把观察滞后误判成安全重试条件（`:231-234`）。
- 【代码】SDK 侧实际按 receipt 的 `dispatch_status/action_sent` 抛 `ComputerUseError{actionSent}`，`possibly_sent` 一律 reject（`computer-use-client.mjs:240-297`）——回执契约在客户端侧被转成「重试必须重观察」的硬语义。
### 2.5 焦点自抢防守与超时边界（Windows 最容易踩的坑）

**防守层次**：
1. `make_guard` 对 `is_xaml_host_hwnd` 或 `is_chromium_target_window` 命中的宿主启用 `EnableWindow` shield：归一到 GA_ROOT、保存原 enabled、动作线程 RAII 恢复；原本已禁用的窗口不能无条件启用（总纲 `:240-243`）。
2. `NoActivateGuard`（`WS_EX_NOACTIVATE`）盖 PostMessage burst 与 `RunSetValueReadbackJob`；**不套**到已显式激活的前台 SendInput（S3 `:220-232`）。
3. 2026-10-20 修复：`RunSetValueReadbackJob` 缺 NoActivateGuard、`elementWindowId` 接缝未接线导致 `host_root` 恒 nullptr（守卫整体空转）——双层守卫 + `GetWindowId(ref)` 导出后 WPF Focus() 抢前台缺陷修复、13 格焦点违规清零（S4 计划 `:323-376`）【规格】。

**条件式前台恢复（2026-10-20 补齐）**：
- 原实现 `previousForegroundWindowId` 全仓零消费、`listWindows({pid})` 只覆盖同 pid 兄弟窗——typeText/键盘/click 升级/scroll/drag 激活后**无一恢复原前台**（真机 E5/E7 终态停在目标窗）（总纲 `:288-302`）。
- 补齐两个薄原语：`getForegroundRootWindowId`（激活前快照任意前台根）+ `restoreForegroundWindow(prev, expectOccupied)`，内建三条件：prev 有效、prev≠目标根窗、此刻前台仍==目标根窗（用户切第三窗不抢回）；两连投 `SetForegroundWindow` + 12ms + `AttachThreadInput`；best-effort 返回 bool（`:288-302`）。
- **排水竞态（2026-10-21 根因修复）**：SendInput 只入队即返回，立即 restore 会让键盘事件被重路由/丢弃，回执却 accepted（假成功）；修复=finally 里 restore 之前等 120ms（与 native `kInputPacing::ForegroundSettleMs` 同源）（`:304-316`）。
- **逐字符节拍**：V3 前台化 typeText 16 字 CJK 只落地 7 字（泵速 ≈17ms/字）→ `SendTypeText` 改逐字符 down/up + 键间 `Sleep(4)`，Enter 后 `4+20`；TS 侧 `drainHintMs = max(120, n×20)`（斜率 20ms/字 = 泵速上取整）；和弦/hold 维持 120（`:335-367`）。
- **长文本保留前台（CR-02）**：以 UTF-16 单位计，>50 且已发送→完成排水后**不恢复**原前台；≤50 保留条件恢复；部分发送也算已发送、返回 `text_truncated`、`action_sent=true`、禁止重放（`:360-371`）。

**超时的诚实边界**：
- UIA 作业 single-flight + 4s deadline；4s 是等待预算，**不是 provider 取消或窗口恢复期限**（`:250-255, 518-515`）。
- 若调用已开始并永久阻塞：RAII 析构时机取决于目标应用，shield 可能无限期扣在用户窗口上、原设计无恢复路径（2026-10-21 CR-06）；修订=超时分支按 spawn 前 shield root 快照调 `AdoptAbandonedShields`，per-root 计数表 entry 记录构造 pid，depth==1 且窗口归属未变才接管（`:255-270`）。
- 线程不杀（`TerminateThread` 腐坏 COM 套间与 loader lock）；single-flight 超时后保持占用直到真实结束（`:268-271`）。
- 真机仍未证明：provider 永久阻塞、Helper 异常退出后的恢复（S3 `:400-401`；总纲 `:276-280`）。

## 3. 逐工具设计（问题 C）

### 3.1 `left_click`（先行打穿全链）

**坐标对齐（前置）**：capture、window 与实时 probe 共用 `win_window_geometry` 的 `ReadWindowFrameBounds`，读 `DWMWA_EXTENDED_FRAME_BOUNDS`；DWM 失败同次解析回退 `GetWindowRect` 并标记 `geometry_source`；捕获前后、`screenshot_bounds`、`window.bounds` 与 probe 必须同源，来源或几何变化即拒绝并要求重观察；`frame-projection.ts` 保持纯投影、不加 inset；Helper 重启使旧 `frame_id` 失效；PostMessage 的 `ScreenToClient` 独立验证 deepest-child 归属，每层从原始屏幕点换算（总纲 `:473-479`；S3 `:134-154`）【规格】。

**元素路径**：`Invoke → Toggle → SelectionItem.Select → ExpandCollapse.Expand`，四模式**全部明确 pattern_absent** 才进 LegacyIAccessible；token 失效/查询报错 ≠ absent；动作一旦调用，失败也按 `possibly_sent` 终止；`S_OK` 只证明被接受、不证明业务生效；NAPI 主线程查 registry 时 AddRef，向 MTA 作业交付独占强引用，worker 不读写无锁 registry、不接收 borrowed 指针，真实调用结束后才释放、超时不提前清理（总纲 `:481-487`；S3 `:184-212`）【规格】。

**坐标路径 UIA hit-test**：
- 主级 OS 原生 `ElementFromPoint`；命中元素必须归属已验证目标根 HWND（不得对遮挡者执行动作）；遮挡则放弃该全屏候选转窗口域枚举；
- `NativeWindowHandle=0` 时沿 `RawViewWalker` 祖先找最近非零 HWND 再 GA_ROOT；遍历带节点上限、deadline 与取消检查；
- 主级同样执行白名单、bounds 含点与 Invoke/Expand 能力约束，同具两模式优先 Expand（Qt 菜单项 Invoke 无效）；
- 窗口身份独立验证：根 HWND 与 `expected_window_id` 比对、进程与既有 app_ref 身份核对；目标消失/复用/几何过期直接拒绝；**不得比对元素 ProcessId 与 app_ref pid**（UWP 三重 pid 必然误判，总纲 `:395-419, 489-511`）；
- 次级窗口域枚举：bounding 含点 + Invoke/ExpandCollapse 可用 + **坐标无关控件类型白名单**（Button/MenuItem/Hyperlink/TabItem/ListItem/CheckBox/RadioButton/SplitButton/TreeItem，防 Invoke 中心触发丢像素精度）+ 最深（最小面积）匹配；CoreWindow 空壳树按 Miss 放行、不建 desktop-root fallback；
- **Miss 与身份失败分开**：两级均无可调用元素但目标身份与坐标仍有效、且尚未调用动作 → 按 `not_sent` 放行 PostMessage；PostMessage 只能投向已验证目标的子 HWND，裸坐标必须先通过 frame/owner 解析绑定 HWND；
- Busy/Timeout/Unavailable 结构化拒绝且**不重放**（provider 可能超时后才完成激活，重放会重复动作）；UIA 作业 single-flight + 4s deadline，等待超时不代表 COM 已取消，门闩保持到真实结束；COM/DPI 初始化在实际调用线程，外层不阻塞 broker 事件循环（总纲 `:507-515`）。

**Chromium 防假成功**：Chromium 类窗口**不进 UIA 点击路径**——左键单次无修饰直投 PostMessage（元素中心）；双击/右击/修饰走前台升级；对 Chromium 的 UIA「成功」结构性不采信。2026-10-20 探针（bg/occl/fg 三姿态 ×3 重复，`postMessageClickAsync` 完整原语）实测 **9/9 消费、遮挡下 9/9**，据此元素路径彻底绕过矩阵门；嵌套宿主（WebView2/Tauri）维持 UIA 阶梯（`2026-10-20-electron-postmessage-probe-report.md:22-49`；总纲 `:516-528`）【规格】。

**点击节拍（零驻留 no-op 的修复项）**（总纲 `:535-549`）：

| 执行级 | 节拍 |
|---|---|
| PostMessage | `WM_MOUSEMOVE` 预热 → DOWN → 驻留 **35ms**（`CLICK_DELAY_MS`）→ UP；多击间隔 **80ms**；结束 settle **50ms**；双击第二击投 `WM_*BUTTONDBLCLK`（按 `CS_DBLCLKS` 判定，系统不替投递消息推断双击） |
| 前台 SendInput | `SetCursorPos` 先定位光标（不增加独立 hover sleep）；`[move,down,up]` 单批、down→up 零驻留；多击间隔 80ms；修饰键按下后停 5ms 再点击；事后 settle 40ms（前台激活态 120ms）——retained-mode 框架在 mouse-down 建 capture/focus，过早收走光标会丢点击 |
| UIA 元素模式 | 无指针时序概念（COM 调用语义即动作） |

**验证口径**：点击后不读回元素状态，回执保留 `verified:false / unverifiable`；no-op 防守靠丢弃分类 + 结构化回执 + 升级链兜底；矩阵 oracle（click_counter）在测试层断言真实生效（总纲 `:533-534`）【规格】。部分发送只补按钮释放、不补点击；释放失败保留 owned-input ledger（S3 `:494-496`）。

**已知坑**：①Electron 未遮挡后台姿态下首击伴随 1 条目标 pid 焦点事件（Chromium 异步 handler 显式 `SetForegroundWindow`，`WS_EX_NOACTIVATE` 挡不住、消息消费可晚于投递几十毫秒）→ 只在 settle 末尾做条件矫正（事后恢复不预防）；press_gated/hover_gated 两格恢复失败仍红灯保留（单点检测盖不住 ≥15ms 计时器，有界轮询被用户否决）（electron 探针 `:51-73`）。②焦点恢复三条件漏一条就会抢回用户前台或恢复死代码（CR-03/CR-04，总纲 `:317-333`）【规格】。
### 3.2 `type` / `set_value`（读回裁决）

**契约保留**：元素目标的 `type(strategy=auto/a11y)` 与 `set_value` 都经 `element_set_value` → ValuePattern **替换整个值**；空串清空。`type(strategy=event)` 或无元素目标保留光标/选区**插入**语义，不能改成 `CurrentValue + text` 尾部追加。替换路径缺 ValuePattern 且无保持替换语义的既有路径时**明确拒绝并指路**（选内层可编辑元素——Chromium 组合框容器在 UIA 层只读是常态；或 `strategy=event` 插入；或坐标点击后 `type_text`），不给引导的裸拒绝会迫使模型盲试（2026-10-20 P-E10-1；总纲 `:566-576`）【规格】。

**读回裁决**：写前快照 `before` → 写入 → 读回 `after`（ValuePattern 优先，`TextPattern.DocumentRange` 兜底，焦点无关）。
- 替换路径必须 `after == requested` 才 `confirmed`，含「before 已等于目标值」的幂等写入；不能仅以 contains/changed 判成功；
- 插入路径（WM_CHAR）判据 = changed + contains（插入点不可知；`before==after` 时即便已含目标文本也不算送达，防「本来就含有」假阳性）；
- **换行映射两层统一（2026-10-20 修）**：`\n`/`\r` 靠**真实 Enter 键击**（后台 `WM_KEYDOWN/UP(VK_RETURN)`，前台 `SendTypeText` 层 VK_RETURN 带扫描码），`\r\n` 折叠为一次，Enter 后追加额外 settle；`WM_CHAR(0x0A)` 不被富文本控件当换行——前台层此前只发 Unicode 字符导致 WPS 内联续排，属移植不对称（总纲 `:577-589`）；
- `verify` 五档 `confirmed/pending/changed_contains/unchanged/unreadable` 落 `delivery.readback`，与 `effect` 两层正交；写入已接受但读回仍旧 → `suspected_noop`；不可读 → `unverifiable`；两者**禁止自动重放**，回执提示重新观察；COM 返回或一次短延迟复读不能证明效果已发布（`DeferredValueTextBox` 400ms 后更新，必须保留该延迟场景）（总纲 `:553-564, 587-589`）；
- Chromium AX-echo 降级挂在**读回层**：UIA 读回「成功」→ `unverifiable`，`verify` 保留 `confirmed` 但 delivery 带 `axEchoDemoted`；本次调用终止并提示重观察，**不能**在已写入后自动坐标点击再重发文本（总纲 `:590-595`）；
- WM_CHAR 仅作插入语义中间级，前一级确定未发送才可进入；投递后读回未变**不能触发 SendInput 重放**；固定等待只能用于观察（`:596-597`）；
- `set_value` 仅在 confirmed 时宣称达到目标值；其余保留已发送/可能已发送事实（`:598-600`）；
- 上限：请求以 UTF-16 单位计数，**1024 单位上限**（`:361`）【规格】。

**异步边界与前台收尾**：`typeTextGlobalAsync` 把逐字符节拍放原生 worker，Helper 主线程继续处理 IPC/心跳/计时器；无目标文本、全局 `press_key`/`hold_key` 与应用前台输入共用物理 lane，不能因异步化让另一物理按键插入（总纲 `:351-358`）。异步回执 `sent=0`=未发送、`sent>0` 截断=可能已发送，禁止自动重放（`:356-357`）。前台收尾见 §2.5 的 120ms 排水、逐字符 4ms、n×20ms drainHint、>50 单位不恢复。

**实施中修掉的真问题（S4.2/S4.3）**：
- `select_text` collapse 历史 bug：只移动 End 端点 → 实际选中 [0,start+length)，"设光标到 2"变成选中 "ab"；修复=Start 端点同移（S4 计划 `:249-254`）；
- WM_CHAR burst **不套** DisabledHwndGuard（禁用状态下窗口消费 posted 键盘不可靠），NoActivateGuard 保留（`:255-256`）；
- 取证 sentinel 的 `AttachThreadInput` 会清掉目标线程 `hwndFocus` → `FocusedDescendantForRoot` 回落根窗=投给不消费 WM_CHAR 的窗口；修法= `postMessageCharAsync` 增加元素 ref 参数，focused 未命中时解析元素宿主 HWND 回落（`:257-262`）；
- WPF 文本列 deliverable→dropped（用户决策 2026-10-19），锚点格按 D1 前台升级验收（`:263-265`）；
- 键盘类 effect 的树指纹结算在自绘/GTK/web 画布宿主上全是噪声 → `effectKind:"keyboard_input"` + verify-first 文案；携带 `delivery.readback` 的动作跳过树指纹结算（`:266-280`）；
- 2026-10-20 修 set_value 焦点违规：双层守卫 + `GetWindowId(ref)` + `ResolveHostRootFromElement` + 前台插入升级路径**元素建焦前置**（次序红线：窗口先激活→元素后聚焦→聚焦失败不发键）（`:323-376`）。

**后台插入的已知边界**：qt6 单 HWND 树的 focusWidget 不在目标框时 WM_CHAR 送达但控件不接受（readback=unchanged，如实 `unverifiable`）；被否决的补偿方案（unchanged→像素点击建焦+重投）实测 `mirror=abcdX` 破坏插入点，故登记为红灯监督、后台路径不隐式建焦（S4 计划 `:362-369`）。
### 3.3 `left_click_drag`（pacing）

- **pre-dwell**：down 后驻留 **35ms**（复用 `kInputPacing::PostMessageDwellMs`；旧文 50ms 无验证依据已作废）；
- **步进插值**：**12 步 × 25ms**（`DragStepMs`，总时长 ≈300ms）；
- **PostMessage 后台序列**：`WM_MOUSEMOVE(wParam=0)` 预热 → DOWN → 35ms → 12×(MOVE+25ms) → UP，投起始点最深子 HWND；WPF 后台拖拽 no-op 根因 = PostMessage 不更新 `GetKeyState`（不可修复）→ 矩阵 MouseMove 行 dropped → 自动升级；WinUI3 拖拽组合输入站不可达 → 同升级；
- **前台链**：锁步 `SetCursorPos` + SendInput 绝对坐标注入，UP 后 40ms settle（`ForegroundDragSettleMs`）——保留模式框架在 down 建 capture，真实光标过早移走会丢 up；
- **TODO 未落地（spec-实现漂移）**：标题栏/边框（`WM_NCHITTEST` 非 client 区）拖拽应直接拒绝进前台级，但实现里没有任何 NCHITTEST/HTCAPTION 检查（全仓 grep 为空）——现状起点落标题栏时 PostMessage 照投 `WM_LBUTTONDOWN`，原生窗口移动手势由 `WM_NCLBUTTONDOWN` 驱动，实际**静默无效但回执 sent**（总纲 `:602-627`）；
- `strategy=event` 以前在 SDK schema 不可达：drag handler 早已消费 `strategy`，但 `leftClickDragSchema` 无 strategy 字段且 `.strict()` → 前台拖拽链无真机入口；2026-10-21 补齐（S3/spec `:628-648`；【代码】SDK 侧 2026-10-27 补透传 `computer-use-client.mjs:727-739`）；
- 矩阵复验：winforms drag_move_count 死格复活（drag_moves=34）、qt6 新增 `drag_foreground_event_delivery`（moves=7 + path=foreground_send_input）——前台升级链含 CR-04 恢复挂点首次拿到真机证据（总纲 `:645-648`）；
- **夹具坑**：`DragCanvasPanel` 计数门用 `Control.MouseButtons` 读全局物理键态，PostMessage 后台拖拽恒数不到 move → 改为消息自含判定（收到 down 未收到 up 即按住中）（总纲 `:640`）；
- VCL 拖拽必丢（`droppable_vcl`）、WinForms TrackBar 控件级必丢（见 §2.2 注 8/9）【规格】。
### 3.4 `scroll`

- **后台路径 = SB 码族**：`WM_VSCROLL`/`WM_HSCROLL` + `SB_LINE*`/`SB_PAGE*` 投**根窗**，`amount`=消息条数，消息间隔 **30ms**；
- **探针取证（34 组合 × 5 宿主，全程后台）**：wpf 消费（line 3→48、page 1→96）、winui3 消费（line 3→48、page 1→100，**必须投根窗**，投 `DesktopChildSiteBridge` 子窗被忽略）；winforms（AutoScroll 非客户区滚动条）/qt6（自绘）/gtk3 两族全忽略；
- **旧候选 `WM_MOUSEWHEEL`/`WM_HWHEEL` PostMessage 探针全败，按证据收缩不落地**（N1）；Chromium（含内嵌）滚轮属丢弃类别直接升级；
- **路由三档**：wpf/winui3 → SB 码族中间级（`postMessageScrollAsync`）；winforms/qt6/gtk3 → `not_sent` 升级前台滚轮；Chromium 系 → 丢弃矩阵升级；
- `element_index` 目标维持 UIA ScrollPattern（`element_scroll`）首选；但真机取证后 `scroll_element_a11y` **三宿主全撤**：qt6/winui3 的 `SetScrollPercent` 带焦点副作用（focus_events=1/2）、wpf 的 a11y 锚点在折叠线下无有效屏幕点——UIA 后台滚动语义未获证；
- **工具层漏传 strategy 的真 bug（2026-10-25 修）**：显式 `event` 被 broker 当 `auto`，窗口消息已入队而业务滚动计数为零（frame spec `:725`）；
- 横向滚动：无横向 oracle 控件，SB_LINELEFT/RIGHT 与垂直同族、预期同判定，**未验证**（scroll 探针 `:73-74`）【规格】。
### 3.5 `key` / 组合键与 XAML 加速键

- **中间级原语** `postMessageKeyAsync(root, pid, chord, mods)`：focused_descendant 解析 + WM_KEYDOWN/UP 序列（修饰键按下→主键→4ms→KEYUP→逆序释放），alt 走 `WM_SYSKEY*`，lparam 带扫描码/扩展位；
- **可达边界 = 单键 keystroke**：winforms 纯 Win32 泵、qt6 `eventFilter` 多轮真机实证送达；
- **组合键结构性不可投**（决策 2026-10-20，同日由 paste 扩至全和弦）：`WM_KEYDOWN` 的 LPARAM 没有任何能表达 CTRL/SHIFT 的位（仅 ALT 有 context bit / `WM_SYSKEYDOWN` 通道），posted 修饰键也不更新 `GetKeyState`；实证=2026-10-18 J 组 paste（WPS/记事本，皆 deliverable 宿主）`reads=0`；S4.3 矩阵 WPF 前台态 ctrl+shift+h `accel_fired=0`；本仓 354 份 baseline 零正样本。因此**所有多 token 和弦**在 `dispatchWindowsBackgroundKeyed` 内短路：attempts 记 `post_message not_sent (chord_key_state_unreachable)` 后升级前台 SendInput（系统输入队列 `GetKeyState` 可见，复用条件式恢复、`escalated` 照常）；**保留** XAML 宿主先走 UIA 加速键通道、单键照投（总纲 `:662-682`）；
- **无焦点短路（2026-10-25 用户裁决恢复升级契约）**：键盘/文本中间级目标 HWND 解析按 `GUITHREADINFO.hwndFocus` 三态——①焦点=真后代→照投；②焦点=根窗自身（单 HWND 应用 Qt/GTK）→照投根窗；③焦点=0 或指向其他窗口树（后台线程焦点记录被清）→无合法投递目标，原生返回 `focus_missing`，attempts 记 `not_sent` 后升级前台；③不回投根窗的理由：多控件窗口根窗不消费键（真机 `sent` 假成功且无字段级读回）、强设焦点违反零后台焦点事件断言、记目标缓存违反无授权缓存契约；**TS 层不得在中间级前以 `focused_input_window_id===0` 预检硬拒**（4881359c1a 曾引入 `input_focus_unavailable` 硬抛、无 attempt 不升级且误挡加速键通道，已移除，总纲 `:683-699`；frame spec E33 `:479`）【规格】；
- **XAML 加速键通道**：`invokeAcceleratorAsync` 用 UIA `AcceleratorKey`/`Name` 括号提示 canonical 匹配 → Invoke → Toggle 链，miss=确定未执行；KeyboardAccelerator 声明**不自动暴露给 UIA**（真实应用需显式提供 AcceleratorKey 注解，S4 计划 `:293-297`）；
- **键名归一化四层收敛**：SDK `KEY_ALIASES` → 工具层 `PUBLIC_KEY_TOKENS` → broker `KEY_CHORD_KEYS` → native `VkForToken`；归一化在**工具层入口**、`findUnsupportedKeyToken` 之前统一执行，broker/native 只接受 canonical；drift 断言 = 两层白名单 ⊆ 工具层 canonical 集；裸调用面 `computer.key` 进 broker 前归一（总纲 `:706-725`；【代码】SDK 别名表 `computer-use-client.mjs:862-888`）；
- **键盘节拍**：`KEY_DELAY_MS=4ms` 基线 + 字符间追加延时（WM_CHAR 路径另有 40ms settle）（总纲 `:700-704`）【规格】。
### 3.6 UWP / XAML 宿主专项（身份与输入的双重分裂）

**三重 pid 分裂**（总纲 `:401-408`）：

| 角色 | 归属 | 证据 |
|---|---|---|
| 身份 pid（`list_apps` 报出） | `ApplicationFrameHost.exe`，**多个 UWP 共享一个 pid** | `ax_win.cc:1324`；面积启发式两次会话分别报「计算器」与「设置」（pid 14556） |
| 真实渲染进程 pid | AUMID 激活返回的进程（如 348），其窗口 cloaked/无可视帧 | 身份文档 §1.5 表 |
| UIA 元素 ProcessId | 真实进程 pid；且 CoreWindow 的 UIA 树**不挂窗口节点下**（`ElementFromHandle` 返回空壳，真树注册在 desktop root 的兄弟节点） | 输入层按宿主 HWND 验证归属，不依赖元素 ProcessId |

**输入层硬约束**：
1. hit-test owner 校验基准用**窗口 HWND 归属**，不得用元素 ProcessId 对比 app_ref pid（UWP 会被误判 `wrong_owner`）；命中元素 `NativeWindowHandle=0` 时沿 `RawViewWalker` 找最近非零 HWND 再 GA_ROOT；不能假设所有按钮都有自己的 HWND；
2. CoreWindow 空壳免疫靠**主级路径选择**（`ElementFromPoint` 从 desktop root 出发，真树恰注册在 desktop root）：次级 `ElementFromHandle` 在 CoreWindow 上拿空树 → 按 Miss 放行下层，**不做 desktop-root 兄弟遍历**；
3. **键盘路由一行规则**：XAML 宿主（UWP/WinUI3）的 CoreInput 只消费系统输入队列，键盘中间级对 XAML 宿主跳过 PostMessage 直接升级——实现为「丢弃矩阵旁的一张宿主判定表（类名 + exe 名，exe 更可靠）」；**宿主判定统一（2026-10-20 H3 真机根因）**：分类矩阵、双 Invoke 门、DisabledHwndGuard shield 三处必须共用同一谓词 `IsXamlHostHwnd`（覆盖 `WinUIDesktopWin32WindowClass` + UWP 的 `ApplicationFrameWindow`/`Windows.UI.Core.CoreWindow`）；此前只认 unpackaged WinUI3 类名，真实 UWP（计算器，AFH 托管）被判 `unknown+deliverable` → 键盘走 PostMessage 静默失败（H3：alt+2 回执 accepted 但模式不切）。**证据边界**：依据为 H3 键盘负样本 + 同一 CoreInput 机理外推，**鼠标行为仍需直接实测**；
4. cloaked（挂起 UWP）不新增输入侧唤醒：输入层对 cloaked 目标结构化拒绝并说明「先 re-observe（会自动唤醒）」，不在输入路径重复激活逻辑；
5. shield 生命周期：XAML/Chromium 宿主在 UIA 模式动作期间由实际调用线程持有 guard，返回后恢复原 enabled；S3 XAML 普通单击保持后台 UIA、不先激活窗口；
6. raw 前台门禁的 active 比对基准用**唯一 pid 集合**，不得数 active 条目数：AFH 前台且名下多个 UWP 可见时（便笺×2 + 计算器 = 3 条）N 条同时 `active=true`，门禁 `active.length===1` 永不满足 → raw 前台事件整体拒绝且无自愈；修法=按唯一 pid 归并后比对，>1 个 pid 是真瞬态维持 fail-closed；`resolveFrontmostIdentity` 同口径，但同 pid 多 bundle（AFH 托管多个 UWP）时身份无法唯一定名仍返回 null；已知边界：UWP 目标按应用进程 pid 绑定时 `expectedPid ≠ ForegroundPid` 会被拒，根治需窗口级前台比对（总纲 `:445-458`）；
7. 矩阵覆盖分两部分：**XAML 输入栈**（键盘路由 / UIA owner / shield 恢复 / 后台焦点 / 手势拒绝）由 WinUI3 harness 覆盖；**AFH/UWP 拓扑**由真实样本覆盖 `list_apps → get_app_state → 元素/坐标点击`；unpackaged harness 不经过 AFH，不能证明 AFH/UWP 已验证（总纲 `:460-463`）【规格】。

## 4. 坐标 / 窗口 / Frame 新鲜度 / 身份（问题 D）

### 4.1 窗口几何与坐标同源

| 消费者 | 约束 |
|---|---|
| `screen_capture_win.cc` / `ValidateWindowTarget` | expectedBounds、捕获前后矩形与 `screenshot_bounds` 同源 |
| `ax_win.cc` / `BuildWindowFromHwnd` | `window.bounds` 使用共享 frame helper |
| `ax_win.cc` / `ScreenCaptureProbeWindows` | probe 使用同一矩形与来源 |
| broker 帧校验与帧记录 | 记录 `geometry_source`，拒绝来源或几何变化 |

- `PrintWindow` 裁剪不能直接套到 WGC；同源约束须由本项目链路验证（S3 `:134-154`）【规格】。
- 测量工具 `windows-frame-geometry-measure.mjs` 的复核契约：实际屏幕公共边界生成跨屏目标；捕获前后核对窗口/DWM/客户区/DPI/样式/拓扑；用边缘与内部标记的真实点击验证落点；不稳定样本不得准入（S3 `:156-158`）。
- 当前点击矩阵与 GTK3 中途缩放回归已通过，但**不能替代**完整多屏/DPI、负坐标、自绘边框及 DWM 查询失败路径的真机验证（S3 `:152-154, 400-401`）。
### 4.2 App Frame Surface：四 kind、归属链与合成管线（2026-10-23/28）

- **四类 kind**（全部有独立 HWND）：`window`（frame 窗口本身）、`owned_popup`（GW_OWNER 链 ≤8 级到 frame，可跨进程；含 #32770 对话框/layered/非模态浮层）、`menu_popup`（菜单/下拉/工具弹窗，含 owner=0 框架菜单）、`child_window`（WS_CHILD；仅 root 直接 child 中**跨 pid 的可见内容承载者**入 surfaces，同 pid child 不占模型面）（frame spec `:89-127`）【规格】。
- **归属证明三链**：①`GW_OWNER` 链（E08 跨进程 Zotero 面板）、②GUI 菜单态 + child 父链（`hwndMenuOwner` 指向面板内 child 经父链回面板，E09）、③UIA 层级链（E10 Shotcut）；三链覆盖多数实测样本，WPS 类三链全失败只能靠 §3.2 操作关联（D2 排最后，落地前按 unknown/独立候选处理）。**E32 教训**：链③曾因「先查自身 HWND」的复制写法在调用方传入 `ElementFromHwnd(候选)` 时恒自返、链③恒 false（738 次归属记录 `uia_ancestor` 0 命中），修复=严格祖先（先走一步 `GetParentElement`）。
- **D1 像素主路径**：根 WGC **不开** `IncludeSecondaryWindows`（false 下弹层稳定不入根图，E17）+ 已证明弹层各自独立 WGC 捕获（E15 最稳）+ **预乘 alpha** 按真实 z 序合成、范围限根边界（E23：99.55% 像素最大通道误差 ≤8，半透明区平均约 2.51 vs straight 4.20）；完全越界不入图并记 `outside_root_bounds`。ISW 被裁决放弃（E18 行为不确定、E19 真实 Shotcut 反例、E20 owner 非必要非充分、仅 24H2+）。
- **D8（2026-10-28）**：显式主窗观察帧**收编全部已证明 owned/menu 弹层像素**（含模态面板），但树合并仍限菜单链（`is_menu`），面板树/身份仍需显式 `window_id` 切换——「像素收编、树/身份不收编」。
- **捕获前后校验**：每个参与窗口独立做身份/bounds/前台校验，任一变化 → 光栅不可操作、保留树与缺口原因；child HWND 不能独立建 WGC 捕获项（`E_INVALIDARG`，E21 同/跨 pid 7/7），但 child **内容**能经根捕获入图（E17/E25，含跨 pid `Intermediate D3D Window` 三例复测）。
- **坐标权限唯一事实源**：`capture_surfaces` 记录每块像素的来源 surface（原点/z 序/归属证明）；坐标 p 落在哪块已验证区域，其根 HWND 即**预期接收根 surface**，重叠取观察时 front-to-back 最前者；**不得用派发时 `WindowFromPoint` 选目标**（会把无关遮挡者变成目标）；四级接收者各自校验：AX 元素（宿主重解析==合同宿主）、UIA hit-test（`ElementFromPoint` 宿主根==S.root）、PostMessage（S 子树内 `ChildWindowFromPointEx` 定向 child，菜单另需全局命中可见）、SendInput（`WindowFromPoint` 根==S.root + 前台前提 D4）（frame spec `:345-378`）【规格】。
- **D4 菜单前台前提**：统一为「前台根属于该菜单归属链的宿主集合」；菜单 HWND 不必是 `GetForegroundWindow()`（E02 Notion 菜单为 `WS_EX_NOACTIVATE` 顶层 TOOLWINDOW），线程一致与 GUI 菜单态降为佐证。
- **D7 后台定向消息**：仅限「宿主链已证明 + 结果可完整读回的定向消息（WM_SETTEXT 类）+ 投后读回裁决 + 焦点零扰动」，**不适用于键盘、点击、确认类动作**；只有 Win32 Edit 单控件真机验证过（E27），RichEdit/Qt/XAML 未测（frame spec `:424-433`）。
### 4.3 frame 新鲜度与坐标绑定

- 【规格】`frame-coordinate-internal-binding.md:18-47, 63-85`：模型只提交 raster 像素 `{x,y}`，不做 Retina/DPI/crop/resize/显示器原点换算；CUA 在 transport session 内同步绑定「最新可动作 raster」，`frame_id` 内部携带；显式 `frame_id` 权威、未知/过期/被逐出/跨会话/不可动作一律 fail-closed、**不回落最新帧**；state-only 观察既不推进也不吊销隐式像素授权；AX `bounds` 是全局屏幕点，**永远不能**当 raster x/y 提交；搬运/排队期间旧绑定保持有效、不被新几何重绑。
- 【规格】frame spec `:380-390`：lane 实际开始执行时重验 frame/session/元素引用有效、HWND 存活可见、PID/TID/exe 一致、关系仍成立；失败按 `window_source_unavailable/window_identity_unverified/window_gone/surface_replaced` 报告、`action_sent=false`，不静默改投主窗/同名元素/兄弟窗。
- **HWND/RuntimeId 复用**：E30 实测 hide≠destroy 且复用行为按应用分化（记事本菜单重开复用同 HWND/RuntimeId；Shotcut 十轮全换；Notion 同 HWND 三组）——无通用复用规则；已观察到 destroy/hide-reopen/关系或身份变化即失效；WinEvent 只补充失效信号，**不能替代投前读取**（E31：Qt/WPS 菜单无 MENUPOPUPSTART，接收延迟中位约 8ms、P95 约 19ms，非时限合同）。第 4 步实现引入元素 registry 失效与 `window_generation` 代际（旧代际 3 / 新代际 5 拒绝发送、业务计数 0）。
- **Helper 重启**使旧 `frame_id` 失效；producer 实例身份随既有 frame 记录——Helper 重启不可凭相同数字代际复用旧帧（frame spec `:733`）。
### 4.4 多显示器 / DPI / 虚拟桌面（继承现状，防回退）

- 前台 SendInput 级绝对坐标已做**虚拟屏幕归一化**（`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK` + `SM_XVIRTUALSCREEN` 族取值）；升级链保留该路径即继承该行为（总纲 `:887-900`）【规格】。
- UIA 元素级无坐标概念；PostMessage 级经 deepest-child HWND + `ScreenToClient` 得客户区坐标，**天然与屏幕布局、副屏负坐标、多屏 DPI 混排无关**（转换由系统 API 完成）；仅 SendInput 级涉及屏幕绝对坐标；PostMessage 原语还校验 **i16 范围**（S3 `:279`）。
- API 换算不能代替真机验证：副屏负坐标、多屏 DPI 混排、边缘目标仍保留真机验证缺口（S3 `:152-154`）；frame spec §7.8/§9.6.2 明确**多屏负原点与拓扑变化、其他 OS 本轮不执行**（用户 2026-10-25 指示）；探针环境为单屏 3200×2000 / 200% DPI，查询进程以 **PMv2 DPI context** 读物理坐标，采样按握手的物理 bounds 投影、**不能假设 DIP 等于物理像素**。
- 跨虚拟桌面：不做专门优化——目标位于其他虚拟桌面时，后台 UIA/PostMessage 按 HWND 路由本就可达，`delivery` 元数据如实记录实际路径（总纲 `:1022-1024`）【规格】。
### 4.5 透明启动与 app identity（`get_app_state` 吸收 `open_application`）

**设计一句话**：删除 `open_application`，让 `get_app_state(app_ref={name})` 自洽——按应用名找已运行应用；找不到就拉起；需要像素而窗口不可见就弄可见；工具面 15→14（identity 文档 `:7-9`）【规格】。

**当前事实（2026-09-14 真机实测，Windows 11 26200）**：
- Windows 走独立代码路径（`createAxWinReadOnlySource`），macOS 透明拉起控制流在 Windows 从不执行；名字→canonical 身份只有一张 5 条**计算器别名**硬编码表；`app.name` 是窗口标题（`BuildAppInfoFromHwnd` 填 `WindowTitleW`），`windowsNameMatches` 是大小写无关**子串**匹配——`{name:"记事本"}` 必然失败、`{name:"设置"}` 静默选错、`{name:"cli"}` 会命中终端窗（`:17-62`）；
- 一个 UWP 产出两条 cloaked 记录；`EnumerateUserHwndsForPid` 不过滤 cloaked 而 `screenCaptureProbeWindows` 过滤（口径不一致）；`PickPrimaryHwndForPid` 按面积取，共享宿主时报哪个身份由当刻几何决定（`:63-78`）；
- **AUMID 通道的拉起/绑定不对称**：`PKEY_AppUserModel_ID` 只有 AFH 托管的 UWP 窗口才有；新版记事本/画图是 packaged desktop app，`applicationInfoByAumid` 返回 null → **绑定必须 pid 优先、AUMID 兜底**（`:80-103`）；
- `shell:AppsFolder` 实测 134 条：合法 AUMID 38 + `.exe` target 75 = **113/134（84%）可拉起**，21 条（.url/.msc/.html/shell CLSID）拒绝；枚举耗时 280-386ms（`:104-128`）；
- **焦点语义**：`ActivateApplication(AO_NONE)` 不抢焦点（两次独立测量含冷启动）；`CreateProcess`/`spawn` 抢焦点；`STARTF_USESHOWWINDOW+SW_SHOWNOACTIVATE` 仍抢（应用自己调 `SetForegroundWindow`）；`ShowWindow(SW_SHOWNOACTIVATE)` 取消最小化抢焦点；`PreventActivation/IsFocusStealPrevented` 是诚实 no-op（`ax_win.cc:3345-3357` "Windows has no equivalent"）→ **决策：接受 Win32 分支抢焦点，不尝试抑制，工具描述如实告知，不得宣称「后台拉起」**（`:129-139`）；
- **观察能力四状态**：未运行→拉起；运行正常（含被完全遮挡）→无需动作、树 229 元素 + PNG 112,795B、焦点不动；UWP 挂起 cloaked=2→`ActivateApplication(AUMID)` 唤醒（不抢焦点，树 1→129）；最小化 iconic→树完整、UIA `setValue` 仍 ok、截图 `invalid_target`（`ValidateWindowTarget` 只拒 `IsIconic || !IsWindowVisible`，**无前台判据**）（`:141-183`）。

**设计要点**：
- `resolveTarget` 两层：①已安装应用名精确匹配（权威）→ {aumid,target_path} → aumid 合法走 `applicationInfoByAumid`，否则按 `bundle_id===target_path` 在活进程里匹配；②窗口标题子串匹配（沿用既有语义）；③两层都空但①查到已安装应用 → 启动；④②多命中 → 抛歧义错并列候选（取代静默择一）（`:220-230`）；
- `captureApp` 前置扩展为「把目标变成可观察的」：未运行→`launchApplicationAsync(aumid,target_path)` → 绑定（pid 有非 cloaked 窗口用 pid，否则 aumid 兜底，再按 target_path 重解）→ `settleFreshlyLaunchedTree`；cloaked→AUMID 唤醒；iconic+`include_screenshot=false`→只回树；`=true`→恢复窗口（抢焦点）后截图——**`include_screenshot` 就是焦点开关，默认路径永不抢焦点**（`:231-264`）；
- **拉起后等窗预算 10 秒（500ms×20）**：原 3 秒不够，2026-10-26 真机 PowerPoint 冷启动从 launch 返回到首个可见窗口 **7.8 秒**；超时用专属文案（已按名字拉起 pid X、N 秒内窗口未出现、建议同引用稍后重试），不落进通用 "could not be resolved"（`:270-272`）；
- 拉起器 pid ≠ GUI pid：`processExecutablePath(launcherPid)` 拿 canonical 路径后在活进程重解；AppsFolder 枚举结果在 Helper 生命周期内缓存、**名字 miss 时重枚举一次**（`:274-276`）；
- **唯一新增危险面**：AppsFolder 含卸载程序（"卸载微信"），解析层必须显式拒绝名字/目标路径命中 `uninstall`/`卸载` 的拉起（`:374`）；
- 已知不做：`list_apps` 按 pid 去重会把共享宿主后的 UWP 整个藏起来（计算器与设置同挂 pid 时只出一条，`settings` 根本不出现）；不修的理由是「会导致误操作的那一半已 fail-closed」（观察期唤醒不再激活错 AUMID、带错误清单），剩下只是发现不全（`:388-392`）。

**Windows launcher 与 GUI 进程身份（源码开发手册）**：
- `open_application`（已删，语义并入 `get_app_state`）启动前必须异步采集一次 UIA application PID/identity snapshot；launcher PID 能由 UIA 可靠解析时优先使用；只有 Windows 允许把无窗口 launcher 换绑到 GUI child，child 必须同时满足：不在启动前 snapshot、非空 canonical executable identity、与 launcher 解析 identity 精确相等、当前仍由 UIA 暴露且候选唯一；旧进程/仅 name/basename 相同/不同路径同名/多个新候选一律 fail closed（`windows-source-development.md:270-278`）【规格】；
- Win11 `notepad.exe` 是 App Execution Alias——不能假定命令文本或 basename 是进程身份；spawn 返回 PID 后立即 `processExecutablePath(pid)` 查完整路径（实机返回 WindowsApps 内真实 `...\Notepad\Notepad.exe`）；UIA 注册可比 spawn 晚约 2 秒 → 有界 `30×100ms` 等待（同节）；
- packaged app 不能沿用 executable-path rebind：必须由 `bundle_id` 传精确 AUMID、经原生 `IApplicationActivationManager` 激活，再从可见顶层 HWND 的 `PKEY_AppUserModel_ID` 精确解析唯一 PID；0 个或多个候选都 fail closed（`windows-source-development.md:280-287`）。

## 5. 运行与打包（问题 E）

### 5.1 Helper 传输：named pipe + 两阶段就绪

- **形态**：`\\\\.\pipe\zcode-cua-<random>`（每代随机、不复用旧 pipe）；broker 协议为 NDJSON/line-JSON（`parseRequestLine`/`serializeResponse`；`windows-source-development.md:253-262, 316`）。【代码+规格】
- **两阶段就绪**（Windows 没有 Unix socket 的 `.pending → final` 原子让渡语义）：
```text
fork(socket, token) → bind + authenticate → transport_ready → load native/backend → ready + broker_info
```
  `transport_ready` 只代表 pipe 已由当前子进程绑定并完成 bearer-token 认证配置；`ready` 仍是完整 health 信号。功能层初始化期间，除 `broker_info` 外的 broker 方法返回可重试 `broker_unavailable`（`ready_state=warming_up`），listener 生命周期不变；旧 Helper 只发 `ready` 时视同 transport ready（`producer/windows-helper-transport-ready.md:6-20`；`docs/cua-permission-broker/windows-transport-ready.md:7-33`）【规格】。
- **真实代码**（`windowsCuaDevHelperHost.ts`）：`WindowsCuaHelperHost` 持 `lifecycleTail` 线性化所有 fork/terminate；`start/stop/restart/restartAfterCurrentStart/restartAfterCurrentStartPreservingTransport`；generation 计数 + externalStopEpoch 防旧 start 复活；ready 消息的 `socketPath` 必须等于本代 mint 的 pipe、`pid` 必须等于 child.pid（`ready-pid-mismatch`）；health probe 的 pid 必须精确等于 child PID（`health-pid-mismatch`）；`startupTimeoutMs=30_000`、`shutdownTimeoutMs=1_000`（`:37-38, 304-475`）【代码】。
- **spawn 形态**：`fork(entryPath, [entryPath, "--socket", pipe, "--parent-pid", process.pid, "--ghost-cursor-overlay"], { cwd: runtime.root, env: {...process.env, ...commandEnv, ADDON_ENV: addonPath, ELECTRON_RUN_AS_NODE: "1"} })`——`command` 是当前 Node/Electron，`execPath` 指定；argv 末位 `--ghost-cursor-overlay` 是 Windows 产品链**恒开**的 agent cursor overlay（`:309-337`）【代码】。
- **凭据**：`pluginAuthority` 由 Host 侧 `randomBytes(16).toString("hex")` 铸造，与 socketPath 组成唯一二元组；node_repl 重构后 broker token 鉴权整体删除，凭据只剩 socket + `pluginAuthority`，不得出现在 child argv/进程标题/日志/错误文本/telemetry（`windowsCuaDevHelperHost.ts:34-35, 426-442`；`windows-source-development.md:69-77`）【代码+规格】。
- **⚠️ 鉴权现状（重要边界）**：broker 的连接门是 native peer-identity（code-signature / `parentProcessPid`），**Windows 侧 peer 校验尚未落地**；自 producer `37a0c9ef` 起该门在 Windows 上被临时放开（`WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN`，因为打包版拿不到 dev 豁免、不放开必然启动失败），带一行 stderr 告警以无鉴权状态启动：**Windows 上任何本机进程都能连这条 named pipe**；M5 原生 peer 原语落地后该常量回收、门自动闭合（`windows-source-development.md:93-101`）【规格】。
- **退出/恢复**：`stop()` 先发 shutdown IPC、等 1s、`kill()`、再等 1s，仍未退出即 `termination blocker`（不谎报已终止）；ready 后意外退出 → 只通知 `onUnexpectedExit`，下次 CUA demand 复用已发布 tuple 恢复、不回收 Agent；`restartAfterCurrentStartPreservingTransport` 复用 host-facing pipe，避免 Helper 重启把 Agent 留在旧 pipe 上（`windowsCuaHelperHostSupport.ts:75-98, 160-188`；host `:198-226, 369-388`）【代码】。
- **产品验收记录（2026-07-29）**：owned Helper PID 39992；ready/health/`broker_info` PID 完全一致；`stop()` 后新 named-pipe 连接 500ms 内被拒绝、exact PID 退出、token 未出现在 argv 或捕获日志；四个 live smoke（pointer/clipboard/screen/30-tool surface）exit 0（`windows-product-runtime.md:198-247`）【规格】。
### 5.2 产品运行时结构与打包

- 产品目录固定为 `resources/tools/cua-helper/`，至少含：`runtime-manifest.json`（schema 1，固定字段见下）、`package.json`（`"type":"module"`、version 与 manifest 的 packageVersion 一致）、契约 entry（`dist/windows-helper.js`）、契约 addon（`build/Release/ax_native.node`）、`node_modules/{express,sharp,@img/*}` 依赖闭包（product-runtime `:88-101`）【规格】。
- 生产者 `package.json.zcodeCuaRuntime.{schema:1,windows:{entry,nativeAddon}}` 是严格可版本化契约：不允许未知字段、两 artifact 不能相同、必须是规范 `/` 相对路径（拒绝反斜杠/绝对路径/空段/./../ADS 冒号/NUL）（`windows-product-runtime.md:68-131`；【代码】`windowsCuaDevRuntime.ts:521-567` 逐条实现）【规格+代码】。
- **架构校验**：`arch` 仅 x64/arm64；构建期检查 `.node` 的 PE machine（`0x8664`=x64、`0xaa64`=arm64），不匹配立即失败，禁止把宿主 x64 产物误装进 arm64 包（product-runtime `:126-128`）【规格】。
- **签名与 hash 刷新次序**：staging manifest 记录未签名字节 hash → electron-builder `extraResources` 复制时由 Windows transformer 签 `.node/.dll`（签名改字节）→ `afterPack` 先从 electron-builder context 取实际 os/arch 并与配置严格相等（交叉构建/环境变量不一致必须失败，不能 skipped）→ 校验固定契约 + PE + 依赖闭包 + realpath containment → **只用最终已签名字节刷新 manifest.sha256** → 严格复验（product-runtime `:152-175`）【规格】。
- **物理包含**：source root 必须非链接目录；package.json/entry/addon 必须非链接普通文件且 `realpath` 物理包含在 root realpath 内——防 junction/reparse point 把仓库外文件带入开发运行时或安装包（product-runtime `:147-150`；【代码】`windowsCuaDevRuntime.ts:96-146, 328-349, 409-438`）。
- **准入失败语义**：非 win32 → `unsupported-platform`；`ZCODE_CUA_DEV_ROOT` 非空且非绝对/不存在/包身份不对 → fail closed、**不回退产品资源**；产品模式缺 `resourcesPath`/`resourcesPath` 非绝对/manifest 无效/不兼容/artifact 路径非法/哈希不匹配分别稳定 reason；诊断只报稳定 reason 与相对 artifact（product-runtime `:133-150`；代码 `windowsCuaDevRuntime.ts:38-61, 78-94`）【规格+代码】。
- Helper **懒启动**：特性关闭或官方插件未启用时不启动、不探测、不产生子进程；kill switch `ZCODE_CUA_PRODUCT_HELPER=0|false|off` 零足迹（broker 文档 `:28-32`）；start/stop/restart/health、ready PID、health PID、named-pipe token 与终止阻塞语义沿用既有 Host，不因产品化新增第二套生命周期（product-runtime `:144-145`）【规格】。
- **本地验收限制（2026-07-29）**：`electron-builder --win --x64 --dir` 必须显式 `--config electron-builder.config.js`（裸跑不加载配置，会报 desktop package.json 缺 version）；本机命令 20 分钟上限在 `rcedit` 改 branded exe 时中断，遗留 exe 版本/hash 异常、被 Windows Application Control 拒绝启动，那次不能记为 branded unpacked build 成功；改用 hash 校验过的原始 Electron 41.0.3 x64 只覆盖 resolver 的 command，其余 root/entry/addon/node_modules 仍来自 packaged resources；无发布证书不能外推为最终签名验收（product-runtime `:198-251`）【规格】。
### 5.3 源码开发环境

- **构建**：在 zcode-cua source 根 `pnpm install → pnpm rebuild:native → pnpm build → pnpm test`；产物必须是 `dist/windows-helper.js` + `build/Release/ax_native.node`；改动 source 后再次 `rebuild:native`/`build` 即可，**不需要重新打包 ZCode 产品**（`windows-source-development.md:5-25`）【规格】。
- **运行**：官方 `zcode-cua` plugin 必须已启用；`ZCODE_CUA_DEV_MODE=1`（one-knob bundle，不能拆）+ `ZCODE_CUA_DEV_ROOT`（source 根绝对路径，非链接、artifact realpath 在 root 内）→ `pnpm dev:desktop:prod`；不设 DEV_ROOT 时缺省绑定仓库已安装 producer（与产品 staging 同源），dev Electron 的 `process.resourcesPath` 下没有 `tools/cua-helper`，所以**不设任何值时产品分支必然 `invalid-runtime-manifest` 失败**（同文件 `:33-56`）【规格】。
- **系统安全策略**：Windows Smart App Control 强制模式下，新编译未由 Trusted Root Program CA 签名的 `ax_native.node` 会被 Code Integrity 拒绝加载——**成功编译 ≠ 可执行**；live smoke 会在创建 Helper/fixture 前退出 2 并报 native addon blocked；不要为源码验收关闭系统安全策略（`:27-31`）【规格】。
- **真机冒烟全部 opt-in**：`ZCODE_CUA_WINDOWS_POINTER_LIVE`（移动/零滚轮/位置恢复/hold 中断/clean exit，光标复原 2px 容差）、`..._CLIPBOARD_LIVE`（CF_UNICODETEXT 读写还原）、`..._SCREEN_CAPTURE_LIVE`（WGC/遮挡等价/capture_app/state_image/最小化固定失败）、`ZCODE_CUA_WINDOWS_TOOL_SURFACE_LIVE`（canonical 工具面真实 MCP 调用）；未 opt-in/锁屏/非 default input desktop/native 签名策略阻断时**在创建任何 Helper/fixture 前退出 2**（`:170-239`）【规格】（注：30-tool 口径是 14-tool 收缩前 2026-07 的历史文档，与当前 14 工具面并存于文档，勿混用）。
- Windows 通用 broker 单测：live server/client 必须用每例唯一 `\\\\.\pipe\zcode-cua-test-<random>`，不能把临时目录的 `b.sock` 交给 `net.listen()`；`0600/0700`/symlink/stale socket 等 POSIX 断言只在 macOS/Linux 跑（`:253-262`）【规格】。
### 5.4 用户可见提示：操作指示器 vs ghost cursor overlay

- **操作指示器（desktop main 的 Electron BrowserWindow，顶部提示条）**：
  - 事实判定收敛为一个布尔：「这个 cell 是否在用 Computer Use」；锚点是模型必须原样照抄的引导语句 `setupComputerUseRuntime({ globals: globalThis })`——这是架构强制而非文档软要求（node_repl 每 cell 全新 Worker、SDK 绑定不跨 cell，文档要求每个 CUA cell 的第一条可执行语句就是该 bootstrap，且 bootstrap 与动作必须在**同一 cell**）；链路 `bootstrap mapComputerUseOperationEvent → sideband → services cuaOperationTurnTracker → reporter → desktop windowsCuaOperationIndicator`（`2026-09-14-windows-cua-indicator-repair-spec.md:37-63`）【规格】。
  - 为什么不再按动作名判定：旧正则从 `input.code` 提取 SDK 调用名，node_repl 重构后没有任何真实调用形态能命中（`app.click`/`agent.computerUse.computer.left_click` 都不匹配），唯一能命中的形态只存在于夹具里——**测试长期假绿**（同文件 `:18-31`）。
  - 关闭有六条路（turn 终态/session 关闭/新 turn 顶掉/workspace 关闭/runtime 不可用/应用退出）+ 30s 兜底；`AUTO_HIDE_MS` 从 10s 调到 **30s**（事实按 cell 上报，单 cell 可跑很久）；放弃「stop_computer_control 即时隐藏」换整条链不再依赖动作词表（`:78-104`）【规格】。
  - 【代码】`windowsCuaOperationIndicator.ts`：`BrowserWindow{transparent, frame:false, alwaysOnTop, focusable:false, skipTaskbar, contentProtection, ignoreMouseEvents}`；位置=光标所在 display 的 `workArea` 顶部居中；每次显示 `showInactive() + setAlwaysOnTop(true,"screen-saver") + moveTop()`（**Windows 隐藏透明窗口后可能清除 WS_EX_TOPMOST**，必须重新声明层级）；`HIDE_ANIMATION_MS=120`；`AUTO_HIDE_MS=30_000`；`hide()` 抛错时降级销毁——「它在声称 ZCode 正在操作电脑，隐藏失败就等于向用户撒谎」（`:11-19, 171-196, 297-333`）【代码】。
  - 被否决的替代方案：Helper 自持窗口（用 `dispatchRequest` 做触发点、Electron `BrowserWindow`）——Windows Helper 以 `ELECTRON_RUN_AS_NODE=1` 启动**没有窗口 API**，去掉它每个 Helper 多 3 个 Chromium 子进程、需新协议方法把 IPC 版本 2→3、跨两仓交付；实测数据留档（完整 Electron 下 Helper ready/RPC/stop 通过、`setAlwaysOnTop(_, "screen-saver")`/screen API 可用、硬杀后 3 个 Chromium 子进程随父退出零孤儿）（indicator spec `:145-173`）【规格】。
  - 教训（可迁移）：**展示旁路不能从模型源码模式推断动作**；要锚在架构强制的锚点上，并让「隐藏」是 fail-hidden 的（计时器兜底 + hide 失败销毁），展示路径异常只 warn、绝不截断主 session 链路（`:95-97`）。
- **ghost cursor overlay（Helper 侧 agent 光标反馈）**是另一条链路：argv `--ghost-cursor-overlay` 在 Windows 产品链恒开（`windowsCuaDevHelperHost.ts:309-319`）；多 session overlay 升级 spec（`producer/multi-session-cursor-overlay-spec.md`，942 行，2026-10-24）用于新版 gate/kill-switch，**Windows 真机验收待做**（该 spec 头部状态）【规格】——与本指示器不要混为一谈：一个是 desktop main 的提示条，一个是 Helper 内绘制在桌面上的光标/反馈浮层。
### 5.5 进程生命周期 / 清理 / Job Object containment

- **Agent/MCP 进程树清理（desktop Host 侧，已确认缺陷修复过）**：Windows 不用 POSIX process group，而是 `taskkill /T` 与经身份复核的 `taskkill /T /F`；三个叠加竞态（等待式回收未暴露 flight、`taskkill /F` 成功后 Node `exit` 晚 1-10ms 投递导致把已死 root 追加回残留、多 workspace 共用进程表早于新 Agent spawn）已修（`docs/windows-agent-process-cleanup-state-machine.md:3-33`）【规格】。
- **安全不变量**：PID 只表示数字不代表所有权；强杀前必须匹配进程创建身份，或由 Host 持有的受管 `ChildProcess` live root 句柄证明所有权（句柄例外只适用 root、不适用后代）；身份查询失败/超时 → 未复核后代与 `unverifiedRootOnly` 必须 fail-closed，禁止向裸 PID 发 `/F`；生前进程树快照是 root 退出后认领后代的唯一依据；只有绝对 deadline 到达后仍属原进程树的 PID 才是 confirmed residual，且必须继续向调用方报错（同文件 `:35-45`）【规格】。
- **Deadline**：transport 在 cleanup 起点固定 `cleanupStartedAt + 2,000 + 1,000 + 250 = 3,250 ms` 全链硬上限（低于 desktop Host/server stdio lifecycle 的 3,500ms service-dispose phase）；身份复核在 force 预算内并发进行；多 workspace 并行、总等待不随数量增长（`:108-130`）【规格】。
- **Job Object containment 仍是原型**：为 Windows 本地 stdio MCP root 建**每 transport 独立** Job（不用全局 Job，避免误杀其他 workspace/MCP），MCP close 时优先 `TerminateJobObject + CloseHandle`，失败降级既有 `taskkill /T /F`；只用 transport 自身 root PID attach、不按进程名/全局进程表匹配；**原型在 Node SDK 已创建 root 后才 attach，不能保证覆盖 attach 之前创建的后代**——生产化需 native `CREATE_SUSPENDED → AssignProcessToJobObject → ResumeProcess` launcher（`docs/windows-mcp-job-object-containment.md:7-31`）【规格】。
- 【推断】对 PCCtrl：Windows 执行器进程必须由服务侧以「Job Object 先建、进程后起」的方式托管（或至少 Job + 有效 PID），否则模型/插件崩溃会留下孤儿输入进程；ZCode 的 Helper 清理走 Host 代际 + shutdown/kill 双阶段 + 意外退出恢复，而 MCP root 的 Job Object 是另一条链——**两套 ownership 不能混用**。
- 产品运维文档口径：关闭桌面应用后 Host 先停止接收新工作，再发 Helper shutdown、关 named pipe、等 child 退出；任务管理器应无遗留 `windows-helper.js`，若仍有则保留脱敏日志并停止使用该 broker 直到修复（`windows-source-development.md:385-388`）【规格】。

## 6. 已知缺陷 / 阶段门禁 / 验证集合（问题 F）

### 6.1 阶段与门禁机制

| 阶段 | 状态（文档时点） | 出口判据 |
|---|---|---|
| S1 harness/证据链 | 已具备八宿主 runner、独立 oracle、焦点 watcher | 固定集合 + 原始报告 |
| S2 key 别名/provided paste/list_apps | 已实施 | 保留工具契约与测试 |
| S3 派发地基与点击 | **v3 极简重构进行中**（租约→物理 lane、删全屏主级、6 格拒绝→自动升级） | 原 53/53 口径随 D1 变化需重跑矩阵重建基线 |
| S4 文本/键盘/滚动/拖拽/后台并发 | 已实施（S4.2–S4.5 全部落地并有真机记录） | 八锚点格转绿、键盘族/滚动族/drag 族断言绿 |
| S5 完整矩阵验收 | 首轮全量完成（2026-10-20） | 不能以点击专项通过替代全动作全矩阵 |

（总纲 `:947-962`；S4 计划 `:192-205`）

- **矩阵永远如实**：已知缺陷对应用例照常 `status=fail + focusViolation:true`，不放宽断言；豁免只作用于**阶段出口判据**；账本条目必须「缺陷已定位到机制级（含源码依据）+ 有明确修复方向 + 修复超出本批 scope」，当前条目为空；修复后条目必须移除、门禁口径自动收紧（总纲 `:964-981`）【规格】。
- **必过集合**：普通点击、坐标/身份/权限保护、精确 HWND、并发隔离与条件恢复；适用宿主的 PostMessage 按压驻留/hover/双击；前台点击功能；回执与路径证据。**诊断集合**：UIA 元素路径的 press/hover 门控、保留零驻留单批契约的前台 press 门控——oracle 不满足仍记 fail/noop，另用断言证明 `effect=unverifiable`、不重放、路径如实；同一用例实际改道 PostMessage 后转为该路径必过项。不能看回归结果再调集合、改断言或扩大豁免（总纲 `:983-1008`；S3 `:444-461`）【规格】。
- **路径见证口径**：`strategy=event` 的 press/hover 只证明前台路径；独立 `coordinate+auto` 用例必须断言 `delivery.path=post_message`、未先执行 UIA 动作、未升级，再以业务 oracle 判 pass——路径错误不能因控件最终变化而通过（S3 `:424-437`）【规格】。
- **证据基线**（防「selective memory」）：点击专项 53/53；原始 117 动作 72 delivered/27 noop/9 refused/9 unresolved；全量 133 动作 100 delivered/21 noop/3 refused/9 unresolved；矩阵 harness 与评测从 broker `delivery` 元数据取数、取证覆盖率要求 1.0（S3 `:303-359`；总纲 `:955`）【规格】。
### 6.2 失败案例清单（对 PCCtrl 的直接警示）

1. **焦点违规 21 项（S3 时点）**来自文本路径：WPF `UIElement.Focus()` 抢前台 + 前台插入通道；后被「NoActivateGuard + EnableWindow 双层守卫 + 接缝接线 + 前台建焦前置」修复（13 格清零）。警示：UIA/COM 动作与焦点链会互相污染，**guard 只盖一层必漏**。
2. **Electron press_gated/hover_gated 两格长期红**：单点恢复盖不住 ≥15ms 计时器的晚自激活；有界轮询（100ms×6）实测可覆盖但会让遮挡格事件形态不稳定，被用户否决。警示：**不要用轮询换稳定格**，保存已知残差比伪造稳定更重要（electron 探针 `:69-73`）。
3. **qt6 后台插入两格红**：被否决的补偿（unchanged→点击建焦+重投）实测 `mirror=abcdX`，破坏插入点；决策=登记为已知边界、红灯监督、后台路径不隐式建焦（S4 计划 `:362-369`）。警示：**readback 未变不等于可以补偿重放**。
4. **GTK3 元素路径结构性不可用**（UIA 只有系统 TitleBar、MSAA 无子对象、WM_GETOBJECT=0、无 atk-bridge/gail），从固定集合删除；坐标路径仍需真机。警示：先探针取证再登记「不可用」，不要靠猜；**winforms paste readTimeout 环境存量**：干净 HEAD 复现，环境剪贴板观察者干扰 → paste suite 需串行执行（总纲 `:955`）。警示：全局桌面资源（剪贴板/焦点/光标）的测试必须串行。
5. **WPS 纯图像下拉停止点**：下拉有图但 45 个原生 AX 元素无 Menu/MenuBar/MenuItem，`has_menu=false`，正式工具对 AXExpand 明确 `not_sent`；合同缺口（「图像语义证明由谁生成、如何传入/复验」）需补充裁决，**没有把图像文字伪造成 AX 元素**（frame spec §9.6.1）。警示：不能为了覆盖率把「看图」升级成授权。
6. **`WM_NCHITTEST` 标题栏拖拽 TODO 未落地**：现状投 `WM_LBUTTONDOWN` 静默无效但回执 sent（spec-实现漂移）。警示：**「消息入队成功 + 原语回执 ok」不是生效**；非客户区手势必须真实输入；**WinForms TrackBar 控件级丢弃**：`classify` 只回 host 字符串、不回窗口类名，控件级拦截需要新 native 接口；当前以 noop 契约格钉死。警示：宿主级分类不够，控件级怪癖要单独登记。
7. **E32 链③起点自返 bug**：复制「先查自身 HWND」的写法后链③恒 false，738 次真实归属记录 `uia_ancestor` 0 命中却一直「看起来正常」（靠操作关联兜底）。警示：**指标为 0 的路径要专门断言它在正例上非零**；**4881359c1a 的 TS 预检回归**：在 broker 中间级入口加 `focused_input_window_id===0` 硬抛 `input_focus_unavailable`，不记 attempt、不进升级链、还误挡 XAML 加速键通道；用户裁决后删除并翻转 WSF-09 oracle。警示：**「确定未发送」的唯一表达是 attempts 里的 not_sent + 升级链**，不能在入口 throw 绕过取证面。
8. **夹具/oracle 通道缺口**：WPF/WinUI3 的 `lbl-scroll-offset` 不进状态文件（滚动事件不触发发布）；runner appRef 缺 `window_id` 导致键盘中间级判「层不适用」静默回落前台（四格首轮全走前台）；drag 画布在折叠线下导致端点校验拒绝起点；双 fixture 固定重叠 (4,4) 时 z-order 顶层窗消费彼此坐标点击（P1 取证证伪）。警示：**测试夹具本身是产品合同的一部分**，oracle 通道断了会得到「假红」或「假绿」。
9. **「association changed during image capture」**：19 场景顺序全量跑到第 19 个场景报错，干净基线 worktree 复跑在第 2 场景即复现同款 → 预存缺陷（跨会话 `capture_app` 收尾 verify 的状态污染），非本轮引入（frame spec §9.7 遗留验证项）。警示：先做干净基线对照再归因。
10. **架构检查 15 项 CUA 模块未登记违规（阻断）、基线新鲜度检查远端连接失败未完成、全仓 `fmt:check` 10553 个失败（CRLF 基线）**——门禁与工作区卫生问题必须如实分开报告，不能写成通过（S3 `:360-371`；frame spec `:691-693`）。
### 6.3 验证集合与命令（可直接抄给本项目）

- **真机 native 回归**（S3 时点）：`test/native/win-uia-timeout-lifecycle-cua.test.ts`、`win-uia-patterns-cua.test.ts`、`win-gtk-click-cua.test.ts`；开关 `ZCODE_CUA_HARNESS_E2E=1`；矩阵基线脚本 `packages/zcode-cua/scripts/windows-harness-matrix-baseline.mjs`（S3 `:376-386`）【规格】。
- **S4 验证项 N1-N12**：后台滚轮消息、SB 码族可达性、WM_CHAR 后台插入、set_value 读回、echo 降级、键盘自动升级、Chromium 元素 bounds 新鲜度、物理动作排队、per-root burst 队列、并发 E2E、同进程多 session、插入位置保真（S4 计划 `:221-241`）【规格】。
- **并行实验 P1-P8**（同 Helper 单进程内）：双窗口并发后台点击、同窗并发双击×2（无拆单）、后台 PostMessage+前台升级并发、WM_CHAR 打字+异窗点击、双 paste、双 session、双窗口 UIA 元素点击、同窗元素+坐标；runner `windows-harness-parallel-experiments.mjs`，对照断言 busyTotal=0（S4.0 全文）【规格】。
- **frame/panel/menu/operation surface**（2026-10-25/26）：`windows-frame-surface.cua.test.ts`、`windows-panel-surface.cua.test.ts`（12 组）、`windows-menu-surface.cua.test.ts`（9 组）、`windows-operation-surface.cua.test.ts`（17→22 组）、`windows-operation-native-cua.test.ts`、`windows-receipt-boundary.cua.test.ts`；真机组摘要 48→49 组；入口命令见 frame spec §9.5/§9.6【规格】。
- **14 工具面 smoke**：`ZCODE_CUA_WINDOWS_TOOL_SURFACE_LIVE=1 node packages/zcode-cua/scripts/windows-tool-surface-live-smoke.mjs`（14 工具全部真实 MCP 调用、业务计数/读回/清理）（frame spec `:745`）【规格】。

## 7. 对本项目（PCCtrl / Windows 执行器）的建议（问题 G）

### 7.1 技术选型：混合升级链，不要单押一条

| 候选 | 适用面 | 代价/边界 | 建议 |
|---|---|---|---|
| **UIAutomation（COM，`IUIAutomation`）** | 元素语义动作（Invoke/Toggle/Select/Expand/Value/Scroll/TextRange）、树与身份、background hit-test | COM 线程模型（每实际调用线程 `CoInitializeEx(MTA)` + 私有 `CUIAutomation`，`RPC_E_CHANGED_MODE` fail-closed）、元素强引用生命周期、Chromium echo 不可信、UWP CoreWindow 空壳、4s deadline 只是等待预算 | **首选元素路径**；保留 LegacyIAccessible 末级兜底 |
| **PostMessage（WM_* 定向消息）** | 后台坐标点击/单键/文本插入/WPF-WinUI3 的 SB 滚动；不抢焦点 | 宿主依赖极强（丢弃矩阵）；组合键结构性不可投；滚轮全败；WPF 键盘靠 GetKeyState 必败；VCL/GTK 部分必丢；UIPI 阻断返回 error 5；消息入队≠控件消费 | **坐标点击 + 单键 + WM_CHAR 的优选后台级**；**不要**作为滚动/拖拽/和弦的主路径 |
| **SendInput（物理注入）** | 和弦、滚轮、拖拽、非客户区手势、XAML/UWP 手势、最后兜底 | 必须前台 + 抢焦点 + 条件恢复 + 排水/逐字节拍；用户切走要停；长文本可能不恢复；UIPI | **物理 lane 串行 + 节拍 + 条件恢复**；所有真机验收以业务 oracle 为准 |
| **合成 pen/touch（digitizer）** | 前台升级占比不可接受时的候选 | 设备生命周期/tap 节拍/焦点影响/重复输入未验证；ZCode 明确 defer | **不建议 day-1**，留作未来项 |
| **外部工具（PowerShell/UIAutomation 脚本、AutoHotkey、RobotJS）** | 快速原型 | 启动开销、权限/会话边界、签名与杀软、进程孤儿、不可控焦点；ZCode 全程不用 PowerShell/外部命令做输入（剪贴板/截图也走原生） | 仅调试，不进产品 |

**语言/形态建议（【推断】，结合 PCCtrl 未定技术栈）**：
- 执行器做成**常驻本机进程**，对外用**本地 IPC + 明确鉴权**（named pipe/AF_UNIX + NDJSON 或 gRPC），不要由模型侧每次起进程；输入/观察原语在单进程内用互斥/队列串行化。
- 语言优先级：C#（`UIAutomationClient` + P/Invoke）或 C++/Rust（Win32 + UIA COM + SendInput）> Node N-API。ZCode 选 N-API 是因为 Helper 本身就是 Electron/Node 且要跟既有 JS broker 同仓；PCCtrl 无此约束，**C# 的 UIA COM 互操作成本最低、调试最直观**；若团队是 JS/TS，则 Node + `koffi`/N-API 也可，但要自担 COM 线程与 addon 签名（Windows Smart App Control/Code Integrity 会拦未签名 `.node`）。
- 传输层学 ZCode 的**两阶段就绪**与「portable tuple 只暴露 socket + authority」；但**不要照抄它当前 Windows 无鉴权状态**（peer identity gate 临时放开）：PCCtrl 至少要 pipe ACL + 随机一次性 token + 父进程/签名校验，否则本机任意进程都能驱动桌面。
### 7.2 必须处理的 Windows 边界（按踩坑概率排序）

1. **UIPI**：高完整性目标 fail-closed 预检；PostMessage 失败 error 5、SendInput 无法区分原因。
2. **焦点**：SetForegroundWindow 前台锁；`AttachThreadInput` 借锁；用户切走后不得抢回；自身 UIA/消息动作可能触发框架自激活（XAML/WPF/Chromium 三类不同机制）。
3. **节拍**：down→up 零驻留对 retained-mode 控件无效；PostMessage 35ms 驻留/80ms 多击/50ms settle；前台 40ms/120ms；拖拽 35ms pre-dwell + 12×25ms + 40ms settle；键 4ms；文本逐字 4ms + 120ms 排水（长文本 n×20ms，>50 单位不恢复前台）；**DPI/多屏**：Per-Monitor V2；物理像素；`MOUSEEVENTF_ABSOLUTE|VIRTUALDESK` 归一化；坐标只来自「刚观察到的 raster 像素」，模型不做任何换算；副屏负坐标/混排 DPI 必须真机验证（ZCode 自己也没验完）。
4. **UWP/XAML**：AFH 共享 pid + 真实渲染 pid + UIA ProcessId 三重分裂；XAML CoreInput 只吃系统输入队列（键盘/手势后台必走前台或 UIA 加速键）；cloaked 需 AUMID 唤醒。
5. **WPF**：单 HWND、键盘焦点门控 → WM_CHAR/键盘后台不消费；`Focus()` 会自抢前台；用双层 guard；**Chromium/Electron**：PostMessage 点击可达（直接宿主实测 9/9），但异步 handler 会晚自激活；UIA 的 S_OK 不可信（DOM 无事件）；组合键/文本必须前台。
6. **菜单/弹层/模态面板**：不要用 PID/class/标题猜归属；用 owner 链/GUI 菜单态/UIA 层级；坐标权限绑定像素属主；面板要显式激活实际窗；这块是 ZCode 花了 2026-10-23～28 五步 + 49 组真机才落地的重活——**PCCtrl 首版可先不做 popup 合成**，先保证单窗观察与派发正确。
7. **窗口/元素身份复用**：HWND 与 UIA RuntimeId 都可能复用，hide≠destroy；引入 `window_generation`/frame 代际 + 投前重验，不接受「同 HWND/RuntimeId 就续期」；**剪贴板/会话安全**：锁屏/Session 0/非交互桌面必须 fail-closed；剪贴板全局资源要串行与恢复；读剪贴板/写剪贴板在 14 工具面已删除，PCCtrl 若要 paste 必须自己做 provided-paste 语义（快照→延迟渲染→读回→还原）。
8. **打包与签名**：原生模块会被 Smart App Control/Code Integrity 拦；产品化路径必须含 afterPack 签名与 hash 刷新；测试/开发可用未签名但不得对外宣称。
9. **进程与孤儿**：Job Object 先建后起（`CREATE_SUSPENDED→Assign→Resume`）；shutdown/kill 双阶段 + exact PID；意外退出恢复不能回收 Agent 侧凭据。
### 7.3 推荐实现顺序与验收用例

**顺序（每步先写 fixture/oracle 红测再实现）**：
1. **P0 观察地基**：窗口枚举/身份（HWND+PID+exe+window_id）、WGC 帧、`ReadWindowFrameBounds` 同源、坐标→raster 绑定、frame 新鲜度（TTL/代际）→ 先让「看得到、点得准」可复现。
2. **P1 点击**：UIA 元素梯（Invoke/Toggle/Select/Expand）→ 坐标 UIA hit-test（ElementFromPoint + 窗口域白名单枚举）→ 前台 SendInput 兜底（精确 HWND gate + 单批 + 节拍）；`delivery` 元数据从第一天就带。
3. **P2 焦点与恢复**：NoActivateGuard/EnableWindow shield 双层、条件恢复三条件、120ms 排水、长文本策略、用户切走检测。
4. **P3 文本**：set_value 替换 + 三段读回；type(event) 插入（WM_CHAR + focused descendant 三态 + changed/contains）；Chromium echo 降级。
5. **P4 键/滚动/拖拽**：单键 PostMessage + 和弦短路前台 + XAML AcceleratorKey；scroll 三档路由；drag 节拍 + 非客户区拒绝（NCHITTEST 先做，避免 ZCode 的 sent 假成功）。
6. **P5 UWP/XAML 与多宿主矩阵**：AFH 拓扑、宿主判定统一谓词、每宿主 fixture。
7. **P6 多屏/DPI/高完整性/崩溃恢复**：真机矩阵（ZCode 留的坑）。

**验收用例（可直接作为 PCCtrl 的 first-green 集合）**：
- WinForms 按钮坐标点击（业务 counter+1、无焦点事件、path=post_message）；
- WPF 按钮元素点击（Invoke；无抢前台）与 WPF 文本框 `set_value` 全文读回、type 插入位置保真（`abXcd/aXd`）；
- 记事本：`get_app_state` 冷启动 → type → **Ctrl+S 和弦前台 SendInput 落地**（文件 oracle）；
- Electron/Chromium 页面按钮后台+遮挡点击（DOM oracle +1）、双击第二击 `WM_*BUTTONDBLCLK`；WPF/WinUI3 SB 码族后台滚动（offset=N）vs WinForms 前台滚轮升级（escalated=true）；
- 拖拽 slider（value 前进、path 正确、UP 后 40ms settle）、标题栏拖拽**拒绝**；
- WinUI3/UWP 元素双击两次 Invoke（计数恰好 +2）、UWP 计算器坐标单击；
- 高完整性窗口 → `refused` + attempts `uipi_blocked` + 零激活；
- 200% DPI 单屏 + 副屏负坐标（若有多屏机）点击落点；
- 用户在多击间隙/派发间隙切走：停止余下输入、不抢回、回执如实；目标窗口 4s 内不返回 → `timeout`，无重放、无重复动作；Helper 崩溃/被杀 → 无孤儿、后续调用可恢复。

## 8. 未解问题 / 边界发现（范围外但值得 Lead 知道）

1. **总纲内部文档张力（3 处）**：①§3.1 首段「已确认不支持的后台手势返回 `background_unavailable`」 vs v3 契约与 §2.1「自动升级、不再预登记拒绝」；②§2.1 表「Chromium 普通左键单击：直接与嵌套宿主均先尝试已验证 PostMessage（2026-10-25 对齐）」 vs §4.1-4「嵌套宿主维持 UIA/特例路由不动」（同日期，口径不一）；③§2.1 表「WinUI3 坐标双击/右击/中键明确拒绝」 vs v3「跳过本级直接进 4」。建议主报告按「v3 自动升级」为现行契约、其余为残留，并标注这是文档漂移而非实现证据。
2. **`classify_delivery` 接口只回 host 字符串**，不回窗口类名——WinForms TrackBar 这类控件级丢弃无法在现有接口表达，需要新 native 接口（总纲 `:174-184`）。
3. **NCHITTEST/HTCAPTION 检查未实现**（总纲 §4.3 TODO），拖拽标题栏会 sent 假成功——若 PCCtrl 做拖窗，必须自己实现。
4. **Windows broker peer-identity gate 临时放开**：本机任意进程可连 named pipe（源码开发手册明写），属于产品安全债；**真实高完整性 UIPI、完整多屏/DPI、虚拟桌面负坐标、provider 永久阻塞、Helper 异常退出恢复均未验证**（S3 `:400-401`）。
5. **多屏/拓扑切换与其他 OS 本轮明确不做**（frame spec §7.8/§9.6.2，用户 2026-10-25 指示）；WPS 图像语义合同缺口保留（§9.6.1）。
6. **Electron 晚自激活残差**：press_gated/hover_gated 两格红灯保留，单点恢复边界；有界轮询被否决。
7. **Job Object containment 仍是原型**（attach 时机 race），生产化需 native launcher。
8. **ghost cursor overlay 的 Windows 实现/真机验收不在本仓**（multi-session overlay spec 明确 Windows 真机验收待做）；**本仓所有 2026-07/09 的「30-tool」口径与 2026-09-10 后的「14-tool」口径在 docs 中并存**（windows-product-runtime / windows-source-development 是 14 工具收缩前写的），引用旧文档的 smoke 数字时不要混算。

## 证据索引（文件:行号）

- **输入派发总纲** `docs/cua/2026-10-13-windows-input-delivery-spec.md`——`:1-35`（状态/三级阶梯/53-53 口径）、`:36-76`（当前实现表+状态链）、`:77-127`（升级链）、`:129-197`（丢弃矩阵）、`:199-235`（UIPI/回执）、`:237-393`（焦点/排水/长文本）、`:395-464`（UWP/XAML）、`:465-704`（逐工具）、`:706-826`（key 别名/paste/list_apps/UWP 归属）、`:876-1025`（约束/多屏/物理 lane/阶段/门禁/不做）。
- **S3 计划（部分 superseded）** `docs/cua/2026-10-16-windows-input-s3-dispatch-plan.md`——`:43-131` 状态机与租约历史、`:132-212` 几何/归属/强引用、`:242-302` 节拍/原语/delivery、`:303-498` 验证摘要与边界。
- **S4 计划** `docs/cua/2026-10-19-windows-input-s4-plan.md`——`:36-119` 文本/键盘/scroll/drag/锁、`:120-205` P1-P8 与阶段、`:243-376` §12-14 实测修正。
- **探针与并行基线** `docs/cua/2026-10-19-scroll-postmessage-probe-report.md:18-74`、`docs/cua/2026-10-20-electron-postmessage-probe-report.md:22-73`、`docs/cua/2026-10-19-windows-input-s4-parallel-baseline.md:9-41`。
- **App Frame Surface** `docs/cua/2026-10-23-windows-app-frame-surface-spec.md`——`:14-38` D1-D8、`:89-150` 四 kind、`:191-241` 归属/操作关联、`:243-342` 枚举/合成/坐标/装饰窗、`:343-449` 派发侧合同/重验/D7/回执、`:451-579` E01-E33、`:608-883` WSF 验收与六步实施/真实应用。
- **指示器** `docs/cua/2026-09-14-windows-cua-indicator-repair-spec.md:37-104`（bootstrap 锚点/关闭路径/30s）、`:143-171`（否决方案与实测）。
- **运行与开发** `docs/cua/windows-product-runtime.md:12-251`（运行时/manifest/签名/验收）；`docs/cua/windows-source-development.md:5-101`（构建/peer gate 放开）、`:170-312`（smoke/身份解析）、`:385-387`（清理）；`docs/cua/producer/windows-helper-transport-ready.md:1-32`、`docs/cua-permission-broker/windows-transport-ready.md:5-33`（两阶段就绪）。
- **身份/透明拉起** `docs/cua/producer/windows-app-identity-and-transparent-launch.md:13-190`（真机现状/焦点/四状态）、`:194-324`（设计）、`:370-394`（风险与未验证）。
- **进程/containment** `docs/windows-mcp-job-object-containment.md:7-31`；`docs/windows-agent-process-cleanup-state-machine.md:35-130`。
- **真实代码** `packages/services/src/cua-permission-broker/windowsCuaDevHelperHost.ts:34-475`；`windowsCuaHelperHostSupport.ts:16-244`；`windowsCuaDevRuntime.ts:38-519`；`packages/desktop/src/main/windowsCuaOperationIndicator.ts:11-333`（+Content.ts 未逐行核实）；`packages/zcode-cua/broker.d.ts:26-96`、`broker-server.d.ts:108-167`（契约）。
- **工具面/坐标契约** `apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:159-209`；`scripts/computer-use-client.mjs:30-71, 204-218, 312-366, 706-888`；`docs/cua/producer/frame-coordinate-internal-binding.md:18-85`；`cua-14-tool-e2e-validity.md:11-68`。
- **旁证** `docs/cua/cua-tool-app-identity-summary.md:8-51`、`docs/cua/2026-08-19-cua-target-app-display-metadata-spec.md:115-161`、`docs/cua/producer/multi-session-cursor-overlay-spec.md:31-73, 435-535`。
