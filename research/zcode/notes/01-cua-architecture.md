# ZCode CUA 总体架构、权限主体、生命周期、信任边界与多端会话

> 研究范围：ZCode CUA 的端到端拓扑、进程/信任边界、Helper 生命周期、macOS TCC 权限主体与 Windows 差异、broker 凭据与 fail-closed、多端会话与 PiP/overlay 归属。
> 阅读材料：`docs/cua-permission-broker/` 9 篇全部；`docs/cua/` 中 permission/onboarding/migration/pip/windows/permission-error 相关 9 篇 + `docs/cua/producer/` 6 篇（overlay 只做归属/生命周期概览）；`packages/services/src/cua-permission-broker/*.ts` 全部 11 个；`packages/zcode-cua/*.d.ts|*.js` 全部；`apps/zcode-cli` 真实 glue（node-repl-host/browser-use 侧 cua-bridge/cua-broker、bootstrap/mcp-config、cli/plugin-host-command、core/mcp.ts）；`packages/shared/src/runtimeEnv.ts`；protocol CUA 事件。
> 核实基线：ZCode-main @ 2026-10 工作区快照（占位包 `@zcode/zcode-cua` 0.6.5）。证据类型：【代码】真实源码 /【规格】docs 设计规格 /【契约】占位包类型 /【推断】推论。

## 0. 结论速览

1. **2026-08 后 CUA 没有独立 MCP 工具面**：模型只看到 shared `node_repl` 的单一 `js` 工具；`computer-use-client.mjs` 在每个 fresh Worker 内 bootstrap `agent.computerUse`，请求经"一次性认证的本地 bridge"路由到 shared-host `CuaControlPort`。旧 `mcp__computer-use__*` 只做历史只读回放。【规格】`docs/cua-permission-broker/zcode-cua-permission-broker.md:7-12`
2. **端到端有两条 socket 链，不要混淆**：① Worker→node_repl host 私有 bridge（每次 js 调用签发一次性 token，主线程账本校验）；② node_repl host/共享 runtime→签名 Helper broker（稳定 socket/pipe + pluginAuthority）。Worker 永远碰不到 Helper 的原始 socket/凭据。【代码】`apps/zcode-cli/packages/node-repl-host/src/cua-broker.ts:26-128`、`.../src/cua-bridge.ts:78-131`
3. **TCC 授权主体是外置 `.app`**：产品 Helper 必须是独立签名的 `ZCode Computer Use.app`（bundle `dev.zcode.cua-helper`，Team `8A5X4JJ39T`），装在 `${ZCODE_HOME:-$HOME/.zcode}/computer-use/`，由 LaunchServices 拉起，绝不是 Electron 子进程；Python/uvx/local checkout 不得成为隐式权限主体。【规格】`cua-helper-installation.md:5-22,46-56`；`zcode-cua-permission-broker.md:20-26`
4. **懒启动是当前核心不变量**：Host 启动/Agent spawn/session create **零 Helper IO**；凭据是纯数据（稳定 socket/pipe 名 + spawn 铸 authority）；Helper 只由三类触发者启动（SDK 首调 `ensureBrokerAvailable`、设置页 getStatus、权限维护 restart）；darwin standalone 300s idle 自退是特性。【规格】`2026-09-21-cua-helper-lazy-startup-spec.md:19-38`
5. **Helper 不再拥有 Agent 生命周期**：2026-09-02 规范切断 `Helper ready/recovery → disposeWorkspace` 反向控制链，`agentInvalidationRequired` 恒 false；失败只降级为 CUA-only retryable（`broker_not_ready`）。【规格】`2026-09-02-helper-lifecycle-agent-runtime-isolation-spec.md:20-32,142-151,178-193`
6. **凭据已从 bearer token 演进为"身份模式"**：现行代码注释明确"当前 broker 是 identity 模式（socket + authority，无口令）"，遗留 `ZCODE_CUA_PERMISSION_BROKER_TOKEN` 仅作清洗兜底；docs 中的 token 叙述是迁移前威胁模型。**这是 docs 与代码的时差，引用时必须以代码为准**。【代码】`packages/shared/src/runtimeEnv.ts:64-76`；`cli/src/plugin-host-command.ts:88-95`
7. **confused-deputy 防护靠三重收口**：sanitize 从所有子进程 env 剔除 broker socket/authority；`__zcode-plugin-host` 在 import server 前校验 pluginId + 成对凭据 + `ZCODE_CUA_NODE_REPL_HOST=1`；官方 server 识别要求 pluginAuthority 与进程内捕获值逐字相等。【代码】`runtimeEnv.ts:47-117,222-235`；`plugin-host-command.ts:83-106`；`core/src/runtime/methods/mcp.ts:37-64`
8. **Windows 没有 TCC，但有自己的两阶段就绪与 peer 门**：named pipe 无 POSIX rendezvous，Helper 先 `transport_ready`（pipe 已绑 + bearer 认证装配）再 `ready`（完整 health）；Host 只接受自己 fork 的 child PID + 自己 mint 的 pipe 名；M8 peer 门在 win32 现状 fail-closed（缺 4 个 native peer 原语）。【规格】`windows-transport-ready.md:3-32`；`2026-09-21...lazy-startup-spec.md:155-172,267-277`
9. **权限错误不再自动分类**：`CUA_PERMISSION_REQUIRED` 已被删除，permission_denied 作为普通 MCP error 交模型判断；只有 official live `request_access` 的结构化 `_meta` 能触发一次 Host 确认 + 既有 onboarding。【规格】`2026-08-25-cua-permission-error-passthrough.md:9-26,51-65`
10. **多端只有两种投递语义**：桌面 `desktop-continuous`，手机/Web 只能 `web-remote-replayable` attachment 到已有桌面 host；remote/SSH/WSL/Docker/Linux 不创建 Helper、不跑 orphan reaper、不接权限观察；subagent 在 provider 面与执行面双层禁用 CUA。【规格】`zcode-cua-permission-broker.md:14-16,27`；`2026-08-27-computer-use-unsupported-environment-ui-spec.md:14-23`；`2026-08-18-subagent-computer-use-unavailable-spec.md:5-26`
11. **共享 host 持有 CUA 状态，Worker 是短命**：CuaRuntimePool 按 `workspaceKey + sessionId + remoteSessionId` 隔离；frame registry/action-state/kill switch/ControllerLease 由 shared host 持有；Worker 重建不销毁 runtime。【规格】`2026-08-24-cua-node-repl-sdk-migration-spec.md:90-99`；【规格】`multi-session-cursor-overlay-spec.md:55`
12. **对本项目最关键的可迁移点**：把"权限主体（谁被 OS 授权）"与"调用入口（谁在调）"拆成两个进程/两种凭据，入口侧只拿一次性调用凭据；执行器本地 fail-closed 且生命周期与调用方解耦。**不能照搬**：macOS TCC/.app/LaunchServices/Team 签名、Windows named pipe 两阶段语义、node_repl Worker 模型。

## 1. 端到端拓扑（A）

### 1.1 组件与进程/机器归属

| 组件 | 跑在哪 | 语言/形态 | 职责 | 证据 |
| --- | --- | --- | --- | --- |
| 模型 | 云端 provider | - | 只调 `mcp__node_repl__js`（单工具） | 【规格】`zcode-cua-permission-broker.md:7-12` |
| Agent CLI | 本机（desktop-local）或 remote host | Node 进程 | session/turn、MCP 客户端、spawn Worker | 【代码】`apps/zcode-cli/...` |
| node_repl MCP/host | 本机 Agent 子进程（stdio MCP） | Node（`@zcode/browser-use-plugin` 的 server） | 唯一 MCP server：`js` Worker、call ledger、私有 CUA bridge | 【代码】`node-repl-host/src/server.ts:395-462`；`cua-broker.ts:26-68` |
| shared-host CuaControlPort / ComputerUseRuntime | 同上进程（Agent 侧宿主） | producer `createComputerUseRuntime`（闭源，本仓为占位） | CUA runtime/session/frame/kill switch；按 workspaceKey/sessionId/remoteSessionId 隔离 | 【规格】`cua-dev-mcp-runtime-assets.md:29-36`；【契约】`packages/zcode-cua/index.d.ts:3-39` |
| Desktop Main / Local Host（services） | 桌面 App 主进程 | Electron main + Node | 组装默认 Helper host、resolver、权限服务、PiP 投递、spawn env 注入 | 【代码】`packages/services/src/node.ts:1051-1128,2378-2449` |
| Permission Broker / Helper | 本机独立进程 | 签名 `.app`（macOS SEA）或 Electron Node fork（Windows） | 持有 TCC/原生能力：AX/截图/输入/剪贴板；broker socket server | 【规格】`cua-helper-installation.md:5`；`windows-product-runtime.md:49-66` |
| 手机/Web 客户端 | 另一台设备 | Web | 只消费 projection，attachment 到桌面已有 Host | 【规格】`cua-pip-session-consumer-integration.md:28-31` |

### 1.2 ASCII 架构图（desktop-local，macOS/Windows 同构）

