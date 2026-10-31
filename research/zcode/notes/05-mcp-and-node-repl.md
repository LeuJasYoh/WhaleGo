# 05 MCP 集成与 node_repl 运行时（真实代码 + 设计规格）

> 研究范围：`apps/zcode-cli/packages/node-repl-host/**`（真实源码）、`bootstrap/src/app/built-in-node-repl.ts`、
> `packages/shared/src/mcp.ts`、`docs/browser-use/*node-repl*`、`docs/mcp-*.md` 全套、`docs/external-agent-mcp-import.md`、
> `docs/plugin-mcp-session-runtime-merge.md`、`docs/windows-mcp-job-object-containment.md`。
> 核实基线：ZCode-main @ 2026-10 快照（工作区只读）。证据类型标注：【代码】【规格】【契约】【推断】。
> 本笔记只对「MCP 宿主 / node_repl 运行时 / 传输 / 预算 / 认证」负责；CUA 语义、权限 broker、浏览器后端细节属于 task-2/3 范围。

## 0. 结论速览

1. **node_repl 是一个"单工具 MCP 宿主"**：模型只看到一个工具 `js`（MCP 全名 `mcp__node_repl__js`），
   但一次 `js` 调用可以按需 import SDK、执行任意多步动作。它把「N 个 MCP 工具 × 每次往返」压成
   「1 个工具 × 1 段代码」。工具面在 2026-09-18 收敛到只剩 `js`，`js_reset`/`js_add_node_module_dir` 已删除。【代码】
   `node-repl-host/src/tool-contract.ts:13-22`、`src/server.ts:91-98`。
2. **执行是「每次调用一个一次性 Worker」**：每次 `js` 都新起 Node `worker_threads.Worker`，Worker 内新建
   `NodeReplSession`（引擎基于 `node:vm` 持久 context，见 `docs/browser-use/2026-07-07-node-repl-engine-spec.md:14-19`），
   执行完即 `terminate()`，没有跨调用的 global/模块缓存。【代码】`src/server.ts:312-348`（`executeJsInWorker`）、
   `src/server.ts:169-179`（finally dispose）。
3. **无状态 ≠ 无会话**：MCP frontend 进程按 `workspaceKey` 共享（`isolation:"workspace"`），
   同一 MCP frontend 服务同 workspace 的多个 session；每次 `js` 用一次性 kernel。【代码】
   `bootstrap/src/app/built-in-node-repl.ts:45-51`；【规格】`docs/mcp-stateless-node-repl.md:13-39`。
4. **SDK 注入，而不是暴露工具**：Worker 里注入两个领域 bridge（Browser Use、Computer Use）+
   `nodeRepl` 辅助对象（`write`/`emitImage`/`setResponseMeta`/`cwd`/`homeDir`/`tmpDir`），
   模型写 `agent.computerUse.*` / `agent.browsers.*` 这类 SDK 代码，而不是逐个工具调用。【代码】
   `src/server.ts:139-158`；`src/tool-contract.ts:40-42`。
5. **凭证不落在 Worker/SDK 里**：主线程持有 CUA runtime 与上游 browser broker 的 address/token；
   每次调用由主线程"账本"签发 32 字节一次性 token，Worker 只拿本次调用 token 连本机 socket/pipe；
   broker 端用 `ledger.resolve(token)` 换回可信 requestMeta。【代码】
   `src/call-ledger/ledger.ts:45-61`、`src/server.ts:251-278`、`src/cua-broker.ts:93-107`。
6. **结果可信度分层**：Worker 返回的消息"只当内容"，可信元数据（CUA 目标 App、浏览器截图下标、
   browser_use/computer_use 会话标签）只取主线程账本记录的 facts，且这些 `_meta` 键在合并前被无条件删除再重写。【代码】
   `src/result.ts:85-124`；`src/call-ledger/CONTRACT.md:12-16`。
7. **MCP 传输面**：内置 node_repl 走 **stdio**（`type:"stdio"`，`ELECTRON_RUN_AS_NODE` 启动，
   固定 `protocolVersion:"2026-07-28"`）；第三方支持 stdio / Streamable HTTP / 旧 SSE，默认 dual-era `auto`。【代码】
   `bootstrap/src/app/official-plugin-runtime.ts:19-43`、`built-in-node-repl.ts:45-51`；【规格】`docs/mcp-dual-era-version-negotiation.md:21-41`。
8. **结果预算/图片有明确阈值**：普通第三方 MCP 图片 base64 > **200 KiB** 不再 inline，走 artifact 或文本降级；
   `node_repl/js` 的宿主图片先压到预算内（base64 ≤200 KiB、decoded ≤153600 B、最长边 ≤2000），失败才 artifact。【规格】
   `docs/mcp-tool-output-budget.md:21-42`。
9. **防挂死是三层**：① 进程级 `unhandledRejection/uncaughtException` 降级为 stderr（不让子进程死）；
   ② stdin end/close + SIGINT/SIGTERM 触发 shutdown（防孤儿）；③ `no-progress-guard` 按 session+turn
   统计同码/同结果/调用量，在结果末尾追加"可以结束本轮"提示。【代码】`src/process-lifecycle.ts:20-79`、`src/no-progress-guard.ts:40-76`。
10. **Windows 特定**：broker 用 named pipe（`\\\\.\\pipe\\zcode-node-repl-cua-<uuid>`），
   非 Windows 用 tmpdir 下 unix socket 并在 close 时 unlink。【代码】`src/cua-broker.ts:32-35,58-66`。
11. **stdio 子进程回收与传输选型**：Windows 上用 per-transport Job Object（`KILL_ON_JOB_CLOSE`）+ `TerminateJobObject`，
    失败回退 `taskkill /T /F`。【规格】`docs/windows-mcp-job-object-containment.md:1-31`；而本项目
    **跨容器场景 stdio 不可用** ⇒ 选 **Streamable HTTP**，不要用在 ZCode 里已被归为 legacy-era 的旧 HTTP+SSE，
    并照抄其 token/预算/健康检查/结果包装/进程守护设计。【推断，见 §7】

## 1. node_repl 是什么：定位、进程模型、调用链

### 1.1 一句话定义

node_repl 是「**ZCode 官方能力共用的 JS 执行宿主 MCP server**」，只暴露一个 `js` 工具；
Browser Use 与 Computer Use 都是通过注入的 SDK 在这个 JS kernel 里被"bootstrap"起来的。

> 【代码】`node-repl-host/README.md:3-5`：
> "`node_repl` 的共享宿主：JS 执行面（只有 `js` 一个工具）与两个领域 bridge（Browser Use、Computer Use）都在这里。"

> 【规格】`docs/mcp-stateless-node-repl.md:43-49`：
> "模型只看到一个宿主工具：`mcp__node_repl__js` … 不得把它改成通用 JavaScript、文件、Shell、包检查或数据处理工具"

### 1.2 为什么现在只有 `js` 一个工具（演化证据）

- 早期是 core 内建三工具 `js/js_reset/js_add_node_module_dir`。【规格】`docs/browser-use/2026-07-07-node-repl-tools-spec.md:11-18`
- 2026-07-13 改成真实 stdio MCP server（宿主保留），模型工具名变 `mcp__node_repl__js*`。【规格】
  `docs/browser-use/2026-07-13-node-repl-mcp-runtime-spec.md:28-40,126-144`
- 2026-08-06 起改为无状态 kernel + workspace 共享 frontend + MCP `2026-07-28`。【规格】`11` 命中处同文件；
  【规格】`docs/mcp-stateless-node-repl.md:13-24`
- 2026-09-18 删除 `js_reset`、`js_add_node_module_dir`：实测 310 个本地会话 / 17134 次真实 `js` 调用中
  两者调用量为 0；`js_reset` 每轮还白背约 117 token 定义，且"永不失败"让弱模型反复连调
  （工单 ZCT-2100503886992535552：155 次连调、19 分钟 4837 万输入 token 直到 429）。【规格】
  `docs/mcp-stateless-node-repl.md:77-99`；【代码】注释 `src/tool-contract.ts:13-18`

