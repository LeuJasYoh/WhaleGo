# ZCode 电脑操控（Computer Use / CUA）实现研究

> **用途**：PCCtrl 项目的前期技术参考 + 给后续开发智能体的交接文档。
> **研究基线**：工作区 `ZCode-main/`（ZCode 开源构建快照，文档时间戳至 2026-10 前后）；
> 证据类型标注【代码】【规格】【契约】【推断】，详见 §1.4。
> **报告日期**：2026-10-31（研究完成日）；**产出方式**：6 个分题研究智能体并行 + Lead 交叉核验与综合。
> **本报告不产出实现代码**；所有"建议"均标注为建议，与 ZCode 事实分开。

---

## 0. 一页速览（TL;DR）

> 本报告研究的是 `ZCode-main/` 里 **Computer Use（CUA，电脑操控）** 的实现方式。
> ZCode 是"桌面端 Coding Agent"产品：模型通过 `node_repl` 单一 `js` 工具执行代码，
> 代码里调用注入的 `agent.computerUse` SDK，最终由**用户本机的签名 Helper 进程**调用
> macOS/Windows 原生 API 完成"看屏幕、点鼠标、敲键盘"。

**十条核心结论**

1. **执行位置是"用户本机桌面"，不是容器/远端。** CUA 只在 desktop-local Host 上可用；
   SSH/WSL/Docker/remote workspace 不创建 Helper，手机/Web 只能 attachment 到已有桌面 Host。
   这与 PCCtrl "容器内 AstrBot → Windows 本机执行器"正好相反，**跨机器/跨容器的受控通道与认证必须由我们自己设计**。
   （`docs/cua-permission-broker/zcode-cua-permission-broker.md:14-16,27`；`docs/cua/windows-source-development.md:3`）
2. **模型面被刻意收敛成"1 个入口工具 + 一套 SDK"，而不是 N 个 MCP 工具。**
   模型只看到 `mcp__node_repl__js`；CUA 是 SDK 注入，底层另有 14 个工具但只作逃逸口。
   工具面经历 **30 → 14**（2026-09-10 SDK v3）与 **3 → 1**（2026-09-18 删 `js_reset`/`js_add_node_module_dir`）两次有意收缩。
   （`apps/zcode-cli/packages/node-repl-host/src/server.ts:91-98`；`docs/cua/producer/2026-09-10-cua-sdk-v3-api.md:16,32-54`）
3. **观察以 Accessibility 树为第一优先，截图像素只作兜底。** AX 元素动作语义化、精确、可在后台执行且不抢焦点；
   坐标点击/截图留给 canvas、游戏、Electron 等 AX 不可见内容。模型拿到的是**渲染后的树文本 +（可选）图片**，
   树支持 diff、按优先级裁剪、`elements()` 取完整元素表。
   （`apps/zcode-cli/packages/zcode-cua-plugin/skills/computer-use/SKILL.md:39-65,128-177`）