```text
┌───────────────────────── 用户桌面（desktop-local Host 所在机器） ─────────────────────────┐
│                                                                                          │
│  模型 provider（云）                                                                      │
│      │  mcp__node_repl__js(code, title)   ← 模型可见工具面只有这一个                       │
│      ▼                                                                                   │
│  ┌────────────────────────── Agent CLI 进程 ──────────────────────────┐                  │
│  │ node_repl MCP host (stdio)                                        │                  │
│  │   ├─ call ledger（主线程）：每次 tools/call 签发 32B hex token     │                  │
│  │   ├─ Worker(thread)：bootstrap computer-use-client.mjs            │                  │
│  │   │     └─ agent.computerUse → 私有 bridge globals               │                  │
│  │   ├─ 私有 CUA broker：#1  仅本进程可见                             │                  │
│  │   │     win32  \\.\pipe\zcode-node-repl-cua-<uuid>                 │                  │
│  │   │     POSIX  $TMP/znrc-<uuid>.sock                             │                  │
│  │   │     校验 per-call token → 用账本 requestMeta 构造 context      │                  │
│  │   └─ ComputerUseRuntime (shared host CuaControlPort)              │                  │
│  │         └─ CuaRuntimePool（workspaceKey+sessionId+remoteSessionId）│                  │
│  │              └─ AccessibilitySession + KillSwitch + InputHold     │                  │
│  │                   └─ BrokerClient ── #2 Helper broker 连接 ───┐   │                  │
│  └───────────────────────────────────────────────────────────────┼──┘                  │
│                                                                  │ 稳定 socket/pipe      │
│   Desktop Local Host（services, 与 Agent 同机）                    │ + pluginAuthority     │
│     ├─ createDefaultCuaProductHelper → Helper host/resolver       ▼                      │
│     ├─ resolveSpawnEnv：注入 broker 凭据（纯数据）        ┌──────────────────────────┐   │
│     ├─ ICuaPermissionService：设置页/授权入口             │ 签名 Helper 进程          │   │
│     ├─ PiP 投递（macOS）/ 操作指示（Windows）             │ mac: ZCode Computer Use  │   │
│     └─ orphan reaper（darwin only, 一次性）               │ win: Electron RUN_AS_NODE│   │
│                                                          │  └─ ax_native(.node)     │   │
│                                                          └──────────┬───────────────┘   │
│                                                                     │ OS API            │
│                                          macOS: TCC(AX/ScreenRec) / Windows: UIA/WGC/输入│
└──────────────────────────────────────────────────────────────────────────────────────────┘
        ▲
        │ web-remote-replayable attachment（只挂到已有桌面 Host，不建 Helper）
   手机 / Web / remote client
```

### 1.3 每一跳的传输与认证

| # | 跳 | 传输 | 谁认证谁 | 证据 |
| --- | --- | --- | --- | --- |
| 1 | 模型 → node_repl | MCP stdio（Agent CLI 内部） | Agent 对 provider 的 MCP 工具面；外层 `js` 只做一次高风险审批 | 【规格】`2026-08-24...migration-spec.md:113-115` |
| 2 | node_repl host → Worker | `worker_threads` + `workerData` | host 给 Worker 注入 per-call bridge token；Worker 不接触 Helper 凭据 | 【代码】`ledger.ts:26-37,45-61`；`cua-bridge.ts:16-20` |
| 3 | Worker bridge → 私有 broker | Unix socket / Windows named pipe，NDJSON 单行请求/响应 | broker 用 `ledger.resolve(token)`：只认本次调用的 token，过期/伪造→"not authorized"；Worker 传来的 context 不采信 | 【代码】`cua-broker.ts:70-128` |
| 4 | runtime → Helper broker | 稳定 socket（darwin，`resolveBrokerSocketPath()` 推导）或稳定 named pipe（win32，Host 模块级 singleton）；JSON 行协议 | Helper 侧：peer 身份门（darwin 验对端签名；win32 M8 暂 fail-closed）+ 凭据组；Host 侧：`ready` PID == 自己 fork 的 child PID、pipe 名 == 自己 mint 的名字 | 【规格】`windows-transport-ready.md:28-32`；`2026-09-21...:67-78`；【代码】`windowsCuaDevHelperHost.ts:389-442` |
| 5 | Helper → OS | macOS TCC（Accessibility/Screen Recording/Automation）；Windows UIA/WGC/SendInput/剪贴板 | 由 OS 按 Helper 进程签名身份裁决；Windows 无对应授权主体，高完整性目标 fail closed | 【规格】`cua-helper-installation.md:5-6`；`windows-product-runtime.md:14-19,257` |

### 1.4 node_repl 私有 bridge 的关键契约（防伪造）

- **per-call token**：`createNodeReplCallLedger().open()` 每次 `tools/call` 生成 `randomBytes(32)`，`close()` 后立即失效；broker 只用 token 反查账本，**会话信息一律取账本里来自 core 的 request-context**，不信任 Worker 请求体里的 context（MR !3207 CR-01 修复：cell 可经 `parentPort` 冒充结果）。【代码】`ledger.ts:45-61`、`cua-broker.ts:92-107`
- **能力事实只由 broker 记录**：`entry.facts.computerUse = true` 在 token 校验后、subagent 检查前就记（"真实尝试"口径），供会话能力标签派生；CUA app identity 只从 runtime 结果的 `_meta` 读取 primary，Worker 改不了。【代码】`cua-broker.ts:98-120,138-155`
- **subagent 双兜底**：Worker bridge 先拒；broker 再按账本 context 的 `runtimeScope==="subagent"` 拒，绕过 bridge 也无效。【代码】`cua-bridge.ts:43-52`、`cua-broker.ts:105-107`
- **响应上限**：请求 1 MiB、响应 32 MiB 上限，超限直接失败（图片/大 AX 树的工程约束）。【代码】`cua-broker.ts:17`、`cua-bridge.ts:8,104-107`
- **runtime 捕获时机**：`captureComputerUseRuntimeFromEnvironment()` 在 `main()` 里就执行（bootstrap 前后 env 会被清），只用 `ZCODE_CUA_PERMISSION_BROKER_SOCKET`/`REFRESH_MARKER` 构造 runtime——否则"node_repl 永久把 Computer Use 判为 unavailable"。【代码】`server.ts:395-403,453-462`

## 2. Helper 生命周期（B）

### 2.1 安装与校验（macOS）

- **唯一来源**：`ZCode.app/Contents/Resources/cua-helper/ZCode Computer Use.app`；缺失/无效/不可读直接 fail-closed，**产品不下载、无下载兜底**。预览版用独立 variant 目录但签名身份仍是 `dev.zcode.cua-helper`（TCC 里显示同一个 app）。【规格】`cua-helper-installation.md:5-29`
- **安装位置**：`${ZCODE_HOME:-$HOME/.zcode}/computer-use/ZCode Computer Use.app`（preview 加 `/preview/`）。【规格】`cua-helper-installation.md:9-22`
- **校验清单**（替换前在 staging 校验）：`CFBundleIdentifier == dev.zcode.cua-helper`；本地下载源比较版本；`Contents/MacOS/<exe>` 含目标 arch；`codesign --verify --deep --strict`；`codesign -dv` 得非 ad-hoc TeamIdentifier 且等于期望 team（默认 `8A5X4JJ39T`，release 下空值直接拒）；`spctl -a -vv -t exec` 过 Gatekeeper/notarization。失败不得启动、不得注入凭据。【规格】`cua-helper-installation.md:46-56`
- **原子替换**：staging→校验→已有重命名 backup→staging 改名到最终→失败回滚→写 `.zcode-cua-helper-meta.json`；避免跨卷 rename。【规格】`cua-helper-installation.md:105-115`
- **每次启动前再验**（不只 install 时）：install 已清 quarantine，`open` 不会重评；只查一次 inode 挡不住同 UID 换二进制。`cuaHelperHost.doStart` 在 `launcher.launch` 前重跑 `verifyInstalled`（签名/Team/bundle/version），失败不启动。【规格】`zcode-cua-permission-broker.md:33`
- **entitlements 最小集**：恰好 `allow-jit` + `allow-unsigned-executable-memory`；已移除 `disable-library-validation` 与 `allow-dyld-environment-variables`；启动 env 经 `sanitizeHelperLaunchEnv` 剔除 `DYLD_*`/`LD_*`/`NODE_OPTIONS`。【规格】`cua-helper-installation.md:117-133`
- **dev 例外**：`ZCODE_CUA_HELPER_ALLOW_UNSIGNED_LOCAL=1` 保留 bundle/version/arch/Mach-O 校验但允许 ad-hoc、跳过 Gatekeeper；仍走产品路径 LaunchServices + 注入 socket/authority；同版本内容变化必须原子刷新。**dev 稳定 TCC 身份**靠自签证书 `zcode-cua-helper-dev`（designated requirement 锚公钥而非 CDHash）。【规格】`cua-helper-installation.md:58-90`；`local-dev-helper-runbook.md:8-24`

### 2.2 启动触发者与两种 Helper 形态

- **托管 managed Helper**（授权流/restart 用）：随机 socket、`launcherPid=Host`、无 idle 自退；host dispose 时收口。**standalone Helper**（默认懒启动）：稳定 socket、无 launcherPid → 300s 无连接自退。【规格】`2026-09-21...:202-208`
- 合法启动触发者只有三类：① Agent 首次 CUA 调用的 SDK `ensureBrokerAvailable`（唯一首调路径）；② 用户显式动作（设置页 getStatus 拉起、权限授权流）；③ Host 维护动作（`restartAfterPermissionGrant` 等 resolver 重启入口）。【规格】`2026-09-21...:32-35`
- macOS：由 LaunchServices `/usr/bin/open` 拉起，argv 统一由 `buildHelperOpenArgs(spec)` 构造（socketPath、exitLogPath、`ghostCursorOverlay:true` 等；设置页只读探测**故意不传** `--launcher-pid`/`--pip-mode`，即 standalone 语义）。【代码】`node.ts:1866-1886`；【契约】`broker-server.d.ts:6-10`
- Windows：Host `fork`（`ELECTRON_RUN_AS_NODE=1` + entry + `--socket` + `--parent-pid` + `--ghost-cursor-overlay`，addon 路径经 env `ZCODE_CUA_HELPER_ADDON`）。【代码】`windowsCuaDevHelperHost.ts:304-330`；【规格】`windows-product-runtime.md:49-61`
- **Windows 稳定 pipe 名**：Host app 进程铸一次（模块级 singleton，含随机段），Host 重建复用同名、跨 app 运行必不相同；spawn env 额外注入 **fork recipe** `ZCODE_CUA_WIN_HELPER_RECIPE`（JSON：entryPath/command/root/addonPath/commandEnv/hostPid）。【代码】`stableCuaTransport.ts:15-22`；【规格】`2026-09-21...:70-78,155-172`

### 2.3 健康检查与两阶段就绪