> 摘录【规格】`docs/mcp-stateless-node-repl.md:86-87`：
> "失败结果才是模型换策略所需的信号。"

### 1.3 进程/线程模型（真实代码）

```text
MCP client (ZCode Agent, per workspace MCP connection pool)
   │ stdio (JSON-RPC, one line per message)
   ▼
[进程 A] node_repl MCP frontend  —— src/server.ts main() + serveStdio
   │  NodeReplMcpRuntime（每个 MCP connection 一个）
   │   ├─ call-ledger（主线程，唯一可信事实源）
   │   ├─ cua-broker：本机 socket/named pipe server → ComputerUseRuntime
   │   └─ browser-broker-proxy：本机 socket/named pipe server → 上游 ZCode 主进程 browser broker
   │
   │ 每次 tools/call js 时 new Worker(同一个 server.ts 文件, workerData)
   ▼
[进程内 Worker B] 一次性 kernel
   │  new NodeReplSession({ injectedGlobals, restrictProcess: true })
   │  bridge globals 通过 socket 连回 A 的 broker（带本次调用 token）
   └─ 结果（result/logs/error/images/structuredResults/responseMeta）postMessage 回 A
```

【代码】关键事实：
- `src/server.ts:404-411`：`serveStdio(() => createNodeReplMcpRuntime(...), { legacy: "reject" })` —— 直接
  **拒绝 legacy 握手**（配合 pin `2026-07-28`）。
- `src/server.ts:430-451`：非主线程分支 = Worker 入口，用 `isWorkerCallData(workerData)` 识别
  （`kind === "zcode-node-repl-call"`，`src/server.ts:56`）。
- `src/server.ts:322`：`new Worker(new URL(import.meta.url), { workerData, env: workerEnvironment() })`。
- `src/server.ts:363-368`：Worker 环境**删掉** `NODE_REPL_BROWSER_BROKER_SOCKET_ENV/TOKEN_ENV`，
  且注释明确写"这只是缩小暴露面，不是可信边界"。
- `src/server.ts:469`：只有 `isThreadMain` 且 `isDirectMcpEntrypoint(...)` 才跑 `main()`；
  `process-lifecycle.ts:81-98` 用 `realpath` 比较避免 macOS `/tmp`→`/private` 软链误判。

### 1.4 一次 `js` 调用的完整链（逐跳）

| # | 位置 | 动作 | 证据 |
|---|------|------|------|
| 1 | core/Agent | 发 `tools/call {name:"js", arguments:{code,timeout_ms,title}}`，MCP `_meta["com.zcode/request-context"]` 带 session/turn/trace/workspace | 【代码】`src/server.ts:381-389` 解析；`src/server.ts:74-89` schema |
| 2 | MCP frontend | 校验 `jsInputSchema`（strict），非空 code；`serialized(sessionKey, ...)` 同 session 串行 | 【代码】`src/server.ts:242-296` |
| 3 | MCP frontend | 组合 signal：`AbortSignal.any([mcpReq.signal, callController.signal, AbortSignal.timeout(timeoutMs)])`；默认 60_000ms，上限 120_000ms | 【代码】`src/server.ts:251-261`；`tool-contract.ts:3` |
| 4 | MCP frontend | `ledger.open(callMeta)` 签发一次性 token + facts 容器 | 【代码】`src/server.ts:255`；`ledger.ts:45-57` |
| 5 | MCP frontend | 把 `{socketPath, token}` 作为 `cuaBroker`/`browserBroker` 放进 workerData | 【代码】`src/server.ts:262-278,314-321` |
| 6 | Worker | `createInProcessNodeReplExecutor()`：`new NodeReplSession({injectedGlobals, restrictProcess:true})`，注入两个 bridge globals，`session.run(code,{signal,syncTimeoutMs})` | 【代码】`src/server.ts:133-181` |
| 7 | Worker 内 SDK | `agent.computerUse.*` → `bridge.call(method,input)` → 本机 socket 发 `{id, token, method, input}` | 【代码】`src/cua-bridge.ts:59-73,78-132` |
| 8 | broker（主线程） | `ledger.resolve(token)` 校验 → 用条目 requestMeta 构造 context → `runtime.execute(...)` → `entry.facts.computerUse=true`、记 `cuaApps` | 【代码】`src/cua-broker.ts:93-121` |
| 9 | Worker | 结果 postMessage 回主线程 | 【代码】`src/server.ts:441-450` |
| 10 | MCP frontend | `toMcpRunResult(run, call.facts)` 组装 `CallToolResult`；`noProgressGuard.observe` 追加提示；`call.close()` 作废 token | 【代码】`src/server.ts:279-292`；`result.ts:89-214` |

### 1.5 为什么"一个 js 工具 + SDK 注入"优于"一堆 MCP 工具"（证据化归纳）

ZCode 没有一篇文档正面写"省 token"，但代码与规格给出了四条可核对理由：

1. **工具面成本**：`js` 的定义是常驻 system tools，工具越多每轮越贵；反过来 ZCode 删 `js_reset`
   的直接理由就是"每轮请求携带约 117 token 的工具定义"。【规格】`docs/mcp-stateless-node-repl.md:84-87`
2. **灵活性/演化**：能力以 SDK/脚本随 skill 发布，新增能力不必新增 MCP 工具；CUA 迁移到
   shared node_repl 时"不再构造或调用 MCP tool envelope"（`cua-broker.ts:108-110` 注释）。【代码】
3. **安全集中**：只有一个高权限入口，权限系统可对"精确 MCP tool name"配预算/审批；
   且 broker/账本把可信事实与执行者分离。【代码】`result.ts:106-124`；【规格】`docs/browser-use/2026-07-13-node-repl-mcp-runtime-spec.md:188-189`
4. **减少往返**：一个 cell 内可串多步（等待、轮询、多 tab），只有一次工具往返；
   代价是必须靠 fresh-kernel 语义和 bootstrap 约定保证可重入。【规格】`docs/mcp-stateless-node-repl.md:60-76`

## 2. 真实代码结构（node-repl-host）

### 2.1 包定位与产物归属

- 包名 `@zcode/node-repl-host`，`private:true`，version `0.7.0`，依赖 `@modelcontextprotocol/server@2.0.0`。【代码】`package.json:1-6,22-29`
- 它独立成包的原因：宿主是**官方能力共用**，过去长在 `browser-use-plugin` 里，导致"改 CUA 必须动
  browser-use 包"、"CUA 的 SDK 与文档两份手工副本"、"`resolveBuiltInNodeReplMcpServers` 只在
  browser-use 的 rootPath 下找宿主产物"。【代码】`README.md:6-13`
- 产物仍由插件包携带：`dist/mcp/server.js`、`scripts/computer-use-client.mjs` 在 SEA 发布清单
  `OFFICIAL_BROWSER_USE_REQUIRED_SEED_PATHS` 里，"路径不能动"。【代码】`README.md:18-23`

### 2.2 server 启动与工具注册

- `main()`：先 `setNodeReplMcpProcessTitle()`（`process.title = "zcode-node-repl-mcp"`），
  再**在 main() 生命周期内**捕获 CUA runtime，然后 `serveStdio`。【代码】`src/server.ts:395-428`
  > 关键坑（原文）："官方 plugin host 在 main() 返回后会清除短暂恢复的 Helper 凭据，而 serveStdio 的
  > server factory 要到 MCP initialize 时才执行。过去在 factory 内读取 process.env，必然得到空值，
  > 导致 node_repl 永久把 Computer Use 判为 unavailable。"【代码】`src/server.ts:398-402`
- `createNodeReplMcpRuntime()`：`new Server({name:"node_repl", version:NODE_REPL_SERVER_VERSION},
  {capabilities:{tools:{}}, instructions:NODE_REPL_SERVER_INSTRUCTIONS})`；注册 `tools/list`、`tools/call`。【代码】`src/server.ts:231-236`
- 只有一个 Tool 条目（`name:"js"`），`inputSchema` 复用 `@zcode/contracts` 的 `JsInputJsonSchema`
  （"禁止 MCP server 另写一份会漂移的 JSON Schema"）。【代码】`src/server.ts:91-98`；【规格】`docs/browser-use/2026-07-13-node-repl-mcp-runtime-spec.md:72-73`