4. **坐标契约是"最新 raster 的整数像素"，其余全部内部化。** 模型只看图抄 `x/y`，不传 `frame_id`、
   不做 DPI/Retina/多屏原点换算；CUA 内部把坐标绑定到"本 transport 最新可动作的帧"，过期/跨会话/不可动作一律 fail-closed。
   AX 的 `bounds` 是全局屏幕点，**明确禁止**当坐标用（这是最常见的误用）。
   （`docs/cua/producer/frame-coordinate-internal-binding.md:18-47,57-85`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:425-428`）
5. **"动作被接受"≠"操作生效"。** 动作方法成功返回 `undefined`，失败抛 `ComputerUseError`；
   回执核心三字段：`action_sent`、`dispatch_status(accepted/possibly_sent/not_sent)`、`retry_action`。
   `possibly_sent` 时必须先重新观察、**禁止自动重放非幂等动作**；工具层不隐式重试/重观察。
   （`docs/cua/producer/cua-receipt-observation-contract.md:8-50`；`SKILL.md:237-263`）
6. **Windows 输入是一条"后台优先的自动升级链"：UIA → PostMessage → 前台 SendInput。**
   升级只发生在"确定未发送且身份/权限仍有效"时，成功路径不向模型披露升级；UIPI 预检先于一切；
   物理动作（激活/SendInput/滚动/拖拽/全局键盘）进程内串行排队，后台 UIA/PostMessage 不排队。
   （`docs/cua/2026-10-13-windows-input-delivery-spec.md:27-29,77-127,199-207`）
7. **Windows 最大的坑不是"怎么点"，而是焦点、时序与身份。** 包括：焦点自抢防守三件套、
   SendInput 注入后 120ms 键盘排水、逐字符 4ms 节拍、精确 HWND gate、UWP 三重 PID 分裂、
   Chromium/WinUI3/WPF/VCL/GTK 的 PostMessage 丢弃矩阵。这些是真实的工程资产，PCCtrl 可逐条复用。
   （`docs/cua/2026-10-13-windows-input-delivery-spec.md:129-197,237-367,395-463`）
8. **安全模型的核心是"权限主体与调用入口分离"。** macOS 上 TCC 授权主体必须是独立签名的
   `ZCode Computer Use.app`；入口侧（Agent）只拿 per-call token / broker 凭据，不能裸跑；
   Helper 校验 peer/launcher 身份；凭据从所有子进程 env 剔除，fail-closed 规则多达 18 条。
   （`docs/cua-permission-broker/zcode-cua-permission-broker.md:18-35`；`docs/cua-permission-broker/cua-helper-installation.md:46-56`）
9. **可靠性设计可直接照抄：** Helper 懒启动、Windows 两阶段就绪（`transport_ready`→`ready`）、
   崩溃恢复复用 socket/pipe、kill switch、孤儿清理；`node_repl` 侧一次性 Worker、per-call token、
   结果预算（图片 base64 ≤200 KiB）、错误只回 message、`ping` 判活、no-progress 提示。
   （`docs/cua-permission-broker/2026-09-21-cua-helper-lazy-startup-spec.md:19-38`；`docs/mcp-tool-output-budget.md:21-42`）
10. **仓库里唯一"完整可读"的自动化实现是 Browser Use（浏览器操控）。** 它把"对象模型 + 单一命令通道 +
    snapshot/ref + CDP 真实事件 + 结构化结果/错误"做成了真实代码（不是规格），
   是设计"Windows 桌面操控 MCP 服务"时可逐层参考的工程范本。
    （`packages/desktop/src/main/browserView/browserCommandExecutor.ts:36-134`；`packages/shared/src/browser-use/commands.ts:307-591`）

**对 PCCtrl 的三条最强建议（详见 §11）**

- **A. 先定"执行器侧协议"，再定语言/框架。** 建议 Windows 侧一个常驻 MCP server（Streamable HTTP，
  仅监听回环或受控 LAN + 强 token），AstrBot 用内置 MCP 客户端注册；工具面保持"粗粒度、少而稳"
  （`observe / act / verify / app_launch / window_* / stop` 一类），错误语义照抄 ZCode 的 `actionSent/retry`。
- **B. 观察与坐标方案照抄 ZCode 的契约，而不是自己发明。** "截图→模型给像素→内部绑定最新帧→过期即拒绝"、
  "AX bounds 只做诊断"、"动作后必须重新观察才能下一步"，这三条能消灭坐标类 bug 的大头。
- **C. 安全与失败关闭必须 day-1 设计。** token 与来源校验、单会话租约、危险动作确认、
  `possibly_sent` 防重放、审计日志、屏幕数据不外泄、执行器与 AstrBot 生命周期解耦；
  容器→Windows 的链路天然跨机器，不能依赖"同机 peer 校验"。

**文档导航**

| 想了解 | 读 |
| --- | --- |
| 整体架构与一次点击的完整链路 | §2 |
| 模型能看到的工具/参数/错误/调用范式 | §3、附录 A/B |
| 观察（AX/截图/坐标/窗口定位）怎么做 | §4、§6.4-6.6 |
| 动作（点击/输入/拖拽/滚动/按键/粘贴）语义 | §5、§6.3 |
| Windows 专项实现与坑 | §6 |
| 权限/安全/失败关闭 | §7 |
| 生命周期/可靠性/MCP 工程 | §8、§9 |
| 浏览器自动化实现参考 | §10 |
| PCCtrl 落地方案与分阶段路线 | §11 |
| 分题证据笔记（6 篇，含逐文件行号） | `research/zcode/notes/*.md` |

---

## 1. 研究对象、方法与边界

### 1.1 ZCode 是什么（与本项目相关的部分）

ZCode 是一套"桌面 Agent"产品形态：Electron 桌面应用（`packages/desktop`）+ Coding Agent CLI（`apps/zcode-cli`）
+ 一堆插件/skill/MCP 运行时。与本项目直接相关的是其 **Computer Use（CUA）** 能力：

- 产品能力图上，CUA 被描述为"macOS/Windows 本地默认启用的 CUA Helper、权限引导、屏幕读取、原生输入"，
  模型通过 shared-host `node_repl` 的 `agent.computerUse` SDK 调用，状态留在 shared host；
  Windows 通过 `computer-use/operation-event` 在 CUA turn 期间显示顶部操作提示。
  （`docs/product-capability-map-office-computer-use.md` CUA 行）
- 2026-08 发生过一次关键迁移：CUA **不再注册独立 MCP server**，而是收敛到 shared `node_repl` 的单一 `js` 工具；
  `computer-use-client.mjs` 在 Worker 内注入 `agent.computerUse`，请求经一次性认证的本地 bridge
  路由到 shared-host `CuaControlPort`。
  （`docs/cua-permission-broker/zcode-cua-permission-broker.md:5-12`）
- 本文的"电脑操控"= CUA（原生应用 UI 的读取与操作），**不包含** Browser Use（网页内操控）；
  但 Browser Use 是同宿主上的真实实现，§10 单独作为工程参考。

### 1.2 本仓库能证明什么、不能证明什么（开源构建 vs 闭源 CUA 运行时）

这是使用本报告时**最重要的一条边界**——本快照不是完整产品源码：

| 类别 | 内容 | 在本快照中的状态 |
| --- | --- | --- |
| 模型侧工具契约 | `zcode-cua-plugin` 的 SKILL.md、docs/computer-use.md、`scripts/computer-use-client.mjs` | **真实代码/文档**（可逐行引用） |
| node_repl 运行时 | `apps/zcode-cli/packages/node-repl-host/**` | **真实 TypeScript 源码**（含测试） |
| CUA 权限/生命周期 glue | `packages/services/src/cua-permission-broker/*.ts` | **真实源码**（Helper host/installer/permission service 等） |
| CUA 运行时契约 | `packages/zcode-cua/*.d.ts` | **API-compatible placeholder**（`0.6.5`）：所有运行面报 unavailable、fail closed；真实实现不在仓库 |
| CUA native producer | `ax_win.cc`/`windowsInputDispatch.ts`/`screen_capture_win.cc` 等 | **不在仓库**（全仓 grep 零命中）；只能依赖 `docs/cua/**` 设计规格 |
| CUA 产品规格 | `docs/cua/**`（约 60 篇）、`docs/cua-permission-broker/**`、`docs/cua/producer/**` | **真实产品文档**（可作事实来源，但不是可执行代码） |
| 浏览器操控 | `apps/zcode-cli/packages/browser-use-plugin/**`、`packages/desktop/src/main/browserView/**`、`packages/shared/src/browser-use/**` | **真实完整实现** |
| MCP 宿主 | `packages/shared/src/mcp.ts`、`packages/adapters/**`（注：部分文件不在快照内） | 部分真实代码 + `docs/mcp-*.md` 规格 |

因此本报告使用**四类证据标记**（见 §1.4），并在关键处标出版本"时差"。
**不要把 placeholder 的 `packages/zcode-cua/*` 当作实现来读**——它只有类型契约（`index.d.ts`、`broker.d.ts`、
`frame-contract.d.ts`、`request-access-contract.d.ts`、`pip-session.d.ts` 等），
恰好适合用来确认"对外接口长什么样"。

### 1.3 研究方法与材料地图

研究按"问题—证据—结论"组织，分成 6 个分题并行（见 `research/zcode/notes/`），Lead 负责交叉核对与综合：

| 分题 | 覆盖材料 | 产出 |
| --- | --- | --- |
| 架构/权限/生命周期 | `docs/cua-permission-broker/` 全 10 篇、`packages/services/src/cua-permission-broker/`、`packages/zcode-cua/*.d.ts` | `notes/01-cua-architecture.md` |
| 模型侧工具契约 | `zcode-cua-plugin/skills/computer-use/SKILL.md`、`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md`、`scripts/computer-use-client.mjs` | `notes/02-cua-model-tool-contract.md` |
| 底层 SDK/回执/坐标/AX | `docs/cua/producer/*.md`、`docs/cua/2026-08-*/**` | `notes/03-cua-runtime-contract.md` |
| Windows 输入/帧/运行 | `docs/cua/2026-10-13-windows-input-delivery-spec.md` 等 14 篇、`windowsCua*.ts` | `notes/04-windows-input-and-frames.md` |
| MCP/node_repl | `node-repl-host/**`、`docs/mcp-*.md`、`docs/browser-use/*node-repl*` | `notes/05-mcp-and-node-repl.md` |
| 浏览器真实实现 | `browser-use-plugin/**`、`packages/desktop/src/main/browserView/**`、`docs/browser-use/**` | `notes/06-browser-control.md` |

补充材料：AstrBot 侧的 MCP 客户端与内置 computer_tools（`/d/tmp/AstrBot-src`，基线 master@2026-10-31），
用于 §9.4/§11 的**对接可行性**判断；AstrBot 插件机制已有前序研究（`research/astrbot/`），本报告不重复。

### 1.4 证据等级与引用约定

| 标记 | 含义 | 典型来源 |
| --- | --- | --- |
| 【代码】 | 本快照内可编译/可运行的真实源码 | `node-repl-host/src/*.ts`、`computer-use-client.mjs`、`browserView/*.ts` |
| 【规格】 | 产品设计规格/事实账本；是本仓库的"文档事实"，但对应实现可能闭源或未在本快照 | `docs/cua/**`、`docs/cua-permission-broker/**` |
| 【契约】 | 类型定义、常量、占位包 | `packages/zcode-cua/*.d.ts` |
| 【推断】 | 基于以上材料的推论，报告中显式标注 | 本报告 §9.5、§11 |

引用格式：`ZCode-main/<相对路径>:<行号>`；为简洁，正文中也使用简写 `docs/...`、`packages/...`、`apps/...`
（均相对 `ZCode-main/`），以及 AstrBot 的 `astrbot/...`（相对 `/d/tmp/AstrBot-src`）。行号以工作区快照为准；
若后续仓库路径不同，按文件名检索即可。

**已知的版本"时差"（引用时务必注意）**：

- **docs 里的 broker token 叙事是迁移前威胁模型，现行代码是"身份模式"**（socket + pluginAuthority，无口令）。
  （`packages/shared/src/runtimeEnv.ts:64-76` 注释；详见 §7.2）
- **设计文档用 `cua` 全局对象，现行模型面是 `agent.computerUse`**；v3 文档里"getWindow 不文档化"等结论
  也已被后续 SKILL/docs 覆盖。（`notes/02` 结论 11；`docs/cua/producer/2026-09-10-cua-sdk-v3-api.md:745-762`）
- **回执契约（producer 侧）与模型面（SDK 侧）不是同一个东西**：producer 工具默认返回
  `action_receipt`，SDK 把它转成"动作 resolve(void) / 失败抛 `ComputerUseError`"。
  （`docs/cua/producer/cua-receipt-observation-contract.md:8-42`；`SKILL.md:237-250`）
- Windows 总纲内部存在**上一版残留文案**（如 `background_unavailable`）与 v3"自动升级"口径的张力，
  本报告以 v3 现行口径为主并标注。（`docs/cua/2026-10-13-windows-input-delivery-spec.md:83,99-100`；`notes/04` §2.1）

---

## 2. 全局架构：一次"模型操控电脑"到底经过哪些进程

> 本章回答：一次"点击"从模型输出到系统 API，中间经过几个进程、几种凭据、几条 socket。
> 结论先行：**ZCode 是"本机桌面 Agent"的同机架构**，跨机器/跨容器不是它的场景；
> PCCtrl 必须在这套架构骨架上，额外补上"网络传输 + 远程认证"这一层（见 §11）。

### 2.1 组件拓扑图

```text
                       ┌──────────────────── 云端 ────────────────────┐
                       │  模型（provider）                             │
                       │   只看到一个工具：mcp__node_repl__js          │
                       └───────────────────┬──────────────────────────┘
                                           │ MCP（工具定义/调用）
┌──────────────────────── 用户本机（desktop-local Host） ─────────────────────────┐
│                                                                                 │
│  ┌───────────────────── Agent CLI 进程 ──────────────────────┐                  │
│  │  node_repl MCP server（stdio，唯一 MCP 入口）              │                  │
│  │    ├─ call ledger：每次 tools/call 铸一个一次性 token      │                  │
│  │    ├─ Worker 线程（每个 js 调用临时创建，执行完销毁）        │                  │
│  │    │    └─ bootstrap computer-use-client.mjs               │                  │
│  │    │         agent.computerUse.*  → 私有 bridge globals    │                  │
│  │    ├─ CUA broker #1（本进程内）：pipe/socket + token 校验    │                  │
│  │    │    Windows: \\.\pipe\zcode-node-repl-cua-<uuid>       │                  │
│  │    │    POSIX:  $TMP/znrc-<uuid>.sock                       │                  │
│  │    └─ ComputerUseRuntime（shared host，闭源）               │                  │
│  │         └─ CuaRuntimePool 按 workspaceKey+sessionId 隔离     │                  │
│  │              └─ frame registry / kill switch / 输入租约      │                  │
│  └───────────────────────────────┬────────────────────────────┘                  │
│                                  │ 稳定 socket/pipe（Helper broker #2）           │
│  ┌──────────────── Desktop Local Host（Electron main / services）──────────────┐ │
│  │  Helper host / installer / permission service / spawn env 注入 / PiP 投递   │ │
│  └───────────────────────────────┬────────────────────────────────────────────┘ │
│                                  │ 启动 + 注入 broker 凭据（纯数据）              │
│  ┌───────────────────────────────▼────────────────────────────────────────────┐ │
│  │  签名 Helper 进程（权限主体）                                               │ │
│  │    macOS: "ZCode Computer Use.app"（bundle dev.zcode.cua-helper）          │ │
│  │    Windows: 安装包内 Electron（ELECTRON_RUN_AS_NODE=1）fork 的 Node 进程     │ │
│  │    └─ native 插件（ax_native.node 等）：UIA / SendInput / WGC / 剪贴板       │ │
│  └───────────────────────────────┬────────────────────────────────────────────┘ │
│                                  │ OS API                                       │
│        macOS: TCC（辅助功能/录屏）│ Windows: UIAutomation / SendInput / WGC      │
└─────────────────────────────────────────────────────────────────────────────────┘
        ▲
        │ 手机/Web：web-remote-replayable（只 attachment 到已有桌面 Host，不建 Helper）
        └── 其他设备
```

（图依据：`docs/cua-permission-broker/zcode-cua-permission-broker.md:5-16`；`notes/01-cua-architecture.md` §1.2；
`apps/zcode-cli/packages/node-repl-host/src/cua-broker.ts:26-67`；`docs/cua/windows-product-runtime.md:44-61`）

**组件职责表**

| 组件 | 运行位置 | 职责 | 证据 |
| --- | --- | --- | --- |
| 模型 provider | 云端 | 只输出 `js` 工具调用（代码 + title + timeout） | 【规格】`zcode-cua-permission-broker.md:7-12` |
| node_repl MCP server | Agent CLI 子进程（stdio MCP） | 注册唯一 `js` 工具；管理 Worker、call ledger、两个私有 broker | 【代码】`node-repl-host/src/server.ts:91-98,395-462` |
| Worker | Agent 进程内线程（每次调用新建） | 跑 JS、注入 `nodeRepl` 与两个域 bridge | 【代码】`node-repl-host/src/server.ts:312-348` |
| ComputerUseRuntime（CuaControlPort） | Agent 侧 shared host（闭源） | CUA 会话/frame/AX 缓存/动作策略/kill switch | 【契约】`packages/zcode-cua/index.d.ts:3-39` |
| Desktop Local Host | Electron main / Node services | 组装 Helper host、安装校验、权限服务、spawn env 注入、UI 投影 | 【代码】`packages/services/src/cua-permission-broker/*.ts` |
| Helper | 独立进程（签名 .app / Electron Node fork） | 唯一持有系统权限与原生能力：AX/截图/输入/剪贴板 | 【规格】`cua-helper-installation.md:5-22`；【代码】`windowsCua*.ts` |
| 手机/Web | 另一设备 | 只消费 projection，attachment 到桌面 Host | 【规格】`cua-pip-session-consumer-integration.md:28-31` |

### 2.2 一次点击的完整调用链（时序）

以"模型点击一个元素索引"为例，逐跳如下（`node-repl-host` 为真实代码；CUA runtime/Helper 为规格/契约）：

| # | 位置 | 发生什么 | 证据 |
| --- | --- | --- | --- |
| 1 | 模型 | 生成 `js` 调用：`{code: "bootstrap...; const app = await agent.computerUse.getApp(...); await app.click(42); await app.getAXState()", title, timeout_ms}` | 【规格】`SKILL.md:144-157` |
| 2 | node_repl server | 校验 `js` 入参（strict schema）、按 `session_id` 串行、组合超时/取消 signal、`ledger.open()` 铸 32B hex token | 【代码】`server.ts:242-296`、`call-ledger/ledger.ts:45-61` |
| 3 | node_repl server | 新建一次性 Worker，注入 `workerData`（broker pipe 路径 + 本次 token） | 【代码】`server.ts:312-348` |
| 4 | Worker | 新建 `NodeReplSession`（`node:vm`），注入 bridge globals；执行 `setupComputerUseRuntime` 后 `agent.computerUse` 可用 | 【代码】`server.ts:133-181`；`cua-bridge.ts:29-76` |
| 5 | SDK（Worker 内） | `app.click(42)` → 组装 `{method:"left_click", input:{...}}` → 本机 pipe 发一行 JSON `{id, token, method, input}` | 【代码】`cua-bridge.ts:78-132` |
| 6 | CUA broker #1（主线程） | `ledger.resolve(token)` 校验；用账本条目里的 requestMeta 构造 context；`runtime.execute({toolName, arguments, context, signal})` | 【代码】`cua-broker.ts:84-128` |
| 7 | ComputerUseRuntime | 解析元素索引→native 元素、检查 frame/state 新鲜度、策略（kill switch/controller lease/settler）、调用 Helper broker | 【契约+规格】`notes/03` §2.3；`docs/cua/producer/cua-receipt-observation-contract.md:8-50` |
| 8 | Helper | 按平台执行：macOS AX action / Windows UIA→PostMessage→SendInput 升级链；返回 action receipt | 【规格】`docs/cua/2026-10-13-windows-input-delivery-spec.md:77-127` |
| 9 | SDK | 校验 receipt：`dispatch_status=accepted` → resolve void；`possibly_sent` → 抛 `actionSent:true` 错误 | 【代码】`computer-use-client.mjs:240-297` |
| 10 | node_repl server | 收集 Worker 结果（logs/result/images/structured），清洗 `_meta` 后只按 call ledger 的 facts 重写，组装 MCP `CallToolResult`；`no-progress-guard` 决定是否追加提示；`call.close()` 作废 token | 【代码】`result.ts:101-213`、`server.ts:279-292` |

**关键观察**：

- 模型"一次工具调用"可以在一个 cell 里完成 **bootstrap + 多次动作 + 一次收尾观察**；
  动作之间不需要再经过模型/远端，这正是"单 js 工具"的收益（见 §3.4）。
- "点一下"其实是**两次往返**：Worker→broker→Helper（动作），然后同 cell 里再观察一次（状态）；
  模型侧的"再观察确认"是产品契约，不是可选建议（`SKILL.md:128-263`）。
- token 只在第 2～10 跳的"本次调用"有效，Helper 的凭据在第 5 跳之前就已经不在 Worker 手里。

### 2.3 进程/信任边界表

ZCode CUA 有**两条独立的 socket 链**，安全模型完全不同，不能混写：

| 链 | 连接双方 | 传输 | 认证/信任 | 证据 |
| --- | --- | --- | --- | --- |
| #1 node_repl 私有 broker | Worker ↔ node_repl 主线程 | Windows named pipe / POSIX unix socket，NDJSON | **per-call token**：主线程 call ledger 签发/销毁；broker 只信 token 反查的 requestMeta，不采信 Worker 自报 context | 【代码】`cua-bridge.ts:78-132`、`cua-broker.ts:84-128`、`ledger.ts:45-61` |
| #2 Helper broker | shared-host runtime ↔ Helper | 稳定 socket（darwin）或稳定 named pipe（win32） | 身份模式：socket + pluginAuthority；macOS 另有 peer/launcher 签名校验；Windows peer 门尚未落地（见 §7.3） | 【代码】`stableCuaTransport.ts:15-22`；【规格】`windows-transport-ready.md:28-32` |

**信任边界要点**

1. **模型生成的是"代码"，不是"工具参数"**：因此 Worker 是**不可信执行环境**。ZCode 的应对是
   把"可信事实"全部搬到主线程（call ledger + broker 侧记账），Worker 输出只当内容。
   （`result.ts:101-124`；`notes/05` §2.6）
2. **Helper 是唯一持有 OS 权限的进程**：入口进程（Agent/CLI/插件）即使被攻破，也只能通过 Helper broker
   的受限方法发请求；Helper 侧还会做 peer/launcher/generation 校验（macOS 完整，Windows 部分）。
3. **Desktop Local Host 只做胶水**：安装/启动/注入/UI 投影，不承载 CUA 动作状态；
   CUA 的 frame/kill switch/controller lease 都在 shared host 的 runtime 里。
   （`docs/cua-permission-broker/2026-09-02-helper-lifecycle-agent-runtime-isolation-spec.md:20-32`）
4. **远程/手机是"投影消费者"**：只 attachment 到已有桌面 Host，不创建 Helper、不发起权限探针。
   （`zcode-cua-permission-broker.md:14-16,27`；`notes/01` §6）

### 2.4 为什么把 CUA 放在"本机桌面"而不是容器/远端

这是整个设计最值得 PCCtrl 注意的一点。ZCode 的选择不是"没做远程"，而是**明确拒绝**：

- **macOS 的授权主体必须是本机进程的签名身份。** TCC 的辅助功能/录屏授权记录在"被授权的 app 进程"上，
  所以 Helper 必须是独立签名、LaunchServices 启动的 `.app`；"Python/uvx must never become the implicit
  permission owner"（`zcode-cua-permission-broker.md:20-24`；`cua-helper-installation.md:5-22`）。
- **远程/容器工作区不创建 Helper。** SSH/WSL/Docker/remote workspace 只通过 shared desktop host 工作；
  `shouldCreateDefaultCuaProductHelper` 在 remote 下为 false（`zcode-cua-permission-broker.md:27`；
  `docs/cua/2026-08-27-computer-use-unsupported-environment-ui-spec.md:14-23`）。
- **Windows 没有 TCC，但同样 desktop-local。** CUA 在 Windows 的进程模型是"本机安装包内的 Helper + named pipe"，
  远程/容器场景不发布这条链路（`docs/cua/windows-product-runtime.md:63-66`；`notes/04` §1.1）。
- **跨机器会打破 peer 校验。** macOS 的 Helper 会校验 launcher 的代码签名与进程树；
  Windows 未来也要做 peer identity——这些校验只在同机成立。远程 CUA 若要做，需要"独立的安全模型与规格"，
  而不是简单开个端口（`zcode-cua-permission-broker.md:26`；`notes/01` §4.3）。

**对 PCCtrl 的直接推论（【推断】）**：ZCode 的"本机 + 独立权限主体 + 入口/执行分离"骨架可以借鉴；
但**"同机 peer 校验"这条不能照搬**。容器里的 AstrBot 与 Windows 执行器天然跨机器，
必须显式设计：网络认证（token/配对）、来源限制（回环/内网/白名单）、请求签名、重放防护、
以及"执行动作需要用户确认"的产品策略。这部分在 ZCode 里没有现成答案，是本项目真正的新增设计。

### 2.5 CUA 与 Browser Use 的同宿主双 bridge 架构

`node_repl` 是"官方能力共用宿主"：**一个 MCP server、一个 `js` 工具、两个领域 bridge**（Browser Use + Computer Use）。
这个结构解释了为什么 CUA 迁移后没有独立 MCP server：

- 宿主独立成包 `@zcode/node-repl-host`，原因是过去它长在 browser-use 插件里，导致
  "改 CUA 必须动 browser-use 包 / SDK 与文档两份手工副本 / 找不到宿主产物"（`node-repl-host/README.md:6-13`）。
- 两个 bridge 的差异：**Browser Use 允许 subagent 使用**（0.6.1 起），**CUA 在 provider 面与执行面双层禁用 subagent**
  （`cua-bridge.ts:7,43-52`；`docs/cua/2026-08-18-subagent-computer-use-unavailable-spec.md:5-26`）。
- 状态归属：浏览器 tab 状态在 BrowserControlPort，CUA 的 frame/kill switch 在 CuaControlPort；
  node_repl frontend 本身可以无状态、按 workspace 共享（`docs/mcp-stateless-node-repl.md:13-49`）。

对 PCCtrl 的启发：**"执行宿主"与"能力 SDK"可以拆开**。Windows 执行器不必自己实现一个 Agent 框架，
只需要暴露稳定的 MCP 工具面；"如何组合动作"可以由模型所在侧（AstrBot 的 agent）通过多步工具调用完成。
ZCode 之所以能收敛成单 js 工具，是因为它同时拥有 Node Agent 运行时与 JS 沙箱；
PCCtrl 若坚持 Python/HTTP，则更适合"少量粗粒度 MCP 工具 + 明确的调用顺序约定"（见 §11.3）。

---

## 3. 模型看到什么：单一 `js` 工具 + `agent.computerUse` SDK

### 3.1 `node_repl`：模型唯一的入口工具

模型侧只有**一个** MCP 工具：`mcp__node_repl__js`（server 名 `node_repl`，工具名 `js`）。

**工具契约**（`apps/zcode-cli/packages/contracts/src/tools/node-repl.ts:4-66`；`node-repl-host/src/server.ts:66-73`）：

```ts
// 模型面（tools/list）：strict
{ code: string,            // 必填，非空 JS 源码
  title: string,           // 1..120 字符，必填，用户可见的一句话描述
  timeout_ms?: number }    // 1..120000，默认 60000
```

- 默认超时 **60s**、硬上限 **120s**；与模型文案共用同一常量，避免"模型以为 120s、执行层 60s"的漂移
  （`node-repl-host/src/tool-contract.ts:1-3`）。
- server 级 `instructions` 明确限域："Browser Use and Computer Use only…Do not use this server for unrelated tasks,
  including general-purpose JavaScript, filesystem, shell…"（`tool-contract.ts:24-32`）。
- 工具描述里塞了大量"模型必须遵守"的细节（不用 console.log 整个结果、x/y 必须是 raster 内整数、
  每次调用都要重新 bootstrap SDK、`get_app_state` 要赋值并 write）——**这些是模型合同，不是注释**
  （`tool-contract.ts:34-58`）。
- 每次调用在**全新 Worker** 中执行，globals/模块缓存/绑定全部不保留；所以"bootstrap + 动作"必须在同一个 cell 里。

**bootstrap 模板**（模型必须逐字照抄，`SKILL.md:17-37`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:26-44`）：

```js
const root =
  process.env.ZCODE_CUA_PLUGIN_ROOT ??
  process.env.ZCODE_PLUGIN_ROOT ??
  process.env.CLAUDE_PLUGIN_ROOT;
const { join } = await import("node:path");
const { pathToFileURL } = await import("node:url");
const { setupComputerUseRuntime } = await import(
  pathToFileURL(join(root, "scripts", "computer-use-client.mjs")).href,
);
await setupComputerUseRuntime({ globals: globalThis });

// 然后才是动作
const app = await agent.computerUse.getApp("Notes");
await app.click(42);
await app.getAXState();
```

为什么必须这样：`computer-use-client.mjs` 不是 import 即生效的普通 SDK——它需要拿宿主注入的
bridge globals 来构造 `agent.computerUse`；Worker 被销毁后，下一 cell 的 runtime bridge 是新的，
但"UI 状态本身"由 shared host 保存（所以`getApp` 是重新绑定，不是重新观察）。
（`computer-use-client.mjs:1-22,892-899`；`docs/cua/2026-08-24-cua-node-repl-sdk-migration-spec.md:47-52`）

> 注意：**`js` 是"高权限入口"而不是通用 JS 沙箱**。ZCode 用 server instructions + tool description 双重限域；
> 它的安全边界不靠"模型守规矩"，而靠 broker/ledger/Helper 的进程与凭据隔离（见 §7）。

### 3.2 `agent.computerUse` 的 19 个成员（17 交互 + requestAccess + stop）

模型真正写代码面对的是注入的全局对象 `agent.computerUse`（**不是**设计文档早期的裸 `cua`）。

```ts
declare const agent: {
  computerUse: {
    getState(options?): Promise<{ apps: AppInfo[] }>;
    getApp(target: string | AppRef): Promise<App>;   // 绑定，不展示；不运行则后台拉起
    listApps(options?): Promise<AppInfo[]>;
    computer: Record<string, (args: object) => Promise<unknown>>; // 14 个底层工具逃逸口

    requestAccess(capabilities?): Promise<AccessStatus>;
    stop(reason?): Promise<void>;
  };
};
```

`App`（以及等价的 `Target`）提供 12 个交互成员：

| 成员 | 签名 | 说明 |
| --- | --- | --- |
| `getAXState` | `(options?: StateOptions) => Promise<string>` | 展示并返回 AX 状态文本；默认 diff |
| `getScreenshot` | `(options?) => Promise<Uint8Array>` | per-app 截图；建立内部 frame |
| `getAXStateAndScreenshot` | `(options?) => Promise<{state, screenshot?}>` | 树 + 图一起返回并展示 |
| `elements` | `() => Promise<AXElement[]>` | 完整元素表（含被树裁剪掉的行） |
| `paste` | `(text, options?) => Promise<void>` | 原生剪贴板粘贴（保存→写→粘贴→还原） |
| `click` | `(target: number \| Vec2, options?) => Promise<void>` | 元素索引或 raster 像素 |
| `drag` | `(from: Vec2, to: Vec2, options?) => Promise<void>` | 唯一拖拽入口 |
| `pressKey` | `(key, options?) => Promise<void>` | 单键/组合键/长按 |
| `scroll` | `(target: number \| Vec2, direction, pages?, options?)` | 滚动，单位 pages |
| `selectText` | `(elementIndex, text, options?)` | 按内容匹配选择/放置光标 |
| `setValue` | `(elementIndex, value) => Promise<void>` | 直接设值（优先于 type/paste） |
| `typeText` | `(text) => Promise<void>` | 逐字符输入 |

计数口径：12（Target）+ 4（根对象 `getState/getApp/listApps/computer`）+ `elements` = 17 个交互面成员，
再加 `requestAccess` + `stop` = **19**。设计规格明确 R1：**模型可见成员固定，不做"更好的命名"、不加位置参数、不改返回类型**；
新增能力只能进"可选选项键 / 尾部可选参数"（`2026-09-10-cua-sdk-v3-api.md:32-54`）。

**选项袋（唯一扩展点）**

| 选项 | 字段 | 归属 |
| --- | --- | --- |
| `ObservationOptions` | `emit?: boolean`（默认 true） | 所有观察方法 |
| `StateOptions` | `emit?, disableDiffing?` | `getAXState` / `getAXStateAndScreenshot` |
| `ClickOptions` | `mouseButton?, clickCount?, modifiers?, strategy?` | `click` |
| `SelectTextOptions` | `prefix?, suffix?, selectionType?` | `selectText` |
| `PasteOptions` | `format?: "text" \| "md" \| "html"` | `paste` |
| `PressKeyOptions` | `holdSeconds?, strategy?` | `pressKey` |
| `ScrollOptions` / `DragOptions` | `strategy?`（drag 另有 `modifiers?`） | `scroll` / `drag` |

`strategy` 三值：`auto`（默认，优先 Accessibility，必要时走合成事件）、`a11y`（强制语义路径）、
`event`（走全局输入，**要求目标已前台**，否则 `FOREGROUND_REQUIRED` 且零发送）。
（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:198-202`；`SKILL.md:50-52`）

### 3.3 底层 14 个工具与参数

`agent.computerUse.computer.<tool>(args)` 是**低层逃逸口**：模型平时用绑定 API，只有绑定 API 表达不了的
能力（枚举窗口、key repeat、同一调用里读回状态）才直接调工具。工具参数"严格"：未声明键直接拒绝。

| # | 工具 | 必填参数 | 关键可选参数 | 绑定 API 对应 |
| --- | --- | --- | --- | --- |
| 1 | `list_apps` | — | — | `listApps()` / `getState()` |
| 2 | `list_windows` | `app_ref` | — | `getWindow()` / `getApp({window_id})` |
| 3 | `get_app_state` | `app_ref` | `include_screenshot=false`, `disable_diffing=false` | `getAXState`/`getScreenshot` |
| 4 | `left_click` | `target` | `mouse_button="left"`, `click_count=1`, `modifiers`, `strategy`, `app_ref`, `return_state="none"` | `click()` |
| 5 | `scroll` | `target`, `scroll_direction`, `scroll_amount` | `strategy`, `app_ref`, `return_state` | `scroll()` |
| 6 | `left_click_drag` | `from_target`, `to` | `modifiers`, `strategy`, `app_ref`, `return_state` | `drag()` |
| 7 | `type` | `text` | `target`, `app_ref`, `strategy`, `return_state` | `typeText()` |
| 8 | `set_value` | `target`, `value` | `strategy`, `app_ref`, `return_state` | `setValue()` |
| 9 | `select_text` | `target` | `text_range=[start,length]`, `app_ref`, `return_state` | `selectText()` |
| 10 | `key` | `text` | `repeat`, `hold_seconds`, `app_ref`, `strategy`, `return_state` | `pressKey()` |
| 11 | `perform_action` | `target`, `action` | `app_ref`, `return_state` | `performSecondaryAction()` |
| 12 | `paste` | `text` | `format="text"`, `app_ref`, `return_state` | `paste()` |
| 13 | `request_access` | — | `capabilities` | `requestAccess()` |
| 14 | `stop_computer_control` | — | `reason` | `stop()` |

（`notes/02` §2.1-2.2；`SKILL.md:265-307`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:138-209`；`computer-use-client.mjs:30-45` 三方对齐）

补充语义：

- `app_ref` = `{name}` / `{bundle_id}` / `{pid}`，可加 `window_id`；**裸字符串按 bundle id 解释**，
  显示名必须写 `{name:"..."}`。Windows 上 display name 是"OS 列出的名字（开始菜单名）"，不是窗口标题。
- `target` = `{type:"element", index}` 或 `{type:"coordinate", x, y}`；绑定 API 让你直接传 number 或 `[x,y]`。
- `scroll_amount` 单位是 **pages**，clamp 到 0–100；`key` 的 `repeat` 替代"循环 pressKey"，`hold_seconds` 替代按住语义。
- `return_state` = `compact|full|none`（默认 none）：直调工具时可在动作后顺带返回一次状态；绑定 API 自己会观察。
- 已删除工具（30→14 的减法）：`double_click/triple_click/right_click/middle_click`（改为 `left_click` 参数）、
  `hold_key`（`key` 的 hold）、全屏 `screenshot`（改为 per-app 观察）、`zoom`、`list_displays/switch_display`、
  `cursor_position/wait/mouse_move/mouse_down/mouse_up`、剪贴板读写（`paste` 取代）、
  `open_application`（并入 `get_app_state` 的 "未运行则后台拉起"）。
  （`docs/cua/producer/cua-14-tool-e2e-validity.md:11-45`）

### 3.4 从"30 个 MCP 工具"到"1 个工具 + SDK"：设计动机与取舍

**演化路径（有据可查）**

1. 早期：CUA 是独立 MCP server，模型面对 30 个 `mcp__computer-use__*` 工具。
2. 2026-08：迁移到 shared `node_repl`，CUA 变成 Worker 内注入的 SDK；旧 MCP 只保留历史回放。
   （`zcode-cua-permission-broker.md:5-12`；`2026-08-24-cua-node-repl-sdk-migration-spec.md`）
3. 2026-09-10：SDK v3 把底层工具 **30 → 14**、broker 方法 **63 → 49**，并冻结"模型可见 19 成员"。
   （`2026-09-10-cua-sdk-v3-api.md:16,226-361`）
4. 2026-09-18：`node_repl` 工具面 **3 → 1**，删除 `js_reset`、`js_add_node_module_dir`。
   （`node-repl-host/src/tool-contract.ts:13-22`；`docs/mcp-stateless-node-repl.md:77-99`）

**为什么这么做（ZCode 给出的四类理由，均有证据）**

| 理由 | 证据/数字 |
| --- | --- |
| **工具定义成本**：工具越多，每轮请求携带的定义越贵。`js_reset` 每个请求约 **117 token** 定义，且实测 310 个会话/17134 次调用中 **0 次使用**；删它是因为"永不失败"诱导弱模型连续调用——曾出现同一个会话 **155 次连调、19 分钟、4837 万输入 token 直到 429**。 | `docs/mcp-stateless-node-repl.md:77-99`；`tool-contract.ts:13-18` |
| **维护成本**：每个工具要养 schema、handler、tier 表项、annotation、e2e oracle、三平台各一条 act-verify 场景；v3 的判据是"能不能被两行 JS / 一次组合调用 / SDK 机制替代"，能替代就删。 | `2026-09-10-cua-sdk-v3-api.md:32-54,226-361` |
| **安全集中**：只有一个高权限入口，权限系统可对该精确工具名配预算/审批；broker/ledger 把"可信事实"与"执行者"分离。 | `notes/05` §1.5；`result.ts:106-124` |
| **减少往返**：一个 cell 内可串多步（等待、轮询、多 tab、多动作 + 收尾观察），只有一次模型工具往返；代价是 fresh-kernel 语义 + 强制 bootstrap 约定。 | `docs/mcp-stateless-node-repl.md:60-76` |

**这套设计适不适合 PCCtrl？**

【推断】ZCode 的"单 js 工具"是**为其拥有 Node Agent 运行时 + JS 沙箱**这一前提服务的；
PCCtrl 的模型在 AstrBot 里，插件/工具面是 Python，且用户明确"Windows 端技术栈未定"。
因此**不建议照搬"单 js 工具"**；更合理的是"少而稳的粗粒度 MCP 工具 + 明确调用范式"（§11.3）。
但 ZCode 的取舍逻辑（工具数量=模型每轮成本；能力能用组合表达就不要新工具；安全入口少而集中）
是可直接套用的设计原则。

### 3.5 一次工具调用的数据流

见 §2.2 的十跳表。这里补充"模型侧可感知"的部分：

- **观察是显式动作**：`getAXState()` / `getScreenshot()` / `getState()` / `listApps()` **会自动把结果展示给模型**；
  `getApp()` 只绑定不展示。
- **动作是静默的**：`click/typeText/pressKey/...` 成功时 resolve `undefined`，不产生展示内容；
  模型要看到效果必须自己再调 `getAXState()`。
- **"一次结果一张图"规则**：一个 MCP 结果里出现第二张栅格 → 整帧被移除（模型一张图都看不到）。
  所以 SDK 会做"多帧取最新"（`result.ts:53-76`），文档也警告模型不要把观察返回值再传给
  `nodeRepl.write` / `nodeRepl.emitImage`（`SKILL.md:179-188`）。
- **图片是 inline base64 image block**，紧邻一个 `image_ref` 文本块（权威凭证，模型不需要读它）；
  `frame_id` 已内部化，模型只提交"最新 raster 的整数像素"。

### 3.6 工具结果如何回到模型（文本树/图片/一图规则）

`toMcpRunResult(run, facts)` 是 node_repl 的"结果组装器"，规则全部是真实代码（`result.ts:89-214`）：

1. **结构化结果优先**：SDK 通过专用 sink 写入的 `structuredResults`；没有时才把普通返回值当"嵌入 MCP result"解析。
2. **可信元数据只按 call ledger 的 facts 重写**：`_meta` 里的 CUA app 身份、浏览器截图下标、能力标签等键
   先**无条件删除**，再按主线程记录重写——否则 cell 可以自己 emit 一份，让卡片声称操作了别的应用。
   （`result.ts:101-124`）
3. **图片排在 text 之前**：因为 Anthropic 兼容网关只解析 `tool_result.content` 开头的连续 image block；
   `[image,text]` 可见、`[text,image]` 不可见（实测结论，`result.ts:125-129`）。
4. **同图去重、多帧取最新、错误只回 message、结果永不为空（`(no output)`）**。
5. **预算**：普通第三方 MCP 图片 base64 > 200 KiB 不 inline；`node_repl/js` 的宿主图片先压到预算内
   （base64 ≤200 KiB、decoded ≤153600 B、最长边 ≤2000），失败才落 artifact。
   （`docs/mcp-tool-output-budget.md:21-42`）

**对 PCCtrl 的直接启示**：如果 Windows 执行器要回传截图给模型，必须提前想清楚：
图片大小预算、一张还是多张、失败降级（文本还是 artifact）、图片在结果中的位置（是否兼容你的 provider 网关）。

---

## 4. 观察（Observation）：AX 树、截图与坐标

### 4.1 Accessibility 树：采集、规范化、diff、裁剪

**为什么 AX 优先**：ZCode 的模型文档把"辅助功能"定为第一动作路径，理由写在 SKILL 开头：
"Accessibility is the primary action path. It is semantic, precise, works on a background app and does not steal the user's focus."
（`SKILL.md:39-42`）坐标只留给 canvas、游戏、Electron 等 AX 看不见的内容。

**快照身份模型（full / delta / no_change）**

- 只有 **full** 观察会签发并向模型暴露新的 `state_id`；delta/no_change 不 mint 新 id，模型复用上一次 full 的 id。
- delta **必须声明 `base_state_id`**；base 丢失、compaction、重连、导航、节点大比例重建、schema 变化时强制 full。
  （`docs/cua/producer/ax-tree-optimization-design.md:100-101,136-139`）
- 真机基线（用于估算模型上下文成本）：Calculator full ≈ 9,435 字符 / 58 元素；TextEdit full 3,636 字符 / 29 元素；
  同会话 11 个带 `state_id` 的结果合计 112,279 字符（平均约 10,207 字符/次）。
  （`ax-tree-optimization-design.md:9-17`）

**模型实际看到的形态**（2026-10 实现）：

- 渲染后的**树文本**（含 header、缺失说明、`[effect_evidence unchanged]` 等注记）；
  `getAXState()` 返回 string，且默认**只给 diff**（对比"本 cell 已经展示过的树"）：
  omitted 行未变、索引仍有效；首次绑定后与"只截图观察后"的第一次观察强制整树。
  （`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:329-335`；`computer-use-client.mjs:592-610`）
- 绑定 API 的 `elements()` 返回结构化元素表：`{index, kind, title, value, actions[]}`（macOS 另有 `selected`/`default_action`），
  包含被树文本裁剪掉的行——"文本可以是 diff，结构必须完整"。
- **裁剪规则**（设计目标，`ax-tree-optimization-design.md:140-185`）：
  先保留 focused/editable/actionable、selected/checked/error/status；再保留有 name/value 的 text/link/button/menuitem；
  然后为保留节点补最短父链；纯布局容器折叠成 summary；预算从"固定 node count"改为"字符/token 预算 + 语义配额"。
  实现侧现状：超限时按优先级选元素并补祖先链，header 注记 `indices are sparse; N hidden`（真机 **736 → 300**）。
- **两种"缺失"要区分**（模型文档专门强调，`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:407-421`）：
  - 稀疏索引：树被裁剪但元素仍存在 → `elements()` 可恢复；
  - 容器只报部分子节点：header 显示 `showing A-B of N items`，这些行**从未进入观察**，`elements()` 也救不回 →
    只能滚动容器 / 用应用自身选择器（方向键、Name Box、搜索框）移动选区后再观察。
- **动作后的状态一致性**：调用方（SDK）只消费 action receipt，不合成 `refresh_required`；需要 UI 事实时
  模型必须显式再观察并拿新 `state_id`。工具层只有 `return_state=compact|full` 时才抓**恰好一次** `force_full` 观察，
  且 **receipt 必须排在长树之前**（防止宿主截断丢掉防重放证据）。（`docs/cua/producer/cua-receipt-observation-contract.md:31-50`）
- **`[effect_evidence unchanged]`**：producer 为"AX 受理但界面逐字节未变"提供的控件类型无关证据位，
  以独立 text block 下发。模型文档要求出现该提示时**不要重复坐标**，改走元素索引/键盘。
  （`computer-use-client.mjs:555-574`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:429-435`）

**对 PCCtrl 的启示**：AX 树是"低成本、高语义"的观察通道，但 Windows UIA 树同样会遇到
"布局容器噪声 / 长列表截断 / 控件不支持"三类问题。ZCode 的"优先级裁剪 + 稀疏索引 + elements() 兜底"
是可以直接复用的设计；**不要在第一天就做"全量树"**。

### 4.2 截图/frame：raster、frame_id 内部绑定、DPI 与缩放

**截图作用域**：一次捕获**只属于一个 app 的一个窗口**（不是全屏）。多窗口应用必须用 `list_windows` 选 `window_id`；
不给 `window_id` 时每次观察都重新解析 main/key window，因此新弹的 modal 会成为被捕获窗口。
（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:250-316`）

**模型收到的不是"frame 对象"，而是像素 + 一个相邻的权威文本块**：

- 绑定 API `getScreenshot()` 返回裸 `Uint8Array`；没有 `{mime,width,height,frame_id}` 信封。
- MCP 结果里是 inline base64 image block + 紧邻的 `image_ref` 文本块（完整性与诊断用，模型不需要读/重复）。
  `image` 与 `image_ref` 必须直接相邻；中间插入任何块 → 整个结果错误化且不暴露 frame_id。
  （`docs/cua/2026-08-25-cua-authority-verdict-audit.md:42-58`；`frame-coordinate-internal-binding.md:75-80`）
- **one-raster 规则**：一个结果里出现第二张栅格 → 整帧被移除。SDK 的 `keepLatestCuaFrame` 只保留最后一对 image/authority。
  （`result.ts:53-76`；`docs/cua/2026-08-24-cua-node-repl-sdk-migration-spec.md:76-79`）

**frame 的生命周期与坐标绑定**（`docs/cua/producer/frame-coordinate-internal-binding.md:28-85`）：

```text
model-visible raster (e.g. 1280 x 645)
   │ 模型只给 x/y
   ▼
CUA 在 transport session 内同步绑定"最新可动作 frame"（不可变 descriptor）
   │ 校验 actionable + TTL + app/window ownership
   ▼
raster 像素中心 ──► native 指针单位 ──► 派发
```

不变量：

1. 绑定发生在 app 解析/broker I/O **之前**；队列期间不被新 frame 重新解释，也不因"出现更新 frame"而拒绝。
2. 绑定目标在 retained-frame 生命周期内**永不重绑到新几何**。
3. **state-only 观察不推进也不吊销**隐式像素授权；最新保留的可动作 raster 仍是绑定源，直到发布新栅格或 TTL/actionability 失败。
4. 显式 `frame_id` 仍被接受且**权威**：unknown/expired/evicted/cross-session/non-actionable 全部 fail closed，
   **绝不回退到 latest frame**（模型面不暴露 frame_id，只有测试/兼容路径会显式传）。
5. 当前 transport 没有 actionable raster ⇒ 坐标动作**在派发前失败**。
6. 每个栅格观察以规范的相邻 `[image, image_ref]` 对开头；恢复错误不得教模型去抄 frame_id，
   而是要求重新观察取新的 raster `x/y`。
7. **DPI/缩放/多屏换算是 CUA 内部事**：模型抄的是"这张 raster 的整数像素"；Retina/DPI/crop/resize/显示器原点
   全部由 `@zcode/zcode-cua` 处理（`frame-pixel-zoom-regression.md:57-59,128-154`）。
   macOS 实测：窗口 raster `1280×900`，提交整数像素 `(641,496)` 能正确落到 DOM 的 `(450,280)`。

**frame 新鲜度失败语义**：窗口移动/缩放/被替换 → 坐标拒绝并提示 "frame is stale, observe again"；
元素目标不依赖窗口几何，可用于规避。Helper 重启会使旧 `frame_id` 失效。
（`SKILL.md:206-209`；`docs/cua/2026-10-23-windows-app-frame-surface-spec.md:733`）

**macOS 的 provenance 证据链**（对 Windows 有借鉴意义）：
"Surface membership is evidence, not a geometry heuristic." 一个 surface 属于某 app frame 必须**同时**满足：
① AX 从该逻辑应用的 focused/main/top-level window 经**有界关系 walk**（8 层/64 节点）可达；
② `_AXUIElementGetWindow` 返回真实 `CGWindowID`；③ WindowServer 截图前后报告同一窗口（owner/bundle/layer/bounds 稳定）；
④ 截图几何与 display topology 稳定。**绝不用标题或重叠 bounds 推断归属**。
（`docs/cua/producer/macos-app-frame-surface-provenance.md:22-34`）

### 4.3 应用与窗口定位（AppRef / list_windows / Windows 命名规则）

**AppRef 三键 + window_id**：`{name?, bundle_id?, pid?, window_id?}`。

- 裸字符串按 **bundle id** 解释（含点且无空格/斜杠）；显示名必须写 `{name:"..."}`。
  SDK 在"按 name 找不到"时会自动换 `bundle_id` 重试一次（反之亦然）——这是 SDK 的便利，不是协议语义。
  （`computer-use-client.mjs:368-393`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:182-186,449-456`）
- **Windows 的 display name 是"OS 列出的名字（开始菜单名）"，不是窗口标题**。模型文档专门警告：
  用户说 `网易云音乐app` 就不要改写成 `网易云音乐`；猜错会 "app not found"。
  （`SKILL.md:136-142`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:184-186`）
- **没有独立的 open/launch 工具**：`getApp(...)` / `get_app_state({app_ref})` 在应用未运行时**在后台拉起**；
  Windows 上"启动非 packaged 应用会抢焦点"、"include_screenshot=true 会取消最小化"——这两条写进工具描述如实告知。
  （`SKILL.md:274-277`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:449-460`）

**list_windows 字段（模型要能正确选择窗口）**（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:250-289`）：

| 字段 | 含义 / 陷阱 |
| --- | --- |
| `window_id` | 稳定整数；放回 `app_ref` 绑定该窗口 |
| `title` | `""` = 有 AXTitle 但为空（Qt/自绘面板常见）；`null` = 读不到（AX 读失败或 CoreGraphics 合并行），二者不可区分，需看 `subrole` |
| `subrole` | 只有真实 AX 窗口才有；**字段存在性 = 可绑定窗口 vs 残留 surface** |
| `text_preview` | 无标题窗口的内容采样，**绝不是标识符**，可能含用户数据 |
| `bounds` | `[x,y,w,h]` 全局屏幕点，**仅供诊断** |
| `main`/`focused` | AX 主/键窗口，**都不等于系统前台 app**（前台看 `listApps().active`） |
| `onscreen` | false = 关窗后 CoreGraphics 仍持有的窗口，不可见不可操作，选目标时跳过 |
| `index` | 行号，不是元素索引也不是 window id |

**窗口解析的失败语义**：`window_id` 解析失败时，Helper 曾会"静默降级到最前窗口"（只回 `window_id_fallback:true`）；
SDK 现在把这种降级改成抛 `STALE_STATE`——**宁可失败，不可悄悄换一个窗口**。
（`computer-use-client.mjs:1040-1047`；`2026-09-10-cua-sdk-v3-api.md:408-410`）

**未绑定 window 的风险**（模型文档反复强调）：每次观察重新解析 main/key window，
一个刚打开的 modal 会成为被捕获窗口；旧索引可能指向无关控件。看到树"不像预期"时先看观察结果里的 `window` 块。
（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:302-316`）

### 4.4 坐标规则：为什么"元素 bounds 不能当坐标"

坐标契约只有一句话：**模型只看当前返回的 raster，抄其中的整数像素**。

- `x/y` 必须是整数（越界/负数被拒）；模型**不做** Retina/DPI/crop/resize/显示器原点/native 指针换算。
  （`frame-coordinate-internal-binding.md:18-26`；`computer-use-client.mjs:334-341`）
- **元素/窗口 `bounds` 是全局屏幕点，永远不能当 raster 坐标提交**（`SKILL.md:190-198`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:425-428`）。
  这是模型最容易犯的错，也是"看起来点了但没点中"的头号原因。
- 坐标目标有两种写法（模型侧）：绑定 API 直接 `[x,y]`；底层工具 `{type:"coordinate",x,y}`。
  `frame_id` 由 CUA 内部绑定，模型不传（显式传只在测试/兼容路径）。
- **无 raster 不许点**：坐标动作在派发前检查 actionable raster，缺失即失败（`frame-coordinate-internal-binding.md:65`）。
- 坐标动作"被接受"不等于"点中"：某些应用忽略合成指针事件里的位置、按真实光标位置执行；
  再次观察会显示 `[effect_evidence unchanged]`。此时**不要重复坐标**，改用元素索引或键盘。
  （`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:429-435`）
- 坐标拒绝可能报告"意外的 owner"或"frame 过期"；**绝不能靠移动/缩放/关闭窗口让坐标命中**（`SKILL.md:206-209`）。

### 4.5 观察的新鲜度、等待与结算

- **观察自带等待/结算**：observations wait for the UI to settle before capturing；模型**不要** `setTimeout`/轮询等待。
  （`SKILL.md:232-236`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:461-465`）
- **fresh authority 规则**（`docs/cua/producer/cua-14-tool-e2e-validity.md:77-87`）：
  `state_id` 与当前像素 frame 是**单权威快照**；动作后想再做元素动作，必须显式重新观察
  （工具默认不再隐式 `get_app_state`）；像素动作后必须取得新的 actionable raster 才能再做坐标动作。
  复用 stale 视觉证据是**测试 bug，不是产品失败**。
- **索引解析**：元素索引按"该 app 最近一次观察"解析；动作需带 `app_ref` 说明作用域；
  索引是 per-window 的、每次观察重编号；元素消失 → `ELEMENT_UNAVAILABLE`（fail closed）。
  （`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:302-306`；`notes/03` §4.3）
- **观察超时诊断**：macOS `capture_app` 在 Helper 异步 worker 中遍历 AX 树，被 **8 秒 wall-clock fence** 截断；
  超时返回结构化诊断字段（`phase/last_ax_operation/nodes_visited/elapsed_ms/deadline_exceeded/pid/window_id`），
  **不记录控件文本/剪贴板/token/窗口标题/其他用户内容**；诊断读取失败不得改变原错误语义。
  （`docs/cua/producer/capture-app-timeout-diagnostics.md:18-37`）
- **空树是合法观察**：`get_app_state` 永远返回 model-visible full state（工具层发 `force_full=true`）；
  broker 内部 delta/no_change 不作为可执行模型状态暴露；空树不做 warmup 重试。
  （`cua-receipt-observation-contract.md:70-80`）

### 4.6 图像返回与模型侧预算

- 观察方法（`getAXState/getScreenshot/getAXStateAndScreenshot/getState/listApps`）**自己 emit 结果**；
  动作方法静默。不要把它们返回给 `nodeRepl.write`/`emitImage`——第二张图会让整帧被移除。
  （`SKILL.md:179-188`）
- `has_image:false` 表示"这次没要像素"，**不是截图失败**（`SKILL.md:58-61`）。
- 图片预算：普通第三方 MCP 图片 base64 > **200 KiB** 不再 inline，走 artifact/文本降级；
  `node_repl/js` 宿主图片先压到预算内（base64 ≤200 KiB、decoded ≤153600 B、最长边 ≤2000），失败才 artifact。
  （`docs/mcp-tool-output-budget.md:21-42`）
- 图片在结果里**排在 text 之前**（provider 网关兼容性，见 §3.6）；同图去重、多帧取最新。
- 【推断】对 PCCtrl：如果 Windows 执行器要回传 4K 多屏截图，200 KiB 级别的预算大概率需要缩放/裁剪/压缩策略；
  "先给缩略图定位、需要时再取局部高分辨率"比"整屏原图"更适合 MCP 回传。

---

## 5. 动作（Action）：点击、输入、拖拽、滚动、按键

### 5.1 绑定 API 与底层工具的关系

模型平时用的是**绑定对象 API**（`app.click(42)`），底层工具（`left_click({target:{type:"element",index:42}, app_ref})`）
主要作逃逸口与协议锚点。两者的关系：

| 维度 | 绑定 API | 底层工具 |
| --- | --- | --- |
| 参数风格 | camelCase，索引/坐标直接传 | snake_case，一个 arguments 对象，strict |
| 元素索引作用域 | 绑定对象自带 app/window 上下文 | 必须显式传 `app_ref`（多 app 会话里没有上下文会解析到"最近的观察"） |
| 返回 | 动作 `Promise<void>`；观察返回 string/Uint8Array | 返回 tool result：单 text block JSON → 解析；单 text 非 JSON → 字符串；含图片 → 原始 envelope |
| `return_state` | SDK 自己决定观察时机 | `compact/full/none`，直调工具时可顺带返回状态 |
| 用途 | 正常开发路径 | 窗口枚举、key repeat、同调用读回等绑定 API 表达不了的能力 |

（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:135-137,212-244`；`notes/02` §2.4-2.5）

一个容易误解的点：**绑定 API 的动作方法不展示、不返回状态**。`await app.click(42)` 成功就是 `undefined`；
要看效果必须再 `await app.getAXState()`。绑定 API 内部会做索引→目标解析、frame 绑定、receipt 校验，
但**不做隐式再观察**（这是 2026-09 v3 的刻意变更：避免把"想点 A 实际点了 B"变成静默错误）。

### 5.2 动作策略：auto / a11y / event 与"后台优先"

`strategy` 是唯一影响"走哪条派发路径"的模型参数：

- `auto`（默认）：优先 Accessibility 语义路径；语义路径不可用/被证明未发送时，允许走合成事件/坐标（平台驱动侧决定）。
- `a11y`：强制语义路径。目标不可语义操作 → 失败。
- `event`：走全局输入（前台），**要求目标已是前台**，否则 `FOREGROUND_REQUIRED` 且零发送；
  它**从不激活任何窗口**（所以后台 app 上一定失败）。

（`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:198-202`；`SKILL.md:50-52,211-216`）

平台差异（重要）：

- macOS：AX/后台路径成熟，`event` 只是"某些依赖响应链的键盘工作流"的例外。
- Windows：`strategy=event` 或带 `modifiers` 的点击直接进入**物理 lane**（前台 SendInput）；
  `auto` 走"UIA → PostMessage → 前台"自动升级链。升级是**驱动侧自主决策、模型无感**，
  成功路径不会向模型披露"升级了"（信息只进 broker 的 `delivery` 元数据）。
  （`docs/cua/2026-10-13-windows-input-delivery-spec.md:112-127`）

**为什么"后台优先"**：后台 UIA/PostMessage 不抢用户焦点、能多会话并行，是体验与安全的双赢；
前台 SendInput 只是无法后台完成时的兜底。ZCode 在 Windows 上为这条原则付出了大量工程代价
（焦点防守、排水、丢弃矩阵），见 §6。

### 5.3 逐动作细节

**click（元素索引或坐标）**

- 元素索引：语义动作链（Windows：`Invoke → Toggle → SelectionItem.Select → ExpandCollapse.Expand → Legacy`，
  只有四模式全部"明确 absent"才进 Legacy；**动作一旦调用，失败也按 possibly_sent 终止**，不重试/不换模式）。
- 坐标：点"最新 raster 的整数像素"；Windows 上会走 hit-test（UIA）→ PostMessage → 前台 SendInput 升级链。
- 参数：`mouseButton`（left/right/middle，替代已删的 right_click 等）、`clickCount`（2/3 替代双击/三击）、
  `modifiers`（`"cmd+shift"` 形式，按下瞬间生效）、`strategy`。
- 时序：Windows 上 PostMessage 路径有明确的节拍（move 预热 → down → 35ms 驻留 → up；多击 80ms 间隔；
  双击第二击投 `WM_*BUTTONDBLCLK`，不靠系统推断）；前台 SendInput 路径是 move/down/up 单批 + 修饰键 5ms 停 + 40ms settle。
  （`docs/cua/2026-10-13-windows-input-delivery-spec.md:535-549`）
- **点击后不读回元素状态**：回执保留 `verified:false / unverifiable`；no-op 防守靠丢弃分类 + 结构化回执 + 升级链兜底。
  "有效点击"的判定在 e2e 层由独立 oracle（真实 DOM/原生事件）负责。

**drag**

- 唯一拖拽入口（`mouse_move/down/up` 已删，是"接受的能力损失"）；
- 两端各自解析（索引或坐标）；Windows 上 PostMessage 序列有 pacing（down 后 35ms + 12 步×25ms move），
  前台路径是锁步 `SetCursorPos`+SendInput + 40ms settle；
- 已知缺陷：标题栏/非客户区拖拽没有 `WM_NCHITTEST` 检查（spec 标 TODO），起点落在标题栏时会静默无效但回执 sent
  ——**这是"回执 ≠ 生效"的典型样本**，PCCtrl 做窗口拖动时要自己实现 NCHITTEST 判断。

**scroll**

- `scroll_amount` 单位是 **pages**（不是像素），clamp 0–100；元素目标优先 UIA ScrollPattern，坐标目标按宿主选后台/前台路径；
- Windows 后台只对 WPF/WinUI3 的 SB 码族有效；WinForms/Qt/GTK3 后台滚动不可达 → 升级前台滚轮；
  Chromium 滚轮属"必丢"直接升级。横向滚动**未验证**（无横向 oracle）。详见 §6.3。

**type / set_value**

- `type`：逐字符输入；多行/富文本在 macOS 有历史缺陷（自动缩进/补全），推荐 `paste`；
- `set_value`：**首选于 type/paste**（`SKILL.md:47-49`）；语义是"替换整个值"（不是尾部追加）；
  空白串即清空；目标不可设置 → `NOT_SETTABLE`；
- **关键反例（Notes）**：富文本 body 上 `set_value` 可能被 AX 接受并回显，但**不提交到文档模型**——
  必须聚焦 + 真实键盘输入才落盘。工具为此提供 `_non_committal_rich_text_warning` 提示。
  （`docs/cua/producer/cua-act-verify-scenarios.md:352-384`）
- Windows 上有"写前快照→写→读回"的裁决与五档 verify/四档 effect，见 §6.3。

**select_text**

- 底层工具用 `text_range=[start,length]`（对元素当前 value 的偏移）；绑定 API 用"内容匹配"：
  `selectText(index, text, {prefix,suffix,selectionType})`；
- 0 命中 → `NOT_SELECTABLE`；多命中 → 拒绝并报候选数（**歧义不自动取第一个**）；
  `selectionType` = `text|cursor_before|cursor_after`（后两者折叠为光标）；
- 历史 bug：只移动 End 端点导致"设光标到 2"变成选中 `[0,start+length)`——修复是 Start 端点同移。
  （`notes/04` §3.2；`docs/cua/producer/...`）

**pressKey / key**

- 接受短名与 X keysym 风格：`"a"`、`"Return"`、`"Tab"`、`"Control_L+a"`、`"super+c"`、`"Up"`；
  macOS 用 `cmd`，Linux/Windows 用 `ctrl`；`holdSeconds` 表示按住时长；
- 组合键语义是"按下修饰键 → 主键 → 释放 → 逆序释放"；Windows 上**组合键在 PostMessage 路径结构性不可投**
  （LPARAM 无法表达 CTRL/SHIFT，posted 修饰键也不更新 `GetKeyState`），直接短路到前台 SendInput（§6.3）；
- 键位归一化在**工具层入口**统一执行，broker/native 只接受 canonical token；
  `repeat`（重复次数）在模型文档里存在、但在 v3 spec 中被列为删除——**文档/实现存在漂移**，引用时注意版本（`notes/03` §未解问题 2）。

**paste**

- 原生 Helper 完成"保存剪贴板 → 写入 → 发粘贴键 → 还原"，而不是 host 侧四步组合（避免与用户改剪贴板竞态）；
- 无 app 读取粘贴时以**超时失败**，而不是报成功；快捷键无法指定字段，落在当前键盘焦点处，需要检查结果；
- 后台 app 上 `paste` 会以 `FOREGROUND_REQUIRED` 拒绝（macOS 没有前台化手段）；
- `format` = text/md/html；md/html 在 Helper 未支持时抛 `ACTION_UNAVAILABLE` 并提示降级。

**performSecondaryAction / perform_action**

- 只能执行**元素当前树里自报的** actions；猜一个 action 名 → `ACTION_UNAVAILABLE`；
- 这依赖"树/元素表必须带 actions"——v3 因此规定 compact 档**无条件带 actions**（默认档曾把语义动作必需的数据藏起来，是独立缺陷修复）。
  （`2026-09-10-cua-sdk-v3-api.md:266-281`）

**打开应用**

- 没有独立工具：`getApp` / `get_app_state({app_ref})` 在未运行时后台拉起；Windows 上启动非 packaged app 会抢焦点，
  `include_screenshot=true` 会恢复最小化窗口；工具描述如实告知，不宣称"后台拉起"。详见 §6.6。

### 5.4 回执、验证与"可能已发送"

**这是 ZCode CUA 最值得抄的协议设计。**

**默认返回什么**（producer 侧，`docs/cua/producer/cua-receipt-observation-contract.md:6-50`）：

- 变更类工具**默认只回 action receipt**，`return_state="none"`（不做任何 `capture_app`）：

```json
{
  "action_receipt": {
    "schema_version": "zcode-cua-action-receipt-v1",
    "action_sent": true,
    "dispatch_status": "accepted",
    "retry_action": false
  }
}
```

- `return_state="compact"|"full"` 时**恰好一次** post-action `capture_app`（`include_screenshot:false, force_full:true`），
  结果是完整 CUA observation；compact/full 只控制"格式化给模型多少"。
- post-action 观察失败**不回滚 receipt**，也不得重放动作；state 报 unavailable。
- **receipt 块必须排在 AX 树之前**：宿主截断长树时必须保住 `dispatch_status`/`retry_action=false`。

**`dispatch_status` 三态**（安全语义的核心）：

| 值 | 含义 | 调用方必须做什么 |
| --- | --- | --- |
| `accepted` | 动作被接受（成功路径） | 可进入下一步；但仍应观察确认 |
| `possibly_sent` | 请求**可能已经到达原生派发** | **不得自动重放非幂等动作**；先观察再判断 |
| `not_sent` | Helper 证明未派发（如 stale 元素） | `action_sent=false`；**不自动补救**（不重抓树、不按指纹重试） |

**为什么必须这样**：ZCode 的注释写得很直白——
"ZCode 的 `action_sent`/`dispatch_status` 是事故驱动的：一次点击可能已经落地，盲重试会双击。"
（`2026-09-10-cua-sdk-v3-api.md:478-480`；`cua-action-receipt-state-simplification.md:5-39`）

**模型面（SDK）如何呈现**：

- 动作方法 `Promise<void>`；成功 resolve `undefined`；失败抛 `ComputerUseError`。
- 错误对象字段：`code`、`actionSent`（动作是否**可能已下发**，默认 false）、`dispatchStatus?`、
  `retry: "reobserve"|"retry"|"never"`、`details`。
- `possibly_sent` 不会被"成功"吞掉：SDK 的唯一成功判据是 `!isError && dispatch_status !== "possibly_sent"`；
  非 error 但 possibly_sent → 抛 `TIMEOUT`（实际是"可能已发送"）且 `actionSent=true`。
  （`computer-use-client.mjs:278-297`）
- 模型纪律：**非幂等动作只在 `actionSent=false` 时重试；否则先观察**（`SKILL.md:239-250`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:369-405`）。

**验证（verification）与 act-verify 是两个层次**：

- 工具/回执层：可选 `target_verification_status`。回读必须按**具体控件动作**选后置条件：
  `AXSelected` 可用于选行/选择控件，但**不是通用 `AXPress` 成功信号**——菜单类按钮菜单打开前后都可能
  `AXSelected=false`，这不能被判 `mismatched`；找不到合适后置条件时 receipt 保持 `accepted` 但不带 verification claim。
  （`cua-receipt-observation-contract.md:52-59`）
- e2e/act-verify 层：PASS = `model_acted AND verify_passed`，验证器读**模型无法影响**的通道
  （AppleScript 读 app 数据模型、`pbpaste`、文件系统、`defaults read`、System Events），
  "工具返回 `isError=false`"或"模型说成功了"都不算证据。
  （`docs/cua/producer/cua-act-verify-scenarios.md:11-25,469-486`；`cua-14-tool-e2e-validity.md:50-68`）

**对 PCCtrl 的启示**：把"动作是否可能已发送"作为协议一等字段，是防止"模型重试导致双击/重复输入"的关键；
Windows 执行器的每个写操作都应返回 receipt（`action_sent/dispatch_status`），并让失败通道携带同样的事实。

### 5.5 错误码与重试语义

**模型可见错误码（20 个，逐字）**（`SKILL.md:241-246`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:374-379`）：

```text
PERMISSION_DENIED  NOT_AUTHORIZED  APP_NOT_FOUND  AMBIGUOUS_APP  LAUNCH_FAILED  INVALID_APP
ELEMENT_UNAVAILABLE  STALE_STATE  NOT_SETTABLE  NOT_SELECTABLE  ACTION_UNAVAILABLE
FOREGROUND_REQUIRED  CONTROLLER_BUSY  CONTROL_STOPPED  SCREEN_LOCKED  HELPER_UNAVAILABLE
VERSION_MISMATCH  TIMEOUT  STRUCTURED_STATE_UNAVAILABLE  INTERNAL
```

**retry 推导**（SDK 代码，`computer-use-client.mjs:96-130`）：

- `actionSent=true` → `reobserve`；
- 否则 `NEVER_RETRY`（8 个：CONTROLLER_BUSY / CONTROL_STOPPED / PERMISSION_DENIED / NOT_AUTHORIZED /
  VERSION_MISMATCH / ACTION_UNAVAILABLE / NOT_SETTABLE / NOT_SELECTABLE）→ `never`；
- `REOBSERVE`（ELEMENT_UNAVAILABLE / STALE_STATE / STRUCTURED_STATE_UNAVAILABLE）→ `reobserve`；
- 其余 → `retry`。

**broker → SDK 错误码映射**（`computer-use-client.mjs:76-94`）：
`permission_denied→PERMISSION_DENIED`、`not_authorized→NOT_AUTHORIZED`、`launch_failed→LAUNCH_FAILED`、
`invalid_request→INVALID_APP`、`element_unavailable/not_settable/not_selectable/action_unavailable/foreground_required/controller_busy` 同名、
`broker_unavailable|stale_socket→HELPER_UNAVAILABLE`、`version_mismatch→VERSION_MISMATCH`、`timeout→TIMEOUT`、
`unimplemented→ACTION_UNAVAILABLE`、`method_not_found/internal→INTERNAL`。
**未知码归 INTERNAL，绝不静默成功。**

**冷启动 not-ready**：Helper 懒启动时 producer 返回**非 error** 的 `{kind:"CUA_NOT_READY", reasonCode, retryable, message}`；
SDK 只在 `retryable=true` 时按 `[250,500,750,1000,1500]ms` 退避重试，最多 6 次；耗尽后转 `TIMEOUT`/`CONTROLLER_BUSY`。
（`computer-use-client.mjs:240-274,903-932`）

**权限错误的特殊规则**：ZCode **已删除** `CUA_PERMISSION_REQUIRED` 自动分类——`permission_denied` 同时覆盖
TCC、前台策略、Windows UIPI、Linux XTest、无效目标等多种失败，不能据此自动弹权限框；
只有官方 live `request_access` 的结构化 `_meta` 能触发 Host 确认流程。
（`docs/cua/2026-08-25-cua-permission-error-passthrough.md:9-26,51-65`）

### 5.6 焦点、单会话所有权与停止

- **`CONTROLLER_BUSY` 永不可重试**：另一个 live CUA session 持有输入控制权时，返回 owner 信息，
  让用户关闭那个会话（`SKILL.md:252-254`；`apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md:402-405`）。
- **全局唯一输入写者**：多会话可以各自观察，但同一时刻只有一个 controller lease 能写输入（`notes/01` §5）。
- **前台约束**：`strategy=event` 在后台 app 上失败且零发送；macOS 模型面**没有前台化手段**（不要找 activate 调用），
  Windows 上"启动非 packaged 应用/截图恢复最小化窗口"会抢焦点，工具描述如实声明。
- **停止**：`agent.computerUse.stop()` 触发 kill switch；之后必须立即停止 CUA，
  **不得切换成其它 UI 自动化技术**（AppleScript/osascript/System Events 等都被明令禁止，
  除非用户明确要求该技术）（`SKILL.md:256-258`）。
- **观察的作用域提示**：动作失败/树看起来像别的部分时，先看观察结果里的 `window` 块（可能是新弹的 modal），
  不要直接重试。

---

## 6. Windows 专项（最重要的一章）

**证据边界（先读）**：Windows 的 native producer（`ax_win.cc`、`windowsInputDispatch.ts`、`electronInputHandlers.ts`、
`screen_capture_win.cc`、`win_window_geometry.cc`）**不在本仓库**（全仓 grep 零命中）。
本章内容以 `docs/cua/2026-10-13-windows-input-delivery-spec.md`（1025 行，下文简称"总纲"）、
S3/S4 计划与探针报告、`2026-10-23-windows-app-frame-surface-spec.md`（883 行）等【规格】为主，
辅以 `packages/services/src/cua-permission-broker/windowsCua*.ts`、`windowsCuaOperationIndicator.ts` 与 SDK 脚本【代码】。
所有"实测数字"均来自文档记录，不能当作本快照可运行验证。

### 6.1 派发三级阶梯与物理 lane

**总目标**：后台优先（不抢焦点、多会话并行），前台兜底；工具签名不变，改动只发生在 producer/native/broker。

```text
工具调用（14 工具签名不变）
  ├─ 0. strategy=event 或带修饰键 → 直接进物理 lane（第 4 级）
  ├─ 1. 预检：目标身份 + UIPI 完整性（失败 → refused，零发送）
  ├─ 2. UIA 级（仅左/中键单击无修饰）
  │     元素目标：Invoke → Toggle → SelectionItem.Select → ExpandCollapse.Expand
  │               （四模式全部"明确 absent"才用 LegacyIAccessible 末级）
  │     坐标目标：窗口域枚举（ElementFromHandle 子树 + bounds 含点 + 白名单 + 最小面积）
  │     仅 Miss / 模式确实不支持，且确定未发送 → 3；
  │     busy / timeout / unavailable → 结构化拒绝，不重放
  ├─ 3. PostMessage 级：deepest-child HWND + WM_* 投递（节拍见 §6.3）
  │     Chromium 左键单击特例直投；丢弃矩阵必丢格 → 跳过本级直接进 4（自动升级）
  ├─ 4. 物理 lane（前台 SendInput）：进程内排队 await
  │     精确 HWND gate → 激活 → 发送前复核 → 节拍化 SendInput → 条件恢复
  └─ delivery 元数据（成功/失败都保留，不进模型文本）：
      { path: "ax" | "post_message" | "foreground_send_input",
        effect: confirmed | unverifiable | suspected_noop | refused,
        attempts: [...], escalated: bool }
```

（总纲 `docs/cua/2026-10-13-windows-input-delivery-spec.md:77-127`）

**升级规则（v3 现行口径）**：

- 升级是**驱动侧自主决策、模型无感**；只有"允许升级 + 确定未发送（`dispatch_status=not_sent`）+ 身份/权限仍有效"
  才可换下一级。已确认不支持的后台手势（旧口径 `background_unavailable`）**不再预登记拒绝，而是跳过本级自动升级**
  （总纲 `:99-100`；注意 `:83` 是旧残留文案，见 §6.8 文档漂移）。
- **动作一旦调用，失败也按 possibly_sent 终止**：`S_OK` 只证明调用被接受，不证明业务生效；
  busy/timeout/unavailable 不做后续模式重试。元素路径点击后**不读回**，回执 `verified:false / unverifiable`。
- **成功路径不向模型披露升级**：`method` 投影为中性值（如 `click`），路径/尝试/effects 只进 broker `delivery` 元数据；
  模型文本不得出现 `PostMessage`/`double Invoke`/`winui3_exempt` 等内部术语。
- **接收者分通道**：模型（文本，保持原形状） / broker（元数据，新增可选字段不 bump 协议） /
  运行时取证（debug 日志）。`effect` 四档：`confirmed / unverifiable / suspected_noop / refused`；
  每个 attempt 另记 `dispatch_status: not_sent | sent | possibly_sent`（仅 not_sent 可升级）。

**物理 lane**（2026-10 v3 重构的关键）：

- 只串行"物理动作"：激活、SendInput、条件恢复、scroll、drag、全局键盘注入、paste 相关串行。
- **后台 UIA / PostMessage 不进 lane**（多会话并行区）；物理 lane 是进程内 Promise 链，
  "后到者等待前一个物理动作完成，不因队列占用而拒绝"——`input_dispatch_busy` / `uia_job_in_flight` 两类模型可见错误随之废除。
- 2026-10-19 拆锁：`g_inputSequenceMutex` → `g_sendInputMutex` + **per-root-HWND 队列**；同根 burst 排队不拒绝、不同窗口并行。
- S4.0 实测修正：三轮全量并发实验 `busyTotal=0`——拆锁收益从"消除高频 busy"改为"消除偶发撞锁 + 同窗 burst 语义正确性"。

（总纲 `:85-127, 907-916`；`2026-10-19-windows-input-s4-parallel-baseline.md:9-41`）

### 6.2 派发分类与丢弃矩阵（UIPI / 窗口类型）

**native 分类接口**：`classify_delivery(hwnd, event_kind) -> droppable | deliverable | unknown`。
`✕` = 判定 PostMessage 必然静默丢弃；`·` = 不标记，按可投递尝试；规则是**常量表 + 窗口类名匹配**，
不做进程名/heuristic 判断；新增类别必须先有 fixture 证据；`unknown` 按 deliverable 尝试。
（总纲 `:129-197`）

| 窗口类别（判定依据） | 点击 | 移动 | 滚轮 | 单键 | 组合键 | 文本 |
| --- | --- | --- | --- | --- | --- | --- |
| Chromium 顶层（`Chrome_WidgetWin_*` / `CefBrowser`） | ✕ | ✕ | ✕ | ✕ | ✕ | ✕ |
| 内嵌 Chromium（WebView2/Tauri） | · | ✕ | ✕ | · | ✕ | · |
| WPF（`HwndWrapper…`） | ✕ | ✕ | ·（注 1） | ✕（注 2） | ✕（注 2） | ✕（注 6） |
| Tk（`TkTopLevel`） | · | · | · | ✕ | ✕ | ✕ |
| GTK（`gdkWindow/gdkSurfaceToplevel`） | ✕（注 3） | · | · | · | · | · |
| VCL（`SAL*`，LibreOffice 族） | · | ✕（注 8） | · | ✕ | ✕ | ·（注 4） |
| WinUI3 | 刻意豁免（注 5） | · | · | · | · | · |

关键注解（总纲 `:147-184`）：

1. **WPF 滚轮不标记**：scroll 工具直投 `WM_VSCROLL/HSCROLL`，WPF hook 可消费；WPF 指针丢弃的真实机制是
   "仅当真实系统光标悬于窗口上才处理投递的指针消息"。
2. **WPF 键盘前台依赖**：仅当目标非前台时标键盘丢；前台态 PostMessage 键盘可投，但 `GetKeyState` 类加速键仍不生效。
3. **GTK 单击广泛标记**：整窗单 HWND 无法区分 button 与 drawing-area，保守标记（画布拖拽实践中可用）。
4. **VCL 文本可用**（`WM_CHAR` 端到端验证过），但单键/组合键因 `TranslateAccelerator` 读 `GetKeyState` 而丢。
5. **WinUI3 豁免**：普通单击用后台 UIA Invoke；坐标双击/右击/拖拽明确不可达（v3：跳过本级升级前台）。
6. **WPF 文本改标必丢**（S4.2 实测）：WPF 控件无独立 HWND，`WM_CHAR` 只能投根窗，`InputManager` 键盘焦点门控使后台窗口不消费。
7. **Chromium 单击特例**：整类标记但普通单击绕过 UIA 直投 PostMessage；对 Chromium 的 UIA "成功"结构性不采信。
8. **VCL 移动改标必丢**（2026-10-27 bench）：VCL 静默丢弃整段 PostMessage 拖拽序列——消息入队成功、原语回执 ok、实为 no-op。
9. **WinForms TrackBar 是控件级丢弃**：`classify` 只回 host 字符串、不回窗口类名，控件级拦截需要新 native 接口，当前以 noop 契约钉死。

**UIPI 预检必须独立**：

- 目标完整性级别更高时 `PostMessageW` 与 `SendInput` 都受 UIPI 限制；预检比较双方完整性级别，
  不可达 → `refused` + `action_sent:false` + attempts 记 `uipi_blocked`，**不得先激活目标再试 SendInput**；
  本批不新增提权/UIAccess 通道。
- 可观测性不对称：PostMessage 被 UIPI 阻断返回失败（`GetLastError=5`，不是静默成功）；
  **SendInput 的失败返回不能单独识别 UIPI 原因，所以必须预检**。
（总纲 `:199-207`）

### 6.3 逐工具设计细节

**left_click**（先行打穿全链的样板）

- **元素路径**：UIA 模式梯 `Invoke → Toggle → SelectionItem.Select → ExpandCollapse.Expand`；
  四模式全部明确 `pattern_absent` 才进 LegacyIAccessible；token 失效/查询报错 ≠ absent；
  动作一旦调用，失败也 `possibly_sent`；`S_OK` 只证明被接受。
- **坐标路径 UIA hit-test**：
  - 主级 OS 原生 `ElementFromPoint`；命中元素必须归属已验证目标根 HWND（不得对遮挡者执行动作）；
    遮挡则放弃全屏候选转窗口域枚举；
  - `NativeWindowHandle=0` 时沿 `RawViewWalker` 祖先找最近非零 HWND 再 `GA_ROOT`；遍历有节点上限/deadline/取消检查；
  - 次级窗口域枚举：bounds 含点 + Invoke/Expand 可用 + **坐标无关控件类型白名单**（Button/MenuItem/Hyperlink/TabItem/
    ListItem/CheckBox/RadioButton/SplitButton/TreeItem，防"Invoke 中心"丢失像素精度）+ 最深（最小面积）匹配；
  - **Miss 与身份失败分开**：两级均无可调用元素但目标身份/坐标仍有效、且尚未调用动作 → 按 `not_sent` 放行 PostMessage。
- **Chromium 特例**：Chromium 类窗口不进 UIA 点击路径——左键单次无修饰直投已验证 PostMessage（元素中心）；
  双击/右击/修饰走前台升级。2026-10-20 探针（背景/遮挡/前台三姿态 ×3）实测 **9/9 消费、遮挡下 9/9**；
  嵌套宿主（WebView2/Tauri）维持 UIA 阶梯。
- **点击节拍**（零驻留 no-op 的修复项）：

| 执行级 | 节拍 |
| --- | --- |
| PostMessage | `WM_MOUSEMOVE` 预热 → DOWN → 驻留 **35ms** → UP；多击间隔 **80ms**；结束 settle **50ms**；双击第二击投 `WM_*BUTTONDBLCLK`（按 `CS_DBLCLKS` 判定） |
| 前台 SendInput | `SetCursorPos` 定位 → `[move,down,up]` 单批、零驻留；多击间隔 80ms；修饰键按下后停 5ms；事后 settle 40ms（前台激活态 120ms）——retained-mode 框架在 down 建 capture/focus，过早收走光标会丢点击 |
| UIA 元素模式 | 无指针时序概念（COM 调用语义即动作） |

- **验证口径**：点击后不读回元素状态；回执保留 `verified:false / unverifiable`；no-op 防守靠分类 + 回执 + 升级链。
- **已知坑**：Electron 未遮挡后台姿态下首击可能伴随 1 条目标 pid 焦点事件（Chromium 异步 handler 显式 `SetForegroundWindow`，
  `WS_EX_NOACTIVATE` 挡不住、消息消费可晚于投递几十毫秒）→ 只在 settle 末尾做条件矫正；两格红灯保留。

**type / set_value**（读回裁决，最值得照抄的验证工程）

- **契约保留**：元素目标的 `type(strategy=auto/a11y)` 与 `set_value` 都经 `element_set_value` → UIA ValuePattern **替换整个值**；
  空串清空。`type(strategy=event)`/无元素目标保留"插入"语义，不能改成"CurrentValue + text"尾部追加。
  替换路径缺 ValuePattern 且无保持语义的路径时**明确拒绝并指路**（选内层可编辑元素/用 event 插入/坐标点击后 type_text）。
- **读回裁决**：写前快照 `before` → 写入 → 读回 `after`（ValuePattern 优先，`TextPattern.DocumentRange` 兜底）：
  - 替换路径必须 `after == requested` 才 `confirmed`（含"before 已等于目标值"的幂等写入）；
  - 插入路径（WM_CHAR）判据 = changed + contains（插入点不可知；`before==after` 时即便已含目标文本也不算送达）；
  - `verify` 五档 `confirmed/pending/changed_contains/unchanged/unreadable`，与 `effect` 四档**两层正交**；
  - 写入已接受但读回仍旧 → `suspected_noop`；不可读 → `unverifiable`；两者**禁止自动重放**；
  - Chromium AX-echo 降级：UIA 读回"成功"→ `unverifiable`（`verify` 保留 confirmed 但 delivery 带 `axEchoDemoted`），
    本次调用终止并提示重观察，**不能**在已写入后自动坐标点击再重发文本。
- **换行映射**：`\n`/`\r` 靠**真实 Enter 键击**（后台 `WM_KEYDOWN/UP(VK_RETURN)`，前台带扫描码），`\r\n` 折叠一次；
  `WM_CHAR(0x0A)` 不被富文本控件当换行（WPS 内联续排的根因）。
- **上限**：请求以 UTF-16 单位计，**1024 单位上限**；前台长文本见 §6.4 排水策略。
- **异步边界**：`typeTextGlobalAsync` 把逐字符节拍放原生 worker，Helper 主线程继续处理 IPC/心跳；
  无目标文本、全局 press_key/hold_key 与应用前台输入共用物理 lane（不能因异步化让另一物理按键插入）。

**left_click_drag**

- pre-dwell：down 后驻留 **35ms**；步进 **12 步 × 25ms**（总 ≈300ms）；
- PostMessage 后台序列：`WM_MOUSEMOVE(wParam=0)` 预热 → DOWN → 35ms → 12×(MOVE+25ms) → UP，投起始点最深子 HWND；
- 前台链：锁步 `SetCursorPos` + SendInput 绝对坐标注入，UP 后 40ms settle（retained-mode 框架在 down 建 capture）；
- **已知缺陷**：标题栏/边框（`WM_NCHITTEST` 非 client 区）拖拽应拒绝进前台，但实现无 NCHITTEST/HTCAPTION 检查——
  起点落标题栏时 PostMessage 照投 `WM_LBUTTONDOWN`，原生窗口移动手势由 `WM_NCLBUTTONDOWN` 驱动，
  **实际静默无效但回执 sent**（spec-实现漂移，PCCtrl 若做拖窗必须自己实现）；
- VCL 拖拽必丢、WinForms TrackBar 控件级必丢（见 §6.2 注 8/9）。

**scroll**

- 后台路径 = **SB 码族**：`WM_VSCROLL/HSCROLL` + `SB_LINE*`/`SB_PAGE*` 投**根窗**，`amount`=消息条数，消息间隔 **30ms**；
- 探针取证（34 组合 × 5 宿主，全程后台）：WPF 消费、WinUI3 消费（**必须投根窗**，投 `DesktopChildSiteBridge` 子窗被忽略）、
  WinForms（非客户区滚动条）/Qt6（自绘）/GTK3 全忽略；旧候选 `WM_MOUSEWHEEL/HWHEEL` PostMessage 探针全败，按证据收缩不落地；
- 路由三档：WPF/WinUI3 → SB 码族中间级；WinForms/Qt6/GTK3 → `not_sent` 升级前台滚轮；Chromium 系 → 丢弃矩阵升级；
- 元素目标维持 UIA ScrollPattern 首选，但真机取证后 `scroll_element_a11y` 三宿主全撤（Qt6/WinUI3 的 SetScrollPercent 带焦点副作用；
  WPF a11y 锚点在折叠线下无有效屏幕点）；
- 横向滚动无 oracle，**未验证**。

**key**

- 中间级 `postMessageKeyAsync(root, pid, chord, mods)`：focused descendant 解析 + `WM_KEYDOWN/UP` 序列
  （修饰键按下 → 主键 → 4ms → KEYUP → 逆序释放），alt 走 `WM_SYSKEY*`，lparam 带扫描码/扩展位；
- 可达边界 = **单键 keystroke**（WinForms 纯 Win32 泵、Qt6 eventFilter 实测送达）；
- **组合键结构性不可投**：`WM_KEYDOWN` 的 LPARAM 没有任何位能表达 CTRL/SHIFT（仅 ALT 有 context bit）；
  posted 修饰键也不更新 `GetKeyState`；实证：paste 组 WPS/记事本 `reads=0`、WPF 前台态 `accel_fired=0`、354 份 baseline 零正样本。
  因此**所有多 token 和弦**在后台键盘路径短路 → 升级前台 SendInput（复用条件恢复、`escalated` 照常）；
  XAML 宿主先走 UIA 加速键通道、单键照投；
- **无焦点短路**：中间级目标 HWND 解析按 `GUITHREADINFO.hwndFocus` 三态——真后代→照投；根窗自身（单 HWND 应用）→照投根窗；
  焦点=0 或指向其他窗口树 → `focus_missing`，`not_sent` 后升级前台。**TS 层不得在中间级前用 `focused_input_window_id===0` 硬拒**
  （4881359c1a 回归：不记 attempt、不进升级链、误挡 XAML 加速键）；
- **XAML 加速键通道**：`invokeAcceleratorAsync` 用 UIA `AcceleratorKey/Name` canonical 匹配 → Invoke → Toggle；
  KeyboardAccelerator 声明**不自动暴露给 UIA**（真实应用需显式提供 AcceleratorKey 注解）；
- 键名归一化四层（SDK `KEY_ALIASES` → 工具层 `PUBLIC_KEY_TOKENS` → broker `KEY_CHORD_KEYS` → native `VkForToken`），
  在工具层入口统一执行；键盘节拍 `KEY_DELAY_MS=4ms`，WM_CHAR 路径另有 40ms settle。

**paste（provided paste）**

- 原生 Helper 完成：快照剪贴板 → Empty → 延迟渲染占位 → `markDispatched` → 投粘贴键 → awaitRead → 还原；
- 键部分是前台 chord（组合键不可投）；跨进程剪贴板冲突依赖既有接管检测
  （`pasteboard_changed_during_paste` 结构化失败）；
- 无 app 读取 → 超时失败而不是报成功。

**perform_action / perform_secondary_action**

- 直接调元素 pattern（非坐标，同 UIA 作业族）；action 必须来自当前树里元素自报的 actions，猜错 → `ACTION_UNAVAILABLE`。

### 6.4 焦点、HWND gate、多显示器与 DPI

**焦点自抢防守（三件套）**：

1. `make_guard` 对 XAML 宿主或 Chromium 目标窗口启用 `EnableWindow` shield：归一到 `GA_ROOT`、保存原 enabled、动作线程 RAII 恢复；
   原本已禁用的窗口不能无条件启用。
2. `NoActivateGuard`（`WS_EX_NOACTIVATE`）盖 PostMessage burst 与 `RunSetValueReadbackJob`；
   **不套**到已显式激活的前台 SendInput。
3. 条件式前台恢复：补两个薄原语 `getForegroundRootWindowId`（激活前快照任意前台根）+
   `restoreForegroundWindow(prev, expectOccupied)`，内建三条件：prev 有效、prev≠目标根窗、
   此刻前台仍==目标根窗（用户切第三窗不抢回）；两连投 `SetForegroundWindow` + 12ms + `AttachThreadInput`；best-effort 返回 bool。

**排水竞态（重要）**：SendInput 只把事件注入系统队列即返回，立即 restore 会让键盘事件被重路由/丢弃，
回执却是 accepted（假成功）。修复 = finally 里 restore 之前等 **120ms**（与 native `kInputPacing::ForegroundSettleMs` 同源）。
长文本前台化还要逐字符节拍：16 字 CJK 曾只落地 7 字（泵速 ≈17ms/字）→ 逐字符 down/up + 键间 `Sleep(4)`，
Enter 后 `4+20`；TS 侧 `drainHintMs = max(120, n×20)`。
**长文本保留前台**：以 UTF-16 单位计，>50 且已发送 → 完成排水后**不恢复**原前台；≤50 保留条件恢复；
部分发送也算已发送、返回 `text_truncated`、`action_sent=true`、禁止重放。

**超时的诚实边界**：UIA 作业 single-flight + **4s deadline**；4s 是**等待预算**，不是取消/恢复期限；
线程不杀（`TerminateThread` 会腐坏 COM 套间与 loader lock）；超时后真实作业仍持资源；
shield 可能无限期扣在用户窗口上 → 修订 = 超时分支按 spawn 前 shield root 快照 `AdoptAbandonedShields`，
per-root 计数表 entry 记录构造 pid，depth==1 且窗口归属未变才接管。

**精确 HWND gate**：前台点击/激活要求精确 HWND/PID/几何校验，**同 PID 的兄弟窗不算命中**；
激活前快照前台根、发送前复核，用户切走不抢回。

**多显示器 / DPI**：

- 进程 **Per-Monitor-V2、坐标全物理像素**（与 `ReadWindowFrameBounds` / WGC 位图 1:1）；
  overlay 按光标热点所在屏 scale 绘制；native 层用虚拟屏指标（`SM_X/CXVIRTUALSCREEN` 族）作为坐标语言；
- 前台 SendInput 级绝对坐标已做**虚拟屏幕归一化**（`MOUSEEVENTF_ABSOLUTE|VIRTUALDESK`）；
- UIA 元素级无坐标概念；PostMessage 级经 deepest-child HWND + `ScreenToClient` 得客户区坐标，
  天然与屏幕布局/副屏负坐标/多屏 DPI 混排无关（仅 SendInput 级涉及屏幕绝对坐标），且 PostMessage 原语校验 **i16 范围**；
- ZCode 自己**没有验完**：副屏负坐标、多屏 DPI 混排、边缘目标、DWM 查询失败路径仍是真机缺口。
- 跨虚拟桌面：不做专门优化——目标在其他虚拟桌面时后台 UIA/PostMessage 按 HWND 路由本就可达，`delivery` 如实记录实际路径。

### 6.5 UWP/XAML/WinUI3/Chromium/WPF/VCL/GTK 特例

**UWP（身份与输入的双重分裂）**：

| 角色 | 归属 | 证据 |
| --- | --- | --- |
| 身份 pid（`list_apps` 报出） | `ApplicationFrameHost.exe`，**多个 UWP 共享一个 pid** | 总纲 `:401-408` |
| 真实渲染进程 pid | AUMID 激活返回的进程，其窗口 cloaked/无可视帧 | 同上 |
| UIA 元素 ProcessId | 真实进程 pid；CoreWindow 的 UIA 树**不挂窗口节点下**（`ElementFromHandle` 返回空壳，真树注册在 desktop root） | 同上 |

硬约束：

1. hit-test owner 校验基准用**窗口 HWND 归属**，不得用元素 ProcessId 对比 app_ref pid（UWP 会被误判 `wrong_owner`）；
   命中元素 `NativeWindowHandle=0` 时沿 `RawViewWalker` 找最近非零 HWND 再 `GA_ROOT`。
2. CoreWindow 空壳免疫靠**主级路径选择**（`ElementFromPoint` 从 desktop root 出发）；次级 `ElementFromHandle` 在 CoreWindow 上拿空树 → Miss 放行下层，
   **不做 desktop-root 兄弟遍历**。
3. **XAML 宿主键盘路由**：CoreInput 只消费系统输入队列 → 键盘中间级对 XAML 宿主跳过 PostMessage 直接升级；
   实现为"丢弃矩阵旁的一张宿主判定表（类名 + exe 名）"。**宿主判定必须统一**：分类矩阵、双 Invoke 门、DisabledHwndGuard shield
   三处共用同一谓词 `IsXamlHostHwnd`（覆盖 `WinUIDesktopWin32WindowClass` + UWP 的 `ApplicationFrameWindow`/`Windows.UI.Core.CoreWindow`）；
   此前只认 unpackaged WinUI3 类名，真实 UWP（计算器，AFH 托管）被判 unknown+deliverable → 键盘走 PostMessage 静默失败。
4. cloaked（挂起 UWP）不新增输入侧唤醒：输入层结构化拒绝并说明"先 re-observe（会自动唤醒）"。
5. shield 生命周期：XAML/Chromium 宿主在 UIA 模式动作期间由实际调用线程持有 guard，返回后恢复原 enabled。
6. raw 前台门禁的 active 比对基准用**唯一 pid 集合**，不得数 active 条目数（AFH 前台且名下多个 UWP 可见时多条同时 `active=true`）。

**Chromium/Electron**：PostMessage 点击可达（直接宿主 9/9），但异步 handler 会晚自激活；UIA 的 S_OK 不可信（DOM 无事件）；
组合键/文本必须前台。

**WPF**：单 HWND、键盘焦点门控 → `WM_CHAR`/键盘后台不消费；`Focus()` 会自抢前台；用双层 guard。
**VCL**：文本可用，单键/组合键/拖拽/移动因 `GetKeyState`/`TranslateAccelerator` 必丢。
**GTK3**：UIA 元素路径结构性不可用（只有系统 TitleBar、MSAA 无子对象、`WM_GETOBJECT=0`、无 atk-bridge/gail），
从固定集合删除；坐标路径仍需真机。**先探针取证再登记"不可用"，不要靠猜。**

### 6.6 frame 采集、坐标同源与 app identity

**窗口几何与坐标同源**（总纲 `:465-479`）：

- `ReadWindowFrameBounds` 优先 `DWMWA_EXTENDED_FRAME_BOUNDS`；DWM 失败同次回退 `GetWindowRect` 并标记 `geometry_source`；
- capture / window.bounds / screenshot_bounds / 实时 probe **必须同源**；来源或几何变化即拒绝并要求重观察；
- `frame-projection.ts` 保持纯投影、不加 inset；Helper 重启使旧 `frame_id` 失效；
- PostMessage 的 `ScreenToClient` 独立验证 deepest-child 归属，每层从原始屏幕点换算。

**App Frame Surface（2026-10-23/28 的大修，观察侧）**：

- **四类 kind**（全部有独立 HWND）：`window`（frame 窗口本身）、`owned_popup`（`GW_OWNER` 链 ≤8 级到 frame，可跨进程；
  含 #32770 对话框/layered/非模态浮层）、`menu_popup`（菜单/下拉/工具弹窗，含 owner=0 框架菜单）、
  `child_window`（`WS_CHILD`；仅 root 直接 child 中**跨 pid 的可见内容承载者**入 surfaces，同 pid child 不占模型面）。
- **归属证明三链**：①`GW_OWNER` 链；②GUI 菜单态 + child 父链；③UIA 层级链；三链覆盖多数实测样本，
  WPS 类三链全失败只能靠操作关联（落地前按 unknown/独立候选处理）。
  E32 教训：链③曾因"先查自身 HWND"的复制写法恒自返（738 次真实归属记录 `uia_ancestor` 0 命中却"看起来正常"）。
- **像素主路径 D1**：根 WGC **不开** `IncludeSecondaryWindows` + 弹层各自独立 WGC + **预乘 alpha** 按真实 z 序合成、
  范围限根边界；完全越界不入图并记 `outside_root_bounds`。ISW 被裁决放弃。
- **D8**：显式主窗观察帧**收编全部已证明 owned/menu 弹层像素**（含模态面板），但树合并仍限菜单链——
  "像素收编、树/身份不收编"。
- **坐标权限唯一事实源**：`capture_surfaces` 记录每块像素来源 surface（原点/z 序/归属证明）；
  坐标 p 落在哪块已验证区域，其根 HWND 即预期接收者，重叠取观察时 front-to-back 最前者；
  **不得用派发时 `WindowFromPoint` 选目标**（会把无关遮挡者变成目标）；四级接收者各自校验
  （AX 元素/UIA hit-test/PostMessage/SendInput）。
- **D4 菜单前台前提**：统一为"前台根属于该菜单归属链的宿主集合"；菜单 HWND 不必是 `GetForegroundWindow()`
  （如 Notion 菜单为 `WS_EX_NOACTIVATE` 顶层 TOOLWINDOW）。
- **D7 后台定向消息**：仅限"宿主链已证明 + 结果可完整读回的定向消息（`WM_SETTEXT` 类）+ 投后读回裁决 + 焦点零扰动"，
  不适用于键盘、点击、确认类动作；只有 Win32 Edit 单控件真机验证过，RichEdit/Qt/XAML 未测。

**app identity（Windows 身份纪律，`notes/03` §5.3；`notes/04` §4.5）**：

- 普通应用用 **canonical exe 绝对路径**；有 AUMID 时优先 AUMID；**`ApplicationFrameHost.exe` 不得作为身份**；
- packaged app 用 `IApplicationActivationManager` 激活并读 `PKEY_AppUserModel_ID` 精确解析 HWND/PID；多候选 fail closed；
- 窗口标题/active/枚举顺序**都不作判据**；
- app 展示身份键 `appKey`：`windows-aumid:<aumid>` / `windows-exe:<canonical path>`；
  图标由平台服务按 appKey 解析，不经会话协议传输图标字节。

**透明启动 / "没有 launch 工具"的细节**（`docs/cua/producer/windows-app-identity-and-transparent-launch.md:13-190`）：

- `open_application` 已删除，语义并入 `get_app_state`：未运行→拉起；运行正常→无需动作；cloaked→AUMID 唤醒；
  最小化→树完整，`include_screenshot=false` 只回树；`=true` 才恢复窗口（**`include_screenshot` 就是焦点开关**，默认永不抢焦点）；
- 名字解析两层：①已安装应用名精确匹配（权威）→ AUMID/target_path；②窗口标题子串（沿用既有语义）；两层都空但①查到已安装应用→启动；
  ②多命中→抛歧义错并列候选（取代静默择一）；
- **拉起后等窗预算 10 秒（500ms×20）**：原 3 秒不够——PowerPoint 冷启动从 launch 返回到首个可见窗口 **7.8 秒**；
- AppsFolder 实测 134 条：合法 AUMID 38 + `.exe` target 75 = **113/134（84%）可拉起**；21 条（.url/.msc/.html/shell CLSID）拒绝；
  枚举耗时 280-386ms；含卸载程序（"卸载微信"）→ 解析层必须显式拒绝名字/路径命中 `uninstall/卸载`；
- **焦点语义**：`ActivateApplication(AO_NONE)` 不抢焦点；`CreateProcess/spawn` 抢焦点；
  `STARTF_USESHOWWINDOW+SW_SHOWNOACTIVATE` 仍抢（应用自己调 `SetForegroundWindow`）；
  `PreventActivation/IsFocusStealPrevented` 是诚实 no-op（"Windows has no equivalent"）→
  **决策：接受 Win32 分支抢焦点，不尝试抑制，工具描述如实告知，不得宣称"后台拉起"**；
- UWP 挂起 cloaked：`ActivateApplication(AUMID)` 唤醒（不抢焦点）；最小化 iconic：树完整、UIA setValue 仍 ok、截图 `invalid_target`。

### 6.7 操作指示器、进程生命周期与 Job Object containment

**用户可见提示**（两条不同链路，不要混）：

1. **操作指示器**（desktop main 的 Electron 透明置顶窗，顶部提示条）：
   - 判定收敛为一个布尔"这个 cell 是否在用 Computer Use"，锚点是模型必须原样照抄的 bootstrap 语句
     `setupComputerUseRuntime({ globals: globalThis })`——架构强制而非文档软要求；
   - 旧实现按动作名正则匹配 `input.code`，node_repl 重构后没有任何真实调用形态能命中 → **测试长期假绿**；
   - 关闭有六条路（turn 终态/session 关闭/新 turn 顶掉/workspace 关闭/runtime 不可用/应用退出）+ **30s 兜底**；
   - 实现细节：`BrowserWindow{transparent, frame:false, alwaysOnTop, focusable:false, skipTaskbar, contentProtection, ignoreMouseEvents}`；
     位置=光标所在 display `workArea` 顶部居中；每次显示 `showInactive()+setAlwaysOnTop(true,"screen-saver")+moveTop()`
     （**Windows 隐藏透明窗口后可能清除 `WS_EX_TOPMOST`，必须重新声明层级**）；
     `hide()` 抛错时降级销毁——"它在声称 ZCode 正在操作电脑，隐藏失败就等于向用户撒谎"。
   - 教训：**展示旁路不能从模型源码模式推断动作**，要锚在架构强制的锚点上；隐藏必须 fail-hidden（计时器兜底 + hide 失败销毁）。
2. **ghost cursor overlay**（Helper 侧绘制在桌面上的光标/反馈浮层）：argv `--ghost-cursor-overlay` 在 Windows 产品链恒开；
   多 session overlay 升级 spec 有，但 **Windows 真机验收待做**。

**Helper 传输与生命周期**（真实代码 `windowsCua*.ts`）：

- 传输：`\\.\pipe\zcode-cua-<random>`（每代随机、不复用旧 pipe），NDJSON/line-JSON；
- **两阶段就绪**（Windows 无 POSIX rendezvous）：`transport_ready`（pipe 已 bind + bearer 认证装配）→ `ready`（完整 health + broker_info）；
  功能层初始化期间除 `broker_info` 外返回可重试 `broker_unavailable(warming_up)`；旧 Helper 只发 `ready` 时视同 transport ready；
- Host 只接受自己 fork 的**精确 child PID** + 自己 mint 的 pipe；`ready.socketPath` 必须等于本代 socket、`ready.pid` 必须等于 child.pid；
  health probe pid 必须精确等于 child PID；`startupTimeoutMs=30_000`、`shutdownTimeoutMs=1_000`；
- 生命周期 tail 是唯一线性化边界：任何 fresh fork 必须排在上一代 exact child exit 之后；`kill` 只是请求，未观察到 exit 不得判死；
- 恢复复用既有 tuple：`restartAfterCurrentStartPreservingTransport` 停旧 Helper 后用同一 socket/pipe 名重启（Agen 不需换凭据）；
  显式 stop 清 tuple；异常退出保留 tuple 供按需恢复；
- **⚠️ Windows 鉴权现状**：native peer-identity 门**临时放开**（`WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN`），
  因为打包版拿不到 dev 豁免、不放开必然启动失败；带一行 stderr 告警以无鉴权状态启动——
  **Windows 上任何本机进程都能连这条 named pipe**。这是产品安全债，PCCtrl 不能照抄（见 §7.3）。

**进程清理与 Job Object**：

- Windows 不用 POSIX process group：`taskkill /T` 与经身份复核的 `taskkill /T /F`；
  三个叠加竞态（等待式回收未暴露 flight、`/F` 成功后 Node exit 晚 1-10ms 导致死 root 被追加回残留、多 workspace 共用进程表早于新 Agent spawn）已修；
- **安全不变量**：PID 只表示数字不代表所有权；强杀前必须匹配进程创建身份，或由 Host 持有的受管 `ChildProcess` live root 句柄证明所有权；
  身份查询失败/超时 → 未复核后代与 `unverifiedRootOnly` 必须 fail-closed，禁止向裸 PID 发 `/F`；
  只有绝对 deadline 到达后仍属原进程树的 PID 才是 confirmed residual，且必须继续向调用方报错；
- Deadline：transport 在 cleanup 起点固定 3,250ms 全链硬上限；
- **Job Object containment 仍是原型**：每个 transport 独立 Job（不用全局 Job），`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，
  close 时优先 `TerminateJobObject+CloseHandle`，失败降级 `taskkill /T /F`；
  原型在 Node SDK 已创建 root 后才 attach，**不能覆盖 attach 之前创建的后代**；生产化需
  `CREATE_SUSPENDED → AssignProcessToJobObject → ResumeProcess` launcher。

### 6.8 已知缺陷、验收集合与对我们的警示

**可引用的验证基线**（文档记录，非本快照可复跑）：

- 点击专项 **53/53 通过**（47 格生效、6 格明确拒绝，零点击焦点违规）；
- 历史口径 117 动作 = 72 delivered / 27 noop（19 诊断 + 8 S4 锚点）/ 9 refused；
  v3 验收轮 26 noop = 19 诊断 + 8 S4 锚点 + 0 意外，`noopRate=0.257`、后台占比 56/105、refused 9→3；
- 全量 133 动作 = 100 delivered / 21 noop / 3 refused / 9 unresolved（GTK3 结构性）；
- 框架/S4 分阶段：S1 harness、S2 键别名/paste/list_apps、S3 派发地基、S4 文本/键盘/滚动/拖拽/并发、S5 完整矩阵首轮（2026-10-20）；
- 真机回归命令（S3 时点）：`test/native/win-uia-timeout-lifecycle-cua.test.ts` 等；14 工具 smoke：
  `ZCODE_CUA_WINDOWS_TOOL_SURFACE_LIVE=1 node packages/zcode-cua/scripts/windows-tool-surface-live-smoke.mjs`。

**失败案例（对 PCCtrl 的直接警示，逐条来自 `notes/04` §6.2）**：

1. **焦点违规 21 项**来自文本路径：WPF `UIElement.Focus()` 抢前台 + 前台插入通道；后被"NoActivateGuard + EnableWindow 双层守卫 +
   接缝接线 + 前台建焦前置"修复（13 格清零）。教训：UIA/COM 动作与焦点链会互相污染，**guard 只盖一层必漏**。
2. **Electron press_gated/hover_gated 两格长期红**：单点恢复盖不住 ≥15ms 计时器的晚自激活；有界轮询被否决。
   教训：**不要用轮询换稳定格**，保存已知残差比伪造稳定更重要。
3. **Qt6 后台插入两格红**：补偿方案（unchanged→点击建焦+重投）实测破坏插入点；决策=红灯监督、后台路径不隐式建焦。
   教训：**readback 未变不等于可以补偿重放**。
4. **GTK3 元素路径结构性不可用**：UIA 只暴露系统 TitleBar、MSAA 无子对象；从固定集合删除；坐标路径仍需真机。
   教训：先探针取证再登记"不可用"。
5. **WPF 文本列 deliverable→dropped**（用户决策 2026-10-19），锚点格按前台升级验收。
6. **WPS 纯图像下拉停止点**：45 个原生 AX 元素无 Menu/MenuItem，`has_menu=false`，正式工具对 AXExpand 明确 `not_sent`；
   没有把图像文字伪造成 AX 元素。教训：不能为了覆盖率把"看图"升级成授权。
7. **`WM_NCHITTEST` 标题栏拖拽 TODO**：静默无效但回执 sent。教训：**"消息入队成功 + 原语回执 ok"不是生效**。
8. **WinForms TrackBar 控件级丢弃**：宿主级分类不够，控件级怪癖要单独登记。
9. **E32 链③起点自返 bug**：738 次真实归属记录 `uia_ancestor` 0 命中却"看起来正常"（靠操作关联兜底）。
   教训：**指标为 0 的路径要专门断言它在正例上非零**。
10. **TS 预检回归**：在 broker 中间级入口加 `focused_input_window_id===0` 硬抛，导致不记 attempt、不进升级链、误挡 XAML 加速键；
    用户裁决后删除。教训：**"确定未发送"的唯一表达是 attempts 里的 not_sent + 升级链**，不能在入口 throw 绕过取证面。
11. **夹具/oracle 通道缺口**：oracle 通道断了会得到"假红"或"假绿"；**测试夹具本身是产品合同的一部分**。
12. **"association changed during image capture"**：19 场景顺序跑到第 19 个报错，干净基线复跑第 2 个即复现 → 预存缺陷。
    教训：先做干净基线对照再归因。

**文档漂移（引用注意）**：总纲内部 3 处口径不一（`background_unavailable` 残留 vs v3 自动升级；
"Chromium 嵌套宿主先 PostMessage" vs "嵌套维持 UIA"；"WinUI3 坐标双击明确拒绝" vs "跳过本级进前台"）；
2026-07/09 的"30-tool"口径与 2026-09-10 后的"14-tool"口径在 docs 中并存——引用旧文档的 smoke 数字时不要混算。
（`notes/04` §8）

### 6.9 第三方实现参考：trycua/cua（MIT）

总纲开头记录："第三方许可归属：trycua/cua `libs/cua-driver/rust/crates/platform-windows`（MIT）"。
这意味着 ZCode 的 Windows 原生输入层有**开源上游可读**：如果 PCCtrl 团队要自己实现 Windows 输入派发，
除了本报告的规格总结，**强烈建议直接读 trycua/cua 的 Windows driver**（Rust，MIT 许可）：
它至少能提供 SendInput/PostMessage/UIA 的实际封装、窗口枚举与坐标处理代码，比从零踩坑便宜得多。
（`docs/cua/2026-10-13-windows-input-delivery-spec.md:6`）

---

## 7. 权限、安全与失败关闭

### 7.1 授权主体：Helper 是谁、为什么必须是签名进程

**唯一授权主体 = 外置签名 Helper 进程**（macOS）：

- TCC 的 Accessibility / Screen Recording / Automation 授权按**进程签名身份**记录，所以 Helper 必须是独立签名、
  LaunchServices 启动的 `.app`（bundle `dev.zcode.cua-helper`，Team `8A5X4JJ39T`），
  而不是 Electron 子进程；"Python/uvx must never become the implicit permission owner"。
  （`docs/cua-permission-broker/zcode-cua-permission-broker.md:20-26`；`cua-helper-installation.md:5-22`）
- 安装包内是唯一来源（`ZCode.app/Contents/Resources/cua-helper/`），**产品不下载、无下载兜底**；
  安装时校验 bundle id / 版本 / arch / `codesign --verify --deep --strict` / TeamIdentifier / Gatekeeper，
  失败不启动、不注入凭据；原子替换 + 回滚；entitlements 最小集（只 `allow-jit` + `allow-unsigned-executable-memory`）。
  （`cua-helper-installation.md:46-133`）
- **每次启动前重验**（不只 install 时）：install 清过 quarantine，`open` 不会再评估；
  只查一次 inode 挡不住同 UID 换二进制，所以 `cuaHelperHost.doStart` 在 `launcher.launch` 前重跑 `verifyInstalled`。
  （`zcode-cua-permission-broker.md:33`）

**Windows 没有 TCC，但仍有"权限主体"概念**：

- Helper 是安装包内 Electron（`ELECTRON_RUN_AS_NODE=1`）fork 的 Node 进程，加载 `ax_native.node`；
  产品资源固定在 `resources/tools/cua-helper`，manifest 带 SHA-256 + arch/Electron 版本校验；
  权限接口保留但语义为"平台诊断"。（`docs/cua/windows-product-runtime.md:14-61,133-150`）
- **真实限制是 UIPI/完整性级别与会话隔离**："不新增 Windows 管理员提权；高完整性目标继续 fail closed"；
  `permission_denied` 在 Windows 可能表示 UIPI/前台策略而非"缺权限"。（`windows-product-runtime.md:255-258`）

**权限状态查询的两种模式（隐私契约，最容易抄错）**：

| 模式 | 触发 | 行为 |
| --- | --- | --- |
| 只读（默认） | 省略 options 或 `includeFunctionalProbes:false` | 只读 TCC 状态；Settings 挂载、timer 轮询、窗口 focus、普通 refresh **绝不执行像素捕获** |
| 主动像素探针 | `includeFunctionalProbes:true` | 真抓一次非空像素（"TCC granted 只说明系统记录了授权，不保证 WindowServer 放行像素"）；只能在显式权限返回或 Helper 成功重启后请求一次 |

（`zcode-cua-permission-broker.md:83-94`；`packages/ui/src/lib/cuaPermissionStatusStore.ts:255-282`）

**`request_access` 是只读快照，不直接触发系统弹窗**：成功时在 macOS 结果的 namespaced `_meta` 里带版本化状态
（`zcode.cua/request-access-status-v1`：`schemaVersion:1`、`platform:"darwin"`、`grantOwner`、
`accessibility: granted|stale|denied`、`screenRecording: unknown|granted|denied`）。
真正的引导链有五重门（official CUA authority + 精确工具名 + 严格 schema + 本地 desktop-continuous live event + 新鲜 Host 状态），
同一 `workspaceKey+sessionId+turnId+missingPermissions` 只确认一次；两项都缺时顺序固定 Accessibility → Screen Recording。
（`docs/cua/2026-08-25-cua-request-access-onboarding-spec.md:9-37`；`packages/zcode-cua/request-access-contract.d.ts:3-15`）

**权限错误不再自动分类（2026-08 决策，重要）**：ZCode **删除**了 `CUA_PERMISSION_REQUIRED`；
Helper/Broker 失败作为普通 MCP tool error 交模型判断。根因：`permission_denied` 同时承载 TCC、前台策略、
Windows UIPI、Linux XTest、无效 native 能力等不同失败，跨层细分仍不能证明真实权限缺口——
"删除自动分类比收窄分类更可靠"。只有官方 live `request_access` 的结构化 `_meta` 能触发一次 Host 确认流程。
（`docs/cua/2026-08-25-cua-permission-error-passthrough.md:9-26,51-65`）

### 7.2 broker token：两条注入路径、作用域与 sanitize

> **版本时差警告**：`docs/cua-permission-broker/zcode-cua-permission-broker.md` 里的 "bearer token" 叙事是**迁移前威胁模型**；
> 现行代码注释明确"当前 broker 是 identity 模式（socket + authority，无口令）"，遗留 `ZCODE_CUA_PERMISSION_BROKER_TOKEN` 仅作清洗兜底。
> 引用时以代码为准：`packages/shared/src/runtimeEnv.ts:64-76`。**本报告 §7.2 先讲 docs 的 token 模型（作为威胁分析），再讲现行 identity 模式。**

**docs 的 token 模型（历史威胁模型，仍然有借鉴价值）**：

- broker token 是"scoped to the target `zcode-cua` MCP server only"的 bearer credential；
  只能注入该 server 自己的 env/args，**不得在 agent 全局 env 保留/继承**，不得到达其他 MCP server、hooks、Bash/tool 子进程；
  泄漏后果是"任何同 agent 进程都能驱动持有 TCC 的 Helper"（confused deputy）。
- 两条注入路径：
  - **路径 A（desktop `session/create`）**：desktop 解析 `mcpServers` 时把 socket/token 注入目标 server 的 per-server `env`
    （在 `buildMcpStdioEnv` 之后 spread），**从不触碰 `process.env`**；
  - **路径 B（全局 CLI config）**：`~/.zcode/cli/config.json` 的配置随 CLI 子进程 `process.env` 到达；
    CLI 入口在 bootstrap/工具代码运行前把 socket/token **捕获进进程私有内存 store 并从 `process.env` 删除**，
    bootstrap 只读该 store 注入；没有捕获元组时**整个 omit `zcode-cua`**（fail closed，不裸跑）。
- **sanitize**：从所有子进程 env 与 tool-env-passthrough 中剔除 broker socket/token/refresh marker/pluginAuthority，
  以及代理/CA/OTEL 等敏感 env；`NON_TOOL_PASSTHROUGH_RUNTIME_ENV_KEYS` 防止经 tool-env 恢复通道泄漏。
  （`docs/cua-permission-broker/zcode-cua-permission-broker.md:25,34-35`；`runtimeEnv.ts:47-117`）
- **半组 fail-closed**：capture 只在 `socket && pluginAuthority` 同时存在时落快照；只出现一半时清空旧快照
  （"上游注入不完整或正在轮换，不能复用另一半"）。 （`runtimeEnv.ts:157-175`）

**现行 identity 模式（代码事实）**：

- 凭据结构：`{socket/pipe 路径, pluginAuthority（随机数）, 可选 refreshMarker}`；
  authority 的用途是**配置来源证明**（"谁能证明自己是 resolver 注入的官方 server"），不是连接口令。
- 恢复注入只发生在可信边界：bootstrap 仅对 name===`node_repl`、stdio、且属于官方 plugin 的 trusted server 名集合注入；
  `__zcode-plugin-host` 在 import server 模块**之前**校验 pluginId 官方、凭据成对、`ZCODE_CUA_NODE_REPL_HOST=1`，
  任一不符直接拒绝（避免先加载不可信模块再暴露凭据）。
  （`apps/zcode-cli/packages/bootstrap/src/mcp-config.ts:108-166`；`apps/zcode-cli/packages/cli/src/plugin-host-command.ts:23-29,83-106`）
- **官方 server 身份**：需要 resolver 注入的官方 plugin id **且** env 中 pluginAuthority 与本进程捕获值逐字相等；
  server 名、tool 名、manifest env 都可被仿造，不能单独作信任依据。
  （`apps/zcode-cli/packages/core/src/runtime/methods/mcp.ts:37-64`）

**另一套机制（不要混写）**：node_repl 内部 Worker→broker 的 **per-call token**（每次 `js` 调用由主线程 call ledger 签发，
`close()` 即失效；broker 只信 token 反查的 requestMeta）。这是"防 Worker 伪造"的机制，
与 Helper 的 broker 凭据是两条独立的链（§2.3）。

### 7.3 peer 验证、签名校验与 TOCTOU

**macOS（完整）**：

| 校验点 | 校验方 | 规则 |
| --- | --- | --- |
| launcher 代码要求 | Helper | `anchor apple generic and (identifier "dev.zcode.app" or identifier "dev.zcode.app.preview") and certificate leaf[subject.OU]="8A5X4JJ39T"`；不得放宽为仅 Team |
| `broker_info.bundle_id` | Host | 存在且 ≠ `dev.zcode.cua-helper` → `CuaHelperError`（warning 不够，TCC 会记错主体） |
| ready/health PID | Host | `ready.pid == child.pid`、`health.pid == child.pid`；`ready.socketPath == 本代 socket` |
| 启动前重验 | Host | 每次 `launcher.launch` 前重跑 `verifyInstalled`（签名/Team/bundle/version），缩小 TOCTOU 窗口 |
| peer 身份 | Helper | `SecCodeCheckValidity` 校验 launcher 签名 + `getpeereid`/`LOCAL_PEERPID` 走进程树；同 UID 但不在真实 ZCode 进程树的连接被拒 |

（`zcode-cua-permission-broker.md:26,33`；`cua-helper-installation.md:46-56`）

**Windows（现状：部分缺失，是安全债）**：

- Host 侧校验已实现：只接受自己 fork 的精确 child PID、自己 mint 的 pipe 名、ready/health PID 精确匹配、
  代际/终止 blocker/迟到消息忽略。
- **但 native peer-identity 门在 Windows 上"临时放开"**（`WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN`，
  打包版拿不到 dev 豁免、不放开必然启动失败），带一行 stderr 告警以无鉴权状态启动；
  **Windows 上任何本机进程都能连这条 named pipe**，等 M5 原生 peer 原语落地后才闭合。
  （`docs/cua/windows-source-development.md:93-101`；`producer/multi-session-cursor-overlay-spec.md:83-93`）

**对 PCCtrl 的直接警示**：ZCode 在 Windows 的 peer 门都还是缺口（缺 4 个 native 原语），说明
"靠 OS 帮你认证对端"在 Windows 上并不便宜。跨容器场景更是没有同机 peer 语义可用——
**必须自己用显式 token + ACL + 会话/进程校验**（§11.6）。

### 7.4 fail-closed 清单

从 `notes/01` §4.4 的 18 条中浓缩为可直接验收的清单（完整表见笔记）：

| # | 条件 | 必须行为 |
| --- | --- | --- |
| 1 | Helper 缺失/无法启动/拒绝连接/不健康 | 不向 Agent 注入 broker 凭据 |
| 2 | `broker_info.bundle_id` 不匹配 | 产品启动 fail `CuaHelperError`，不是 warning |
| 3 | 安装校验 / 启动前重验失败 | 不启动、不注入；不下载兜底 |
| 4 | 直接 CLI 跑全局 zcode-cua 但无 broker socket（macOS） | 无条件 omit，不裸跑 |
| 5 | zcode-cua matcher 漏匹配 | 漏匹配是 fail-open（危险），过度匹配 fail-closed（无害）——matcher 必须覆盖所有包形态 |
| 6 | 凭据只出现半组 | 清空快照，不注入 |
| 7 | 非官方 plugin / 非 shared node_repl host / 凭据不完整 | plugin-host 在 import 前拒绝启动 |
| 8 | 官方 server 缺 authority 或不等 | 不进入 official CUA 信任/保护路径 |
| 9 | remote/SSH/WSL/Docker/非 desktop-local | 不创建/安装/启动 Helper、不跑 reaper、不接权限观察 |
| 10 | subagent | provider 面移除官方 CUA server/Skill；执行面返回 isError，不进入 kill-switch/Broker/handler |
| 11 | Helper 退出/health 失败/未 ready | 只影响本次 CUA 调用（bounded retry → `broker_not_ready`），不触发 Agent recycle |
| 12 | transport_ready 之前 / 迟到 ready | 不发布 tuple；显式 stop 后忽略迟到消息 |
| 13 | 旧 child 未观察到 exit | 不得 fresh fork（termination blocker） |
| 14 | Windows 高完整性（UIPI）目标 | fail closed，不提权 |
| 15 | 像素探针失败/超时 | `screenCaptureProbeOk=false`，不把"TCC granted"当端到端可用 |
| 16 | 权限状态查询未显式要求 | 只读，绝不抓屏 |
| 17 | Screen Recording 预检失败 | **刻意 fail-open**：沿用 Helper 报告值，不把已授权用户推进无意义引导 |
| 18 | 插件关闭/产品 Helper 开关关闭 | 关闭后零足迹；只门控后续启动，不杀已有会话 |

（完整证据行号见 `research/zcode/notes/01-cua-architecture.md` §4.4）

### 7.5 kill switch、controller lease 与 stop

- **产品级 kill switch**：`ZCODE_CUA_PRODUCT_HELPER=0|false|off` 显式关闭；关闭后零足迹
  （no host object / orphan reaper / resolver injection / Helper process / TCC prompt），
  但开关是**启动时门控**：只写配置，不处理已运行 Agent/Helper。
- **会话级停止**：`agent.computerUse.stop()` 触发 kill switch；之后立即停止，不得换其它 UI 自动化技术。
- **单写者**：`ControllerLease` 全局唯一 controller（文件锁）；多会话可观察，但同一时刻只有一个会话能派发输入；
  `CONTROLLER_BUSY` 永不重试，返回 owner 让用户关闭那个会话。
- **输入 hold**：runtime 内部维护输入 hold，session 关闭/turn 结束要释放（避免按键/鼠标卡在按下态）。

（`zcode-cua-permission-broker.md:28-32`；`SKILL.md:252-258`；`notes/01` §5.5）

### 7.6 对 PCCtrl 的威胁模型（容器 → 网络 → Windows 执行器）

ZCode 的威胁模型是"**同机、同 UID、防 confused deputy**"；PCCtrl 的威胁面**多了两个**：
容器可能被攻破/被恶意插件利用，网络链路可能被监听/重放。因此不能只照搬"token 放 env"的做法。

| 威胁 | ZCode 的对应措施 | PCCtrl 必须补的 |
| --- | --- | --- |
| 容器内其它插件/进程盗用执行器凭据 | 凭据 scoped + sanitize + 不落全局 env | 凭据只存插件配置/Windows 凭据库（DPAPI）；MCP client 初始化后不注入子进程；日志脱敏 |
| 网络监听/中间人 | 不跨机器（无此问题） | TLS 或 VPN/内网；至少 token + 来源 IP/回环限制；禁止明文公网暴露 |
| 重放已批准的请求 | per-call token + 账本 + actionSent | 请求 nonce + 幂等键 + 短期 token；危险动作二次确认；审计 |
| 恶意模型/提示注入驱动危险操作 | `possibly_sent` 防重放、controller lease、KILL switch、前台可见提示 | 动作分级（只读/写/危险）、用户确认、速率限制、可中断（取消/panic key） |
| 屏幕数据外泄（截图进模型上下文） | 图片预算、按需截图、不抓全屏 | 明确"何时截、截哪个窗口、是否脱敏"；避免整屏进第三方 provider |
| 容器崩溃/重启后执行器被遗忘 | Helper 与 Agent 生命周期解耦、idle 自退 | 执行器常驻但无会话时零输入；会话租约超时释放；启动清理旧状态 |
| 插件供应链 | 官方插件签名/身份 + 官方 CUA 信任路径 | 固定依赖、校验 MCP server 身份、最小权限运行 |

（威胁→措施映射参考 `notes/01` §4.5、§6；§11.6 给出具体设计建议）

---

## 8. 生命周期与工程化

### 8.1 Helper 生命周期

**安装/校验**：见 §7.1（唯一来源、原子替换、每次启动前重验、entitlements 最小集、dev 模式用自签证书保持稳定 TCC 身份）。

**启动触发者**（懒启动是核心不变量）：

- Host 启动 / Agent spawn / session create **零 Helper IO**；凭据是纯数据（稳定 socket/pipe 名 + spawn 铸 authority）。
- 合法触发者只有三类：① Agent 首次 CUA 调用的 SDK `ensureBrokerAvailable`（唯一首调路径）；
  ② 用户显式动作（设置页 getStatus 拉起、权限授权流）；③ Host 维护动作（`restartAfterPermissionGrant` 等）。
- macOS：LaunchServices `open` + argv（`--socket/--launcher-pid/--ghost-cursor-overlay`）；
  Windows：Host `fork`（`ELECTRON_RUN_AS_NODE=1` + entry + `--socket` + `--parent-pid` + `--ghost-cursor-overlay`）。
- 两种形态：**托管 managed**（授权流/restart 用，随机 socket、launcherPid=Host、无 idle 自退）与
  **standalone**（默认懒启动，稳定 socket、无 launcherPid → **300s 无连接自退**）。
  （`docs/cua-permission-broker/2026-09-21-cua-helper-lazy-startup-spec.md:19-38,202-208`）

**健康检查与两阶段就绪**：

- `broker_info` 是身份/健康握手：产品启动等它返回；`bundle_id` 不匹配必须 fail；
  还包含 `authorization_subject` 诊断字段（kind/bundle_id/display_name/version/pid/app_bundle_path/
  code_signing_identifier/team_identifier/signature/stable_identity/warnings）。
- `probeHelperHealth` 返回 `{bundleId, pid}`；Host 用它做 `isExactHealthPid`（health.pid 必须等于自己 fork 的 child pid）。
- Windows 两阶段（`transport_ready` → `ready`）见 §6.7。

**重启/恢复/generation**：

- **生命周期 tail 是唯一线性化边界**：任何 fresh fork 必须排在上一代 exact child exit 之后；`kill` 只是请求，
  未观察到 exit 不得判死（termination blocker）。
- 恢复复用既有 tuple：`restartAfterCurrentStartPreservingTransport` 停旧 Helper 后用同一 socket/pipe 名重启，
  已有 Agent 不需要换凭据；**只有显式 stop 清 tuple**；异常退出保留 tuple 供按需恢复。
- 崩溃恢复路径：Helper 意外退出 → Host 置 handle=null、invalidate transport、`onUnexpectedExit` 只通知"状态失效"，
  **不回收 Agent**；下一次 CUA demand 复用已发布 tuple 恢复。
- 迟到的 `ready/transport_ready` 必须忽略（检查 `generation.stopped/externalStopEpoch/current.id`），不得复活旧 tuple。
- 历史 admission gate 已废弃（`CuaAgentAdmissionGate` 仅保留兼容导出，默认装配不注入、不阻塞 Agent spawn）
  ——Helper 不再拥有 Agent 生命周期。
  （`2026-09-02-helper-lifecycle-agent-runtime-isolation-spec.md:20-32,142-151,316-346`；
  `packages/services/src/cua-permission-broker/windowsCuaDevHelperHost.ts:48-98`）

**孤儿清理/idle/卸载/kill switch**：见 §6.7、§7.5。

### 8.2 broker/bridge/worker 生命周期

**node_repl 侧（真实代码）**：

- MCP frontend 按 `workspaceKey` 共享（`isolation:"workspace"`），同一 frontend 服务同 workspace 的多个 session；
  每次 `js` 用**一次性 Worker**（`new Worker(import.meta.url, workerData)`），结束 `terminate()`。
- 每次调用由主线程 **call ledger** 铸 token（32 字节 hex），`close()` 即失效；Worker 只拿本次 token 连本机 pipe。
- 请求上限 1 MiB、响应上限 32 MiB；`generation` 不匹配报 "Computer Use runtime binding is stale after kernel reset"。
- subagent 在 Worker bridge 与主线程 broker **双层拒绝**。
- `dispose()`：置 disposed、abort 所有 in-flight call、清队列、关两个 broker、dispose CUA runtime。
  （`node-repl-host/src/server.ts:211-309,312-348`；`cua-bridge.ts:36-76`；`cua-broker.ts:26-128`）

**CuaRuntimePool（shared host）**：

- 按 `workspaceKey + sessionId + remoteSessionId` 隔离；frame registry/action-state/kill switch/ControllerLease
  由 shared host 持有；Worker 重建不销毁 runtime。
- 两个 bridge（Browser/CUA）都在 host 侧有 broker；Worker 永远碰不到 Helper 的原始 socket/凭据。

**MCP server 生命周期（node_repl 进程本身）**：

- `main()`：`setNodeReplMcpProcessTitle` → **在 main() 生命周期内捕获 CUA runtime**（因为 bootstrap 前后 env 会被清，
  在 factory 内读 `process.env` 会永久判 CUA unavailable）→ `serveStdio`（`legacy:"reject"`，pin MCP 版本 `2026-07-28`）。
- 防挂死三层：① `unhandledRejection/uncaughtException` 降级 stderr（不让子进程死）；
  ② stdin end/close + SIGINT/SIGTERM 触发 shutdown（MCP SDK 的 stdio transport 不监听 stdin end/close，
  父进程异常退出时 runtime 会成孤儿）；③ `no-progress-guard`。
- `no-progress-guard`：按 session+turn 统计同码/同结果/调用量（5/10/30/80/200…），命中时在结果末尾追加
  "You may end this turn." 提示，**只授权结束、不要求换做法**——因为现场模型已在反复写"本轮结束"，
  换做法式文案只会把它推回去继续调用。缺 turn_id 不计、turn 变化清零。
  （`process-lifecycle.ts:20-79`；`no-progress-guard.ts:28-76`）

### 8.3 MCP server 生命周期与进程守护

**stdio 子进程清理**（ZCode 的真实工程教训）：

- 清理所有权：`adapters/src/mcp` 拥有 stdio 进程清理；每条 close 路径都必须 terminate SDK transport pid 的**整棵进程树**；
  必须幂等（重连/断开/adapter close/连接超时/连接失败都可能竞争）。
- Windows：`taskkill /PID <pid> /T /F`；macOS/Linux：能发进程组就发进程组，并额外收集已知子孙；
  HTTP/SSE 不做进程清理；清理失败只 warn，不阻断 SDK close。
- Windows **Job Object（原型）**：每 transport 独立 Job（不用全局 Job），`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，
  close 时 `TerminateJobObject+CloseHandle`，失败回退 taskkill；**attach 发生在 SDK spawn 之后**，
  覆盖不了 attach 前创建的后代，生产化需 `CREATE_SUSPENDED → AssignProcessToJobObject → ResumeProcess` launcher。
- 进程树清理超时：POSIX grace 250ms / force 750ms / verify 250ms / poll 25ms；Windows taskkill 2s；process lookup 1s。
  （`docs/windows-mcp-job-object-containment.md:7-31`；`adapters/src/mcp/process-tree.ts:4-9`）

**连接池与健康**：

- idle grace **30s**（无引用后延时关闭，timer.unref 不阻止进程退出）；
  `connectionKey = [serverName, scope, stableStringify(config)]`。
- lease 引用计数（`leaseId`），释放后到期关闭。
- **主动 ping 判活**：`client.ping()` 上限 5s；传输级失败判死并**原地重连**（保持 entry 身份）；
  并发刷新共享一次探测。这是从真实 bug 里长出来的——设置页 lease 命中旧 entry 后永远显示"已连接"。
- **HTTP/SSE 断连不会产生 close 事件**，只等 onclose 会让状态永久 connected——必须有主动 ping。
  （`docs/ui-mcp-status-refresh-issue.md:16-85`；`adapters/src/mcp/pool.ts:18,113-171`）

**工具目录缓存**（可选的性能优化，不是正确性组件）：

- LRU 32 entries、单 entry sanitized JSON ≤2 MiB、进程总预算 16 MiB、TTL 30 分钟；
  只缓存普通 **stdio**（HTTP/SSE 因缺少稳定 auth principal 不缓存）；
  只缓存 server/tool identity、name、description、schema，**禁止缓存 annotations/权限/instructions/OAuth 状态**；
  publish 走 entry-local generation CAS；只有"所有 enabled 可缓存 server 都新鲜"才叫 complete-hit。
- **铁律**：catalog cache 只负责"广告"，**tool call 必须在参数校验与权限判断前切到 live descriptor**。
  （`docs/mcp-async-tool-catalog-cache.md:41-69,424-528`）

### 8.4 超时、取消、并发、多会话

**超时分层**（真实值，可作为 PCCtrl 的初始参数）：

| 层 | 值 | 说明 |
| --- | --- | --- |
| node_repl `js` 工具 | 默认 60s，上限 120s | 模型可显式传 `timeout_ms`；预计 >30s 必须显式传"估算总时长 + 15s" |
| MCP 普通工具 | 默认 30s | 进度通知会**重置**超时（长任务持续上报就不会被打断） |
| Browser Use bridge | 30s | 超时反向下发 cancel，并标 `sideEffect=uncertain` |
| navigate | 10s | |
| Playwright 动作 | 3s | |
| UIA 作业 deadline | 4s | **只是等待预算**，不是取消/恢复期限；真实作业仍持资源 |

**取消**：`AbortSignal.any([mcpReq.signal, callController.signal, AbortSignal.timeout(timeoutMs)])`；
`cancelRequest` 命令与 `AbortController` 贯穿；cancelled 有独立错误码（不是 execution_error），
上层才能按取消生命周期清理请求。

**并发**：

- 同 session 的 `js` **串行**（`serialized(sessionKey, run)`），不同 session 并发。
- 物理输入 lane 串行（§6.1）；后台 UIA/PostMessage 不排队。
- 输入单写者（ControllerLease）；多会话只做观察/展示并发。
- **不要周期探活**：ZCode 删除了每 10s 的 Helper 健康探测——它会触发 Agent 反复 recycle 的工单教训；
  健康检查只发生在明确边界（调用/握手/设置页），并用缓存 TTL 控制频率。

### 8.5 投影与 UI（PiP / overlay / 指示器）

**所有权划分**：ZCode 只发布 product facts；`@zcode/zcode-cua`（producer）拥有 PiP panels/grouping/dismissal/
terminal badges/capture admission/native stacking/retry policy。
（`docs/cua/cua-pip-session-consumer-integration.md:3-6`）

**数据流**：`Renderer activeTaskId 变化 → Desktop Main focus router → FocusChanged(revision, windowId, sessionId)
→ window-scoped Local Host → producer pip-session client`；
`computer-use/operation-event → TurnStarted/TurnEnded/SessionClosed → producer PipSessionCoordinator`。

**只在 Desktop Main 持 ephemeral 事实**：`{windowId → activeSessionId}`、当前 focus 的 ZCode 窗口、
进程生命周期内单调递增 `focusRevision`；后台 renderer 可更新自己的 map，但只有**最前 ZCode 窗口**发布全局 focus。
Main 不承载 task/session 业务状态。

**Windows 不参与 PiP**：`cuaPipSessionService` enabled 条件写死 `platform==="darwin" && desktop-local`；
Windows 用户可见反馈是顶部操作指示器（消费 `computer-use/operation-event`，30s 兜底，ephemeral，不进 replayable/snapshot）。

**turn/session 清理与"丢首事件"问题**：PiP 的 `turn-started` 可能早于 Helper 启动（Helper 由首次 CUA 调用懒启动），
早期实现静默丢弃导致 PiP 永久停在上一轮完成态。修复：只缓存**一条** pending `turn-started`（新 turn 顶掉旧的），
凭据一可用立即补发，另有 250ms 有界轮询、30s deadline；协调器按 `sequenceNumber` 判重，补发幂等；
同一 turn 的 `turn-ended/session-closed` 若先到则作废缓存。
（`packages/services/src/cua-permission-broker/cuaPipSessionService.ts:59-94,136-187`）

**对 PCCtrl 的启发**：如果做"正在操控电脑"的用户提示，**展示层与会话状态必须解耦**（sideband，不进快照/恢复）；
展示失败要 fail-hidden（隐藏失败就销毁，不能向用户撒谎）；不要从工具名/代码模式推断状态，要锚在可验证的事件上。

### 8.6 对 Windows 服务的镜像设计建议

【推断，基于 ZCode 上述机制 + PCCtrl 跨容器约束】

| 关注点 | ZCode 的做法 | PCCtrl 的建议 |
| --- | --- | --- |
| 进程形态 | Helper 懒启动、可 idle 自退；Host 有代际与 tail 线性化 | **常驻进程**（容器无法 fork Windows 进程）+ 启动时单实例 mutex + generation/epoch；idle 只做指标，不自动退出 |
| 权限主体 | macOS 签名 Helper / Windows 安装包 Helper | 跑在**交互式用户会话**（非 Session 0/非管理员）；UIA/SendInput/WGC 都依赖交互桌面 |
| 健康 | broker_info + 按需 probe；无周期 watchdog | `/healthz` 或 MCP `ping`；只在握手/调用边界检查；返回 `{version, session, integrityLevel, capabilities, permissions, uptime}` |
| 恢复 | tuple 复用、迟到消息忽略、termination blocker | 同思路：重启不改凭据/端口；显式 stop 后旧 ready 不得复活；崩溃后**不自动重放**可能已执行的动作 |
| 清理 | taskkill /T /F + Job Object 原型 | 启动清理旧进程/临时文件；**Job Object 先建后起**（CREATE_SUSPENDED→Assign→Resume） |
| 日志 | debug/info/warn/error 分级；不写凭据/用户内容 | 审计日志记录"谁、何时、哪个动作、结果、是否 possibly_sent"；截图/文本内容默认不落盘 |
| 更新 | Helper 随 App 更新，版本不匹配 fail-closed | 执行器声明协议版本；容器插件与执行器版本不匹配 → 明确错误，不降级执行 |
| 停止 | kill switch + controller lease + 输入 hold 释放 | 本地 panic/停止按钮；取消在途调用并释放输入 hold（keys up/mouse up） |

---

## 9. MCP 工程实践（ZCode 的 MCP 生态 + 对 AstrBot 的映射）

### 9.1 ZCode 的 MCP 宿主能力

ZCode 对 MCP 的宿主能力（真实代码 + 规格混合）：

| 能力 | 事实 | 证据 |
| --- | --- | --- |
| 传输 | **stdio** 主路径；**Streamable HTTP**（配置 `type:"http"`，legacy `type:"remote"` 归一化为 http）；**旧 HTTP+SSE** 仍支持但固定 legacy era | `apps/zcode-cli/packages/adapters/src/mcp/index.ts:1948-2000`；`packages/shared/src/mcp.ts:18-31,311-347` |
| 配置分层 | 来源 `mcp / zcodeagentmcp / claudeclimcp / geminiclimcp / codexclimcp / opencodemcp`；作用域 `common/user/workspace`；格式 json/toml | `packages/shared/src/mcp.ts:18-27,80-94` |
| 配置兼容 | 归一化 `environment→env`、`http_headers→headers`、`enable→enabled`、`type:"remote"→"http"`；按 server 粒度容错（单 server 非法只跳过并记 diagnostics） | `docs/mcp-config-compat-diagnostics.md:10-31` |
| 双版本协商 | `protocolVersion?: "legacy"|"auto"|"2026-07-28"`；不配 = auto（stdio/http）；`sse` 直接 legacy；auto 先 `server/discover`，只有 transport-specific legacy evidence 才回退 `initialize` | `packages/shared/src/mcp.ts:193,202`；`docs/mcp-dual-era-version-negotiation.md:44-71,109-119` |
| probe 预算 | stdio probe 是 disposable sibling process；上限 5s 且不超过总连接预算一半；pin 模式例外 | `index.ts:113-115,2399-2421` |
| OAuth | 两阶段：Phase 1 纯 `AuthProvider`（被动连接零 listener/零 discovery/零 DCR）；Phase 2 交互授权编排（独立进程锁/leader-follower）；caller 15s vs 授权事务 300s | `docs/mcp-oauth-two-phase.md:23-42` |
| 工具目录缓存 | TTL 30 分钟 / LRU 32 / 16 MiB 总预算；只缓存 stdio；cache 只做"广告"，调用前必须切 live descriptor | `docs/mcp-async-tool-catalog-cache.md:41-69` |
| 结果预算 | 图片 base64 >200 KiB 不 inline；MCP Apps 资源每次累计 **8 MiB** 总量检查（超限整次拒绝，不截断）；未知/空/可执行 MIME 一律拒绝 | `docs/mcp-tool-output-budget.md:21-42`；`docs/mcp-apps-resource-mime-spec.md:5-35` |
| 错误 | 业务失败用 in-band `isError:true`（HTTP 200 也可能是失败）；传输级失败 throw；超时区分"预算耗尽"与"对端/网络断"；默认 tool 超时 30s，进度通知重置超时 | `index.ts:1186-1270` |
| 进程/连接 | 连接池 idle 30s、lease 引用计数、ping(5s) 判活原地重连、按 `workspaceKey` 隔离；stdio 进程树清理 | `adapters/src/mcp/pool.ts:18,113-171,640-663`；`docs/windows-mcp-job-object-containment.md:7-31` |

### 9.2 `node_repl` MCP server 的真实实现

`@zcode/node-repl-host`（`apps/zcode-cli/packages/node-repl-host/`）是真实 TypeScript 源码，几个值得学习的工程点：

1. **单工具 MCP server**：`new Server({name:"node_repl", version:"0.7.0"},{capabilities:{tools:{}}, instructions})`；
   只注册 `js`；`inputSchema` 复用 `@zcode/contracts` 的 `JsInputJsonSchema`（"禁止 MCP server 另写一份会漂移的 JSON Schema"）。
   （`src/server.ts:91-98,231-236`）
2. **模型合同与运行时合同分离**：`tools/list` 强制 `title`（1..120，用户可见、禁实现术语），
   但执行层继续接受旧 provider/历史回放的 code-only 输入（`title` optional）。（`src/server.ts:66-73`）
3. **serverInfo 版本是"宿主契约版本"**：注释记录每次升版理由（0.5.0 抽出独立 seed、0.6.0 工具面收敛到只剩 js、
   0.6.1 subagent 允许 Browser Use、0.6.2 无进展提示、0.7.0 CUA bridge 变化），与插件版本解耦。
   （`src/tool-contract.ts:5-22`）
4. **会话串行**：同 `session_id` 的 `js` 串行（`serialized`），不同 session 并发；无 session 用 `__unscoped__`。
5. **结果组装**：`toMcpRunResult`（§3.6/§8.2）；图片在前、同图去重、多帧取最新、error 只回 message、始终非空。
6. **防挂死/防孤儿/防刷**：process-lifecycle + no-progress-guard（§8.2）。
7. **CUA runtime 捕获时机**：`captureComputerUseRuntimeFromEnvironment()` 在 `main()` 里执行，
   否则 "官方 plugin host 在 main() 返回后会清除短暂恢复的 Helper 凭据" → node_repl 永久判 CUA unavailable。
   （`src/server.ts:395-403,453-462`）

### 9.3 插件 MCP、外部导入与命名空间

- **插件 MCP 统一命名** `plugin:<pluginName>:<serverName>`；定义来源两个：插件根 `.mcp.json` 与 manifest `mcpServers`
  （manifest 覆盖）；变量替换 `${...}`；env 自动注入 `ZCODE_PLUGIN_ROOT/ZCODE_PLUGIN_DATA/ZCODE_PROJECT_DIR`；
  **插件身份 `ZCODE_PLUGIN_ID` 由 resolver 权威覆写**（防第三方伪造 official CUA 身份）。
  （`adapters/src/plugins/mcp.ts:19,22-29,67-69,219-235`）
- **session runtime merge**：`{...pluginMcpServers, ...explicitUserMcpServers}`——先注入插件 MCP，再让用户配置覆盖同名；
  修复前把 runtimeConfig 当完整覆盖，导致插件 MCP 在用户 MCP 存在时整体丢失。（`docs/plugin-mcp-session-runtime-merge.md:5-22`）
- **宿主保留名**：`node_repl` 在合并后覆盖同名项；第三方/inline 插件强制 `plugin:<plugin>:<server>` 不能冒充
  `mcp__node_repl__*`；MCP server 禁用/失败时**不回退**暴露 core 内建 `js`（"避免模型看到两套状态所有者"）。
- **官方 MCP 鉴权**：不发静态 token，宿主按当前登录身份**每请求解析** `Authorization: Bearer <zcode JWT>` +
  `X-Bigmodel-Authorization: Bearer <MaaS JWT>`（+ TEAM 头）；in-flight 去重、不做时间缓存；stdio 形态把身份头放
  `_meta` 而不是 env。（`services/src/official-mcp/officialMcpCredentials.ts:13-17,422-500`；`plugins/mcp.ts:200-203`）
- **外部 Agent 导入**：把 Claude Code/Codex/OpenCode 等配置合并进 ZCode 配置；同名 server 保留现有配置、
  外部标 skipped；不扫描 Gemini 目录；TOML 只读、写入永远是 ZCode JSON。
  （`docs/external-agent-mcp-import.md:14-44`）

### 9.4 AstrBot 侧现状（MCP 客户端 / 内置电脑工具）

> 证据基线：`/d/tmp/AstrBot-src`（AstrBot master@2026-10-31，`__version__ = "4.29.3"`）；
> 本节的目的是判断"PCCtrl 用 AstrBot 内置 MCP 客户端接 Windows 执行器"是否可行。

**AstrBot 的 MCP 客户端能力（正面结论：够用）**：

- 配置形态：`mcpServers` 字典（`DEFAULT_MCP_CONFIG = {"mcpServers": {}}`），每个 server 一个配置对象；
  （`astrbot/core/provider/func_tool_manager.py:29,152-154,546,576`）
- 传输：`url` + `transport`（`streamable_http` 或旧 `sse`）；`headers`；`timeout`/`sse_read_timeout`/`session_read_timeout`；
  没有 `url` 则 stdio（`command/args/env`），并有 Windows stdio 可执行解析兼容。
  （`astrbot/core/agent/mcp_client.py:466-560,617-650`）
- 连接前 **quick test**：streamable_http 用 POST initialize 探活；sse 用 GET；返回 HTTP 状态与错误。
  （`mcp_client.py:278-345`）
- 失败重连：`call_tool_with_reconnect`；MCP 工具注册为 `MCPTool(FunctionTool)`，调用时带 `context.tool_call_timeout`。
  （`mcp_client.py:727-821`）
- **工具名被 sanitize 成 `[A-Za-z0-9_-]`**（MCP 原名保留在 `mcp_tool.name` 供实际调用），
  且**没有 server 前缀**——多个 MCP server 工具名冲突时需要自己保证唯一。
  （`mcp_client.py:794-808`）
- 初始化超时：默认 180s、上限 300s（`ASTRBOT_MCP_INIT_TIMEOUT`）；启用超时同理（`ASTRBOT_MCP_ENABLE_TIMEOUT`）。
  （`func_tool_manager.py:29-35,94-123,302-312`）
- 工具权限：MCP 工具走统一的 per-tool 权限代理（`_check_tool_permission`，默认权限来自 `tool_permissions` 配置），
  管理员/成员可分别控制。（`func_tool_manager.py:215-260,453-468`）
- 配置入口：Dashboard 的 MCP 管理 API/服务（`astrbot/dashboard/api/tools.py`、`dashboard/services/tools_service.py`），
  用户可在 WebUI 添加/编辑 MCP server。

**AstrBot 自带的"电脑操作"工具不是给宿主机的**：

- `astrbot/core/tools/computer_tools/cua.py` 定义 3 个内置工具：
  `astrbot_cua_screenshot` / `astrbot_cua_mouse_click` / `astrbot_cua_keyboard_type`
  （`cua.py:53,111,148`）。
- 它们操作的是 AstrBot 自己的 **CUA 沙箱 booter**（`booter_type == "cua"` → `CuaBooter(image, os_type, ttl, ...)`，
  有 `cua_idle_timeout` 回收），即"容器/远端沙箱里的 GUI"，**不是用户的 Windows 桌面**。
  （`astrbot/core/computer/computer_client.py:36-88,567-667`；`cua.py:32-49`）
- 因此 PCCtrl 不能复用这些工具直连 Windows；但它们可以作为"工具命名/参数风格"的参考。

**结论**：AstrBot 内置 MCP 客户端**可以直接消费一个 Windows 侧的 Streamable HTTP MCP server**，
不需要写插件（除了配置）；如果需要对工具面做深度定制（动态工具、确认流程、审计、会话绑定），
则写 AstrBot 插件并用插件自带的 HTTP client / Python MCP SDK 实现更可控（见 §11.1 方案对比）。

### 9.5 推荐连接方案：容器内 AstrBot ↔ Windows MCP 服务

【推断+方案设计，供 §11 细化】

```text
┌──────────── Docker 容器（Linux）────────────┐        ┌──────── Windows 主机 ────────┐
│ AstrBot（模型编排 / 插件 / MCP client）      │  TCP   │ PCCtrl 执行器（常驻进程）      │
│   mcpServers: {                              │ ─────► │  - Streamable HTTP MCP server │
│     "pcctrl": {                              │ HTTPS/ │  - token 校验 / 来源校验      │
│       url: "http://host.docker.internal:8765/mcp",     │  - observe/act/verify 工具     │
│       transport: "streamable_http",          │  内网  │  - UIA/PostMessage/SendInput  │
│       headers: { Authorization: "Bearer ..." }│        │  - 截图/AX 采集                │
│     } }                                      │        │  - 审计/健康/停止              │
└──────────────────────────────────────────────┘        └───────────────────────────────┘
```

**关键设计点（对应证据）**：

| 点 | 建议 | 依据 |
| --- | --- | --- |
| 传输 | **Streamable HTTP**，不要 stdio（跨容器不可用），SSE 只作兼容兜底 | AstrBot 支持两种（`mcp_client.py:466-560`）；ZCode 把 SSE 归 legacy（§9.1） |
| 地址 | 容器用 `host.docker.internal`（Docker Desktop）或宿主 LAN IP + 端口映射；**不要硬编码** | 容器网络事实；ZCode 配置分层思想（`shared/src/mcp.ts`） |
| 认证 | 执行器强制 bearer token（初始化/每次请求都可校验）；token 只存配置/凭据库，不进 tool 参数、不进日志 | ZCode 的 scoped 凭据 + sanitize（§7.2）；AstrBot 支持 `headers`（`mcp_client.py:490-500`） |
| 监听面 | 只监听回环或受控网卡；公网必须 TLS + 额外网关；**不要 0.0.0.0 裸奔** | ZCode 的同机威胁模型不覆盖网络，须自补（§7.6） |
| 健康 | 实现 MCP `ping` 或 `/healthz`；AstrBot 有 init/enable 超时与重连，但**不要只依赖它** | ZCode 教训：HTTP/SSE 无 close 事件，要主动 ping（§8.3） |
| 工具数 | 控制在 10 个以内、名字唯一（AstrBot 会 sanitize 且无前缀） | `mcp_client.py:794-808`；ZCode 工具面成本论证（§3.4） |
| 超时 | 每个工具声明超时；长动作返回"已受理 + 可查询"；AstrBot 侧 `tool_call_timeout` 可传导 | `mcp_client.py:820`；ZCode 分层超时（§8.4） |
| 图片 | MCP image block 回传截图；注意 provider 网关对图片位置/大小的限制 | §3.6、§4.6 |
| 失败语义 | 工具错误用 MCP `isError` + 结构化文本（`action_sent/retry`）；不要用 HTTP 500 表达业务失败 | §5.4-5.5 |
| 备选 | 若需要动态工具/确认/审计，写 AstrBot 插件直接实现 MCP client 或 HTTP 调用 | AstrBot 插件可注册 `FunctionTool`（前序研究 `research/astrbot/`） |

**已知坑清单（AstrBot 侧）**：

1. `mcpServers` 的 key 是 server 名；工具名不带服务器前缀，**多 server 重名会互相覆盖/歧义**。
2. AstrBot 的 `_normalize_mcp_input_schema` 会修复"属性级 required:true"这类非标准 schema；
   但**不要在工具 schema 里用非 2020-12 的写法**，否则不同客户端行为可能不同。
   （`mcp_client.py:340-390`）
3. AstrBot 的 MCP 初始化默认 180s、上限 300s；执行器启动慢会导致 AstrBot 启动被拖慢——让执行器**快速 ready**，
   重活懒加载。
4. 容器内 DNS/网络对 `host.docker.internal` 的支持取决于 Docker 配置（Linux 上需 `--add-host=host.docker.internal:host-gateway`，
   Docker Desktop 默认支持）；文档必须写清。
5. AstrBot 的 MCP 工具会经过统一权限代理；给执行器工具配好 admin/member 权限，避免普通用户驱动桌面。

---

## 10. 真实完整实现的参考：Browser Use

> 为什么把浏览器单独写一章：它是**本仓库里唯一完整、可运行的真实自动化实现**（CUA 原生层闭源）。
> 它示范了"模型工具面 → 命令协议 → 执行器 → 平台 API"的端到端工程结构，其中大量设计可平移到 Windows 桌面操控。

### 10.1 分层架构与命令协议

**分层**（真实代码）：

| 层 | 位置 | 职责 |
| --- | --- | --- |
| 模型面 | 同一个 `node_repl` `js` 工具 | 写 JS 调 `agent.browsers.*`（对象模型：Browser/Tab/playwright/cua/dom_cua） |
| 客户端 SDK | `browser-use-plugin/src/browser-client.ts` → `@zcode/core/browser-client` | 把对象模型调用编译成 `BrowserCommand` |
| 共享契约 | `packages/shared/src/browser-use/{commands,command-metadata,result,snapshot}.ts` | Zod 判别联合（51 个 method，`.strict()`）、结果/快照 schema；agent 与 desktop **同源** |
| 宿主桥 | `host/browserControlMainBridge.ts` | postMessage + pending map + 超时/取消/副作用标记 |
| 执行器 | `packages/desktop/src/main/browserView/` | 三层：`browserCommandExecutor`（总分发 134 行）→ page/interaction handlers → Playwright executor |
| 载体 | Electron `<webview>` guest + `webContents.debugger` | CDP 直通；截图/输入/快照都落到这一层 |

**为什么"单一命令通道 + 判别联合"优于"一堆 MCP 工具"**：

- 模型只看到一个 `js` 工具，上下文成本恒定；命令的扩展不影响工具面。
- 契约单源（Zod）让 agent/桥/executor 三者共用一个 schema，从结构上消灭漂移。
- 每个命令带**独立 context 结构**（不是塞进每条 command）：
  `{workspaceKey, sessionId, tabId?, requestId, clientMode}`——`requestId` 用于去重（`duplicate_request_id`），
  `clientMode` 区分桌面连续/手机可重放链路。
- 结果是一个"胖结果"：`{ok, scope?, state?, snapshot?, image?, tabs?, value?, element?, dialog?, error?, meta?, elapsedMs}`；
  **executor 不 throw、只返回结构化结果**（跨进程桥的硬要求：异常会丢结构）。
- 9 个稳定错误码 + `sideEffect: "none"|"uncertain"`：
  `backend_unavailable / capability_unsupported / duplicate_request_id / ref_not_found / navigation_blocked /
  timeout / renderer_unreachable / cancelled / execution_error`。
  **错误即指引**：未实现的方法在 message 里列出当前真正支持的方法；locator 超时直接告诉模型
  "Do not retry the same locator. Take a fresh domSnapshot()…"。
  （`packages/shared/src/browser-use/command-metadata.ts:72-157`；`browserCommandExecutor.ts:36-133`；`result.ts:95-133`）

**执行器只依赖窄接口**（最值得抄的解耦范式）：

```ts
// browserCommandTypes.ts:1-24（意图注释：便于 stub 单测，无需真 Electron webContents）
interface ControlledViewWebContents { loadURL; getURL; getTitle; canGoBack; canGoForward; goBack; goForward; reload; executeJavaScript }
interface ControlledViewCdp { send(method, params?, sessionId?): Promise<unknown> } // webContents.debugger.sendCommand 直通
interface ControlledView { webContents; cdp; captureViewportScreenshot?; normalizeScreenshotToCssPixels?; resizeScreenshotToCssPixels? }
```

生产注入 Electron，测试注入 stub，headless 后端注入 Playwright——**执行器不依赖宿主框架**。
PCCtrl 的 Windows 执行器可以照此定义为 `DesktopBackend{nativeWindow, uiaTree, input, capture, clipboard}`，
生产注入 UIA/SendInput，测试注入 fake。

### 10.2 snapshot + ref 的观察模型

**双通道快照**（`browserCommandScripts.ts:23-65`；`snapshot.ts:17-77`）：

| 通道 | 选择器 / 上限 | 用途 | 字段 |
| --- | --- | --- | --- |
| `elements` | 可交互元素；默认 200 | 可点/可填，分配 `ref` | ref/tag/role/name/text/value/disabled/checked/selector/xpath/rect/inViewport/parentRef/attributes |
| `dom` | 语义节点；默认 300 | 只读懂页面，不占动作预算 | tag/depth/inViewport/ref?/role?/name?/text?/attributes? |

关键设计：`dom` 在 schema 里**排在 elements 之前**——Zod 按字段序重建对象，大页面被截断时模型先看到语义而不是 selector 噪声。
（`snapshot.ts:64-68`）

**ref 生命周期**：

- ref 按 DOM 序分配 `e1,e2…`，同时写页面侧 `window.__zcodeRefs = Map<ref, Element>` 与闭包 `WeakMap<Element,ref>`（算 parentRef）；
- 解析时 `scrollIntoView` 后取元素中心（**不用 snapshot 里的 rect**——可能已滚动失效）；
- 导航/重新 snapshot 后 ref 失效；约定"**每次交互前重新 snapshot**"；
- 找不到返回结构化 `ref_not_found` + "take a fresh snapshot() first"，不静默兜底；
- `elementInfo(x,y)`：`document.elementFromPoint` → 反查元素并现分配 `p1,p2…`——**把视觉坐标路径接回结构路径的桥**。

（`browserCommandScripts.ts:51-92,161-193`；`docs/browser-use/2026-07-07-interaction-core-snapshot-click-type-spec.md:21-44`）

**"观察-动作-验证"纪律**（已写进工具文档/错误消息）：

- 每个逻辑批次先单独一次调用返回完整 `tabs.list()`，下一步再绑定；"never select [0] merely because the list is non-empty"；
- **一次观察周期最多一个状态变更动作**；"An unchanged source-tab URL does not prove the click failed"；
- 可能开新窗时**同一 cell** 内同时读受控 tabs 与 user tabs，返回 `{controlledTabs,userTabs}`；
- 例行等待 ≤3000ms；"A timeout is a signal to refresh the snapshot and rebuild the locator, not to retry it unchanged"。

（`browser-use-plugin/docs/{overview,workflow,browser-troubleshooting}.md`）

### 10.3 截图、坐标与缩放

**截图不是一次 `capturePage`**，而是"稳定化流水线"（`browserCommandPageHandlers.ts:235-304` 等）：

1. **宿主面直读**：无 clip/non-fullPage 且宿主提供 `captureViewportScreenshot` → 直接取合成图
   （动机：Desktop 生产实现从 main 进程直读 guest surface，**避开 Windows 下 CDP 对小 surface 的平铺问题**）。
2. **CDP `Page.captureScreenshot`** + **CSS 像素归一**：只读 PNG 头解析 IHDR 宽高，按当次 clip **动态反推**缩放，
   最多重抓一次，再交宿主降采样到 CSS 目标；质量上界 `MAX_SCREENSHOT_QUALITY_SCALE=2`、最小收益 1.25、
   最大边 4096、最大像素 16,777,216；首帧已大于目标则直接降采样（避免 Chromium 渲染阶段丢文字细节）。
   **价值：不假设固定 DPR，每次用 clip 反推。**
3. **截图表面握手**：main 与 owner renderer 的 `prepare → ready(viewport,surfaceScale) → capture → release`；
   同一 guest 并发截图复用一份 renderer 表面；不同 guest 必须串行；prepare 超时用共享常量；activity watchdog 35s。
4. **activity 泵**：Electron `backgroundThrottling=false` 会唤醒同窗全部 WebContents；改为按 owner 窗口维持
   **可逆 capturer 计数**（1×1 探测 capture 当"泵"），结束后由 Chromium 原生 capture lifecycle 恢复节流，**同窗其他 guest 不被唤醒**。
5. **瞬态重试**：只把 `UnknownVizError` 视为瞬态（Viz surface 建立前的 CopyFromSurface），100ms 退避/2s 预算；
   其它失败（guest 销毁/跨窗口）致命。
6. **在途/放弃保护**：同 tab 同时只允许一次截图；CDP 截图没有单请求取消 API，超时后底层可能仍挂着，
   按 tab 计"已放弃未结算" capture 上限 **3**，达上限快速失败并提示重开 tab（真实事故：隐藏窗口等帧 30s 后被判死但底层永不 settle，
   导致后续截图全部 0ms 秒拒）。
7. **轮尾自动截图**：仅在"本轮成功调用过关联具体页面的 Browser API"时，正常完成边界自动截当前 active tab 作为独立图片块，
   **不进模型上下文**；压缩状态机 PNG→优化 PNG→JPEG(80/60/40/20)。

**坐标契约**（`docs/browser-use/2026-09-07-desktop-zoom-coordinate-spec.md:7-61`）：

- **统一单位：CSS 像素**——viewport、截图像素、模型坐标、DOM 坐标同一套；
- 桌面 zoom 补偿**只在 renderer**：webview 布局 × `1/desktopZoom` 再 `scale(desktopZoom)`；
- CDP metrics 只固定 CSS viewport 与截图 DPR，**不带 scale**：`{width,height,deviceScaleFactor:1,mobile:false,dontSetVisibleSize:true}`；
- 截图选择：Fit<1 或 Desktop zoom>1 → `guest.capturePage()`（native）再归一到 CSS 尺寸，否则 CDP capture；
- **反例**：把缩放补偿放进 CDP metrics `scale`（per-session 状态），命令空闲 detach 后被清掉 → 页面"操作期间变大、空闲变回"。
  教训：**状态不能放在会被生命周期事件清空的会话态里**。

### 10.4 会话隔离、CDP 生命周期与权限治理

**scope/generation（真实代码）**：

```ts
sessionScopeKey = [browserId, browserGeneration, windowId, workspaceKey, remoteSessionId ?? "", sessionId, clientMode].join("\u0000")
scopeKey        = sessionScopeKey + "\u0000" + (controlScopeId ?? "")
ownedBySession  = windowId + workspaceKey + remoteSessionId + sessionId
```

- "会话只读视图与实例控制权是两个边界"；`execute` 入口先校验 backend identity（browserId/generation 必须等于 owner 当前值，
  否则 `backend_unavailable`）——**身份校验从 Host 移到资源 owner**，兼容请求不能因桥重连绕过代次校验。
- `requestId` 在 bridge 与 manager 双端去重；每请求一个 `AbortController`；`cancelRequest` 返回是否命中；
  `turnEnded` 只取消请求/释放 CDP，**不关 tab**；`closeSession{closeTabs?}` 才关。
- `isSideEffecting(command)` 集中登记有副作用的命令（含 `evaluate`），供超时/取消语义使用。

**guest 生命周期状态机**（`browserGuestManager.ts:139-195`）：
`guestGeneration`（拒绝 stale guest）、`guestLifecycle(detached/attached/detaching/destroyed)`、
`pendingCdpCommands`（teardown 等归零或超时）、`guestCdpRestoreFlight`（re-attach 恢复屏障）、
`guestTeardownFlight`（single-flight）、debugger/crashGuard cleanup。

**CDP 懒释放 + 恢复屏障**：

```text
turnEnded / closeSession / 命令空闲 1.5s → debugger.detach()   （避免 guest 销毁时 api::Debugger 隐式析构 → UAF）
下一命令 → ensureGuestCdpAttached → 恢复会话：
  attach("1.3") → Page.enable → Emulation.setDeviceMetricsOverride（viewport 重放）→ 失败回滚
→ 之后才真正 sendCommand
```

背景事故：v3.9.1 连续自动化主进程 native access violation，日志 "detachGuest cdp still attached on destroyed guest"——
**destroyed 之后再 detach 太晚**，必须有"拒绝新 dispatch → abort tab 请求 → 有界等 pending → detach 确认 → ACK → 才卸 <webview>"的 teardown 顺序。

**权限治理（可整段复用为"默认拒绝 + 用户弹窗 + 审计"范式）**：

- 问题：内置浏览器是标准 Electron `<webview>`，持久分区未注册任何 permission handler → **Electron 默认自动批准所有权限**。
- 决策链：无害白名单静默放行（clipboard-sanitized-write/fullscreen/pointerLock/mediaKeySystem）→ inflight 去重 →
  同意记录（内存 sessionAllow → 持久 store）→ 无记录弹窗 → dismiss/超时本次拒绝不记忆；
  敏感权限（camera/mic/clipboard-read/geolocation/notifications/midi/…）弹窗；display-capture 源选择器；
  HID/USB/Serial 设备选择器；`openExternal`/unknown **fail-closed**。
- 弹窗在面板内渲染，**面板不可见时 20s 超时自然拒绝**——"agent 后台跑的页面天然被拒"；
- origin 归一化坑：request 写标准 origin，Electron check 回调传入**带尾部 `/`** 的 origin，直接比对会 miss →
  两侧都过 `URL.origin`；
- 审计固定行：`[embedded-browser-permission] decision=… origin=… permission=… source=…`（info 级）；
- agent browser-use **零影响**：截图走 `Page.captureScreenshot`、粘贴走合成事件，不触发 permission。

（`embeddedBrowserPermissionPolicy.ts:10-49,142-206`；`docs/browser-use/2026-10-08-embedded-browser-permission-governance-spec.md:9-177`）

### 10.5 可迁移到桌面操控的经验

**可直接迁移**（`notes/06` §7.1 十条，摘要）：

1. **单一命令通道 + 判别联合 + 运行时 schema**：工具面收敛、契约单源、扩展不影响模型上下文。
2. **结构化结果 + 稳定错误码 + `sideEffect`**：Windows 输入不可回滚，重复执行代价高 → `sideEffect:"uncertain"` 必抄；
   bridge 超时后反向 `cancelRequest` 同抄。
3. **执行器只依赖窄接口**（`DesktopBackend`）：生产注入 Win32/UIA，测试注入 fake。
4. **snapshot+ref 观察模型**：有界语义树（读）+ 带 ref 可动作元素（做）双通道；动作前重取；失效返回 `ref_not_found` + 提示。
5. **坐标与结构双路径 + 反查桥**：截图坐标与元素句柄并存，提供 `elementInfo(x,y)` 式反查。
6. **分层超时预算**：外层工具 deadline > 中间桥 > 单动作；桥层超时要能反向 cancel；不要让两层 deadline 竞争。
7. **scope key + generation 校验**：Windows 端对应 `(sessionId, windowHandle, desktopId)`；窗口重建/分辨率变化时递增 generation。
8. **观察-动作-验证纪律写进工具描述与错误消息**：一次观察只做一个状态变更动作；失败不重放而是重新观察。
9. **截图稳定化三件套**：等待/泵 + 只重试已知瞬态 + 尺寸/像素归一（不假设 DPR）。
10. **权限默认拒绝 + 用户弹窗 + 审计**：白名单静默/敏感弹窗/未知 fail-closed/每条决策一行审计/超时即拒绝。

**需改造后迁移**：

- **teardown 顺序**：Windows 没有 Electron debugger，但"资源销毁前先断开所有订阅/句柄、销毁 single-flight + fail-closed"完全适用
  （UIA 事件订阅、钩子、截图 surface 句柄都类似 CDP session）。
- **CDP 懒 attach + 恢复屏障**：对应"惰性初始化 COM/UIA/输入注入器 + 恢复后等状态就绪再派发"；
  核心是**显式屏障而非 fire-and-forget**，以及"状态被外部清空后必须可重放"（zoom 反例）。
- **DPI 归一**：Windows 对应 Per-Monitor V2；要抄的是"统一到一个逻辑坐标系（DIP），输入/截图都换算到该系，缩放补偿只在一处做"。
- **tab 占用预算（32）+ 受保护状态 LRU**：多窗口/多应用会话同样会累积；可借用其"受保护状态 + 最近活动 LRU"策略。

**浏览器特有、不可迁移**：`__zcodeRefs`/elementFromPoint/scrollIntoView/DOM selector/CSS px 坐标；
`ClipboardEvent+execCommand` 虚拟粘贴；Playwright locator/ARIA 树/`Page.captureScreenshot`/`Emulation.setDeviceMetricsOverride`；
导航 URL allowlist/iframe target。Windows 对应物是"允许操控的进程/窗口白名单"。

**值得后续开发精读的文件**（性价比排序）：`packages/shared/src/browser-use/commands.ts` + `command-metadata.ts`（契约模板）→
`browserCommandExecutor.ts` + 两个 handlers（三层分工）→ `browserCommandInput.ts`（键位/鼠标序列，最接近 SendInput 参照）→
`browserCommandTypes.ts`（先读 52 行）→ `host/browserControlMainBridge.ts`（pending/超时/取消）→
`browserGuestManager.ts` 的 execute/teardown/CDP 段落 → `browserScreenshotCapture.ts` + transient retry →
`embeddedBrowserPermissionPolicy.ts` + UI bridge。

---

## 11. 对 PCCtrl 的落地建议

> 本章全部是**建议/设计**（【推断】），基于 §2-§10 的 ZCode 事实。技术栈仍未定，因此本章聚焦
> "协议与边界"，而不是选型结论；语言/框架放到 §11.9 的决策点里。

### 11.1 架构选型：三种可行方案对比

| 方案 | 组成 | 优点 | 缺点 | 适用 |
| --- | --- | --- | --- | --- |
| **A. Windows 常驻 MCP server + AstrBot 内置 MCP 客户端**（推荐 MVP） | Windows 执行器暴露 Streamable HTTP MCP；AstrBot 在 `mcpServers` 注册 | 不写插件；AstrBot 已支持 streamable_http/SSE、超时、重连、工具权限；ZCode 的"MCP 服务 + 工具面"经验可直接套 | 工具面完全由服务端定义；无法在 AstrBot 侧做动态工具/复杂确认；认证只能靠 header/token；调试要看两侧日志 | 快速打通"模型→工具→Windows 动作" |
| **B. AstrBot 插件内实现 MCP 客户端 / HTTP 调用 + 注册 FunctionTool** | 插件调 Windows 服务（MCP 或自定义 HTTP），注册 `@filter.llm_tool` | 完全控制工具面、参数、确认流程、审计、会话绑定、权限；可做动态工具/多后端 | 要写并维护插件；要自己实现协议客户端/重试/超时；AstrBot 插件开发有硬约束（前序 `research/astrbot/`） | 需要产品级控制/多用户/安全确认 |
| **C. 自定义 HTTP API（非 MCP）+ 插件工具** | Windows 服务用普通 REST/JSON-RPC；插件封装成工具 | 协议最简单；不需要 MCP 库；调试容易 | 失去 MCP 生态（其他客户端无法复用）；工具描述/错误/图片语义要自己定义；AstrBot 内置 MCP 能力浪费 | 团队想最小依赖、只服务 AstrBot |

**建议**：**A 先打通（1-2 周内可跑通"截图 + 点击"），B 作为产品化路径**。关键是把 §11.3 的
"工具契约 + 回执/错误语义"设计成**独立于传输**的协议，这样 A→B 不需要重写执行器。

### 11.2 推荐架构与组件职责

```text
┌──────────────────────────── 容器（Linux）────────────────────────────┐
│ AstrBot                                                              │
│  ├─ 模型编排 / agent loop                                            │
│  ├─ MCP client（方案 A） 或 PCCtrl 插件（方案 B）                      │
│  └─ 配置：mcpServers.pcctrl = {url, transport:"streamable_http",      │
│             headers:{Authorization:"Bearer <L1>"}, timeout}          │
└───────────────────────────────┬──────────────────────────────────────┘
                                │ Streamable HTTP（+TLS 可选）
┌───────────────────────────────▼──────────────────────────────────────┐
│ Windows 主机（用户交互会话，非 Session 0）                             │
│  ┌──────────────────── PCCtrl Executor（常驻单实例）────────────────┐  │
│  │ 1) MCP/HTTP 传输层 + L1 认证 + 版本/协议校验                      │  │
│  │ 2) 工具层（§11.3）：observe / act / apps / windows / health / stop │  │
│  │ 3) SessionManager：sessionId → {lastObserve, frame, lease, audit}  │  │
│  │ 4) ObservationEngine：窗口枚举/身份 + WGC 截图 + UIA 树 + frame 绑定│  │
│  │ 5) ActionDispatcher：UIA 元素 → PostMessage → 前台 SendInput 升级链 │  │
│  │ 6) PlatformAdapter（窄接口）：Win32/UIA/SendInput/WGC/剪贴板        │  │
│  │ 7) 审计/日志/健康/停止/取消                                        │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────┘
```

**分层规则（照抄 ZCode/Browser Use 的解耦范式）**：

- 工具层只表达意图 + 校验 schema，不直接调 Win32；`ActionDispatcher` 只依赖 `PlatformAdapter` 窄接口
  （便于测试注入 fake、未来换语言/框架）。
- `ObservationEngine` 是**唯一** UI 事实来源；动作层不自己"猜"目标，必须拿观察产生的 state/frame。
- `SessionManager` 是状态所有者：last observation、frame 生命周期、输入租约、审计；**不要**把状态放在模型或插件里。
- 传输层只做认证/协议/限流/大小预算，不含业务状态。

### 11.3 工具面设计草案

**核心决策：像 Browser Use 一样"少而稳"，而不是把 ZCode 的 14 个工具原样搬过来。**
理由：AstrBot 的 MCP 工具名会被 sanitize 且没有 server 前缀；工具越多，每轮工具定义成本越高；
Windows 执行器语言未定，工具面越稳定越容易换实现。

**推荐工具集（7 个，MVP）**

| 工具 | 用途 | 关键参数 | 返回 |
| --- | --- | --- | --- |
| `pc_health` | 健康/能力/版本 | — | `{ok, version, os_session, integrity_level, capabilities, uptime}` |
| `pc_apps` | 列出可操控应用（身份） | `include_windows?: bool` | `{apps:[{app_id, name, exe/aumid, pid, windows:[...]}]}` |
| `pc_windows` | 列出窗口（必要时单独） | `app_id` | `{windows:[{window_id,title,onscreen,foreground,bounds}]}` |
| `pc_observe` | 观察（唯一 UI 事实来源） | `app_id?, window_id?, include_screenshot=false, mode="full"|"diff"` | `{state_id, app/window, elements[], text?, image?, frame:{width,height,dpr,actionable}}` |
| `pc_act` | **单一动作通道**，op 区分动作 | `op, target, args, state_id/frame_id?, expected_precondition?` | `{action_receipt:{action_sent,dispatch_status,retry_action,effect?}, state_available}` |
| `pc_stop` | kill switch / 取消 | `reason?` | `{ok}` |
| `pc_session`（可选） | 显式会话租约（B 方案时更有用） | `action=open|close|renew` | `{session_id, lease_expires_at}` |

**`pc_act` 的 op 列表（对齐 ZCode 14 工具的能力，但收敛成一个通道）**：

`click`（target=元素索引/坐标；args: mouse_button/click_count/modifiers/strategy）、
`type`（text; target?; strategy）、`set_value`（target, value）、`key`（text, repeat?, hold_seconds?）、
`scroll`（target, direction, pages）、`drag`（from, to, modifiers?）、`paste`（text, format?）、
`select_text`（target, text_range?）、`perform_action`（target, action）、`activate_window`（window_id，可选）。

**请求/回执/错误 JSON 草案**（从 `notes/03` §7.3 改写，推荐直接作为协议 v1）：

```jsonc
// 观察（唯一 UI 事实来源；wire 用 snake_case）
{ "schema_version": "pcctrl-observe-v1", "session_id": "sess_...", "seq": 12,
  "target": {
    "app":    {"app_id": "win-exe:...", "pid": 23192, "name": "Calculator"},
    "window": {"window_id": 27367, "title": "Calculator", "onscreen": true,
               "foreground": false, "bounds": [100,100,400,600]},   // 屏幕点，禁止当坐标
    "state_id": "st-8f21", "window_resolution": "exact" },          // exact | main_fallback(禁止静默降级)
  "state": { "mode": "full|delta|no_change", "base_state_id": "st-7e00",
             "elements": [{"index": 6, "kind": "button", "title": "7", "value": null,
                           "actions": ["press"], "selected": false, "enabled": true}],
             "omitted": {"by_kind": {"container": 4}, "reason": "budget", "sparse_indices": true}},
  "frame": { "frame_id": "fr-4c9a", "width": 400, "height": 600, "dpr": 1.5,
             "actionable": true, "captured_at_ms": 1760000000000 },
  "advisories": ["effect_evidence unchanged"] }

// 动作（单次派发；frame_id/state_id 由服务端填充或模型仅回传 state_id）
{ "schema_version": "pcctrl-act-v1", "session_id": "sess_...", "request_id": "req_01H...",
  "op": "click|type|key|scroll|drag|set_value|select_text|perform_action|paste",
  "target": {"kind": "element", "index": 6, "state_id": "st-8f21"},
            // 或 {"kind":"coordinate","x":38,"y":105}
  "args": {"mouse_button": "left", "click_count": 1, "modifiers": "", "strategy": "auto"},
  "expected_precondition": {"window_id": 27367, "surface": "stable"} }

// 回执（默认返回，不隐含观察；回执块必须排在状态/树之前）
{ "action_receipt": { "schema_version": "pcctrl-action-receipt-v1",
    "action_sent": true, "dispatch_status": "accepted|possibly_sent|not_sent", "retry_action": false,
    "dispatch_surface": {"window_id": 27367, "owner_pid": 23192, "method": "uia|post_message|sendinput"},
    "verification": {"status": "verified|unverified|mismatched", "postcondition": "value_changed|null"},
    "state_available": false } }

// 错误（唯一失败通道；不可重放/可能已发送的事实必须随错走）
{ "code": "PERMISSION_DENIED|FOREGROUND_REQUIRED|STALE_STATE|ELEMENT_UNAVAILABLE|...",
  "action_sent": false, "dispatch_status": "not_sent", "retry": "reobserve|retry|never", "retry_action": false,
  "details": {"owner": "sess_..."},
  "diagnostic": {"phase": "tree_traversal", "nodes_visited": 312, "elapsed_ms": 8012,
                 "deadline_exceeded": true, "pid": 23192, "window_id": 27367} }
```

**三条硬契约（从 ZCode 抄）**：

1. **动作与观察分离**：动作默认只回 receipt；要状态就再 observe。回执块永远排在状态/树之前。
2. **`action_sent` / `dispatch_status` 三态**：进入原生调用之后的任何错误一律 `possibly_sent`；
   调用方（AstrBot/模型）**禁止自动重放非幂等动作**。
3. **新鲜度**：服务端持有 `state_id`/`frame_id`，模型只给元素索引/像素；缺失或过期 → `STALE_STATE`；
   不自动补观察（避免"想点 A 实际点 B"）。

> 备选：如果团队坚持"14 个独立工具"（AstrBot 侧更好读），用 `notes/02` §7.4 的 13-tool 草案
> （`computer_*` 命名，observe 图文合一、回执字段、13 个错误码），但建议仍保留"单 `pc_act` 通道"作为内部实现，
> 外层工具只做薄封装。

### 11.4 观察与坐标策略

**推荐顺序**（与 ZCode 一致）：

1. **AX/UIA 元素优先**：语义、精确、可后台、不抢焦点。Windows UIA 提供 Invoke/Toggle/Select/Expand/Value/Scroll/TextRange。
2. **截图 + 坐标作为兜底**：canvas/游戏/Electron/自绘 UI；模型看 raster 抄像素。
3. **截图以"单窗口"为单位**：隐私更可控（不把整屏进模型）、坐标更简单（无需多屏换算）、与 ZCode 一致。
4. **坐标契约**：模型只提交"最新返回 raster 的整数像素"；服务端内部绑定 `frame_id`；
   `bounds` 只做诊断；过期/窗口变化/owner 变化 → fail closed。
5. **DPI/多屏**：执行器内部统一到物理像素（Per-Monitor V2），raster 与派发同源；
   对外只暴露"这张 raster 的像素"，不要把屏幕坐标泄漏给模型。
6. **AX 树预算**：先做"节点数/字符预算 + 优先级裁剪 + 稀疏索引注记 + elements 表"；
   不要第一天做全量树（ZCode 真机 736→300 的教训）。
7. **截图预算**：默认不回传全屏原图；先缩略图定位（≤200 KiB base64 量级），需要细节时再取局部。

**观察返回的"双通道"建议**：`text`（给模型读的渲染树/摘要）+ `elements[]`（结构化的 index/kind/title/value/actions），
与 ZCode 的"文本树 + elements()"一致；图片作为独立 image block。

### 11.5 输入执行策略（Windows）

**day-1 推荐组合**（取自 `notes/04` §7）：

| 能力 | day-1 路径 | 说明 |
| --- | --- | --- |
| 元素点击/设值/选择 | **UIA 元素动作** | Invoke/Toggle/Select/Expand/ValuePattern；后台、不抢焦点；需要读回验证的用 `set_value` 三段读回 |
| 坐标点击 | **PostMessage（已验证宿主）→ 前台 SendInput 兜底** | Chromium 直接宿主、Win32 泵宿主优先；未知宿主按 deliverable 尝试 |
| 文本输入 | `set_value`（替换语义）优先；`type` 插入用 WM_CHAR + changed/contains 读回；前台长文本逐字符 | 必须做"写前快照→写→读回"裁决 |
| 单键 | PostMessage（焦点三态）+ XAML 加速键 | 组合键不要走 PostMessage（结构性不可投） |
| 组合键/快捷键 | **前台 SendInput**（物理 lane 串行 + 条件恢复） | ZCode 结论：LPARAM 无 CTRL/SHIFT 位，posted 不更新 GetKeyState |
| 滚动 | WPF/WinUI3 的 SB 码族后台；其它前台滚轮 | 后台滚轮 PostMessage 全败 |
| 拖拽 | 后台 PostMessage 节拍序列 → 前台锁步 | 标题栏/非客户区必须真实输入（NCHITTEST） |
| 粘贴 | provided-paste（快照→写入→粘贴键→读回→还原） | 剪贴板全局资源要串行 |

**必须带的工程项**：

- 焦点防守（shield/NoActivateGuard/条件恢复），**排水 120ms**，逐字符 4ms + drain `max(120,n×20)`，>50 UTF-16 不恢复前台（§6.4）。
- UIPI 预检、锁屏/会话切换 fail-closed、单会话输入租约、`possibly_sent` 不重放。
- `delivery` 元数据（path/effect/attempts/escalated）落审计，模型文本不披露内部路径。
- 丢弃矩阵先用**小集合**（WinForms/WPF/Chromium/记事本）验证，不要一开始照搬全表。

### 11.6 安全与权限设计

**两层令牌**（来自 `notes/01` §6.2，PCCtrl 必做）：

| 层 | 用途 | 实现 |
| --- | --- | --- |
| **L1 会话/配对令牌** | 容器 ↔ 执行器建 MCP 连接/重连 | 长随机串，存 AstrBot 配置 + Windows 凭据库（DPAPI）；只走 HTTP header/初始化握手；可撤销/轮换；**不进 tool 参数、不进日志** |
| **L2 每调用能力令牌** | 每次工具调用授权 | 执行器侧校验/签发短期 token，绑定 `sessionId+turnId+toolCallId+expiry`；调用结束失效；校验通过才进 OS Adapter |

**网络与监听面**：

- 执行器只监听 **loopback**，或用 Windows named pipe + ACL（当前用户 SID）；若必须 LAN，则受控网卡 + TLS + 防火墙白名单。
- 容器访问：Docker Desktop 用 `host.docker.internal`；Linux Docker 需 `--add-host=host.docker.internal:host-gateway`
  或直接用宿主 LAN IP；**不要把 token 写进 URL/镜像**。
- 反向代理不是必须，但真实威胁模型下建议 terminator（TLS/mTLS）与执行器分离，执行器只接受来自 terminator 的连接。

**审计与用户可见性**：

- 每个动作记录：谁（session/turn）、何时、哪个 app/window、什么 op、结果、`action_sent`/`dispatch_status`、是否升级路径；
- 用户可见的"正在操作"提示（对应 ZCode 的 Windows 指示条）：sideband 事件，不进会话快照；失败要 fail-hidden；
- 危险动作（删除、发送、购买、系统设置）建议加**确认环节**（AstrBot 交互或插件按钮），至少要有速率限制与停止键。

**隐私边界**：

- 截图只截目标窗口；默认不把整屏/全屏发给模型；截图可配置脱敏/模糊区域（未来项）；
- 文本内容（剪贴板/输入）默认不进日志；诊断日志只记 phase/nodes/elapsed 等元数据（ZCode 的 capture 诊断就是这么做的）。

### 11.7 分阶段路线图

| 阶段 | 目标 | 验收（first-green） |
| --- | --- | --- |
| **P0 地基**（协议 + 观察） | MCP server 骨架、L1/L2 认证、`pc_health`/`pc_apps`/`pc_windows`/`pc_observe`；WGC 截图 + UIA 树 + frame 绑定 | AstrBot 能连上并列出窗口；`pc_observe` 返回树 + 截图；坐标/窗口身份测试通过 |
| **P1 点击** | 元素动作（Invoke/Toggle/Select/Expand）+ 坐标 PostMessage/SendInput 升级链 + delivery 元数据 | WinForms 按钮、WPF 按钮、Chromium 后台点击（业务计数器/DOM oracle 通过） |
| **P2 文本** | `set_value` 三段读回、`type` 插入、WM_CHAR、Chromium echo 降级 | 记事本/WPF 文本框读回一致；插入位置正确；失败不重放 |
| **P3 键盘/滚动/拖拽** | 单键 + 组合键前台 + XAML 加速键；SB 码族滚动 + 前台滚轮；拖拽节拍 + NCHITTEST 拒绝 | 快捷键落地（文件 oracle）；WPF/WinUI3 滚动 offset；slider 拖拽 |
| **P4 多窗口/焦点/边界** | 多窗口/弹层（先单窗，后 popup）、焦点防守、UIPI、锁屏、多显示器/DPI、长文本 | 焦点违规 0；双屏/200% DPI 点击落点；高完整性 refused |
| **P5 产品化** | AstrBot 插件（方案 B）或确认流程、审计 UI、停止/panic、版本协商、安装器/自启（交互用户会话） | 端到端演示 + 安全评审 + 故障注入（执行器崩溃/网络断/模型取消） |

**里程碑建议**：P0+P1 先打通"看一眼 + 点一下"（这是用户最直观的价值），再逐步加动作面；
每一步先写 fixture/oracle 红测再实现（ZCode 的纪律）。

### 11.8 验收与测试建议

**从 ZCode 抄的测试纪律**：

1. **每个写动作必须有独立 oracle**：业务计数器、DOM/AX 状态变化、文件/剪贴板内容；
   "工具返回 ok"不是证据。
2. **焦点 watcher**：断言动作期间不产生意外的焦点事件（ZCode 的 13 格零焦点违规就是靠这个）。
3. **回执边界测试**：`possibly_sent` 的路径要能构造出来（例如超时/Helper 崩溃），断言调用方不重放。
4. **夹具本身是合同**：oracle 通道、窗口标题、重叠窗口顺序都会影响测试；先做干净基线对照再归因。
5. **全局桌面资源串行**：剪贴板/焦点/光标相关测试必须串行（ZCode 的 paste suite 教训）。
6. **矩阵记录**：每次跑完整动作面，把 `path/effect/attempts/escalated` 落盘，防止"选择性记忆"。
7. **不做"假绿"**：无法验证的路径宁可记 `unverifiable`，不要用轮询/重试掩盖；已知残差保留红灯。

**建议的 first-green 用例集**（改写自 `notes/04` §7.3）：

- WinForms 按钮坐标点击（业务计数 +1、无焦点事件、路径=post_message）；
- WPF 按钮元素点击（Invoke，无抢前台）与文本框 `set_value` 全文读回；
- 记事本冷启动 → `type` → `Ctrl+S`（文件 oracle）；
- Chromium 后台/遮挡点击（DOM oracle +1）、双击第二击 `WM_*BUTTONDBLCLK`；
- WPF/WinUI3 SB 码族后台滚动 vs WinForms 前台滚轮升级；
- slider 拖拽（value 前进、UP 后 40ms settle）、标题栏拖拽**拒绝**；
- UWP 计算器元素双击（两次 Invoke）、坐标单击；
- 高完整性窗口 → `refused` + `uipi_blocked` + 零激活；
- 200% DPI 单屏 + 副屏负坐标点击落点；
- 用户在多击间隙切走：停止余下输入、不抢回、回执如实；
- 目标窗口 4s 不返回 → timeout、无重放；执行器被杀 → 无孤儿、后续可恢复。

### 11.9 开放问题与决策点

| # | 决策 | 选项 | 建议 |
| --- | --- | --- | --- |
| 1 | Windows 执行器语言 | C#（UIA COM 最省事）/ C++/Rust（Win32+UIA）/ Node（N-API） | 若团队 C# 熟悉 → C#；若已有 Rust/Node 基础设施 → 另议；**不要为了"和容器同语言"选跨平台方案** |
| 2 | 宿主形态 | 交互用户会话常驻进程（推荐）/ Session 0 服务 + per-user agent / 计划任务 | 必须跑在交互会话；服务只做隧道/鉴权（如需开机自启） |
| 3 | 传输 | Streamable HTTP（推荐）/ SSE / WebSocket / 自定义 | Streamable HTTP；SSE 兼容；WebSocket 无必要 |
| 4 | 工具面 | 单 `pc_act` 通道（推荐）/ 14 独立工具 | 单通道 + 薄封装；保持 10 个以内 |
| 5 | 观察格式 | 树文本 + elements（推荐）/ 纯 JSON / 纯截图 | 树文本 + elements + 可选图片；先不做视觉模型 |
| 6 | 安全等级 | token-only / TLS+mTLS / 配对码 + 确认 | MVP token+回环；产品化至少 TLS + 危险动作确认 |
| 7 | AstrBot 集成 | 内置 MCP 客户端（推荐 MVP）/ 插件 | A 先跑通，B 按需演进 |
| 8 | 截图策略 | 单窗口按需（推荐）/ 全屏轮询 / 不截图 | 单窗口按需 + 预算压缩 + 隐私脱敏 |
| 9 | 弹层/多窗口 | 首版单窗（推荐）/ 完整 surface 合成 | 先单窗，弹层显式 window_id 切换；popup 合成后置 |
| 10 | 协议版本管理 | 简单版本号 / schema_version 字段（推荐） | 所有请求/回执带 `schema_version`，服务端拒绝未知大版本 |

---

## 12. 交接指南

### 12.1 文档地图

**主报告（本文件）**：`research/zcode/ZCode电脑操控实现研究.md`
—— 面向人类阅读 + 给后续开发智能体的总入口。§0 速览 → §2 架构 → §3-§6 工具/观察/动作/Windows →
§7-§9 安全/生命周期/MCP → §10 浏览器参考 → §11 落地建议 → §12 交接。

**分题证据笔记**（`research/zcode/notes/`，每篇都有"结论速览 + 逐文件行号证据索引"）：

| 文件 | 主题 | 什么时候读 |
| --- | --- | --- |
| `01-cua-architecture.md` | 架构/权限主体/生命周期/信任边界/多端会话 | 设计执行器权限、令牌、生命周期时 |
| `02-cua-model-tool-contract.md` | 模型侧工具契约（SKILL/docs/SDK、14 工具手册、错误语义、AstrBot 工具草案） | 设计 MCP 工具面/参数/返回时 |
| `03-cua-runtime-contract.md` | SDK v3、回执/act-verify、坐标绑定、AX 树、app identity、JSON 契约草案 | 设计观察/动作协议、字段级契约时 |
| `04-windows-input-and-frames.md` | Windows 输入派发、丢弃矩阵、逐工具设计、焦点/DPI、frame surface、打包、缺陷清单 | 实现 Windows 执行器时（最重要） |
| `05-mcp-and-node-repl.md` | MCP 宿主/传输/双版本/预算、node_repl 真实实现、对 AstrBot 的映射建议 | 做 MCP 服务/客户端、传输选型时 |
| `06-browser-control.md` | Browser Use 真实代码：命令协议/执行器/快照/截图/会话/权限治理 | 需要完整自动化工程范本时 |

**协作简报**：`research/zcode/00-研究简报与协作约定.md`（任务分工、证据标准、项目背景）。
**前序研究**：`research/astrbot/`（AstrBot 插件机制/约束，本报告 §9.4 只做增量核实）。

### 12.2 源码索引（按主题；路径相对工作区根）

**模型侧/工具契约（真实代码）**

- `ZCode-main/apps/zcode-cli/packages/zcode-cua-plugin/skills/computer-use/SKILL.md`：模型常驻决策指南（307 行）
- `ZCode-main/apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md`：按需完整参考（471 行）
- `ZCode-main/apps/zcode-cli/packages/zcode-cua-plugin/scripts/computer-use-client.mjs`：**SDK 真实实现**（1210 行；14 方法、错误映射、receipt 判定、frame 绑定）
- `ZCode-main/apps/zcode-cli/packages/node-repl-host/src/{server,tool-contract,cua-bridge,cua-broker,result,process-lifecycle,no-progress-guard}.ts`：node_repl 真实运行时
- `ZCode-main/apps/zcode-cli/packages/node-repl-host/src/call-ledger/ledger.ts`：per-call token
- `ZCode-main/packages/contracts/src/tools/node-repl.ts`：`js` 工具 schema

**CUA 租约/权限/生命周期（真实代码）**

- `ZCode-main/packages/services/src/cua-permission-broker/`：`cuaPermissionService.ts`、`cuaHelperInstaller.ts`、
  `stableCuaTransport.ts`、`cuaPipSessionService.ts`、`windowsCuaDevHelperHost.ts`、`windowsCuaHelperHostSupport.ts`、
  `windowsCuaDevRuntime.ts`、`cuaAgentAdmissionGate.ts`
- `ZCode-main/packages/zcode-cua/*.d.ts`：API 契约（placeholder 0.6.5，fail-closed）
- `ZCode-main/packages/desktop/src/main/windowsCuaOperationIndicator*.ts`：Windows 操作指示器
- `ZCode-main/packages/shared/src/runtimeEnv.ts`：凭据 sanitize

**规格文档（真实产品文档，按主题）**

- 架构/权限：`docs/cua-permission-broker/zcode-cua-permission-broker.md`、`cua-helper-installation.md`、
  `2026-09-21-cua-helper-lazy-startup-spec.md`、`2026-09-02-helper-lifecycle-agent-runtime-isolation-spec.md`、
  `windows-transport-ready.md`
- 工具/回执/坐标：`docs/cua/producer/2026-09-10-cua-sdk-v3-api.md`、`cua-receipt-observation-contract.md`、
  `cua-14-tool-e2e-validity.md`、`cua-act-verify-scenarios.md`、`frame-coordinate-internal-binding.md`、
  `ax-tree-optimization-design.md`、`capture-app-timeout-diagnostics.md`
- Windows：`docs/cua/2026-10-13-windows-input-delivery-spec.md`（总纲）、`2026-10-16-windows-input-s3-dispatch-plan.md`、
  `2026-10-19-windows-input-s4-plan.md`、`2026-10-19-scroll-postmessage-probe-report.md`、
  `2026-10-20-electron-postmessage-probe-report.md`、`2026-10-23-windows-app-frame-surface-spec.md`、
  `2026-09-14-windows-cua-indicator-repair-spec.md`、`windows-product-runtime.md`、`windows-source-development.md`
- MCP：`docs/mcp-*.md`（传输/双版本/OAuth/预算/缓存/诊断）、`docs/mcp-tool-output-budget.md`、
  `docs/mcp-stateless-node-repl.md`、`docs/mcp-async-tool-catalog-cache.md`、`docs/windows-mcp-job-object-containment.md`
- 浏览器：`docs/browser-use/`（交互核心/协议/运行时/缩放/截图/权限治理/teardown）、
  `packages/shared/src/browser-use/{commands,command-metadata,result,snapshot}.ts`、
  `packages/desktop/src/main/browserView/{browserCommandExecutor,browserCommandPageHandlers,browserCommandInteractionHandlers,browserCommandInput,browserCommandTypes,browserScreenshotCapture,browserScreenshotTransientRetry,browserGuestManager}.ts`、
  `packages/desktop/src/host/browserControlMainBridge.ts`
- AstrBot（外部参照）：`astrbot/core/agent/mcp_client.py`、`astrbot/core/provider/func_tool_manager.py`、
  `astrbot/core/tools/computer_tools/cua.py`、`astrbot/core/computer/computer_client.py`

### 12.3 术语表

| 术语 | 含义 |
| --- | --- |
| **CUA** | Computer Use：读取/操作原生桌面 UI 的能力 |
| **node_repl** | ZCode 的共享 JS 执行宿主 MCP server，只暴露一个 `js` 工具 |
| **Worker** | 每次 `js` 调用创建的一次性 Node 线程/执行环境 |
| **bridge** | Worker 与宿主之间的私有通信通道（注入 SDK + 本地 socket/pipe） |
| **call ledger** | 主线程的调用账本：签发 per-call token、记录可信事实（能力标签/app identity） |
| **broker** | 两处：① node_repl 主线程的私有 CUA broker；② Helper 进程暴露的 broker（named pipe/socket） |
| **Helper** | 独立进程（macOS 签名 .app / Windows Electron Node fork），唯一持有 OS 权限与原生能力 |
| **CuaControlPort** | shared host 里 CUA runtime 的调用入口（execute/closeSession/dispose） |
| **AX / UIA** | macOS Accessibility / Windows UI Automation：语义元素树 |
| **frame / raster** | 某窗口某一时刻的截图；模型坐标以这张 raster 的像素为准 |
| **state_id / frame_id** | 观察/帧的内部权威标识；模型通常不直接持有 frame_id |
| **action receipt** | 动作回执：`action_sent`、`dispatch_status`、`retry_action` 等 |
| **possibly_sent** | 动作可能已进入原生派发；调用方不得自动重放非幂等动作 |
| **act-verify** | 验收层"模型动作 + 独立 oracle 验证"双条件 |
| **delivery** | broker 元数据：path（ax/post_message/foreground_send_input）、effect、attempts、escalated |
| **physical lane** | Windows 前台物理输入的进程内串行队列（激活/SendInput/恢复/滚动/拖拽） |
| **PiP** | Picture-in-Picture：macOS 的 CUA 会话浮层（producer 所有） |
| **overlay / ghost cursor** | Helper 侧绘制在桌面上的 Agent 光标/反馈浮层 |
| **UIPI** | Windows 完整性级别隔离：低完整性进程不能向高完整性窗口注入输入 |
| **AUMID** | Windows 应用用户模型 ID（packaged app 身份） |

### 12.4 已核实 / 未核实 / 风险清单

**本报告"已核实"= 读取了本快照内真实源码或产品文档并逐字引用**：

- node_repl 运行时、CUA 私有 broker/bridge、结果组装、进程守护（真实 TS 代码）。
- 模型侧 SKILL/docs/SDK 脚本（真实插件资产，1210 行 SDK）。
- 浏览器自动化全链路（真实代码 + 规格）。
- CUA 的权限/生命周期 specs 与 Windows 输入/帧 specs（产品文档；文档内记录的真机数字来自 ZCode 团队，不是本环境复测）。

**未核实 / 本环境无法验证**：

1. CUA 原生 producer（`ax_win.cc`/`windowsInputDispatch.ts`/`screen_capture_win.cc`）与真实 broker server（51 文件）
   不在本仓库；Helper 内部 peer 门算法、native 启动流程未证实。
2. macOS TCC/签名/Gatekeeper/LaunchServices 链路、Windows named pipe 实测行为：本环境无法跑。
3. 文档之间/文档与代码存在时差（token→identity、`cua` 全局→`agent.computerUse`、回执契约 vs SDK 呈现、
   Windows 总纲 3 处口径张力、30-tool vs 14-tool 旧口径）；本报告已按"代码/最新文档优先"标注，但引用旧文档时需复核。
4. AstrBot 侧只核实了 MCP client 与内置 computer_tools 的代码事实；未实测"容器→Windows"网络链路、未验证
   AstrBot 对 MCP image content 的模型投影行为（受 provider 影响）。

**风险清单（若要做 PCCtrl）**：

| 风险 | 等级 | 缓解 |
| --- | --- | --- |
| 跨机器链路无同机 peer 语义，token 泄漏即可控桌面 | 高 | L1/L2 双层令牌 + TLS + 回环/ACL + 轮换 + 审计 |
| 截图/输入数据进入第三方模型 provider | 高 | 只截目标窗口、按需、预算压缩、可脱敏、明确告知用户 |
| 模型被提示注入驱动危险操作 | 高 | 动作分级 + 确认 + 速率限制 + panic 停止 + 不自动重放 |
| Windows 原生输入的宿主矩阵复杂（WPF/Chromium/UWP/VCL/GTK） | 中 | 先小集合验证、逐宿主加 fixture、保留 unverifiable 红灯 |
| 焦点/剪贴板/光标是全局资源，多会话互相干扰 | 中 | 单输入租约、串行物理 lane、测试串行 |
| 执行器跑在 Session 0/服务会失去桌面能力 | 中 | 交互用户会话常驻进程；服务只做隧道 |
| 原生模块签名（Smart App Control/Code Integrity）阻止加载 | 中 | 产品化需签名；开发环境如实记录限制 |
| 文档过期/口径漂移导致实现错误 | 中 | 以代码+最新 normative 文档为准；协议带版本号 |

---

## 附录 A：14 工具参数速查

| # | 工具 | 必填 | 可选（默认） | 说明 |
| --- | --- | --- | --- | --- |
| 1 | `list_apps` | — | — | 返回 `{pid,name,bundle_id,active}` |
| 2 | `list_windows` | `app_ref` | — | 每个窗口一行（见 §4.3 字段表） |
| 3 | `get_app_state` | `app_ref` | `include_screenshot=false`, `disable_diffing=false` | AX 树 + 可选截图 |
| 4 | `left_click` | `target` | `mouse_button="left"`, `click_count=1`, `modifiers=""`, `strategy`, `app_ref`, `return_state="none"` | 元素索引或坐标 |
| 5 | `scroll` | `target`, `scroll_direction`, `scroll_amount` | `strategy`, `app_ref`, `return_state` | amount=pages，clamp 0-100 |
| 6 | `left_click_drag` | `from_target`, `to` | `modifiers`, `strategy`, `app_ref`, `return_state` | 两端各自绑定 |
| 7 | `type` | `text` | `target`, `app_ref`, `strategy`, `return_state` | 逐字符/插入语义 |
| 8 | `set_value` | `target`, `value` | `strategy`, `app_ref`, `return_state` | 替换语义 |
| 9 | `select_text` | `target` | `text_range`, `app_ref`, `return_state` | `[start,length]`；省略=全选 |
| 10 | `key` | `text` | `repeat`, `hold_seconds`, `app_ref`, `strategy`, `return_state` | 短名或 X keysym |
| 11 | `perform_action` | `target`, `action` | `app_ref`, `return_state` | action 必须元素自报 |
| 12 | `paste` | `text` | `format="text"`, `app_ref`, `return_state` | 原生剪贴板粘贴 |
| 13 | `request_access` | — | `capabilities` | 只读权限快照 |
| 14 | `stop_computer_control` | — | `reason` | kill switch |

> `app_ref` = `{name?|bundle_id?|pid?, window_id?}`；裸字符串按 bundle id。
> `target` = `{type:"element", index}` 或 `{type:"coordinate", x, y, frame_id?}`。
> `strategy` = `auto|a11y|event`；`return_state` = `compact|full|none`。

## 附录 B：错误码与重试语义

**模型可见 20 码**：`PERMISSION_DENIED, NOT_AUTHORIZED, APP_NOT_FOUND, AMBIGUOUS_APP, LAUNCH_FAILED, INVALID_APP,
ELEMENT_UNAVAILABLE, STALE_STATE, NOT_SETTABLE, NOT_SELECTABLE, ACTION_UNAVAILABLE, FOREGROUND_REQUIRED,
CONTROLLER_BUSY, CONTROL_STOPPED, SCREEN_LOCKED, HELPER_UNAVAILABLE, VERSION_MISMATCH, TIMEOUT,
STRUCTURED_STATE_UNAVAILABLE, INTERNAL`。

**retry 规则**：

| 条件 | retry |
| --- | --- |
| `actionSent=true` | `reobserve`（先看再决定，禁止盲重放） |
| CONTROLLER_BUSY / CONTROL_STOPPED / PERMISSION_DENIED / NOT_AUTHORIZED / VERSION_MISMATCH / ACTION_UNAVAILABLE / NOT_SETTABLE / NOT_SELECTABLE | `never` |
| ELEMENT_UNAVAILABLE / STALE_STATE / STRUCTURED_STATE_UNAVAILABLE | `reobserve` |
| 其它 | `retry` |

**broker→SDK 映射**（节选）：`permission_denied→PERMISSION_DENIED`、`not_authorized→NOT_AUTHORIZED`、
`element_unavailable→ELEMENT_UNAVAILABLE`、`foreground_required→FOREGROUND_REQUIRED`、
`broker_unavailable|stale_socket→HELPER_UNAVAILABLE`、`version_mismatch→VERSION_MISMATCH`、
`timeout→TIMEOUT`、`unimplemented→ACTION_UNAVAILABLE`、`method_not_found|internal→INTERNAL`；
**未知码归 INTERNAL，绝不静默成功**。

**冷启动**：`CUA_NOT_READY {kind,reasonCode,retryable,message}`；SDK 仅对 retryable 按
`[250,500,750,1000,1500]ms` 退避重试，最多 6 次。

**文档与实际映射的缺口**（引用时注意）：`APP_NOT_FOUND/AMBIGUOUS_APP/CONTROL_STOPPED/SCREEN_LOCKED`
在 SDK 映射表中没有对应项，若 producer 以这些字符串回码会被压成 `INTERNAL`（`notes/02` §2.6）。

## 附录 C：证据来源索引（顶层）

| 来源 | 用途 |
| --- | --- |
| `docs/cua/producer/2026-09-10-cua-sdk-v3-api.md` | 模型面设计规格、30→14 瘦身、错误码、内部分层 |
| `docs/cua/producer/cua-receipt-observation-contract.md` | action receipt / return_state / 回执顺序 / 目标回读 |
| `docs/cua/producer/cua-14-tool-e2e-validity.md` | normative 14 工具、删减算术、证据边界、fresh authority |
| `docs/cua/producer/frame-coordinate-internal-binding.md` | 坐标契约 + 内部帧绑定不变量 |
| `docs/cua/producer/ax-tree-optimization-design.md` | AX 裁剪/预算/身份模型 |
| `docs/cua/producer/macos-app-frame-surface-provenance.md` | surface 归属四条件（对 Windows frame 有借鉴） |
| `docs/cua/2026-10-13-windows-input-delivery-spec.md` | Windows 输入总纲（最重要） |
| `docs/cua/2026-10-23-windows-app-frame-surface-spec.md` | Windows 窗口/面板/菜单 surface 与坐标权属 |
| `docs/cua-permission-broker/*` | 权限主体、Helper 生命周期、Windows 两阶段就绪、凭据/fail-closed |
| `docs/browser-use/*` + `packages/{shared,desktop}/**/browser*` | 浏览器真实实现 |
| `apps/zcode-cli/packages/node-repl-host/**` | node_repl 真实运行时 |
| `apps/zcode-cli/packages/zcode-cua-plugin/**` | 模型侧文档 + SDK 脚本 |
| `packages/services/src/cua-permission-broker/*` | Helper host/installer/permission/PiP 真实代码 |
| `packages/zcode-cua/*.d.ts` | API 契约（placeholder） |
| AstrBot `astrbot/core/agent/mcp_client.py` 等 | 容器侧 MCP 客户端能力 |
| `research/zcode/notes/01-06` | 六个分题的全部逐文件行号证据 |

---

*报告完 — 研究基线：ZCode-main @ 2026-10 工作区快照；AstrBot master@2026-10-31。
建议后续智能体先读 §0、§11，再按需跳到分题笔记。*