- **broker_info** 是 Helper 的身份/健康握手：产品启动等它返回；`broker_info.bundle_id` 存在且 ≠ `dev.zcode.cua-helper` 必须 fail 为 `CuaHelperError`（"warning 不够，因为 TCC grants 会属于错误的进程身份"）；还包含 `authorization_subject` 诊断字段（kind/bundle_id/display_name/version/pid/app_bundle_path/code_signing_identifier/team_identifier/signature/stable_identity/warnings）。【规格】`zcode-cua-permission-broker.md:18-23,61-79`
- **probeHelperHealth**（占位包导出契约，真实实现在闭源 broker barrel）：返回 `{bundleId, pid}`，Host 用它做 `isExactHealthPid`（health.pid 必须等于自己 fork 的 child pid）。【契约】`packages/zcode-cua/broker.d.ts:26-49`；【代码】`windowsCuaHelperHostSupport.ts:67-70,235-240`；`windowsCuaDevHelperHost.ts:413-442`
- **Windows 两阶段**：named pipe 无 `.pending→final` 原子让渡，所以 `transport_ready`（listener 已 bind + bearer 认证装配）先发，Host 收到后才向 Agent 发布 tuple；功能层初始化期间除 `broker_info` 外的 broker 方法返回 retryable `broker_unavailable` + `ready_state=warming_up`；完整 `ready` 才代表 health probe 成功。旧 Helper 只发 `ready` 时 Host 把它同时视为 transport ready（向后兼容）。【规格】`windows-transport-ready.md:5-32`；`producer/windows-helper-transport-ready.md:6-20`
- **Host 校验**：只接受自己 fork 的精确 child PID + 自己 mint 的 pipe 名；`ready.socketPath` 必须等于本代 socket、`ready.pid` 必须等于 child.pid；`transport_ready` 一经发布即成为 Agent-facing identity，后续 full health 失败也不得改发新 pipe。【规格】`windows-transport-ready.md:30`；【代码】`windowsCuaDevHelperHost.ts:389-411,267-276`

### 2.4 重启 / 崩溃恢复 / generation

- **生命周期 tail 是唯一线性化边界**：任何 fresh fork 必须排在上一代 exact child exit 之后；`kill` 只是请求，未观察到 exit 不得判死（termination blocker）。【代码】`windowsCuaDevHelperHost.ts:48-51,72-98,234-244`
- **恢复复用既有 tuple**（P1 stable facade 已落地）：`restartAfterCurrentStartPreservingTransport` 停旧 Helper 后用同一 socket/pipe 名重启，返回 `{handle, reused:true}`；已有 Agent 不需要换凭据。**只有显式 stop 清 tuple**；异常退出保留 tuple 供按需恢复。【代码】`windowsCuaDevHelperHost.ts:198-226,493-509`；【规格】`2026-09-02...:329-338`
- **Token/凭据轮换语义**：只能"Host lifetime 内稳定的 broker facade socket/token，Helper 只是可替换 downstream"或"Agent 侧 MCP 原地重连"；禁止"每次 restart 新 socket 让 Agent 拿旧 token"。【规格】`2026-09-02...:161-176`
- **崩溃恢复路径**：Helper 意外退出→Host 置 handle=null、invalidate transport、`onUnexpectedExit` 只通知"H​elper 状态失效"，不回收 Agent；下一次 CUA demand 复用已发布 tuple 恢复（懒启动 spec 图 3）。【代码】`windowsCuaDevHelperHost.ts:376-387`；【规格】`2026-09-21...:118-129`
- **显式 stop 的迟到消息**：stop 已开始但旧子进程未退出时，迟到的 `ready/transport_ready` 必须忽略（检查 `generation.stopped/externalStopEpoch/current.id`），不得复活旧 tuple。【规格】`2026-09-02...:316-327`；【代码】`windowsCuaDevHelperHost.ts:389-392`
- **历史 admission gate 已废弃**：`cua-agent-recovery-admission-gate.md` 全文标注"历史方案，已废弃"；`CuaAgentAdmissionGate` 仅保留兼容导出，默认装配不再注入、不阻塞 Agent spawn。【规格】`cua-agent-recovery-admission-gate.md:1-6`；【代码】`cuaAgentAdmissionGate.ts:13-19`、`cuaPermissionService.ts` 同目录 `index.ts:29-33`；lifecycle spec `:339-341`

### 2.5 孤儿清理 / idle / 卸载 / kill switch

- **孤儿清理**：darwin 进程级 once `reapOrphanedHelpers`（best-effort，永不 throw、不阻塞启动），与 Helper 内部 launcher-pid watchdog 互补；remote/非 desktop-local 不跑。【代码】`node.ts:1060-1066`；【规格】`zcode-cua-permission-broker.md:27`
- **idle 自退**：darwin standalone 300s 无连接 && 无 in-flight → `exit(0)`；副作用是 PiP/ghost cursor 随 Helper 关闭（属懒启动自然语义）。【规格】`2026-09-21...:36,273-275`
- **卸载**：产品无独立 Helper 下载/更新器；更新包复用桌面应用更新通道，Helper 随 App resources 更新（Windows 明确"不建立独立下载/更新状态"）。【规格】`windows-product-runtime.md:164,255-257`
- **kill switch**：`ZCODE_CUA_PRODUCT_HELPER=0|false|off`（macOS/Windows）显式关闭产品 Helper resolver；关闭后零足迹：无 host 对象、无 orphan 扫描、无 resolver 注入、无 Helper 进程、无 TCC 提示。但**开关是启动时门控**：显式禁用只写配置，不处理已运行 Agent/Helper，新 Host/Agent/App 启动时读取 disabled 才不注入 CUA MCP。【规格】`zcode-cua-permission-broker.md:28-32`；`2026-09-02...:196-215,344-346`；【代码】`runtimeEnv.ts:33-45`、`node.ts:767-779`
- **不支持平台**：Linux/远端 SSH/WSL/Docker/Server 设置页显示"不支持当前环境"信息卡、不提供控件、不发任何 CUA 启用/安装/卸载请求；手机 Web 远控沿用灰度、不启动本机 Helper。【规格】`2026-08-27-computer-use-unsupported-environment-ui-spec.md:5-12,14-36`

### 2.6 lazy startup 的执行细节（macOS vs Windows）

- macOS：spawn 恒注入稳定 socket + spawn 铸 authority；SDK 首调 `connect` 失败 ENOENT → `ensureBrokerAvailable`（5s 冷却）→ ① ensure-installed ② LaunchServices open standalone → flock 单实例 → warmup-retry。Windows：spawn env 注入 pipe 名 + authority + recipe；SDK 首调 `connect \\.\pipe\…` 失败 → win32 分支 `fork(entry,[--socket 稳定名,--parent-pid hostPid])` → bind ~300-500ms → warmup-retry（2s 窗）；并发首调第二个 fork bind 失败退出无害，两 Agent 连同一 Helper。【规格】`2026-09-21...:100-140`
- **设置页 getStatus 也遵循懒启动**：probe（300ms ping，不拉起）→ 无则拉起（darwin LS open / win32 Host fork）→ 就绪后查 `permission_status` 真值；拉不起 → `{available:false, idle:true}` + 友好文案，不报错。win32 已删除"only available on macOS"短路。【规格】`2026-09-21...:181-200`；【代码】`node.ts:1941-2016`
- **实现状态诚实标注**：lazy startup spec 标注 darwin MC-1/2/3 与 Windows 全链路 MC-4~7/9 "待实机验证"；Windows 实机缺口在 spec §7 明确列出（开发机是 macOS）。【规格】`2026-09-21...:5,267-277`

## 3. 权限模型（C）

### 3.1 授权主体与 grant owner

- **唯一授权主体 = 外置 Helper 进程**：macOS TCC 的 Accessibility / Screen Recording / Automation 授权按进程签名身份记录，所以 Helper 必须是独立签名的 `.app`；产品模式"keeps the Helper as the TCC owner. Python/uvx must never become the implicit permission owner"。【规格】`zcode-cua-permission-broker.md:20-24`
- **身份诊断字段**：`broker_info.authorization_subject` 与 `request_access.owner` 暴露"macOS 眼里这次授权会记到谁"：`kind/bundle_id/display_name/version/pid/app_bundle_path/code_signing_identifier/team_identifier/signature/stable_identity/warnings`；打包构建的签名信息来自 `/usr/bin/codesign -dv --verbose=4`；开发/非 mac 测试允许缺失但必须作为 diagnostics 报出，不能假装 identity 稳定。`stable_identity=true` 的判据是"签名 app bundle 且 signing identifier 匹配期望 bundle id 且有 team identifier"，否则 warnings 必须说明缺哪个信号。【规格】`zcode-cua-permission-broker.md:61-79,109`
- **grant owner 在 UI 上的含义**：设置页只展示"授权主体是哪个 app + 权限两行"（2026-08-17 收敛），不再展示 Helper 运行态/身份行与 Input controller 行；授权目标路径固定 `${ZCODE_HOME:-$HOME/.zcode}/computer-use/ZCode Computer Use.app`，Python/Terminal/uvx/local checkout/legacy broker 不得显示 product onboarding。【规格】`cua-helper-permission-onboarding.md:12-17,24-28`
- **Renderer 拿不到敏感身份**：onboarding IPC 只返回 `success/canceled/sessionId/returnedFromSettings/restartHelperAfterReturn/error`；Helper 文件系统路径与身份诊断字段留在 Main 内部做验签/session 协调。Renderer 请求只含 `operationId`/`initialPermission`/`requiredPermissions`（早期 `prepareAllRequired`/`missingPermissions` 已不在契约）。【规格】`cua-helper-permission-onboarding.md:35-39`

### 3.2 权限状态查询的两种模式：只读 vs 主动像素探针

这是 CUA 权限设计里最容易抄错的隐私契约：