- 并发模型：`serialized(sessionKey, run)` —— 同 session 的 `js` 串行，不同 session 并发；
  `sessionKey = meta.session_id ?? "__unscoped__"`。【代码】`src/server.ts:211-229,388-391`
- `dispose()`：置 disposed、abort 所有 in-flight call、清队列、关两个 broker、dispose CUA runtime。【代码】`src/server.ts:300-309`

### 2.3 tool-contract（参数/结果 schema + 模型文案）

- `NODE_REPL_DEFAULT_TIMEOUT_MS = 60_000`、`MAX_SYNC_TIMEOUT_MS = 120_000`；执行层与模型文案共用该常量
  （注释写明原因：旧默认 30s 与模型常用 30s 页面等待相同，会在结果读取前被中止）。【代码】`tool-contract.ts:1-3`；`server.ts:54`
- `NODE_REPL_SERVER_VERSION = "0.7.0"`，注释记录了 0.5.0/0.6.0/0.6.1/0.6.2/0.7.0 每次"宿主契约变化"的升版理由；
  这是 serverInfo 的版本，**不是插件版本**。【代码】`tool-contract.ts:5-22`
- `NODE_REPL_SERVER_INSTRUCTIONS` 与 `JS_TOOL_DESCRIPTION` 都显式限域"Browser Use and Computer Use only"，
  并列出 `nodeRepl.*` 可用方法与 CUA 使用细则（不用 console.log 整个结果、get_app_state 要赋值并 write、
  x/y 必须是 raster 内整数像素、每次调用都要重新 bootstrap SDK）。【代码】`tool-contract.ts:24-58`
- 模型输入 schema（contracts）：`code` 必填、`timeout_ms` int 1..120000 可选、`title` 1..120 必填
  （`JsInputSchema`），另有 `JsRuntimeInputSchema` 把 title 设为可选供运行时兼容旧调用；输出 `JsOutputSchema`
  含 `result/logs/error{name,message,stack?}/images[{base64,mimeType}]/browserScreenshotPaths/responseMeta`。【代码】
  `contracts/src/tools/node-repl.ts:4-66`
- server 侧 `jsInputSchema` 用 zod strict 再校验一次（`code/timeout_ms/title`），并对 `title` 保持 optional
  ——注释："tools/list 对新调用强制 title，但执行层必须继续接受旧 provider 和历史回放的 code-only 输入"。【代码】
  `server.ts:66-73`
- 错误码：参数非法抛 `INVALID_PARAMS`；未知工具名也走 `invalidParams`。【代码】`server.ts:294-295,370-379`

### 2.4 cua-bridge / cua-broker：认证请求怎么校验、SDK 如何不碰 token/native

**bridge（在 Worker/SDK 侧）**
- 暴露为一个 `Symbol.for("zcode.node-repl.computer-use-bridge")` 全局，代替直接给模型 MCP 工具。【代码】`src/cua-bridge.ts:6,29-76`
- `assertActive()`：generation 不匹配就报 "Computer Use runtime binding is stale after kernel reset"；
  `assertAvailable()`：`runtime_scope==="subagent"` 拒绝、无 broker 拒绝。【代码】`cua-bridge.ts:36-52`
  > 摘录："Computer Use is not available in subagent"（`cua-bridge.ts:7`）——与 Browser Use 不同（浏览器在
  > 0.6.1 起允许 subagent）。
- `call(method,input)` 只发方法名与参数；会话信息由主线程 broker 用账本条目构造；
  结果里的 `responseMeta` 合并进 session。注释明确这是 MR !3207 评审 CR-01 的修复：
  "原来身份在这里记进 Worker 的运行状态，cell 能经 worker_threads 冒充 Worker 的结果消息伪造它"。【代码】`cua-bridge.ts:59-73`
- wire：一行 JSON `{id, token, method, input}\n`；响应一行 JSON `{id, ok, result?, error?, responseMeta?}`；
  **响应上限 32 MiB**，超限报 "exceeded the 32 MiB limit"；response id 必须匹配。【代码】`cua-bridge.ts:78-132`

**broker（在主线程/MCP frontend 侧）**
- socket 路径：Windows `\\.\pipe\zcode-node-repl-cua-<uuid>`，其他 `<tmpdir>/znrc-<uuid>.sock`；
  `server.unref()`；close 时非 Windows 才 `rm`。【代码】`src/cua-broker.ts:32-67`
- 校验顺序：`ledger.resolve(payload.token)` → 不存在则 "Computer Use broker request is not authorized"
  → 记 `entry.facts.computerUse = true`（**在校验 id/method 与 subagent 检查之前**，即"真实尝试"就记）
  → 校验 id/method 都是 string → `requestContext(entry.requestMeta)` 构造 context → 再拒 subagent
  → `runtime.execute({toolName: method, arguments: input, context, signal})`。【代码】`cua-broker.ts:84-121`
- 目标 App 身份由 broker 在 runtime 返回后从 `_meta[CUA_APP_ASSOCIATIONS_META_KEY].primary` 读，
  只取 `appKey/displayName`，**不取内联 icon PNG**（"会话协议不承载图标字节，UI 按 appKey 派生 locator"）。【代码】`cua-broker.ts:116-155`
- 请求上限 1 MiB；`readLine` 逐块累积并检查换行。【代码】`cua-broker.ts:17,238-270`
- 缺 session_id / workspaceKey 时报错固定文案（`cua-broker.ts:158-199`）；`clientMode` 默认
  `"desktop-continuous"`，只接受 `web-remote-replayable|desktop-continuous` 两值。

> 安全模型一句话（可引用给主报告）：**token 每次调用一个、只在账本条目打开期间有效；broker 只用条目的
> requestMeta 构造会话信息，不采信 Worker 自报的值**。【代码】`src/call-ledger/CONTRACT.md:14-15`

### 2.5 runtime-bridge 与 browser-bridge

- `runtime-bridge.ts` 只有 20 行：定义 `NODE_REPL_BROWSER_BRIDGE_SYMBOL`、`NodeReplBrowserRuntimeBridge`
  （extends `BrowserClientTransport` + `documentationRoot` + `assertAvailable()`）与
  `readNodeReplBrowserRuntimeBridge(globals)`；读不到时报
  "Browser runtime bridge is unavailable. Use Browser from a ZCode desktop or shared-host session."。【代码】`src/runtime-bridge.ts:1-20`
- `browser-bridge.ts`：transport 只有 `list()` 与 `execute(browserId, browserGeneration, command)` 两跳；
  **不再直连 ZCode 主进程 browser broker**，而是连主线程代理（见 §2.6）。【代码】`src/browser-bridge.ts:26-70`
- `execute` 成功后 `mergeBrowserResponseMeta` 写 `zcode/browserUse`、`zcode/toolSurface`、
  `browser_use{url}`，并在"自动截图触发命令"上写 `zcode/browserTurnScreenshot`；`finalizeTabs` 写
  `sessionEnded:true`。【代码】`browser-bridge.ts:135-178`
- 白名单式判断：`isAutoScreenshotTriggerCommand` 排除 capabilities/list/listUserTabs/browserVisibilityGet
  与 cancelRequest/closeSession/finalizeTabs/nameSession/turnEnded；`isBrowserSurfaceSideEffect` 列出
  click/fill/type/press/scroll/hover/select/check/drag/handleDialog/close 等。【代码】`browser-bridge.ts:166-209`

### 2.6 call-ledger：把"可信事实"从 Worker 搬到主线程

- 背景（原文）："修复原因（MR !3207 评审 CR-01）：可信事实原来由 Worker 里的 bridge 记进运行状态，
  再随 Worker 的结果消息发给主线程；cell 能经 worker_threads.parentPort 冒充这条消息，事实因此可以伪造。"【代码】`src/call-ledger/ledger.ts:4-6`
- 结构：`NodeReplCallFacts { cuaApps, browserScreenshots, browserUse, computerUse }`；
  `open(requestMeta)` 生成 `randomBytes(32).toString("hex")` token，`close()` 删除条目，
  `resolve(token)` 只接受 string。【代码】`ledger.ts:11-61`
- 浏览器代理 broker：`SESSION_CONTEXT_FIELDS = ["token","runtimeScope","sessionId","turnId","trace"]`
  从转发的 payload 中删除，再用账本 requestMeta 重写 + 换上游 token。【代码】`browser-broker-proxy.ts:26-27,114-118`
- 只在 `op === "execute" && command.method === "screenshot"` 且 `ok===true` 且 image 有 base64/mimeType
  时把截图记入 `facts.browserScreenshots`。【代码】`browser-broker-proxy.ts:150-169`
- 已知缺口（原文）："cell 可经 `node:inspector` 影响主线程，账本只防住冒充 Worker 结果消息"。【代码】`CONTRACT.md:17`

### 2.7 result：输出预算、截断、图片顺序、`_meta` 清洗

`toMcpRunResult(run, facts)` 的顺序与规则（全部真实代码）：
1. `structuredResults`（SDK 通过专用 sink 写入）优先；仅当无 structured 且无 error 时才把 `run.result` 当
   "embedded MCP result" 解析（允许 `=> {...}` 前缀）。【代码】`result.ts:22-51,96-100`
2. `responseMeta` 合并后**无条件 delete** 四个 key：`zcode/nodeReplBrowserScreenshotContentIndices`、
   `CUA_APP_ASSOCIATIONS_META_KEY`、`zcode/nodeReplComputerUseApp`、`zcode/browserUse`/`zcode/computerUse`，
   然后只按 `facts` 重写。【代码】`result.ts:101-124`
   > 原文："cell 里自己 emit 一份就能让卡片声称操作了别的应用"（`result.ts:110-112`）。
3. **图片排在 text 之前**，原因是 Anthropic 兼容网关只解析 `tool_result.content` 开头的连续 image block
   （实测 `[image]`/`[image,text]` 可见，`[text,image]`/`[text,image,text]` 不可见）。【代码】`result.ts:125-129`
4. CUA 的"多帧取最新"：一个 cell 里多次带截图观察时，只保留最后一对 image/authority text
   （`keepLatestCuaFrame`），因为"每个结果只允许一张最终 raster"的门禁会把整帧原子否决。【代码】`result.ts:53-76`
5. 图片去重：结构化结果里已有的同一张图（逐字节相等）不再重复 emit。【代码】`result.ts:165-182`
6. 文本：error 时**只放 `run.error.message`**（不含 name/stack/日志）；否则 `logs` + `=> result`。【代码】`result.ts:143-153`
7. 永远非空：无任何 content 时返回 `(no output)`；`isError` 由 `run.error || embedded.isError || structuredIsError` 决定。【代码】`result.ts:187-213`
8. 任何图片存在时打 `_meta["zcode/nodeReplEmittedImage"]=true`。【代码】`result.ts:195-213`

### 2.8 process-lifecycle / no-progress-guard：防挂死、防孤儿、防刷 token

**process-lifecycle.ts**
- `installNodeReplProcessGuards`：把 `unhandledRejection`/`uncaughtException` 降级为
  `node_repl <kind> (process kept alive): <stack>` 写 stderr；若错误码属于
  `{EPIPE,EIO,ENXIO,EBADF,ERR_STREAM_DESTROYED}` 则视为输出关闭 → `onOutputClosed` → shutdown。
  注释记录过一次事故：旧 handler 把 EPIPE 堆栈写回同一 stderr 形成 EPIPE→uncaughtException→stderr.write 的无限循环。【代码】`process-lifecycle.ts:2-53`
- `installNodeReplShutdownTriggers`：监 `stdin.once("end"|"close")` + `SIGINT/SIGTERM`；
  原文理由："MCP SDK 的 stdio transport 不监听 stdin end/close。父进程异常退出时 runtime 因此收不到
  生命周期终点并沦为孤儿进程。"【代码】`process-lifecycle.ts:73-79`
- shutdown 实现：dispose 所有 runtime → `handle.close()` → `process.exit(0)`；有 `shutdownStarted` 幂等位。【代码】`server.ts:412-427`

**no-progress-guard.ts**（2026-10-26 加入，0.6.2）
- 常量：`SAME_CALL_FIRST_HINT=5`、`NO_PROGRESS_FIRST_HINT=10`、`STREAK_HINT_GROWTH=3`、
  `CALL_VOLUME_HINTS=[30,80,200]`、之后每 `200` 一次；文案后缀固定 `"You may end this turn."`。【代码】`no-progress-guard.ts:5-10`
- 指纹：`sha256(code)` 与 `sha256(JSON.stringify({content, isError}))`；按 `session_id + turn_id` 维护计数，
  **缺少 turn_id 不计**，turn 变化清零；只在同一 session 的串行区内读写。【代码】`no-progress-guard.ts:28-76`
- 优先级：同码 > 无进展 > 调用量；命中时在结果 content **末尾**追加一条 text block，并向 stderr 打
  `[node_repl] no-progress hint kind=... count=... session=... turn=...`（不含 code/结果正文）。【代码】`server.ts:280-287`
- 设计取舍（原文）："文案只授权结束、不要求换做法：现场模型已在反复写「本轮结束」，换做法式文案只会把它
  推回去继续调用。"【代码】`no-progress-guard.ts:37-39`

## 3. MCP 工程（传输、配置、目录、预算、认证、错误）

### 3.1 支持哪些传输

| 传输 | 是否支持 | 证据 |
|---|---|---|
| **stdio** | ✅ 主路径（内置 node_repl、官方插件 MCP、第三方） | 【代码】`adapters/src/mcp/index.ts:1948-1978`（`ProcessTreeStdioClientTransport`）；`shared/src/mcp.ts:311-340` |
| **Streamable HTTP** | ✅（配置 `type:"http"`，甚至 legacy `type:"remote"` 归一化为 http） | 【代码】`adapters/src/mcp/index.ts:1985-1996`（`StreamableHTTPClientTransport`）；【规格】`docs/mcp-config-compat-diagnostics.md:15,26` |
| **旧 HTTP+SSE** | ⚠️ 仍支持但固定 legacy era；插件侧只在 `type:"sse"` 时拒绝 official auth | 【代码】`index.ts:1998-2000`（`SSEClientTransport`）；`index.ts:2366-2371`；`adapters/src/plugins/mcp.ts:18,200-209` |
| 其他（`streamableHttp` 字面量等） | 配置层只做兼容字段接受，运行时归一化为 `http`/`sse` | 【代码】`shared/src/mcp.ts:30-31,341-347`；`plugins/mcp.ts:18,182-183`（`SUPPORTED_MCP_TYPES = {stdio,http,sse}`，其他直接报错） |

> 结论：ZCode 的"现代"路线是 **stdio + Streamable HTTP**；SSE 被当作 deprecated 边缘，
> 只能承载 legacy era（`resolveVersionNegotiationMode`：`config.type === "sse" → "legacy"`）。【代码】`index.ts:2366-2371`

### 3.2 如何配置 / 发现 / 缓存工具目录

- **配置来源分层**（`McpSource`）：`mcp | zcodeagentmcp | claudeclimcp | geminiclimcp | codexclimcp | opencodemcp`；
  作用域 `common | user | workspace`，文件格式 `json | toml`。【代码】`shared/src/mcp.ts:18-27,80-94`
- **目录/字段兼容**：入口归一化 `environment→env`、`http_headers→headers`、`enable→enabled`、
  `type:"remote"→"http"`；**按 server 粒度容错**（单个 server 非法只跳过并记 diagnostics，不拖垮整份 config.json）。【规格】`docs/mcp-config-compat-diagnostics.md:10-31`
- **list 必须发生在 agent 进程**：`listWorkspaceMcpServerStatuses` 注释——"真实 connect/listTools 检查必须发生在
  agent 进程（PATH/cwd 是 workspace 环境），本服务只是 UI 的注入面"。【代码】`services/src/mcp-sync/mcpSync.ts:23-33`