| 模式 | 触发 | 行为 | 证据 |
| --- | --- | --- | --- |
| 只读（默认） | 省略 options 或 `includeFunctionalProbes:false` | 只读 TCC 状态；Settings 挂载、timer 轮询、窗口 focus、普通 refresh **绝不执行像素捕获** | 【规格】`zcode-cua-permission-broker.md:83-94` |
| 主动像素探针 | `includeFunctionalProbes:true` | 真抓一次非空像素（"TCC granted 只说明系统记录了授权，不保证 WindowServer 放行像素"）；Settings 只能在显式权限返回或 Helper 成功重启后请求一次 | 【规格】同上；【代码】`node.ts:2035-2043` |
- Renderer 侧用 OR 语义合并并发刷新：`slot.pendingFunctionalProbe ||= options?.includeFunctionalProbes === true`——至多一个排队请求携带 active 标志，后续只读刷新不能把它擦掉；workspace/context 变化使排队请求与迟到结果失效；一次 active 结果建立 readiness 后可用 sticky-ready 快照维持。【代码】`packages/ui/src/lib/cuaPermissionStatusStore.ts:255-282`；【规格】`zcode-cua-permission-broker.md:90-93`
- remote/mobile attachment 永不发起该探针、永不创建 Helper。【规格】`zcode-cua-permission-broker.md:94`
- **命名注意**：文档与 UI wire 用 `includeFunctionalProbes`；占位包 `CuaPermissionStatusQueryOptions` 只声明了 `probeScreenCapture?: boolean` + `[key:string]:unknown`（历史名），实际生效的是前者。引用实现时以 UI/services 代码为准。【契约】`packages/zcode-cua/broker.d.ts:123-126`；【代码】`cuaPermissionStatusStore.ts:282`

### 3.3 `request_access` 引导链（只读快照 → 确认 → 系统设置 → 重启 Helper）

- `request_access` 是**只读权限快照，不直接触发 native prompt**；producer 在 macOS 成功结果的 namespaced `_meta` 提供版本化状态（key `zcode.cua/request-access-status-v1`，schemaVersion 1、platform `darwin`、`grantOwner`、accessibility `granted|stale|denied`、screenRecording `unknown|granted|denied`）。【规格】`2026-08-25-cua-request-access-onboarding-spec.md:9-11`；【契约】`request-access-contract.d.ts:3-15`、`request-access-contract.js:1`
- 触发五重门：official-CUA authority + 精确工具名 + 严格 schema + 本地 `desktop-continuous` live event + 新鲜 Host 状态。链路：live official request_access → typed metadata → authority-gated CLI projection → desktop-only live observation → fresh Host status → ZCode confirmation → fresh Host status → 既有 onboarding → Helper restart + refresh；history/recovery/mobile/remote/普通错误一律无副作用。【规格】`2026-08-25-cua-request-access-onboarding-spec.md:13-25`
- 去重与顺序：同一 `workspaceKey+sessionId+turnId+missingPermissions` 只确认一次；确认前后都调 `getStatus(...,{includeFunctionalProbes:false})` 只用新鲜状态；两项都缺时顺序固定 Accessibility → Screen Recording；用户取消不打开系统设置；授权返回后只按 owner 合同重启 Helper 并刷新，不调用 conversation command、不自动 continuation、不重放动作。【规格】`2026-08-25-cua-request-access-onboarding-spec.md:29-37`
- Accessibility 入口会拉起 Helper 让 Helper 自己触发 macOS prompt；Screen Recording 没有等价 prompt API，只能打开系统设置页。后台任务 live request_access 缺权时先只读复查 Helper 状态 → 弹 ConfirmDialog → 用户确认后再复查并按序打开仍缺项；取消/同轮重复/已恢复/未知/不可用均不打开。【规格】`cua-helper-permission-onboarding.md:18-22`
- 历史 transcript 里的旧 `CUA_PERMISSION_REQUIRED` 文本、Tool Card 文本、普通 tool error、第三方同名 MCP、malformed metadata 都不能触发 onboarding。【规格】`2026-08-25-cua-request-access-onboarding-spec.md:45-46`；`2026-08-25-cua-permission-error-passthrough.md:51-65`

### 3.4 权限错误如何穿透到 UI（已改版）

- **不再自动分类**：ZCode 不再消费/生成 `CUA_PERMISSION_REQUIRED`；Helper/Broker 失败作为普通 MCP tool error 进入模型和 ToolCallRow，模型读准确错误后自行决定调只读 `request_access`、重新激活应用或重新观察。根因是 `permission_denied` 同时承载 TCC、foreground policy、Windows UIPI、Linux input、无效 native 能力等不同失败，跨层细分仍不能证明真实权限缺口——**删除自动分类比收窄分类更可靠**。【规格】`2026-08-25-cua-permission-error-passthrough.md:9-26`
- 普通错误仍无 UI 副作用（Renderer 不因 tool error 自动弹确认框/系统设置/onboarding）；只有官方 live `request_access` 的结构化 `_meta` 可触发一次 Host 确认流程。`broker_unavailable` 继续映射 `CUA_NOT_READY`（transport readiness/重试安全，不属于权限推断）。【规格】`2026-08-25-cua-permission-error-passthrough.md:52-64`
- 设置页与 Composer 状态入口保留；桌面 continuous 与手机 replayable 展示相同普通失败事实，live permission observation 只存在于本地 desktop continuous。【规格】同上

### 3.5 Windows 侧的权限差异与现状

- **没有 TCC**：Windows 不需要独立 `.app` 授权主体，Helper 由安装包内的 Electron Node 运行时启动，`ax_native.node` 作为安装资源发布；因此没有"grant owner"概念，权限接口仍保留但语义为平台诊断（`getStatus` 在 win32 走"probe → Host fork 拉起 → `permission_status` 真值"）。【规格】`windows-product-runtime.md:14-19`；`2026-09-21...:181-200`；【代码】`node.ts:1895-1906,1947-2016`
- **真实限制是 UIPI/完整性级别与会话隔离**：产品明确"不新增 Windows 管理员提权；高完整性目标继续 fail closed"；`permission_denied` 在 Windows 可能表示 UIPI/前台策略而非"缺权限"。【规格】`windows-product-runtime.md:255-258`；`2026-08-25-cua-permission-error-passthrough.md:24-26`
- **Windows peer 身份门尚未落地**：lazy startup spec 自述"M8 fail-closed，缺 4 个 native peer 原语"，"现状 CUA 在 win32 fail-closed"；overlay spec 进一步指出 Windows peer 门有暂态（`WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN`，`helperMain.ts:1054-1070`）。**这是移植到 Windows 时最需要注意的信任缺口**。【规格】`2026-09-21...:171-172,271-273`；`producer/multi-session-cursor-overlay-spec.md:83-93`
- dev 放开：非打包本地运行时 `allowUnsignedLauncherLocalDev`/`allowExternalBrokerClientLocalDev` 只在 dev 打开；产品运行时默认严格校验 manifest/arch/Electron 版本/SHA-256，任何不匹配 fail closed，诊断只报稳定 reason 与相对 artifact。【代码】`node.ts:1878-1880`；【规格】`windows-product-runtime.md:133-150`

## 4. 安全：凭据、注入、peer、fail-closed（D）

### 4.1 凭据模型的"时差"：docs 写 token，代码是身份模式

- **docs（迁移前威胁模型）**：broker token 是 "bearer credential scoped to the target `zcode-cua` MCP server only"，只能注入该 server 自己的 env/args，不得在 agent 全局 env 保留/继承，不得到达其他 MCP server、hooks、Bash/tool 子进程；泄漏后果是"任何同 agent 进程都能驱动持有 TCC 的 Helper"（confused deputy）。`--launcher-pid` 是调用方提供的，所以产品把 **peer verification 作为必需第二道门**。【规格】`zcode-cua-permission-broker.md:25`
- **代码（现行）**：注释明确"当前 broker 是 identity 模式（socket + authority，无口令）"，`CapturedCuaBrokerCredentials` 只有 `socket + pluginAuthority (+refreshMarker)`；遗留 `ZCODE_CUA_PERMISSION_BROKER_TOKEN` 仍从所有子进程 env 剔除，因为用户机可能装着认 token 的旧版 Helper，"剔除一个已不用的键是零成本的"。`resolveZCodeCuaBrokerToken()` 直接 `return undefined`。【代码】`packages/shared/src/runtimeEnv.ts:64-76,141-175`；`cli/src/plugin-host-command.ts:88-95`；`bootstrap/src/mcp-config.ts:30-32`
- **结论**：引用时不能把"token"当成现行实现；真正在流转的是 `(socket/pipe 路径, pluginAuthority 随机数, 可选 refreshMarker)` 三元组，authority 的用途是**配置来源证明**（谁能证明自己是 resolver 注入的官方 server），不是连接口令。Helper 侧的连接裁决靠 identity/peer 门。【推断，基于上述代码】

### 4.2 两条 env 注入路径与 sanitize

- **路径 A（desktop session/create）**：desktop 解析 `mcpServers` 时把 socket/凭据注入目标 server 的 per-server `env`（在 `buildMcpStdioEnv` 之后 spread），**从不触碰 `process.env`**。【规格】`zcode-cua-permission-broker.md:25`
- **路径 B（全局 CLI config）**：`~/.zcode/cli/config.json` 里的 zcode-cua 配置随 CLI 子进程 `process.env` 到达；CLI 入口在 bootstrap/工具代码运行前把 socket/authority **捕获进进程私有内存 store 并从 `process.env` 删除**，bootstrap 只读该 store 注入；若直接读 `process.env` 会 fail closed（没有捕获元组时全局 zcode-cua **整个 omit**，不裸跑）。【规格】`zcode-cua-permission-broker.md:25,34`；【代码】`runtimeEnv.ts:150-175,201-214`
- **sanitize 清单**（`SANITIZED_RUNTIME_ENV_KEYS`）同时剔除 CUA 连接材料与其他敏感 env：`ZCODE_CUA_PERMISSION_BROKER_SOCKET`、`ZCODE_CUA_PERMISSION_BROKER_TOKEN`（遗留）、`ZCODE_CUA_PERMISSION_BROKER_REFRESH_MARKER`、`ZCODE_CUA_PLUGIN_AUTHORITY`，以及代理/CA/OTEL/telemetry 等；`NON_TOOL_PASSTHROUGH_RUNTIME_ENV_KEYS` 阻止经 tool-env-passthrough 恢复到 Bash/tool 子进程（否则等于绕过剔除）。【代码】`runtimeEnv.ts:47-117`
- **半组 fail-closed**：capture 只在 `socket && pluginAuthority` 同时存在时落快照；只出现一半时清空旧快照（"上游注入不完整或正在轮换，不能复用另一半"）。【代码】`runtimeEnv.ts:157-175`
- **恢复注入只发生在可信边界**：bootstrap 仅对 name===`node_repl`、stdio、且属于官方 plugin 的 trusted server 名集合注入；注入内容含 socket、refreshMarker、pluginAuthority、`ZCODE_CUA_NODE_REPL_HOST=1`、官方 plugin id。【代码】`bootstrap/src/mcp-config.ts:108-166`；trusted 名来自"配置对象身份 + 官方 plugin id"而非名字/命令，避免用户覆盖配置继承 authority（`mcp-config.ts:37-58`）。`__zcode-plugin-host` 在 import server 模块**之前**校验 pluginId 官方、凭据成对、`ZCODE_CUA_NODE_REPL_HOST=1`，任一不符直接拒绝，避免先加载不可信模块再暴露凭据。【代码】`cli/src/plugin-host-command.ts:23-29,83-106`

### 4.3 官方身份与 peer 校验（谁会验谁）

| 校验点 | 校验方 | 规则 | 证据 |
| --- | --- | --- | --- |
| launcher 代码要求（macOS） | Helper | `anchor apple generic and (identifier "dev.zcode.app" or "dev.zcode.app.preview") and certificate leaf[subject.OU]="8A5X4JJ39T"`；**不得放宽为仅 Team**；`ZCODE_CUA_LAUNCHER_BUNDLE_ID` 仅 dev 覆盖 | 【规格】`zcode-cua-permission-broker.md:26` |
| broker_info bundle_id | Host | 存在且 ≠ `dev.zcode.cua-helper` → `CuaHelperError`（warning 不够，TCC 会记错主体） | 【规格】`zcode-cua-permission-broker.md:23` |
| ready/health PID | Host | `ready.pid == child.pid`、`health.pid == child.pid`；`ready.socketPath == 本代 socket`；只接受自己 fork 的 child、只发布自己 mint 的 pipe 名 | 【代码】`windowsCuaDevHelperHost.ts:389-442`；`windowsCuaHelperHostSupport.ts:235-240`；【规格】`windows-transport-ready.md:30` |
| 启动前重验 | Host | 每次 `launcher.launch` 前重跑 `verifyInstalled`（签名/Team/bundle/version），缩小 TOCTOU 窗口 | 【规格】`zcode-cua-permission-broker.md:33` |
| 官方 server 身份 | core/bootstrap | 需要 resolver 注入的官方 plugin id **且** env 中 pluginAuthority 与本进程捕获值逐字相等；server 名、tool 名、manifest env 可被仿造，不能单独作为信任依据 | 【代码】`core/src/runtime/methods/mcp.ts:37-64`；`mcp-config.ts:37-58` |
| 官方 CUA frame authority | Host media bridge | 与 broker credential 注入资格是**两个独立集合**；只有携带真实 plugin authority 的 descriptor 能进 exact-raster 校验；通用 node_repl/Browser Use 结果走普通媒体路径 | 【规格】`2026-08-24...migration-spec.md:81-86`；`2026-08-25-cua-authority-verdict-audit.md:39-45` |

### 4.4 fail-closed 规则清单（逐条）

| # | 条件 | 必须行为 | 证据 |
| --- | --- | --- | --- |
| 1 | Helper 缺失/无法启动/拒绝连接/不健康 | 不向 Agent 注入 broker 凭据 | `zcode-cua-permission-broker.md:22` |
| 2 | `broker_info.bundle_id` 不匹配 | 产品启动 fail 为 `CuaHelperError`，不是 warning | 同上:23 |
| 3 | 安装校验失败 / 启动前重验失败 | 不启动、不注入；产品不下载兜底 | `cua-helper-installation.md:46-56`；`zcode-cua-permission-broker.md:30-33` |
| 4 | 直接 CLI 跑全局 zcode-cua 但无 broker socket（macOS） | 无条件 omit `zcode-cua`（不依赖 `..._UNAVAILABLE` 开关）；本地 dev 可显式 `ZCODE_CUA_HELPER_ALLOW_UNAUTHENTICATED_LOCAL=1` 裸跑 | `zcode-cua-permission-broker.md:34` |
| 5 | zcode-cua matcher 漏匹配 | 漏匹配=fail-open（无 broker→裸 TCC owner）危险；过度匹配=fail-closed（无害）→ 必须覆盖 python -m、尾分隔符等所有形态 | 同上:35 |
| 6 | 凭据只出现半组 | 清空快照，不注入 | `runtimeEnv.ts:157-175` |
| 7 | 非官方 plugin / 非 shared node_repl host / 凭据不完整 | plugin-host 在 import 前拒绝启动 | `plugin-host-command.ts:83-106` |
| 8 | 官方 server 缺 authority 或不等 | 不进入 official CUA 信任/保护路径 | `core/.../mcp.ts:37-64` |
| 9 | remote/SSH/WSL/Docker/非 desktop-local | 不创建/安装/启动 Helper、不跑 reaper、不接 resolver、不接 live 权限观察 | `zcode-cua-permission-broker.md:27`；`node.ts:788-800`；`2026-08-27...:14-36` |
| 10 | subagent | provider 面移除官方 CUA server/Skill；执行面返回 isError `SUBAGENT_COMPUTER_USE_UNAVAILABLE`，不进入 kill-switch/Broker/handler | `2026-08-18-subagent...:12-26,40-42` |
| 11 | Helper 退出/health 失败/未 ready | 只影响本次 CUA 调用（bounded retry → `broker_not_ready`），不触发 Agent recycle/session error；动作请求按 delivery-state，`possibly_sent` 禁止自动重放 | `2026-09-02...:178-193`；`windows-transport-ready.md:32` |
| 12 | `transport_ready` 之前 / 迟到 ready | 不向 Agent 发布 tuple；显式 stop 后忽略迟到 ready/transport_ready，不复活旧 tuple | `windows-transport-ready.md:30-31`；`2026-09-02...:316-327` |
| 13 | 旧 child 未观察到 exit | 不得 fresh fork（termination blocker；kill 只是请求） | `windowsCuaDevHelperHost.ts:48-98,234-244` |
| 14 | Windows 高完整性（UIPI）目标 | fail closed，不新增管理员提权 | `windows-product-runtime.md:257` |
| 15 | 像素探针失败/超时 | screenCaptureProbeOk=false（fail-closed），不把"TCC granted"当端到端可用 | `node.ts:2035-2043,823-836` |
| 16 | 权限状态查询未显式要求 | 只读，绝不做像素捕获 | `zcode-cua-permission-broker.md:83-94` |
| 17 | Screen Recording 预检失败（例外） | **刻意 fail-open**：沿用 Helper 报告值，不比不做预检更糟；硬报 denied 会把已授权用户推进无意义引导 | `node.ts:838-862` |
| 18 | 插件关闭/产品 Helper 开关关闭 | 关闭后不创建 host/不探测/不产生子进程/不弹 TCC；只门控后续启动，不杀已有会话 | `zcode-cua-permission-broker.md:28-32`；`2026-09-02...:196-215` |

### 4.5 confused deputy 的完整攻击面（设计者视角）

1. **同 agent 恶意 MCP/Bash**：若 broker socket 出现在 agent 全局 env，任何同 agent 进程可驱动已授权 Helper → 所以 sanitize 必须覆盖所有子进程与 tool-env-passthrough 恢复通道。【代码】`runtimeEnv.ts:47-117`
2. **伪造官方 MCP server**：server 名/命令/manifest env 都可仿造 → 用 pluginAuthority 逐字匹配 + 配置对象身份 + 官方 plugin id 三重确认。【代码】`core/.../mcp.ts:37-64`；`mcp-config.ts:37-58`
3. **Worker 内伪造会话/结果**：cell 能经 `parentPort` 冒充结果消息 → 会话 context 只取主线程账本、CUA app identity 只在 broker 侧从 runtime 结果读、per-call token 关闭即失效。【代码】`ledger.ts:1-6,45-61`；`cua-broker.ts:92-120`
4. **同 UID 换 Helper 二进制**：install 只验一次不够 → 每次 launch 前重验。【规格】`zcode-cua-permission-broker.md:33`
5. **Python/uvx 成为隐式权限主体**：无 broker 时不能"裸跑" zcode-cua → omit + fail closed。【规格】`zcode-cua-permission-broker.md:24,34`

---

## 5. 会话与多端（E）

### 5.1 两种投递语义（唯一合法组合）

| 端 | delivery profile | 说明 | 证据 |
| --- | --- | --- | --- |
| 桌面 App | `desktop-continuous` | 实时连续链路；Helper/TCC/frame freshness/kill switch/input hold/`possibly_sent` 都在 runtime 内部执行，relay/main 不承载这些状态 | 【规格】`zcode-cua-permission-broker.md:14-16` |
| 手机 / Web | `web-remote-replayable` | 只能 **attachment 到已有桌面 host**；用 snapshot/gap 恢复；不创建第二个 Helper/runtime | 【规格】同上；`2026-09-02...:227-228` |
- 两条链路不得互相扩散：desktop continuous 不拼接 web replayable 的恢复消息，mobile 不接收 live permission observation。修改 stream/snapshot/queue/重连时必须同时验证两种语义（仓库根 AGENTS.md 的同名硬规则）。【规格】`cua-agent-recovery-admission-gate.md:107`；`2026-08-25-cua-request-access-onboarding-spec.md:41-44`
- `computer-use/operation-event` sideband 恒在 conversation snapshot/queue/replayable 之外（PiP 与 Windows 指示器都消费它，不进入会话状态）。【规格】`cua-pip-session-consumer-integration.md:28-31`