- **tool catalog cache（异步初始化快路径）**：LRU 32 entries、单 entry sanitized JSON ≤2 MiB、
  进程总预算 16 MiB、TTL 30 分钟、只缓存普通 **stdio**（HTTP/SSE 因缺少稳定 auth principal 不缓存）；
  只缓存 server/tool identity、name、description、input/output schema，**禁止**缓存 annotations、权限、instructions、OAuth 状态；
  publish 走 entry-local generation CAS；只有"所有 enabled 可缓存 server 都新鲜"才叫 `complete-hit`，
  否则整体等 live startup（不做渐进式工具集）。【规格】`docs/mcp-async-tool-catalog-cache.md:41-59,424-528`
- **铁律**：catalog cache 只负责"广告"，**tool call 必须在参数校验与权限判断前切到 live descriptor**。【规格】同文件 `61-69`

### 3.3 双版本协商（dual-era）

- 配置：`protocolVersion?: "legacy" | "auto" | "2026-07-28"`；不配 = `auto`（stdio/http）；`sse` 直接 legacy。【代码】`shared/src/mcp.ts:193,202`；`index.ts:2366-2371`
- `auto`：先 `server/discover`，只有 **transport-specific legacy evidence** 才回退 `initialize`；
  recognized modern error（如 `UnsupportedProtocolVersionError`）**不得**当作 legacy 降级。【规格】`docs/mcp-dual-era-version-negotiation.md:44-71,109-119`
- **probe 预算**：stdio 的 probe 是 disposable sibling process；上限 `MAX_MCP_VERSION_PROBE_TIMEOUT_MS = 5000` 且不超过总连接预算一半；
  **pin 模式例外**——"沿用 auto 的 5 秒保护上限会无视 server 的长连接预算，把冷启动正常但超过 5 秒的 node_repl 静默移出工具池"。【代码】`index.ts:113-115,2399-2421`
- **node_repl 是严格 pin**：固定 `2026-07-28`，不支持就连接失败，禁止 legacy fallback（保证不会退回持久 session/kernel 语义）。【代码】`built-in-node-repl.ts:45-51`；`server.ts:404-411`（`serveStdio(..., { legacy: "reject" })`）；【规格】`docs/mcp-dual-era-version-negotiation.md:115-117`
- **失败分类**：结构化判定（`SdkError.isInstance && code === EraNegotiationFailed` 或 `UnsupportedProtocolVersionError.isInstance`），
  映射为 `failureKind = protocol_negotiation_failed`，UI 提示"切换为兼容旧版"而不是"网络不可达"。【代码】`index.ts:2374-2392`；【规格】`mcp-dual-era-version-negotiation.md:99-103`
- 每个连接记录：`mcpClientName/Version`、`mcpVersionNegotiationMode`、`mcpProtocolEra`、`mcpProtocolVersion`、`mcpConnectionId`、`mcpIsolation`、`workspaceKey`、`mcpTransportPid`。【规格】同文件 `84-97`

### 3.4 OAuth（HTTP/SSE 远程 MCP）

- **两阶段**：Phase 1 = 纯 `AuthProvider`（只有 `token()` 与 `onUnauthorized()`，**被动连接零 listener/零 discovery/零 DCR**）；
  Phase 2 = 交互授权编排（独立进程锁、leader/follower、独立授权锁）。【规格】`docs/mcp-oauth-two-phase.md:23-33,44-78`
- 两个已定案根因：R1 多进程共享 refresh token 并发刷新 → rotation reuse-detection 直接吊销 token family；
  R2 DCR 把 `redirect_uris` 锁死在注册时随机端口，回调服务器每次换端口 → 永久二次授权死循环。【规格】`mcp-oauth-two-phase.md:14-21`
- 预算分离：**caller 等待预算 15s vs 授权事务寿命 300s**；caller 超时只结束自己的 waiter，不取消共享授权。【规格】`mcp-oauth-two-phase.md:29-32`
- 未配置 `Authorization` header 的 HTTP/SSE 会被推断为 authorization_code——这是历史缺陷的根，已改为两阶段。【规格】`mcp-oauth-two-phase.md:34-42`
- 配置形态：`client_credentials`（clientId+clientSecret 必填）与 `authorization_code`（可选 clientId/secret/redirectPath/scope）。【代码】`shared/src/mcp.ts:209-226,381-405`

### 3.5 stdio 失败诊断 / 不可达判定

- stdio stderr 单独维护一个**尾部有界 buffer**（`MCP_STDIO_STDERR_LOG_MAX_CHARS = 4000`），
  debug 级逐 chunk 记录（脱敏后），**只在 `mcp.server.failed` 时以 warn 附 `stderr` 字段**；成功连接不额外打 warn。【代码】`index.ts:115,2120-2143`；【规格】`docs/mcp-stdio-failure-stderr-diagnostics.md:20-32`
- 背景原文："两个 MySQL MCP server 都以 `MCP error -32000: Connection closed` 失败。该错误只说明 MCP stdio
  子进程关闭了管道，无法判断是 npx、server 参数校验、环境变量、网络还是 server 自身崩溃。"【规格】`mcp-stdio-failure-stderr-diagnostics.md:5-7`
- 不可达判定不靠单一错误码：stdio legacy 判定可来自 -32601/-32602/关闭 probe child/合理超时；HTTP probe 超时=网络故障，不能判 legacy。【规格】`mcp-dual-era-version-negotiation.md:109-119`
- 可复现的"不可达 fixture"：`http://127.0.0.1:9/mcp` 与不存在的 stdio command，`timeoutMs:3000`，默认启用用于观察 UI 状态流转。【规格】`docs/mcp-unreachable-test-server.md:9-44`

### 3.6 结果大小预算与图片

- 普通第三方 MCP 图片 base64 > `200*1024` 字节 ⇒ 不 inline，写 artifact（二进制优先，按 mimeType 选扩展名）或文本 artifact/文本降级；
  超限且无 artifact store 时**也不能**继续 inline。【规格】`docs/mcp-tool-output-budget.md:10-14,34-42`
- `node_repl/js` 宿主图片例外：先 `ImageProcessorPort.prepareForModel` 压到预算内（base64 ≤200 KiB、decoded ≤153600 B、
  最长边 2000、复用既有候选序列），压缩后再按实际 base64 字节复验；失败才落 artifact。【规格】`mcp-tool-output-budget.md:25-33`
- 背景数字：单张约 2 MiB 的 MCP 截图可能超过 provider/proxy 的 HTTP body 限制并触发 413；主模型请求另有 4 MiB media budget。【规格】`mcp-tool-output-budget.md:5`
- 测试夹具：`scripts/mcp-image-payload-test-server.mjs`（`emit_image`，preset `tiny|below_limit|just_over_limit|large|huge`，
  用 PNG `tEXt` chunk 填充字节；`below_limit≈180 KiB`、`just_over_limit≈210 KiB`）。【规格】`docs/mcp-image-payload-test-server.md:5-18`
- MCP Apps 资源：`resources/read` 走共享 MIME 白名单 + **每次结果累计 8 MiB 总量检查（超限整次拒绝，不截断）**，
  并去除 `_meta` 后才交给插件页面；未知 MIME、空 MIME、可执行类型一律拒绝。【规格】`docs/mcp-apps-resource-mime-spec.md:5-35`

### 3.7 错误如何回给模型

- MCP 业务失败用 **in-band** `isError:true`（HTTP 200 也可能是失败，如官方 MCP 配额耗尽/无套餐）；
  adapter 把 isError 记 **warn**（因为生产 logger 最低 info，debug 会完全不可见），并在失败时补
  `_meta[ZCODE_MCP_SERVER_REQUEST_ID_META_KEY]`（成功路径不加，避免噪声）。【代码】`index.ts:1212-1246`
- 传输级失败（超时/连接断）= throw；超时通过 SDK error code `-32001`/`AbortError` 识别，
  并区分"贴着预算被我们掐断"（`budgetExhausted`）与"远小于预算被对端/网络断"。【代码】`index.ts:1247-1270`
- 默认 tool 超时 `DEFAULT_MCP_TIMEOUT_MS = 30_000`；进度通知会**重置**超时（"长任务只要持续上报就不会被 timeoutMs 打断"）。【代码】`index.ts:111,1186-1192`
- node_repl 侧则只返回**结构化 error.message**（不含 name/stack/失败前日志），并保留通用 MCP `isError` 标记，
  避免"只消费模型结果文本的分析链路无法区分成功与失败"。【代码】`result.ts:143-147`