### 5.2 远端 / SSH / WSL / 容器 / Linux 的限制

- `desktop-attached-remote`（mobile /remote、SSH/WSL/container workspace）**不得**自动安装/启动 Helper、跑 orphan reaper、接 product MCP resolver；remote session 通过 shared desktop host attachment，而不是在远端另立权限链。`createLocalServices` 用 `shouldCreateDefaultCuaProductHelper` 强制（非 desktop-local 为 false）。【规格】`zcode-cua-permission-broker.md:27`；【代码】`node.ts:781-800`
- 代码判据：`hasBuiltInCuaPlugin && serviceAuthorityMode === "desktop-local" && !hasRemoteWorkspaceIdentity && !hasInjectedResolver`——四个条件任一不满足都不创建默认 Helper。【代码】`node.ts:788-800`
- 能力矩阵：本地 macOS=开关+macOS 权限；本地 Windows=开关；本地 Linux=不支持提示；远端 SSH/WSL/Docker/Server=不支持提示、不发启用/禁用/安装/卸载/更新请求；手机 Web 远控=沿用 Web 灰度、不启动本机 CUA Helper。【规格】`2026-08-27-computer-use-unsupported-environment-ui-spec.md:14-23`
- **不要照抄的行为**：Linux 旧 Python/uvx 安装路径已 DEPRECATED 移除（"canonical CUA MCP tool is now the TypeScript package @zcode/zcode-cua"）。【规格】`producer/install-linux.md:3-19`

### 5.3 subagent 边界（双层）

- **Provider 面**：CUA server 在部分 launch path 由父子 runtime 共享，所以 core 按"authority-verified official CUA server"从 child 的 provider-visible surface 移除（wildcard profile 移 server/tools；显式声明官方 server/tool/skill → 首次模型请求前配置错误）；SkillPort 隐藏官方 computer-use；第三方同名 MCP/Skill 不受影响。【规格】`2026-08-18-subagent...:5-9,12-22,28-42`
- **执行面**：producer 只信任 namespaced `com.zcode/request-context.runtime_scope`；顶层同名字段不可信；缺失/`main` 保持兼容。`runtime_scope=subagent` 时 30 个工具共享的 preflight 在 kill-switch、session work、Broker I/O、handler 之前返回 `isError` + `SUBAGENT_COMPUTER_USE_UNAVAILABLE`（连 request_access/stop/wait/clipboard 也不能绕过）。【规格】`producer/subagent-runtime-scope-boundary.md:3-16`；`2026-08-18-subagent...:24-26,40-42`
- 消费侧再兜一层：node_repl Worker bridge 的 `assertAvailable` 与私有 broker 都检查 runtimeScope（见 §1.4）。【代码】`cua-bridge.ts:43-52`；`cua-broker.ts:105-107`

### 5.4 PiP 归属与 focus 路由（macOS presentation）

- **所有权划分**：ZCode 只发布 product facts；`@zcode/zcode-cua` 拥有 PiP panels/grouping/dismissal/terminal badges/capture admission/native stacking/retry policy。【规格】`cua-pip-session-consumer-integration.md:3-6`
- **数据流**：`Renderer activeTaskId 变化 → Desktop Main focus router → FocusChanged(revision, windowId, sessionId) → window-scoped Local Host → producer pip-session client`；`computer-use/operation-event → TurnStarted/TurnEnded/SessionClosed → producer PipSessionCoordinator`。【规格】`cua-pip-session-consumer-integration.md:8-19`
- **Desktop Main 只持有 ephemeral 事实**：`{windowId → activeSessionId}`、当前 focus 的 ZCode 窗口、进程生命周期内单调递增 `focusRevision`；后台 renderer 可更新自己的 map，但只有**最前 ZCode 窗口**发布全局 focus；跨 ZCode 窗口切换时先给旧 host `sessionId=null` 再给新 host；无 ZCode 窗口聚焦时最后一个 host 收到 null。Main 不承载 task/session 业务状态。【规格】`cua-pip-session-consumer-integration.md:21-26`
- **严格兼容**：Local Host 只 import `@zcode/zcode-cua/pip-session` 纯事件类型 + `pip-session/node` control client，不解析 NDJSON、不按名字调 broker 方法；必须完成 runtime/version handshake，不匹配就对该 Helper transport 关闭 Auto-PiP、只产一条诊断，**没有** `pip_stop`/clear/freeze/legacy group fallback。【规格】`cua-pip-session-consumer-integration.md:35-40`
- **Windows 不参与 PiP**：`cuaPipSessionService` 的 enabled 条件写死 `platform==="darwin" && serviceAuthorityMode==="desktop-local"`；Windows 用户可见反馈是 Desktop Main 的顶部操作指示器（消费 `computer-use/operation-event`，30s 兜底，ephemeral，不进 replayable/snapshot）。【代码】`node.ts:1910-1911`；【规格】`product-capability-map-office-computer-use.md:20,51,64`

### 5.5 多会话 overlay 边界（只做归属/生命周期概览）

- **光标 key = 产品 sessionId**（`sess_*`），颜色稳定派生（11 色板），per-session 显示/隐藏互不干扰；显示生命周期与 turn 对齐：`turn-ended/session-closed` 且无其他 running session 才隐藏，`turn-started` 刻意不主动显示（无动作显示光标无意义）；20s 活动租约 + 180ms 淡出只是轮内兜底。【规格】`producer/multi-session-cursor-overlay-spec.md:83-131,339-360`
- **输入仍是单写者**：`ControllerLease` 全局唯一 controller（文件锁，Windows 分支 `controllerLease.ts:122` 就绪）；多光标仅展示层，不放开多 controller 并发输入。【规格】`producer/multi-session-cursor-overlay-spec.md:55,325`
- **overlay 平台差异**：macOS 从"单窗并集"改为"单窗跟随反馈点所在屏"（多 cursor 需每屏一窗）；Windows native overlay 2026-10-24 一次到位（多实例+颜色+曲线，复用共享 C++ 运动学核心），真机验收待做；Windows 上 ghost cursor 曾是缺失能力，用户反馈只有 Electron 指示条。【规格】`producer/ghost-cursor-multi-display-spaces.md:15-31`；`producer/multi-session-cursor-overlay-spec.md:53-55,585-600`
- **Windows peer 门暂态的边界**：overlay spec 明确 Windows peer identity gate 有 `WINDOWS_PEER_IDENTITY_GATE_TEMPORARILY_OPEN` 暂态，"overlay 是用户可见反馈面，其安全语义随这道门一起收口"——即展示层不单独引入新门，但也不能当作已安全。【规格】`producer/multi-session-cursor-overlay-spec.md:83-93`

### 5.6 turn / session 清理与"丢首事件"问题

- **PiP 的 `turn-started` 可能早于 Helper**：turn 一开始就发 `turn-started`，但 Helper 由首次 CUA 调用懒启动，此刻 `resolveCredentials()` 拿不到 socket → 早期实现静默丢弃，PiP 永久停在上一轮完成态。现行修复：只缓存**一条** pending `turn-started`（新 turn 顶掉旧的），凭据一可用立刻补发，另有 250ms 有界轮询、30s deadline（因为协调器 capture pending TTL 只有 2s）；协调器按 `sequenceNumber` 判重，补发幂等；同一 turn 的 `turn-ended/session-closed` 若先到则作废缓存。【代码】`cuaPipSessionService.ts:59-94,136-187,199-232`
- **清理动作**：`dispose()` 置 disposed、取消补发定时器、清缓存、关闭 client；client 按 socketPath 作 key，credential 变化时关旧开新；`version_mismatch` 会把该 transport 标记 disabled，不再重试。【代码】`cuaPipSessionService.ts:103-134,274-284`
- **turn/session 事实不重放**：授权完成后不自动恢复/重放原任务；`request_access` 不 replay、不 continuation；历史 transcript 只读。【规格】`2026-08-25-cua-request-access-onboarding-spec.md:37`；`2026-08-25-cua-permission-error-passthrough.md:64-65`
- **workspace 身份隔离**：`workspaceKey = workspaceIdentity?.trim() || workspacePath`（去重/绑定/缓存/队列/持久化/请求关联统一 key）；Helper recovery 不能按裸 workspacePath 把不同 remote workspace 当成同一 Agent；node_repl bridge 请求必须带 `workspaceIdentity/workspacePath/workspaceKey/sessionId/remoteSessionId/turnId/traceId/clientMode/deliveryKind`，subagent 在 bridge 入口 fail closed。【规格】`2026-09-02...:230-231`；`cua-dev-mcp-runtime-assets.md:39-45`；`cua-broker.ts:157-199`

## 6. 对本项目（PCCtrl / Windows 本机执行器）的最小可行方案（F）

> 前提：AstrBot（容器）里模型通过插件/MCP call 工具 → 真正执行在用户 Windows 主机。ZCode 的可迁移点是"权限主体与调用方分离 + 凭据分层 + fail-closed"，不可迁移的是 macOS TCC/.app、node_repl Worker、named pipe 两阶段语义。

### 6.1 执行器形态与"权限主体"

- **运行在交互式用户会话内**（用户登录桌面后的进程，非 Session 0 Windows 服务，非管理员）：UIA/SendInput/WGC 都依赖交互桌面，服务化会直接失去桌面访问并被 UIPI/会话隔离挡住。若必须服务化，让服务只做隧道/鉴权，真正操作在 per-user agent 进程里（两者用本地 IPC，权限主体仍是 per-user agent）。【推断，基于 ZCode"desktop-local 才有 native 能力"与"高完整性 fail closed"的边界】
- **显式拒绝而不是降级**：屏幕锁定/RDP 断开/会话切换/目标窗口高完整性 → 输入类工具返回稳定错误码（如 `os_session_unavailable`/`uipi_blocked`），截图返回错误而不是黑图当成功；绝不为通过检查而请求提权。
- **单一权限主体**：不要让容器、容器内 Python、uvx 等成为隐式主体（这是 ZCode 反复强调的反面案例：Python/uvx 绝不能成为 implicit permission owner）；所有 OS 调用收敛在执行器一个进程/一个 Adapter 层。【规格】`zcode-cua-permission-broker.md:24`