## 4. 生命周期与隔离

### 4.1 MCP 子进程生命周期 / 兜底 / Windows 回收

- **清理所有权**：`adapters/src/mcp` 拥有 stdio 进程清理；每条 close 路径都必须在 `client.close()/transport.close()` 之前或同时
  terminate SDK transport pid 的**整棵进程树**；必须幂等（重连/断开/adapter close/连接超时/连接失败都可能竞争）。【规格】`docs/design/v2/mcp-stdio-process-lifecycle.md:7-19`
- Windows：`taskkill /PID <pid> /T /F`；macOS/Linux：能发进程组就发进程组，并额外收集已知子孙；
  HTTP/SSE 不做进程清理；清理失败只 warn，不阻断 SDK close。【规格】同文件 `12-15`
- Windows **Job Object**（原型）：每个 MCP transport 独立 Job（不用全局 Job），
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，close 时 `TerminateJobObject + CloseHandle`，失败回退 taskkill；
  已知限制：attach 发生在 SDK spawn 之后，无法覆盖 attach 前已创建的后代，生产化需要
  `CREATE_SUSPENDED → AssignProcessToJobObject → ResumeProcess` launcher。【规格】`docs/windows-mcp-job-object-containment.md:5-31`；【代码】`adapters/src/mcp/windows-job-object.ts:1-66`
- 进程树实现有明确超时：POSIX grace 250ms / force 750ms / verify 250ms / poll 25ms，Windows taskkill 2s，process lookup 1s。【代码】`adapters/src/mcp/process-tree.ts:4-9`

### 4.2 idle / 兜底 / 连接池

- 连接池：`DEFAULT_IDLE_GRACE_MS = 30_000`；无引用后延时关闭（`scheduleClose`），`closeTimer.unref?.()` 不阻止进程退出。【代码】`adapters/src/mcp/pool.ts:18,100-111`
- `connectionKey = [serverName, scope, stableStringify(config)]`，其中 `scope = isolation==="workspace" ? workspaceKey : leaseId`。【代码】`pool.ts:640-663`
- lease：`acquireLease({leaseId,sessionId})`，`refs:Set<leaseId>` 引用计数；释放后到期关闭；日志 `mcp.pool.lease.acquired/released` 用同一 `mcpConnectionId`。【代码】`pool.ts:42,174-360`
- **存活校验（revalidate）**：`pingServer` 用 MCP 基础协议 `client.ping()`，上限 `MCP_PING_TIMEOUT_MS = 5000`；
  传输级失败判死并原地重连（保持 entry 身份，避免其他 lease 掉成 "not leased"）；`connecting/disabled/untrusted` 不打扰；
  并发刷新共享同一次校验。**这是从真实 bug 里长出来的**：设置页 lease 命中旧 entry 后永远显示"已连接"。【代码】`pool.ts:113-171`（revalidate）、`index.ts:478-500`（pingServer）；【规格】`docs/ui-mcp-status-refresh-issue.md:16-85`
- 该文档还记录了一个通用坑：HTTP/SSE 服务被停掉时**没有任何 close 事件**，record 会永远停在 connected——所以必须有主动 ping 而不是等 onclose。【规格】`ui-mcp-status-refresh-issue.md:31-33`

### 4.3 多会话 / 多 workspace 隔离

- 默认 **session isolation**；只有 `config.isolation === "workspace"` 的 server 才跨 session 复用连接，
  且插件只能由**官方市场**插件声明该字段，其他插件声明被忽略。【代码】`pool.ts:624,646-651`；`plugins/mcp.ts:186-195`
- `workspaceKey = workspaceIdentity?.trim() || workingDirectory`（identity 用于隔离，path 只用于执行）。【代码】`pool.ts:634-638`；【规格】`docs/zcode-agent-next-steps.md:28`
- 内置 `node_repl` 固定 `isolation:"workspace"` + `protocolVersion:"2026-07-28"`，因此同 workspace 的多 session 共享一个 frontend/一个 stdio PID；
  会话状态由 Browser/CUA 的 session registry 承担，不在 MCP frontend 里。【代码】`built-in-node-repl.ts:45-51`；【规格】`docs/mcp-stateless-node-repl.md:158-167`
- 远程 workspace 必须贯穿 `workspaceIdentity + remoteSessionId`；URL/identity 缺失时宁可 cold miss 也不能退化成按 path 共享。【规格】`docs/mcp-async-tool-catalog-cache.md:465-471`

### 4.4 stateless vs stateful 的取舍（结论）

ZCode 的做法是**把"MCP 连接状态"和"执行/kernel 状态"彻底分开**：
- MCP frontend 可以共享（workspace isolation）、可以无状态（每次 `js` 一次性 worker）；
- 真正的产品状态（browser tab、CUA frame/permission）放在**session registry / BrowserControlPort / CuaControlPort**；
- 好处：frontend 崩溃后重连"不需要恢复 JavaScript"（`docs/mcp-stateless-node-repl.md:166-167`），也不会有一个藏着隐式状态的进程。
- 代价：模型每次都要重新 bootstrap SDK、用稳定 tab id 找回页面（工具描述里反复强调），且跨调用没有模块缓存。【推断，基于 `tool-contract.ts:32,53-58` 与同规格】

## 5. 外部 MCP 导入 / 插件 MCP session runtime merge

### 5.1 从外部 Agent 导入 MCP（配置合并，不是运行时）

- 目标：全局 `~/.zcode/cli/config.json` 的 `mcp.servers`，或项目 `<workspace>/.zcode/config.json`。【规格】`docs/external-agent-mcp-import.md:14-17`
- 来源（首批）：Claude Code、Codex CLI（TOML）、OpenCode、OpenClaw、Qwen Code、Qoder/Qoder CN、Trae、Kiro CLI、Roo Code、CodeBuddy、通用 `.agents/mcp.json`。【规格】`external-agent-mcp-import.md:22-37`
- 规则：同名 server **保留现有 ZCode 配置**、外部标为 skipped 不覆盖；不扫描 Gemini 目录；
  TOML 只读、写入永远是 ZCode JSON；OpenCode 的 `command: string[]` 转成 `command + args`。【规格】`external-agent-mcp-import.md:17-18,39-44`

### 5.2 插件 MCP 的命名空间 / 隔离 / 覆盖顺序

- 插件 MCP 统一命名 **`plugin:<pluginName>:<serverName>`**（`toNamespacedServerName`）。【代码】`adapters/src/plugins/mcp.ts:67-69`
- 定义来源两个：插件根 `.mcp.json` 与 manifest `mcpServers`（`{...fromFile, ...fromManifest}`，manifest 覆盖）。【代码】`plugins/mcp.ts:22-29`
- 变量替换 `${...}`（`TEMPLATE_PATTERN`），env 自动注入 `ZCODE_PLUGIN_ROOT/ZCODE_PLUGIN_DATA/ZCODE_PROJECT_DIR`（含 CLAUDE_* 兼容别名）；
  **插件身份 `ZCODE_PLUGIN_ID` 由 resolver 权威覆写**（manifest/user env 不可覆盖，防第三方伪造 official CUA 身份）。【代码】`plugins/mcp.ts:19,219-235`
- official auth（`auth` 字段）与 `oauth` 互斥，直接禁用；官方鉴权路径下静态 headers 不得含保留头（`findOfficialMcpReservedHeaders`）；
  普通/第三方 MCP 静态带 `authorization` 仍是合法用法（不做全局拦截）。【代码】`plugins/mcp.ts:266-290`
- **session runtime merge**：`{...pluginMcpServers, ...explicitUserMcpServers}` —— 先注入插件 MCP，再让用户配置覆盖同名；
  修复前把 runtimeConfig 当完整覆盖，导致 `plugin:chrome-devtools-mcp:...` 等插件 MCP 在用户 MCP 存在时整体丢失。【规格】`docs/plugin-mcp-session-runtime-merge.md:5-22`