### 6.2 令牌设计（两层，不要只做一层）

| 层 | 用途 | 实现建议 | ZCode 对应 |
| --- | --- | --- | --- |
| L1 配对/会话令牌 | 容器 ↔ 执行器建立 MCP session / 重连 | 长随机串存容器配置与 Windows 凭据库（DPAPI）；只走 HTTP header/初始化握手，**绝不放进 tool 参数或日志**；可撤销、可轮换 | 对应"只发给目标 server 的凭据"边界 |
| L2 每调用能力令牌 | 每次工具调用授权 | 执行器侧 mint（或校验）短期 token，绑定 `sessionId+turnId+toolCallId+expiry`，调用结束即失效；执行器内校验后才进 OS Adapter | node_repl call ledger：per-call 32B token + close 即失效（`ledger.ts:45-61`） |
- **本地监听收口**：执行器只监听 loopback，或用 Windows named pipe 并把 ACL 限制到当前用户 SID；不要监听 0.0.0.0（named pipe 若 ACL 宽松可经 SMB 被远端打开，这是 ZCode 用随机 socket 名+token/peer 想解决的问题，PCCtrl 必须自己显式做 ACL）。【推断，基于 `cua-broker.ts:32-35` 的随机 pipe/socket 名与 token 校验设计】
- **凭据不进执行器全局 env**：ZCode 的 sanitize 规则证明"全局 env 泄漏 = confused deputy"；PCCtrl 容器侧同理——L1 只给 MCP client 初始化用，不注入 Bash/其他插件子进程，不写日志。【代码】`runtimeEnv.ts:47-117`

### 6.3 生命周期（建议：常驻 + 按需校验，而不是懒启动）

- **执行器常驻**：跨容器场景无法像 ZCode 那样由 Agent SDK "首调自拉"（容器不能 fork Windows 进程）。可常驻但保证零桌面侵入：不创建窗口、不做输入/截图除非被调用；`idle` 只用于指标。
- **健康检查**：提供 `broker_info` 等价物：`{pid, version, osSessionId, integrityLevel, capabilities(screen/input/clipboard/ax), permissions(tcc 不适用→null), idle, uptime}`；检查只发生在明确边界（连接握手、每次调用前缓存 TTL、设置页打开），**不要每 10 秒轮询**（ZCode 明确移除周期 watchdog 的教训）。【规格】`2026-09-02...:31-32,127-140`
- **单实例与代际**：进程级 named mutex 保证单实例；内部 generation/epoch 管理"重启/显式 stop"：显式 stop 立即失效 generation 并清空已发布凭据；迟到 ready 不复活旧 tuple（可直接照搬 ZCode 的 `externalStopEpoch/terminationBlocker` 思路）。【代码】`windowsCuaDevHelperHost.ts:48-98,493-509`
- **崩溃恢复**：执行器崩溃 → 容器侧连接失败按可重试错误返回（`broker_not_ready` 语义），**不自动重放可能已执行的动作**；执行器重启后同一 session 继续可用（凭据不变/由 L1 重握手）。
- **启动清理**：执行器启动时清理上一代遗留子进程/临时文件（ZCode orphan reaper 的等价物，darwin once 且不阻塞启动）。【代码】`node.ts:1060-1066`
- **kill switch**：两级——① 容器插件配置开关只门控新 session（不杀运行中 turn）；② 执行器本地硬开关（配置文件/本地 UI）立即拒绝新调用，并可安全取消在途调用（输入类要保证释放按键/鼠标，避免卡住用户桌面）。【规格】`2026-09-02...:196-215`

### 6.4 PCCtrl 版 fail-closed 清单（可直接抄成验收用例）

1. 无 L1 令牌 / 令牌错误 / 过期 / 签名不符 → 拒绝连接，不暴露任何工具（不做"部分可用"）。
2. L2 令牌缺失、sessionId/turnId 不匹配、已消费/过期 → 拒绝该次调用，不进 OS Adapter。
3. 执行器未就绪 / 版本不匹配 / 会话锁被占 → 返回 retryable `not_ready`，不降级到"无校验执行"。
4. Windows 会话非活动（锁定/切换/RDP 断开）→ 输入类拒绝；截图明确报错，不返回黑屏/旧帧。
5. 目标窗口高完整性（UIPI）→ 明确 `uipi_blocked`，不静默失败、不提权。
6. 截图/观察帧过期（超过 TTL 或 target 变化）→ 拒绝基于旧帧的动作（ZCode stale frame/state_id 语义的等价物）。
7. 动作已发出但结果未知 → 返回 `possibly_sent`/结构化 delivery-state，容器侧禁止自动重放副作用动作。
8. 进程/会话关闭 → 释放输入 hold（keys up、mouse up）、清理临时文件、关闭窗口引用；不做"全局清理别人会话"的越界动作。
9. 权限/能力探测失败 → 该能力标 unavailable，不影响其他工具；不把 probe 失败伪装成"用户未授权"。
10. 大结果（截图/AX 树）超预算 → 截断或落 artifact 返回引用，不返回超限 payload（ZCode：请求 1MiB、响应 32MiB 上限）。

### 6.5 多会话隔离

- **命名空间**：所有运行时状态用 `sessionId`（必要时叠加 workspace/trace）作 key；ZCode 用 `workspaceKey+sessionId+remoteSessionId` 隔离 CUA runtime/frame/session，PCCtrl 至少 `sessionId` + 会话级 lease。【规格】`2026-08-24...migration-spec.md:90-95`；`cua-dev-mcp-runtime-assets.md:34`
- **输入单写者**：同一桌面同时只允许一个会话派发输入（ZCode 的 `ControllerLease` 全局唯一 controller）；其他会话排队或返回"桌面被占用"，不要并行注入（会互相打断/拖拽错乱）。【规格】`producer/multi-session-cursor-overlay-spec.md:55`
- **展示层与会话解耦**：overlay/指示器只表达"某会话正在操作"，不进会话快照、不用于恢复；PCCtrl 若做类似提示，同样只做 sideband。【规格】`cua-pip-session-consumer-integration.md:28-31`；`product-capability-map-office-computer-use.md:64`
- **清理语义**：session 关闭/turn 结束释放输入 hold、清除 frame 缓存、取消未完成调用；`turn-started` 类事件若因目标不可用被丢弃，要像 ZCode 一样可补发（单条缓存 + 有界重试 + 幂等 sequence），否则会话级 UI 会卡在上一轮。【代码】`cuaPipSessionService.ts:59-94`

### 6.6 坑（按 ZCode 踩过的）

1. **不要把"文档里的 token"当现状**：ZCode 代码已切 identity 模式；PCCtrl 做自己的设计时也别把"有口令"当安全核心，重点是"每次调用可归因、可失效、不可被同机其他进程复用"。
2. **不要照抄 peer 门**：ZCode 自己的 Windows peer 门都还是 fail-closed 缺口（缺 4 个 native 原语），说明"靠 OS 帮你认证对端"在 Windows 上并不便宜；PCCtrl 用显式 token + ACL + PID/会话校验更实际。
3. **不要把 node_repl 模型搬过来**：node_repl 是同机 Agent 内部的 Worker/桥，跨容器场景直接 MCP 传输 + 执行器内 Adapter 更简单；Worker 沙箱不提供跨机安全。
4. **不要定期探活**：ZCode 明确删除了周期 Helper 健康探测（每 10s probe → restart 会造成 Agent 反复 recycle 的工单）；PCCtrl 应只在调用/握手边界检查。
5. **不要把状态放错层**：Main/relay 只做转发与调度，不保存 task 队列/快照；PCCtrl 若有多端（手机看容器里的任务），同样让权威状态在会话/runtime，执行器只持有"当前能否操作"的本地事实。
6. **图片预算与超时**：截图要压缩/裁剪，默认不回传全屏原图；每个工具声明超时与取消语义（ZCode 工具契约要求）。
7. **屏幕/坐标**：DPI、多显示器、负坐标、缩放是 Windows 上最常见的"能点但点歪"来源；参考 ZCode 对多屏坐标/翻转的处理态度（先做坐标统一与断言，再谈功能）。【规格】`producer/ghost-cursor-multi-display-spaces.md:32-42`
8. **不要用"自动重试"掩盖同步问题**：ZCode 反复强调"不能用超时掩盖同步问题""不能在 Host 内无限后台重试"；PCCtrl 的重试要有绝对 deadline 与幂等边界。【规格】`2026-09-02...:100-101,192-193`

## 7. 未解问题 / 边界发现

1. **真实 broker server（51 个文件）不在本仓库**：`services/src/cua-permission-broker/index.ts:1-21` 明确"broker server 已合并进 @zcode/zcode-cua"，本快照只有 API-compatible placeholder。因此**Helper 内部实现（peer 门具体算法、watchdog、token/identity 校验、native 启动流程）未证实**，只能依据 docs + 消费侧代码推断。🔶
2. **docs 与代码存在时差**（重要）：`zcode-cua-permission-broker.md:18,25` 仍写 "auth token / bearer credential"，而现行代码注释与凭据结构已改为 identity 模式（socket+authority）。引用该文档时须标注"迁移前威胁模型/规格"，实现以 `runtimeEnv.ts`、`plugin-host-command.ts` 为准。🔶
3. **命名差异**：文档/UI 用 `includeFunctionalProbes`，占位契约只声明 `probeScreenCapture`（+索引签名）；两处并存，未见统一。🔶
4. **"spawn env 零 IO"的张力**：lazy startup 规格要求 spawn env 解析"无 await Helper 相关 IO"，但 win32 首次 spawn 会 `await resolveStableCuaWinRecipe()`（内部 `resolveWindowsCuaRuntime` 做文件校验/SHA-256，之后缓存）。属实现与规格的轻微偏离，未证实是否有意；作为移植时的设计权衡点记录（不要为了"零 IO"把运行时解析塞进 spawn 同步路径）。🔶
5. **Windows 实机验证缺口**：lazy startup spec 自述 "darwin 实机 MC-1/2/3 与 Windows 全链路 MC-4~7/9 待实机验证"（开发机是 macOS）；Windows product runtime 验收记录也声明"本机没有发布证书，不能把 ABI/resources 验收外推为最终 branded/signed executable 验收"。🔶
6. **文件名陷阱**：`windowsCuaDevHelperHost.ts`/`windowsCuaDevRuntime.ts` 虽带 Dev，实际是**产品 Windows Helper host 与 runtime resolver**（`node.ts:1097-1118` 产品路径直接使用）；不要误判为仅 dev。🔶
7. **无法在本环境验证的**：签名/Team/Gatekeeper 链、Helper 真实进程行为、Windows named pipe 实测、TCC 授权。均为【规格/代码推断】，非实测。🔶
8. **范围外但重要的邻域**（留给其他 notes）：CUA 30 工具面与 SDK/契约细节（notes/03、05）、Windows 输入派发/截图实现（windows-input-* 系列）、frame/media authority（authority-verdict-audit）、Browser Use。

## 证据索引（文件:行号）

**规格 docs/cua-permission-broker/**
- `zcode-cua-permission-broker.md`：7-12 node_repl 迁移；14-16 两链路；18 Helper/broker_info；20-35 fail-closed 规则+token 作用域+launcher 要求+remote 限制+kill switch+启动前重验+omit；37-59 open_application 信任级；61-79 授权主体诊断；81-94 权限两模式；96-107 SDK 契约；109 stable_identity
- `cua-helper-installation.md`：5-29 安装来源/路径；31-42 dev 覆盖；44-56 校验清单；58-90 dev 模式；105-115 原子替换；117-133 entitlements
- `cua-helper-permission-onboarding.md`：5-8 目标；12-22 交互；24-39 边界/IPC；45-48 验证
- `cua-agent-recovery-admission-gate.md`：1-6 已废弃；38-39 gate 只保护 spawn；105-106 平台差异
- `2026-09-02-helper-lifecycle-agent-runtime-isolation-spec.md`：20-32 不变量；110-126 ready 允许/禁止；127-140 按需健康检查；142-151 recovery 结果；161-176 凭据/transport；178-193 CUA 不可用行为；196-215 插件门控；219-228 owner 表；316-327 stop 与迟到消息；329-346 实现状态
- `2026-09-21-cua-helper-lazy-startup-spec.md`：5 状态；19-38 不变量；44-63 基线问题；67-78 凭据表；100-140 时序图；155-172 win32 fork 契约；174-179 死亡恢复；181-200 设置页；202-208 授权并存窗口；210-219 owner 表；267-277 风险
- `windows-transport-ready.md`：3-32 两阶段就绪全文
- `cua-dev-mcp-runtime-assets.md`：10-22 资产链；27-36 owner 表；39-45 约束
- `local-dev-helper-runbook.md`：8-24 dev TCC 稳定身份

**规格 docs/cua/**
- `2026-08-25-cua-authority-verdict-audit.md`：13-25 判定原则；29-45 authority map；47-70 case/剪枝
- `2026-08-25-cua-request-access-onboarding-spec.md`：9-11 产品合同；13-25 链路；29-37 状态副作用；41-46 身份边界
- `2026-08-25-cua-permission-error-passthrough.md`：9-12 决策；16-26 根因；28-48 新链路；51-65 不变量
- `2026-08-18-subagent-computer-use-unavailable-spec.md`：5-9 目标；12-22 两层边界；24-26 错误码；28-42 语义
- `2026-08-27-computer-use-unsupported-environment-ui-spec.md`：14-23 能力矩阵；25-36 UI/约束
- `zcode-cua-monorepo-migration.md`：5-12 包身份；17-26 目录；39-44 版本/Windows 产物名
- `cua-pip-session-consumer-integration.md`：3-6 所有权；8-31 数据流/focus/sideband；35-40 严格兼容；44-53 生命周期映射；57-65 验证
- `2026-08-24-cua-node-repl-sdk-migration-spec.md`：9-15 工具面；26-45 SDK 契约；47-52 fresh Worker；81-86 两个独立 authority；90-99 runtime 边界；107-118 远程/授权；135-143 sideband
- `windows-product-runtime.md`：14-19 设计结论；21-66 构建/运行链；68-128 manifest 契约；133-150 准入/fail-closed；152-175 签名；253-258 明确不做
- `product-capability-map-office-computer-use.md`：20 CUA 能力行；32 依赖边界；50-53/62-65 事实表；115-116/127-128 不变量与调用链

**规格 docs/cua/producer/**
- `subagent-runtime-scope-boundary.md`：3-16 全文
- `pip-session-coordinator-v2.md`：9-18 目标；22-41 capture binding；45-47 严格 ABI；55-113 排序  
- `multi-session-cursor-overlay-spec.md`：31 gate；53-55 现状/单写者；83-93 Windows peer 暂态；339-360 生命周期；435-450 gate 语义
- `ghost-cursor-multi-display-spaces.md`：15-31 决策；32-42 翻转基准
- `windows-helper-transport-ready.md`：6-20 全文
- `install-linux.md`：3-19 DEPRECATED

**真实源码（services）**
- `packages/services/src/cua-permission-broker/index.ts`：1-21 51 文件合并说明；27 server barrel；29-33 admission gate 废弃；95 Windows host 导出
- `cuaPermissionService.ts`：1-13 type/value 归属；43-55 纯 ports 谓词
- `cuaHelperInstaller.ts`：10-14 arch 归一化；48-52 canonical installer
- `stableCuaTransport.ts`：15-22 稳定 pipe singleton；24-45 win recipe 缓存
- `cuaAgentAdmissionGate.ts`：13-19 废弃说明
- `windowsCuaHelperHostSupport.ts`：16-19 协议常量；58-70 fork/health 默认；72-188 child 生命周期；190-233 控制消息解析；235-240 exact health pid
- `windowsCuaDevHelperHost.ts`：34-38 超时/authority；48-90 状态机字段；102-129 waitForTransport；131-158 start 复用；160-226 stop/restart/preserving；246-302 startNow/stopNow；304-330 fork argv/env；339-411 ready 处理；413-445 health；477-509 终止/外部 stop
- `windowsCuaDevRuntime.ts`：73-94 解析边界；96-146 dev root；163-228 packaged runtime；283-326 manifest 校验；328-349 root realpath；364-438 artifact 路径/包含；463-519 containment/hash
- `cuaPipSessionService.ts`：44-58 服务状态；59-94 pendingTurnStarted 根因；103-134 getClient；136-187 补发轮询；189-272 publish；274-284 dispose
- `packages/services/src/node.ts`：767-813 启用/创建判据；823-862 探针与预检；905-1066 Windows host 懒解析；1051-1128 默认 Helper 组装；1895-1936 设置页拉起/PiP enabled；1941-2070 getStatus；2378-2449 resolveSpawnEnv 注入

**真实源码（CLI / shared / UI）**
- `apps/zcode-cli/packages/node-repl-host/src/cua-broker.ts`：26-68 私有 broker socket；70-128 token→ledger 授权与 context 构造；138-155 app identity
- `.../src/cua-bridge.ts`：29-76 bridge globals；78-131 单行协议/32MiB
- `.../src/call-ledger/ledger.ts`：45-61 per-call token
- `.../src/server.ts`：395-403 启动期捕获 runtime；453-462 env→runtime
- `apps/zcode-cli/packages/cli/src/plugin-host-command.ts`：23-29 威胁说明；47-71 恢复凭据；83-106 授权断言
- `apps/zcode-cli/packages/bootstrap/src/mcp-config.ts`：21-32 socket 解析；37-58 trusted server 名；108-166 注入 node_repl env；188-194 retired server
- `apps/zcode-cli/packages/core/src/runtime/methods/mcp.ts`：37-64 computeOfficialCuaServerNames
- `packages/shared/src/runtimeEnv.ts`：33-45 内部特性/开关；47-117 sanitize 清单；141-175 凭据捕获；201-214 getter；222-235/278-286 sanitize；296-313 tool passthrough
- `packages/ui/src/lib/cuaPermissionStatusStore.ts`：255-282 探针合并/getStatus 调用
- `packages/shared/src/zcode-protocol/index.ts`：1458-1508 CUA operation event schema；4254 method 名

**契约（placeholder @zcode/zcode-cua 0.6.5，全部 fail-closed）**
- `README.md:3-7`、`package.json:5,64`（`zcodeCuaPlaceholder: true`）
- `index.d.ts:3-39`（ComputerUseRuntime/Context/Options）；`index.js:1-14`（execute 恒 isError）
- `broker.d.ts:26-49`（HelperHealth/probe/callBrokerMethod）；`97-126`（权限状态类型/查询选项）；`141-152`（ICuaPermissionService）
- `broker-server.d.ts:6-10`（buildHelperOpenArgs）；`50-59`（installer）；`90-100`（LifecycleManager）；`124-197`（Host/Resolver 接口）；`240-243`（reapOrphanedHelpers）；`250-256`（request permission）；`260-266`（helper 路径/注入）
- `request-access-contract.d.ts:3-15`；`request-access-contract.js:1-9`
- `broker-helper-constants.js:1-6`（bundle id/app 名/overlay argv）
- `frame-contract.d.ts:1-30`；`host-display-contract.d.ts:1-2`；`pip-session.d.ts:1-32`；`pip-session-node.d.ts:1-24`
- 占位实现全 fail-closed：`broker.js:8-9,42-60`；`broker-server.js:25-30,100-131,137-149,171-197`

**代码中未在本仓、只能靠 docs 的部分（标注未证实）**：真实 broker server 51 文件、Helper 进程 main/watchdog/peer 门、`computer-use-client.mjs`（plugin scripts 只随发布物）、`helperMain.ts`（仅被 overlay spec 引用）、native AX/输入/截图实现。