- **宿主保留名**：`node_repl` 在"用户/plugin MCP 合并后覆盖同名项"，且第三方/inline 插件强制 `plugin:<plugin>:<server>` 不能冒充
  `mcp__node_repl__*`；MCP server 禁用/失败时**不回退**暴露 core 内建 `js`（"避免模型看到两套状态所有者"）。【规格】`docs/browser-use/2026-07-13-node-repl-mcp-runtime-spec.md:126-144`

### 5.3 官方 MCP 的宿主签发身份头（另一条鉴权链）

- 官方 Server MCP 不发静态 token，而是宿主按当前登录身份**每请求解析**：
  `Authorization: Bearer <zcode JWT>` + `X-Bigmodel-Authorization: Bearer <MaaS JWT>`（+ TEAM 时 `target-type/organization/project` 头）。【代码】`services/src/official-mcp/officialMcpCredentials.ts:13-17,422-442`
- 头名常量 `OFFICIAL_MCP_AUTH_HEADER_NAMES`；credential resolver 带 **in-flight 去重**（只合并并发，settle 即丢弃，不做时间缓存，
  避免读到已被替换的旧凭证），作用域是 host 全局（"按 plugin/mcpKey/workspace 分桶只会削弱去重、不增隔离"）。【代码】`officialMcpCredentials.ts:457-500`
- stdio 形态的官方 MCP 把身份头放在**每条出站协议的 `_meta`**（`OFFICIAL_MCP_AUTH_META_KEY`）而不是 env。【代码】`plugins/mcp.ts:200-203`；`index.ts:1965-1976`

### 5.4 远端（SSH/WSL/Docker）资产合同里没有 node_repl 宿主

- `REMOTE_AGENT_OFFICIAL_PLUGIN_REQUIRED_RELATIVE_PATHS` 明确不列 `node-repl-host`：注释写"远程工作区当前不承载
  Browser Use / Computer Use，因此宿主 runtime 不进这份远端合同——要支持时把 node-repl-host 补进 PACKAGE_NAMES"。【代码】
  `server/src/remote/zcodeAgentOfficialPluginAssets.ts:52-58`

## 6. 未解问题 / 边界发现

1. **未证实**：ZCode 是否支持 MCP over WebSocket / 自定义 transport。仓库内只找到 stdio/http(Streamable)/sse 三种。
2. **未证实**：node_repl 的一 token 一调用模型是否抵御"同机同 UID 进程直接读 pipe"——CONTRACT.md 自己承认
   "cell 可经 `node:inspector` 影响主线程，账本只防住冒充 Worker 结果消息"，即**不是完整沙箱**。【代码】`call-ledger/CONTRACT.md:17`
3. **未找到**：ZCode 对 MCP server 的"健康检查频率/被动刷新"没有最终方案；`ui-mcp-status-refresh-issue.md:114-121`
   明确"被动新鲜度未做"，可选方案是低频（60s）轮询或 server 推 notification。
4. **未找到**：MCP tool 结果 artifact 的存放路径/保留策略在 docs 里只有"artifact store / tool artifact 降级"描述，没有具体目录约定；
   属于 task-4（证据/产物）可能覆盖的范围，本条不深挖。
5. **边界发现（task-3 相关）**：CUA 的 broker token 是"scoped to the target zcode-cua MCP server only"，且 `sanitizeZCodeRuntimeEnv`
   会从所有子进程 env 剥离——这条与 node_repl 的"每次调用由账本签发二次 token"是**两套不同机制**，主报告不要混写。
   【规格】`docs/cua-permission-broker/zcode-cua-permission-broker.md:24-35`
6. **边界发现**：`js` 的 `title` 字段是**用户可见文案**契约（1..120，禁实现术语），UI 展示依赖它；`tools/list` 必填、运行时可选，
   这种"模型合同与运行时合同分离"的模式值得照抄。【规格】`docs/browser-use/2026-07-13-node-repl-mcp-runtime-spec.md:83-90`；【代码】`contracts/src/tools/node-repl.ts:36-43`

## 7. 对本项目（PCCtrl：容器内 AstrBot → Windows 执行器）的结论

> 本节全部为【推断】，依据是上面的代码/规格事实 + 本项目约束（两个 Docker 容器、Windows 宿主、未定技术栈）。

### 7.1 stdio 还是 Streamable HTTP/SSE？

- **stdio 不可用**：stdio MCP 要求 server 是 client 的**子进程**。AstrBot 在 Linux 容器里，执行器在 Windows 宿主上，
  跨主机/跨容器无法 pipe。ZCode 的 stdio 路径（`ProcessTreeStdioClientTransport` + taskkill/Job Object）只适用于同机。
  ⇒ 结论：**AstrBot 侧只能走 HTTP 类传输**。
- **优先 Streamable HTTP，不要 SSE**：ZCode 自己把 SSE 归为 legacy-era 边缘（`resolveVersionNegotiationMode`
  对 sse 直接返回 `legacy`），且官方鉴权在 sse 上"没有对应通道，继续拒绝"。【代码】`index.ts:2366-2371`；`plugins/mcp.ts:200-209`
  ⇒ 选 `type: "http"`（Streamable HTTP）作为唯一主路径，SSE 只作为兼容兜底（如果 AstrBot 侧 MCP 客户端只支持 SSE）。
- 若 AstrBot 的 MCP 客户端能力有限，也可以**保留 SSE 作为唯一可用选项**，但要在文档里写清"这是 legacy 传输，现代 spec 不推荐"，
  并避免依赖 per-request `_meta`。

### 7.2 容器网络下的地址 / 认证 / 超时 / 重连

- **地址**：容器 → 宿主必须用一个**宿主可达地址**（`host.docker.internal`（Docker Desktop）或宿主 LAN IP + 端口映射），
  绑定 `0.0.0.0` 或明确网卡；不要把地址硬编码进镜像。ZCode 的可借鉴点：身份 key 用 `workspaceIdentity` 而不是 path，
  地址应来自**配置 + 环境变量**而非代码常量（对照 `shared/src/mcp.ts` 的配置分层）。【推断】
- **认证**：照抄 node_repl 的"**每次调用一次性 token**"思路——AstrBot 侧用长期 bearer/共享密钥换短期 token，
  执行器校验 token→会话，且**不要把 token 暴露给模型/插件代码**（ZCode 的账本模型：Worker 只拿本次调用 token，
  上游 token 只在主线程）。【代码】`server.ts:358-368`、`ledger.ts:45-61`、`CONTRACT.md:14-16`
  另外照抄"**可信事实只由服务端记**"：不要相信请求体里自报的 session/app 身份（`result.ts:101-124` 的反面教材）。
- **超时**：分层——连接超时 / 每 tool 调用超时 / 长任务超时。ZCode 的默认 30s 是 tool 默认，node_repl 的 60s 是执行默认，
  "预计超过 30s 必须显式传 timeout"是模型合同写法。PCCtrl 建议：**默认 30s、显式 `timeout_ms` 上限 120s、
  并且用 progress notification 重置超时**（`index.ts:1186-1192` 的 A8 语义：只要持续上报就不打断）。
- **重连**：不要只等 socket onclose——ZCode 的实测教训是 HTTP/SSE 断连**不会**产生 close 事件，必须主动 ping
  （`client.ping()`，5s 上限）判活并原地重连；并发刷新共享一次探测。【代码】`index.ts:478-500`、`pool.ts:113-171`；
  【规格】`ui-mcp-status-refresh-issue.md:31-33,68-85`。PCCtrl 建议给执行器加 `/healthz` 或 MCP `ping`。
- **重试**：ZCode 对 caller 与后台事务的预算是分离的（15s vs 300s）；PCCtrl 里"AstrBot 单次请求 15s 超时"不应该
  杀掉 Windows 侧正在执行的长动作，应返回"已受理 + 后续查询/事件"。【规格】`mcp-oauth-two-phase.md:29-32`（同类设计思想）

### 7.3 可以直接照抄的 7 个设计（按性价比排序）

1. **工具契约三件套**：`inputSchema`（strict, additionalProperties:false）+ 必填用户可读 title + 明确 timeout 语义；
   模型合同与运行时合同分离（tools/list 严、运行时宽）。【代码】`contracts/src/tools/node-repl.ts:27-43`、`server.ts:66-73`
2. **结果包装**：图片排在 text 之前、`isError` 与内容分离、错误只回 message、始终非空（`(no output)`）、
   大结果预算化+artifact。若 PCCtrl 也要回截图，直接照搬"image block 在前 + 每结果最多一张权威 raster"。【代码】`result.ts:125-213`
3. **预算**：文本 `resultBudget`；图片 base64 200 KiB / decoded 153600 B / 最长边 2000；MCP 资源 8 MiB 总量整次拒绝。【规格】`mcp-tool-output-budget.md:21-42`、`mcp-apps-resource-mime-spec.md:32`
4. **健康检查**：MCP `ping` + 5s 上限 + 判死原地重连 + 并发共享探测；UI 状态不要靠"连接成功快照"缓存。【代码】`index.ts:478-500`、`pool.ts:119-171`
5. **进程守护**：Windows 上 per-server Job Object（`KILL_ON_JOB_CLOSE`）+ `taskkill /T /F` 回退；必须有进程树清理，
   否则 `npx`/wrapper 会留下孤儿（ZCode 专门为此写了一份 spec）。【规格】`docs/windows-mcp-job-object-containment.md`、`docs/design/v2/mcp-stdio-process-lifecycle.md`
6. **诊断**：stdio/子进程 stderr 尾部有界 buffer（4KB）、**脱敏**、只在失败事件附带；错误分类用稳定错误码/类型，不匹配文案。【代码】`index.ts:2120-2143,2374-2392`
7. **防刷 token**：像 `no-progress-guard` 一样给"重复无进展"加提示而不是硬停——文案只授权结束，不要求换做法。【代码】`no-progress-guard.ts:37-39`、`docs/mcp-stateless-node-repl.md:101-132`

### 7.4 明确不要照抄的部分

- **不要照抄 workspace 共享 MCP frontend**：PCCtrl 只有一个 Windows 执行器 + 一个 bot 容器，没有多 workspace 概念；
  共享反而会引入会话串线风险（ZCode 靠 requestMeta 账本才敢共享）。
- **不要照抄"零 listener 的 OAuth 两阶段"**：那是为"多个 CLI 进程共享 credential 文件"设计的；PCCtrl 只需
  一个静态共享密钥或短期 token，做两阶段反而过度设计。
- **不要照抄 fresh-kernel 无状态**：node_repl 无状态是因为它要跨 workspace 共享进程且模型每次能重新 bootstrap SDK；
  PCCtrl 的执行器若是"长驻浏览器/桌面会话"，更适合"有状态会话 + 每次调用带 sessionId"，并把状态所有权写死在一处（ZCode 的 `20-node-repl-call-ledger` 就是这种"唯一所有者"范例）。

## 证据索引（文件:行号）

### 真实源码（node-repl-host，均相对 `apps/zcode-cli/packages/node-repl-host/`）
- `src/server.ts:54-98` 常量/pluginRoot/jsInputSchema/唯一 Tool `js`；`:133-181` in-process executor；
  `:183-310` `createNodeReplMcpRuntime`（server、serialized、tools/call、账本/broker、dispose）；
  `:312-348` 一次性 Worker；`:395-476` `main()`/serveStdio(legacy reject)/shutdown/Worker 入口。
- `src/tool-contract.ts:1-58` 超时/server 版本/instructions/js 描述；`src/result.ts:53-214` `toMcpRunResult` 全部规则；
  `src/cua-bridge.ts:29-132` bridge globals + socket 协议；`src/cua-broker.ts:17-236` broker 校验与 facts；
  `src/browser-bridge.ts:29-209` 浏览器 transport/meta；`src/runtime-bridge.ts:1-20` bridge symbol。
- `src/process-lifecycle.ts:20-98` 异常降级/EPIPE/stdin 与信号 shutdown；`src/no-progress-guard.ts:5-96` 三档提示。
- `src/call-ledger/ledger.ts:11-61` facts + 一次性 token；`src/call-ledger/browser-broker-proxy.ts:26-245` 字段清洗/截图记账；
  `src/call-ledger/CONTRACT.md:5-17` 流程与已知缺口。
- `test/mcp-server.test.ts:114-236,255-306,351-405,490-571,807-849`；`test/mcp-server-process-guards.test.ts:17-174`；
  `test/no-progress-guard.test.ts:26-108` —— 上面行为断言的实测来源。

### 真实源码（其他包）
- `apps/zcode-cli/packages/bootstrap/src/app/built-in-node-repl.ts:9-52` —— 启用条件/isolation/protocolVersion/timeout。
- `…/bootstrap/src/app/official-plugin-runtime.ts:19-43` —— `createBundledMcpRuntimeConfig`（stdio + ELECTRON_RUN_AS_NODE）。
- `…/contracts/src/tools/node-repl.ts:4-66` —— `JsInputSchema`/`JsRuntimeInputSchema`/`JsOutputSchema`。
- `packages/shared/src/mcp.ts:18-55,184-207,300-405` —— McpSource/scope、`ZCodeAgentMcpServer`、配置兼容与 DTO 归一化。
- `…/adapters/src/mcp/index.ts:111-115,478-500,1186-1270,1948-2000,2120-2143,2366-2421` —— 超时/ping/结果错误/三种 transport/stderr/协商。
- `…/adapters/src/mcp/pool.ts:18,61-171,600-663` —— idle 30s、revalidate、connectionKey、scope。
- `…/adapters/src/mcp/process-tree.ts:1-44`、`…/windows-job-object.ts:1-66` —— 进程树/Job Object。
- `…/adapters/src/plugins/mcp.ts:18-69,176-299` —— 插件 MCP namespace/type 白名单/official isolation/auth/保留头。
- `packages/services/src/official-mcp/officialMcpCredentials.ts:13-17,121-137,422-500` —— 官方 MCP 身份头 + in-flight 去重。
- `packages/services/src/mcp-sync/mcpSync.ts:16-38` —— 配置读写与 list 边界；`packages/server/src/remote/zcodeAgentOfficialPluginAssets.ts:42-58` —— 远端合同不含宿主。

### 设计规格（均相对 `ZCode-main/docs/`）
- `mcp-stateless-node-repl.md` —— 无状态 kernel/workspace frontend/工具面收敛/no-progress（现役唯一事实源）。
- `mcp-dual-era-version-negotiation.md` —— dual-era 配置、时序、probe 预算、失败分类、日志字段。
- `mcp-async-tool-catalog-cache.md` —— catalog cache 容量/TTL/identity/sanitize/generation/complete-hit。
- `mcp-tool-output-budget.md` 图片预算；`mcp-apps-resource-mime-spec.md` 资源 MIME + 8 MiB；`mcp-stdio-failure-stderr-diagnostics.md` stderr 诊断。
- `mcp-oauth-two-phase.md` 两阶段 OAuth；`mcp-config-compat-diagnostics.md` legacy 字段归一化；`mcp-platform-boundary.md` 平台边界。
- `plugin-mcp-session-runtime-merge.md`、`external-agent-mcp-import.md` —— 合并与导入。
- `browser-use/2026-07-13-node-repl-mcp-runtime-spec.md`（工具合同/命名/broker）、`2026-07-07-node-repl-engine-spec.md`、
  `2026-07-07-node-repl-tools-spec.md`、`2026-07-08-node-repl-toplevel-binding-persistence-spec.md`（NodeReplSession 演进）、
  `2026-07-28-stateless-browser-use-mcp-spec.md`（被否决方案，重要反例）、`browser-use-plugin-runtime-boundary.md`。
- `design/v2/mcp-stdio-process-lifecycle.md`、`windows-mcp-job-object-containment.md` —— 进程回收。
- `ui-mcp-status-refresh-issue.md` —— 连接池/健康检查的真实 bug 与修复。
- `mcp-unreachable-test-server.md`、`mcp-image-payload-test-server.md` —— 测试夹具。
- `cua-permission-broker/zcode-cua-permission-broker.md:5-35` —— 仅取 node_repl 迁移段与 token 作用域（上下文，不与 task-3 重复）。

