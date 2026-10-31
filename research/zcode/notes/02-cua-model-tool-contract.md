# 模型可见的 CUA 工具契约（SKILL / docs / SDK 脚本）

> **研究范围**：`ZCode-main/apps/zcode-cli/packages/zcode-cua-plugin/`（模型可见说明书 + 注入 Worker 的 SDK 脚本 + 分发契约）、`docs/cua/2026-08-24*`（node_repl 收敛背景）、`docs/cua/producer/2026-09-10-cua-sdk-v3-api.md`（模型面设计规格）、以及 `apps/zcode-cli/packages/node-repl-host/` 的 bridge/broker（SDK 请求如何落地）。
>
> **阅读材料**（行数为实际读到的总行数）：`skills/computer-use/SKILL.md`(307)、`docs/computer-use.md`(471)、`scripts/computer-use-client.mjs`(1210)、`package.json`(24)、`.zcode-plugin/plugin.json`(10)、`scripts/check-sdk.mjs`(4)、`scripts/sync-cache.mjs`(116)；`docs/cua/2026-08-24-cua-node-repl-sdk-migration-spec.md`(189)、`docs/cua/cua-tool-app-identity-summary.md`(196)、`docs/cua/producer/cua-14-tool-e2e-validity.md`(126)、`docs/cua/producer/2026-09-10-cua-sdk-v3-api.md`(888)、`docs/cua/producer/frame-coordinate-internal-binding.md`(94)、`docs/cua/producer/cua-receipt-observation-contract.md`(93)、`docs/cua/2026-08-25-*`权限两篇、`docs/cua/2026-10-13-windows-input-delivery-spec.md` 相关节、`docs/cua/producer/ax-tree-optimization-design.md` 相关节；`node-repl-host/src/{cua-bridge,cua-broker,result,tool-contract,server}.ts`、`packages/contracts/src/tools/node-repl.ts`、`packages/bootstrap/src/zcode-protocol/computer-use-operation-event.ts`、`packages/zcode-cua/*.d.ts`（契约占位）。
>
> **核实基线**：`ZCode-main @ 2026-10 快照`；插件版本 `0.6.5`（package.json:4 / plugin.json:3），node_repl host 版本 `0.7.0`（tool-contract.ts:22），SDK 文件头注释标注设计文档为 `cua-sdk-v3-api.md`（client:4）。**重要基线事实**：`packages/zcode-cua/` 是 API-compatible 占位包，producer 工具 handler/schema 源码不在本仓库（README:1-7），因此"逐字 schema/错误字符串"只能以 docs+SDK 代码为准，producer 侧细节标注为未证实。
>
> **证据标注**：【代码】= 本仓库真实源码；【规格】= docs 设计规格；【契约】= 类型/占位包/常量契约；【推断】= 我的推论；未证实 = 找不到证据。

## 0. 结论速览（给主报告直接可用的 12 条）

1. **模型只有 1 个 MCP 工具入口**：`mcp__node_repl__js`（host 0.7.0 的 tools/list 仅 `js` 一个工具）。CUA 全部能力通过 Worker 内注入的 `agent.computerUse` SDK 暴露，不是 MCP 工具。【代码】`node-repl-host/src/server.ts:91-98`、`tool-contract.ts:13-22`
2. **模型看到三层文档**：常驻 `skills/computer-use/SKILL.md`（系统提示里的 skill）+ 按需 `agent.documentation.get("computer-use")`（即 `docs/computer-use.md`）+ 工具/错误文案（`js` tool description、computer-use-client 抛出的错误消息）。【代码】client:1188-1205、docs/computer-use.md:3-6
3. **工具面 = 14 个底层 tool + 绑定对象 API**：底层 `agent.computerUse.computer.<tool>`（14 个 snake_case）；主用面是绑定对象（`getApp/listApps/getState/requestAccess/stop` + `Target` 13 个 camelCase 成员，含已文档化的 `elements()`）。【代码】client:30-45、584-810、1152-1180
4. **14 个名字（当前对齐版）**：`list_apps, list_windows, get_app_state, left_click, scroll, left_click_drag, type, set_value, select_text, key, perform_action, paste, request_access, stop_computer_control`。SKILL(280-299)、docs(160-180)、client `COMPUTER_METHOD_NAMES`(30-45) 三方逐字一致；producer 14-tool 文档的算术 30−17+1(paste)=14 与之相容（`cua-14-tool-e2e-validity.md:44-45`）。【代码+规格】
5. **首选动作路径是 AX 元素索引**：语义、精确、可后台、不抢焦点；坐标是 canvas/游戏/Electron 的最后兜底；`strategy: auto|a11y|event` 显式控制派发路径，`event` 需要目标已在前台否则 `FOREGROUND_REQUIRED`。【代码】SKILL:39-65、docs:198-202
6. **观察方法自动展示结果，动作方法静默**：`getAXState/getScreenshot/getAXStateAndScreenshot/getState/listApps` 自己 emit；`getApp` 只绑定不展示；一个结果里只允许一张 raster，违者整帧被移除。【代码】SKILL:179-188、docs:358-367、client:420-488、result.ts:53-76
7. **图片以 inline base64 image block 交给模型，旁边紧贴 `image_ref` 文本块**；`frame_id` 已被内部化，模型只提交"最新栅格的整数像素"，不复制任何 token。【规格】frame-coordinate-internal-binding.md:16-30、57-85；【代码】client:204-218
8. **动作成功 = `undefined`/void，失败抛 `ComputerUseError{code, actionSent, retry, details}`**；`dispatch_status=possibly_sent` 会被转成 reject + `actionSent:true`，逼模型先观察，禁止盲重放。【代码】client:75-131、278-297
9. **每个 `js` cell 都是全新 Worker**：bootstrap 必须和动作在同一个 cell；跨 cell 的 stateId/frameId/diff 基线由 shared-host runtime 持有，所以下个 cell 的 `getApp` 是重新绑定而非重新观察。【代码】SKILL:17-37、migration:47-52、client:1-22
10. **模型侧硬纪律**：不许 sleep/polling（观察自带 settle）、无变化的独立观察不要马上重复、索引取自最新观察、不改窗口几何让坐标命中、失败/拒绝后先观察、`CONTROLLER_BUSY` 永不可重试。【代码】SKILL:128-263、docs:318-405
11. **契约漂移是真实存在的**：node_repl host 的 `js` tool description 仍在教 v1 写法（`nodeRepl.write(state.text)`、`state.state_id`），与 SKILL/docs 的 v3"观察自动展示/模型不碰 state_id"相反，且被测试钉死；错误码文档清单（20 个）是 SDK 映射（14 个可达）的超集。【代码】tool-contract.ts:43-52、mcp-server.test.ts:167-172、client:76-111
12. **给 AstrBot 最值得抄的 6 件事**：单一粗粒度工具面 + strict schema；`app_ref` 三键 + `window_id`；"最新帧内部绑定"的坐标契约；MCP image content 返回截图；typed receipt（`action_sent`/`dispatch_status`/`retry`）；"成功≠界面已变，必须再观察验证"的文案与错误分级（详见 §7.4）。

## 1. 模型侧入口：唯一工具 js + 三层文档

### 1.1 外层工具：`js`

- host 只注册一个工具 `js`，描述来自 `JS_TOOL_DESCRIPTION`，输入 schema 来自 `JsInputJsonSchema`。【代码】server.ts:91-98
- 模型态 schema：`{code: string, title: string(1-120 必填), timeout_ms?: int 1-120000}`，`.strict()`；执行态允许 title 缺失以兼容旧 provider/回放。【代码】contracts/src/tools/node-repl.ts:4-43、server.ts:66-73
- server 级说明（serverInfo.instructions）限域："Browser Use and Computer Use only. Use `js` ... only when the corresponding official skill instructs you to control a browser or computer."；默认超时 60s、上限 120s；"Every `js` call starts fresh"。【代码】tool-contract.ts:26-32
- 工具面演进：0.6.0 删掉 `js_reset` 与 `js_add_node_module_dir`（理由写在注释：前者"永不失败"诱导重复调用，后者把宿主职责推给模型）；因此 2026-08-24 迁移规格里"模型可见工具面固定为 js/js_add_node_module_dir/js_reset 三个"（migration:14-15）在 0.7.0 **已过时**，当前只有 `js`。【代码】tool-contract.ts:13-22、【规格】migration:14-15
- CUA 是否被使用只按引导语句 `setupComputerUseRuntime` 做布尔判定，用于 Windows 指示器/会话标签；不从源码抽动作名。【代码】computer-use-operation-event.ts:14-32、65-67

### 1.2 三层文档与加载方式

| 层 | 载体 | 何时进入模型 | 关键证据 |
|---|---|---|---|
| 常驻决策指南 | `skills/computer-use/SKILL.md`（307 行，frontmatter `name: computer-use`） | 作为 skill 注入（plugin.json 的 `"skills": "skills"`） | plugin.json:9、SKILL:1-15 |
| 按需参考 | `docs/computer-use.md`（471 行） | 模型显式 `nodeRepl.write(await agent.documentation.get("computer-use"))` | docs:1-6、client:1188-1205（readFile documentationRoot/computer-use.md） |
| 工具/错误文案 | `js` description、`ComputerUseError.message`、producer 回执文本 | 每次调用/失败时 | tool-contract.ts:34-58、client:517-553 |

- SKILL frontmatter 的 description 同时是路由规则："Use when a task needs a native desktop app's own UI or the OS. For anything inside a web page, use Browser Use. Main agent only."（SKILL:2-4）；正文再次强调 "Main agent only. Never delegate Computer Use to a subagent."（SKILL:15）。【代码】
- 插件 manifest 只有 `name/version/description/author/license/skills`，**没有 MCP server 声明**；package.json 的 description 写明 "execution is provided by the shared node_repl host"。【代码】plugin.json:1-10、package.json:6

### 1.3 每个 cell 的 bootstrap（模型必须原样抄写）

```js
const root = process.env.ZCODE_CUA_PLUGIN_ROOT ?? process.env.ZCODE_PLUGIN_ROOT ?? process.env.CLAUDE_PLUGIN_ROOT;
const { join } = await import("node:path");
const { pathToFileURL } = await import("node:url");
const { setupComputerUseRuntime } = await import(pathToFileURL(join(root, "scripts", "computer-use-client.mjs")).href);
await setupComputerUseRuntime({ globals: globalThis });
```

- 官方规则：bootstrap 必须是"每个 CUA cell 的第一条可执行语句"，且与动作**同 cell**；禁止把它放进前一个调用、动作放后一个调用。【规格】migration:47-52、【代码】SKILL:17-37、docs:26-44
- 根因（写在 SDK 头注释）：fresh Worker 使 `const app` 活不到下一个 cell；stateId/frameId/diff 基线由 shared host 的 runtime session 持有，所以下一个 cell 的 `getApp` 是"重新绑定而非重新观察"。【代码】client:1-22
- 入口命名以**当前代码**为准：注入的是 `agent.computerUse`（"不注入裸 `cua` 全局"）。设计文档里的 `cua` 全局/别名已被取代。【代码】client:1182-1186、1152-1173；【规格】v3-api:745-762（旧方案）

## 2. 工具清单与工具手册（问题 A + B）

### 2.1 14 个工具总表

| # | 工具名 | 用途 | 绑定对象对应 | 备注 |
|---|---|---|---|---|
| 1 | `list_apps` | 枚举可操作 app（pid/name/bundle_id/active） | `listApps()` / `getState()` | 唯一无参数工具 |
| 2 | `list_windows` | 枚举某 app 的窗口（window_id/title/main/focused/onscreen…） | 无（用 `getWindow` 或 `getApp({pid,window_id})`） | 多窗口/多面板必备 |
| 3 | `get_app_state` | 观察：AX 树（+可选截图） | `getAXState/getScreenshot/getAXStateAndScreenshot` | 单窗口作用域 |
| 4 | `left_click` | 左键点击（可改键/次数/修饰键） | `click()` | 右/中键与双击均由参数表达 |
| 5 | `scroll` | 滚动（元素或坐标目标） | `scroll()` | 单位 pages，clamp 0-100 |
| 6 | `left_click_drag` | 拖拽 | `drag()` | 拖到文件夹/滑块微调场景由坐标兜底 |
| 7 | `type` | 发送文本（逐字符输入） | `typeText()` | 非幂等，注意 actionSent |
| 8 | `set_value` | 直接设置可编辑元素值 | `setValue()` | 首选于 type/paste |
| 9 | `select_text` | 在可编辑元素内选文本/放光标 | `selectText()` | bound API 按内容匹配，tool 用 range |
| 10 | `key` | 按键/组合键/长按 | `pressKey()` | 组合键、repeat、hold_seconds |
| 11 | `perform_action` | 执行元素自报的语义动作 | `performSecondaryAction()` | 只能取当前树里的 action |
| 12 | `paste` | 原生剪贴板粘贴（Helper 内保存/写入/粘贴/还原） | `paste()` | 富文本/多行首选 |
| 13 | `request_access` | 只读权限快照（不触发系统弹窗） | `requestAccess()` | 返回 AccessStatus |
| 14 | `stop_computer_control` | 停止控制（kill switch） | `stop()` | 之后不得继续动作 |

【代码】client:30-45；【规格】SKILL:280-299、docs:160-180；【规格】cua-14-tool-e2e-validity.md:9-48（30→14 的删除清单：双击/三击/右键/中键/按住/hold_key/全屏 screenshot/zoom/list_displays/switch_display/cursor_position/wait/mouse_move/mouse_down/mouse_up/read_clipboard/write_clipboard/open_application，替代路径见该表）

### 2.2 参数总表（必填 / 可选 / 默认 / 范围 / 单位）

**所有工具都只接收一个 arguments 对象；未声明键会被拒（`unrecognized_keys`），缺必填键会报 "`expected object, received undefined`"。**【代码】docs:138-157

| 工具 | 必填 | 可选（默认） | 取值/单位/约束 |
|---|---|---|---|
| `list_apps` | — | — | `{}` |
| `list_windows` | `app_ref` | — | AppRef；裸字符串=**bundle id** |
| `get_app_state` | `app_ref` | `include_screenshot=false`、`disable_diffing=false` | bool |
| `left_click` | `target` | `mouse_button="left"`、`click_count=1`、`modifiers=""`、`strategy`、`app_ref`、`return_state="none"` | button: left/right/middle（含 l/r/m）；count 整数；modifiers 用 `+` 连接 |
| `left_click_drag` | `from_target`, `to` | `modifiers=""`、`strategy`、`app_ref`、`return_state="none"` | 两个 target 各自解析 |
| `scroll` | `target`, `scroll_direction`, `scroll_amount` | `strategy`、`app_ref`、`return_state="none"` | direction: up/down/left/right；amount 单位 **pages**，clamp 0–100 |
| `type` | `text` | `target`、`app_ref`、`strategy`、`return_state="none"` | target 可选（不给则打给当前焦点/绑定 app） |
| `set_value` | `target`, `value` | `strategy`、`app_ref`、`return_state="none"` | value: string |
| `select_text` | `target` | `text_range`、`app_ref`、`return_state="none"` | `text_range=[start,length]`（对元素当前 value 的偏移）；省略=全选 |
| `key` | `text` | `repeat`、`hold_seconds`、`app_ref`、`strategy`、`return_state="none"` | text 为单键或 `+` 组合；repeat=重复发送次数；hold_seconds=按住时长（秒，数值） |
| `paste` | `text` | `format="text"`、`app_ref`、`return_state="none"` | format: text/md/html（md/html 依赖 Helper 支持，缺失时 ACTION_UNAVAILABLE） |
| `perform_action` | `target`, `action` | `app_ref`、`return_state="none"` | action 必须来自当前树里元素自报的 actions |
| `request_access` | — | `capabilities`（数组） | 只读快照；capability 合法取值未在快照中证实 |
| `stop_computer_control` | — | `reason` | reason: string |

补充语义（docs 182-209）：`app_ref`=`{name}`/`{bundle_id}`/`{pid}`，可加 `window_id`；`target`=`{type:"element",index}` 或 `{type:"coordinate",x,y,frame_id?}`；`strategy`=`auto|a11y|event`（auto 优先 a11y、失败可回落合成事件；event 走全局输入且要求目标已前台）；`return_state`=`compact|full|none`（默认 none，动作内顺带返回状态，仅直调 tool 有用）；`modifiers`="cmd+shift" 形式；`repeat` 用于替代循环 pressKey。

### 2.3 每工具详解（模型视角）

**inventory**

- `list_apps`：返回 `AppInfo[]`：`{pid:number, name:string|null, bundle_id:string|null, active:boolean}`；macOS 的 bundle_id 是 bundle id，Windows 是 AUMID/可执行路径派生身份，Linux 可为空。【代码】docs:100-106；【规格】v3-api:164-170；【代码】client:1097-1109
- `list_windows({app_ref})`：每窗口一行，字段语义严格（docs:251-289）：`window_id` 稳定整数（回填 `app_ref` 用）；`title` 空串=有 AXTitle 但为空（Qt/自绘面板常见），`null`=读不到；`subrole` 仅真实 AX 窗口才有（区分"可绑定窗口"与"CoreGraphics 残留 surface"）；`text_preview` 只在 title 空时给，**永远不是标识符**（可能含用户数据）；`bounds` 是全局屏幕点、**诊断专用、禁止当坐标**；`main`/`focused` 是 AX 主/键窗口，不等于系统前台（前台看 `listApps().active`）；`onscreen=false` 的行不可操作应跳过；`index` 只是本次响应里的行号，不是 window id/元素索引。

**observation**

- `get_app_state`：模型收到的是"渲染后的 AX 树文本"（+可选 raster）；结构化字段 `{state_id, app, window, elements, text, snapshot_mode, base_state_id}` 由 SDK 消费，模型面通过绑定 API 只拿到 `string` 树（`elements()` 才拿表）。`include_screenshot=true` 时结果附带 image + 紧邻 `image_ref` 文本块；`has_image:false` 表示"这次没要像素"，**不是截图失败**。【代码】SKILL:58-61、client:509-553；【规格】migration:145-165、cua-receipt-observation-contract.md:70-80
- 观察作用域是**一个窗口**；不给 `window_id` 时每次观察都重新解析主/键窗口，因此新弹的 modal 会变成被捕获窗口（模型要看观察结果里的 `window` 块再决定动作/关闭）。【代码】SKILL:170-177、docs:298-316
- 大树的处理：按优先级裁剪（保留祖先），头部会说明"indices are sparse"，此时索引跳号；`elements()` 返回完整表（含被裁行），"文本可以是 diff，结构必须完整"；但容器只报部分子节点时 header 写 `showing A-B of N items`，这些行从未进入观察，`elements()` 也拿不到——需滚动容器/用应用自身选择器再观察。【规格】SKILL:166-169、docs:407-421；【规格】v3-api:428-434

**pointer**

- `left_click`：`target` 为元素索引或坐标；`mouse_button` 支持右/中键（替代被删的 right_click/middle_click）；`click_count` 2/3 替代 double/triple click；`modifiers` 在按下那一瞬生效（cmd+click、shift+click）。返回 void；失败抛错。【代码】client:706-725；【规格】v3-api:283-291、387-392
- `left_click_drag`：`from_target`/`to` 各自按"number=索引 / [x,y]=栅格像素"绑定；拖拽唯一入口，被删的 mouse_move/down/up 不保留原语。`strategy` 2026-10-27 才补透传（此前 `event` 被静默吞成 auto）。【代码】client:727-739
- `scroll`：`scroll_amount` 单位是 pages（不是像素），clamp 0–100；direction 取 4 个长名（bound API 还接受 u/d/l/r 简写）。【代码】client:753-768；【规格】docs:194-195

**text / keyboard**

- `type`：逐字符输入；历史上有 macOS unicode 缺陷、多行触发自动缩进/补全，故多行/富文本推荐 `paste`。【规格】v3-api:621-622
- `set_value`：直接设值，**优先于 type/paste**（SKILL:48-49）；目标不可设置时报 `NOT_SETTABLE`。
- `select_text`：tool 用 `text_range=[start,length]`；绑定 API 的 `selectText(index, text, {prefix,suffix,selectionType})` 在 SDK 侧用元素 value 本地算 range：0 命中→`NOT_SELECTABLE`，多命中→`NOT_SELECTABLE` 并报候选数（"歧义被拒绝而不是取第一个"），`selectionType`=`text|cursor_before|cursor_after`（后两者折叠为光标）。【代码】client:770-776、818-855；【规格】SKILL:228-230
- `key`：`text` 接受短名与 xdotool/keysym 风格（`"a"`、`"Return"`、`"Control_L+a"`、`"super+c"`）；macOS 用 `cmd`，Linux/Windows 用 `ctrl`；`hold_seconds` 表示按住时长，`repeat` 表示重复发送。`performSecondaryAction` 只能取元素自报 action。SDK 层做键位别名规范化（Return/Enter/KP_Enter→return；Control_L/R→ctrl；Alt/Meta→alt；Super→cmd/win/super；Escape→esc；Prior/Next→pageup/pagedown；period/comma/slash/minus/equal→符号）。【代码】client:741-751、862-888；【规格】SKILL:211-223、v3-api:628-646
- `paste`：原生 Helper 完成"保存剪贴板→写入→发粘贴键→还原"；不用 host 侧四步组合（会与用户改剪贴板竞态）。无 app 读取粘贴时以超时失败，而不是报成功；粘贴快捷键无法指定字段，落在当前键盘焦点处，需检查结果。md/html 在 Helper 未支持时抛 `ACTION_UNAVAILABLE` 并提示降级。【规格】v3-api:607-626；【代码】docs:444-448

**semantic / lifecycle**

- `perform_action`：action 必须出现在当前树该元素的 `actions` 里（compact 无条件带 actions 是 2026-09 的修复：默认档曾把语义动作必需的数据藏起来）；猜 action 名 → `ACTION_UNAVAILABLE`。SDK 在 action 为空时直接抛 `ACTION_UNAVAILABLE` 并带"do not guess one"文案。【代码】client:796-808；【规格】v3-api:266-281
- `request_access`：**只读权限快照，不直接触发系统弹窗**（macOS 的结构化状态：`{schemaVersion:1, platform:"darwin", grantOwner, accessibility:"granted"|"stale"|"denied", screenRecording:"unknown"|"granted"|"denied"}`）。绑定 API 返回 `AccessStatus{ready, accessibility:"granted"|"denied"|"unknown", screenRecording:..., message?}`。【规格】request-access spec:7-25；【契约】request-access-contract.d.ts:3-9；【代码】docs:108-113、client:1163-1168
- `stop_computer_control({reason?})`：触发 kill switch；之后必须立即停止，不得换用其他 UI 自动化技术。【代码】SKILL:252-258、client:1169-1172

### 2.4 绑定对象 API（模型实际最常用的面）

`agent.computerUse`：`getState(options?)`、`getApp(target|AppRef)`、`listApps(options?)`、`requestAccess(capabilities?)`、`stop(reason?)`、`computer`（14 个逃逸口）。`Target` 当前文档化 13 个成员（App/Window 共享；设计稿冻结的是 12 个交互面成员，`elements()` 于 2026-09 提为文档化）：
`getAXState(StateOptions)`、`getScreenshot(ObservationOptions)`、`getAXStateAndScreenshot(StateOptions)`、`elements()`、`paste(text, PasteOptions)`、`click(number|Vec2, ClickOptions)`、`drag(number|Vec2, number|Vec2, DragOptions)`、`pressKey(key, PressKeyOptions)`、`scroll(number|Vec2, direction, pages?, ScrollOptions)`、`selectText(index, text, SelectTextOptions)`、`setValue(index, value)`、`typeText(text)`、`performSecondaryAction(index, action)`。
【代码】SKILL:67-123、docs:46-133、client:584-810

- 未文档化成员：`getWindow(target, windowId)`（client:1174-1180）与 `elements()`（enumerable:false，但已在 SKILL/docs 文字中列为可用）。注意：设计文档当初写"getWindow 不进 documentation.get()"（v3-api:404-406），而当前 `docs/computer-use.md:298-300` 已明确写 `agent.computerUse.getWindow`，说明"未文档化"标记已漂移。【代码+规格】
- `elements()` 的观察**不展示**但会移动 diff 基线，因此 SDK 清 `treeSeen`，下一次 `getAXState` 强制整树；其返回值就是模型选目标的数据来源，属于"模型可见"（2026-10-25 修正）。【代码】client:1065-1092
- 观察方法 emit 语义：默认展示；`{emit:false}` 只返回值不展示；动作方法永不展示；`getApp` 只绑定不展示（每个 cell 重新绑定，若展示会"每个 cell 多一棵注定作废的树"）。【代码】client:977-1095、SKILL:179-188

### 2.5 返回结构总览

| 调用 | 模型收到 | 证据 |
|---|---|---|
| `list_apps`（bound `listApps()`） | 展示 JSON `{apps:[...]}`；返回 `AppInfo[]` | client:1097-1109 |
| `list_windows`（`computer.*`） | 单一 text block 是 JSON → 被 `JSON.parse` 成窗口数组（"parsed payload"） | docs:212-231、client:1122-1137 |
| `get_app_state`（bound `getAXState`） | 树文本 string（含告知性文本追加） | client:641-648 |
| `getScreenshot`（bound） | `Uint8Array`（SDK 已从 base64 解码）+ 自动 emit 图片 | client:650-674、509-513 |
| `getAXStateAndScreenshot` | `{state:string, screenshot?:Uint8Array}`，树与图都自动展示 | client:676-693 |
| 动作（bound） | `Promise<void>`，成功 undefined，失败 throw | client:621-638、SKILL:239 |
| 动作（`computer.*`） | 解封后的三种形态：①单 text block JSON→解析为对象；②单 text block 非 JSON→原样字符串；③其它（含 image/多块）→原始 envelope `{content:[...]}` | docs:212-244、client:1114-1137 |
| `requestAccess` | `AccessStatus` 对象 | client:1163-1168 |

- 动作回执（producer 契约）：默认 `return_state="none"`，返回 `{action_receipt:{schema_version:"zcode-cua-action-receipt-v1", action_sent, dispatch_status:"accepted"|"possibly_sent"|"not_sent", retry_action}}`；`return_state=compact/full` 时额外做一次 `capture_app`（`include_screenshot:false, force_full:true`），回执块排在 AX 树之前（截断时先保回执）。【规格】cua-receipt-observation-contract.md:6-50
- get_app_state 的 `structuredContent` 是 Worker SDK 唯一结构化事实来源；缺 `state_id/elements/app/window` 任一项 → `STRUCTURED_STATE_UNAVAILABLE`（禁止模型猜 undefined）。【代码】client:515-553；【规格】migration:145-165

### 2.6 错误语义

`ComputerUseError` 字段：【代码】client:113-131
- `code`：字符串码（下表）；
- `actionSent`：动作是否**可能已下发**。默认 false 是故意的保守方向；只有收据明确 `action_sent=true` 或 `dispatch_status=possibly_sent` 才置 true；
- `dispatchStatus`（可选）、`details`（只读）；
- `retry` 计算：`actionSent` → `"reobserve"`；否则 code ∈ NEVER_RETRY → `"never"`；code ∈ REOBSERVE → `"reobserve"`；否则 `"retry"`。NEVER_RETRY={CONTROLLER_BUSY, CONTROL_STOPPED, PERMISSION_DENIED, NOT_AUTHORIZED, VERSION_MISMATCH, ACTION_UNAVAILABLE, NOT_SETTABLE, NOT_SELECTABLE}；REOBSERVE={ELEMENT_UNAVAILABLE, STALE_STATE, STRUCTURED_STATE_UNAVAILABLE}。

文档列出的 20 个码（SKILL:241-246/docs:374-379）：`PERMISSION_DENIED, NOT_AUTHORIZED, APP_NOT_FOUND, AMBIGUOUS_APP, LAUNCH_FAILED, INVALID_APP, ELEMENT_UNAVAILABLE, STALE_STATE, NOT_SETTABLE, NOT_SELECTABLE, ACTION_UNAVAILABLE, FOREGROUND_REQUIRED, CONTROLLER_BUSY, CONTROL_STOPPED, SCREEN_LOCKED, HELPER_UNAVAILABLE, VERSION_MISMATCH, TIMEOUT, STRUCTURED_STATE_UNAVAILABLE, INTERNAL`。

SDK 的 producer→SDK 映射（client:76-94，17 条）：`permission_denied→PERMISSION_DENIED`、`not_authorized→NOT_AUTHORIZED`、`launch_failed→LAUNCH_FAILED`、`invalid_request→INVALID_APP`、`element_unavailable→ELEMENT_UNAVAILABLE`、`not_settable`、`not_selectable`、`action_unavailable`、`foreground_required`、`controller_busy`、`broker_unavailable→HELPER_UNAVAILABLE`、`version_mismatch`、`stale_socket→HELPER_UNAVAILABLE`、`timeout→TIMEOUT`、`unimplemented→ACTION_UNAVAILABLE`、`method_not_found→INTERNAL`、`internal→INTERNAL`；**未知码归 INTERNAL，绝不静默成功**。

- **差距（【推断】，producer 源码不在快照）**：`APP_NOT_FOUND`/`AMBIGUOUS_APP`/`CONTROL_STOPPED`/`SCREEN_LOCKED` 不在映射表内；若 producer 以这些字符串回码，会被压成 `INTERNAL`。文档清单与代码映射需要 producer 侧核对（交 task-3/Lead 决策）。同理 Windows 输入规格里的 `background_unavailable`（windows-input:83）也不在映射表。
- `actionSent` 的模型纪律：非幂等动作只在 `actionSent=false` 时重试；否则先观察再判断。docs 给了一段可抄的错误处理样板（docs:388-399）：`ACTION_UNAVAILABLE`→`getAXState({disableDiffing:true})` 看合法 actions；`CONTROLLER_BUSY`→报告 `e.details.owner` 并让用户关闭另一个会话；`e.actionSent`→先观察；其它→重抛。
- Helper 冷启动 not-ready：producer 返回**非 error** 的 `{kind:"CUA_NOT_READY", reasonCode, retryable, message}`；SDK 只在 `retryable=true` 时按 [250,500,750,1000,1500]ms 退避重试，最多 6 次；耗尽后 retryable→`TIMEOUT`、不可重试→`CONTROLLER_BUSY`，并把 producer 原话与 reasonCode 交给模型。【代码】client:240-274、903-932
- subagent 拒绝：`CUA_UNAVAILABLE_IN_SUBAGENT_MESSAGE = "Computer Use is not available in subagent"`，bridge 与 broker 双层拒绝（防绕过）。【代码】cua-bridge.ts:7、43-52；cua-broker.ts:106-107

### 2.7 前置条件与全局限制（问题 A 的"限制"部分）

- **先绑定/启动**：使用任何 Target 方法前先 `getApp`/传 `app_ref`；绑定会冷启动未运行的 app（无独立 launch 工具），隐藏观察不展示（§1.3/§5.1）。
- **元素索引前置**：必须已有该 app 的最新一次观察（同 cell），否则 `STALE_STATE`（"needs a fresh accessibility state before an element index can be resolved"）；索引身份 = app + 最新观察，跨窗口会重编号。【代码】client:312-332、docs:302-306
- **坐标前置**：会话中必须存在"最新可动作栅格"；没有时 broker 报 "no actionable frame is available in this transport"，SDK 要求先观察。【代码】client:334-360；【规格】frame-coordinate-internal-binding.md:63-72
- **单窗口作用域**：每次捕获只属于一个窗口；多窗口/多面板必须 `window_id`；未钉定时 modal 会接管捕获（§3.3/§5.1）。
- **会话/角色限制**：主 agent only，subagent 直接不可用；同一时刻只有一个 live CUA 会话持有输入（`CONTROLLER_BUSY`，永不重试）。【代码】SKILL:15、cua-bridge.ts:7、SKILL:252-254
- **版本完整性**：producer 与 Helper 必须同源安装；不匹配 → `VERSION_MISMATCH`（不重试、提示升级、可能替换 stale Helper）。【规格】cua-receipt-observation-contract.md:61-68
- **停止/权限**：`stop()`、kill switch、权限拒绝之后必须立即停止，且不得改用其它 UI 自动化技术。【代码】SKILL:255-258

## 3. 截图、图像与坐标（问题 C）

### 3.1 模型收到的"图"

- 图片是 MCP content 里的 **image block（inline base64）**，形式 `{type:"image", data:<base64>, mimeType}`；producer 的官方 CUA 结果还要求图片后**紧邻**一个 `image_ref` text block（`{"image_ref":{"frame_id":"frame-xxxxxxxx"}}`），二者构成原子对。【代码】result.ts:155-185、146-149；【契约】frame-contract.d.ts:1-9、22-30；【规格】authority-verdict-audit.md:39-45
- 顺序进入模型：image blocks 必须从 content[0] 起连续排列，然后才是 text。原因写在代码注释里：Anthropic 兼容网关（如 bigmodel MaaS）只解析 tool_result 开头连续的 image block，先遇到 text 就丢弃后面的图（实测 [image]/[image,text] 可见，[text,image] 不可见）。【代码】result.ts:125-135
- **one-raster rule**：一个结果里只允许一张最终 raster。同一个 cell 多次带截图观察时，host 只保留**最后一对** image/authority（"最新者胜"）；模型若把观察返回值再交给 `nodeRepl.write`/`nodeRepl.emitImage`，第二个 raster 会让整帧被移除，"最终一张图都拿不到"。【代码】result.ts:53-76；SKILL:181-186；docs:358-367；【规格】migration:76-79
- 内联体积：官方 CUA 图片内联 base64 上限常量 `OFFICIAL_CUA_IMAGE_INLINE_BASE64_BYTES = 200 * 1024`（200 KiB）。【契约】packages/zcode-cua/frame-contract.js:5
- 尺寸/缩放：模型拿到的 raster 是 **per-app 窗口图**，尺寸示例 `1280×645`（frame-coordinate-internal-binding.md:32）、`1280×900`（frame-pixel-zoom-regression.md:210）；设计文档明确 resize/byte-budget/Retina/DPI/crop/window-display origin 全部由内部完成，模型不做任何换算；截图走 JPEG 质量阶梯、压不进预算就链式缩小，因此曾用 zoom 补偿、后**删除 zoom**（"per-app raster is delivered at usable scale"）。【规格】frame-pixel-zoom-regression.md:56-59、v3-api:248-253、cua-14-tool-e2e-validity.md:31-33
- **未证实**：具体 MIME（image/png vs image/jpeg）、每张图的最终像素上限/缩放率、当前 producer 是否仍按 JPEG 阶梯——producer 代码不在快照；上述均按规格记录。
- `has_image`：SKILL 说 "`has_image: false` means the call did not ask for pixels (`getAXState`), never that capture failed"（SKILL:60-61）。该字段出现在观察结果的哪一结构位置由 producer 渲染，快照内无法核查（client 注释只说明 v1 曾在 structuredContent 里被丢弃）。【代码+未证实】

### 3.2 坐标契约（看与点如何对应）

- 官方契约（2026 后续版本）：坐标 = **最新一次返回 raster 的整数像素**，`0 <= x < width`、`0 <= y < height`；模型原样提交，不做 Retina/DPI/crop/resize/display-origin/native pointer 换算。【规格】SKILL:190-201、frame-coordinate-internal-binding.md:24-42、docs:425-428
- **frame_id 已内部化**：公开 target 只有 `{type:"coordinate", x, y}`；CUA 在动作前把无 frame_id 的坐标绑定到"当前 transport 最新可动作栅格"，并带着不可变 frame descriptor 过队列与派发。模型**不需要也不应该**读/复制 `image_ref.frame_id`；错误恢复文案禁止教模型复制 token。【规格】frame-coordinate-internal-binding.md:16-30、57-85
- SDK 的实现边界：`bindTarget` 对 [x,y] 只发 `{type:"coordinate", frame_id?, x, y}`；本 cell 没取过栅格时**不再拒绝**（2026-09-14 放宽，早前要求"坐标必须与截图同 cell"导致每个点坐标的 cell 都多截一次图）。帧过期/被替换/不可动作/app 与帧 owner 不一致仍 fail-closed（`frame_dispatch_identity_mismatch`）；一次栅格都没有时 broker 报 "no actionable frame is available in this transport"。【代码】client:334-360
- 元素/窗口 `bounds` 是**诊断用的全局屏幕点，永远不能当坐标**；视觉兜底开始时应丢弃先前文本/AX 结果里的坐标样数字。【代码】SKILL:196-198、docs:425-428、list_windows bounds 说明（docs:279-280）
- 坐标失败的模型侧症状与处置：可能报"意外 owner"或"frame stale"（窗口移动/缩放/被替换）→ 重新观察拿新栅格，或改用元素索引；"Never move, resize or close a window to make a coordinate land."（SKILL:205-209）
- 指针动作"受理但无效"：部分 app 忽略合成事件的坐标、按真实指针位置动作；下一次观察会带 `[effect_evidence unchanged]`，此时**不要重复同一坐标**，改元素索引或键盘（方向键/快捷键/输入框设值）。【规格】docs:429-435；【代码】client:555-574（该告知文本块必须随树一起交给模型）
- 越界/旧协议负例：`756×485` 帧提交 `(295,623)` 被拒、旧 `space:"screen"/"screenshot"` 与 `state_id/index` 坐标格式全部拒绝、zoom 后引用旧帧被拒（broker 调用为零）。【规格】frame-pixel-zoom-regression.md:99-109、135-154

### 3.3 观察类工具的模型可见内容

模型拿到的"状态"由四类东西组成（前 3 类拼进绑定 API `getAXState` 返回的 string；第 4 类只留在 SDK/UI 投影里）：

1. **渲染后的 AX 树文本**（含 header：`@state … app=… window=…` 这类前缀由 producer 生成；精确 DSL 属 producer，快照内只有设计稿 ax-tree-optimization-design.md:186-202，**未证实**当前逐字格式）；
2. **告知性文本块**（producer 另发的独立 text block）：`[effect_evidence unchanged]`、`[screenshot_blank]` 等，SDK 把它们附在树文本后面（"模型常写 `{emit:false}` 再自行过滤，附加在返回值里两条路都覆盖"）；【代码】client:555-574
3. **截图不可用的原因说明**：`getScreenshot` 失败会带 `non_actionable_reason`/`screenshot_blank` 并指回 AX 路径（隐藏/最小化窗口树仍可用，不要抢焦点）；`getAXStateAndScreenshot` 请求了像素但没拿到时追加 `[screenshot unavailable: …]`；【代码】client:650-693
4. **结构化字段**（模型面不直接给，供 SDK/UI）：`state_id`、`app{pid,name?,bundle_id?}`、`window{window_id,main?,focused?,window_id_fallback?,title?,…}`、`elements[]{index,kind,title,value,actions}`、`text`、`snapshot_mode`、`base_state_id`、以及 `non_actionable_reason/screenshot_blank` 等。【代码】client:515-553、SKILL:89-110

- `window` 块是排障关键：动作失败或树看起来像另一个区域时，先看观察的 `window`（弹窗会出现在那里），读它、按需操作或 Escape 关闭后重新观察；"缺失元素"不等于"动作成功"。【代码】SKILL:170-177、docs:298-316
- diff 语义：AX 树默认相对"本 cell 已展示过的树"做 diff，列出 removed/added/changed，未变行省略且索引仍有效；绑定后第一棵、以及 `getScreenshot`/`elements()` 之后的第一棵必为整树；`{disableDiffing:true}` 可强制整树。SDK 还会在"树没展示给模型过"时强制整树（`tree_shown_to_model` / 内部 `treeSeen` 记账）。【代码】SKILL:159-165、client:587-619、1048-1092；【规格】migration:167-170

## 4. 推荐调用范式（问题 D）

### 4.1 标准 cell 模板（模型被教导的调用序列）

```js
// 每 cell 先 bootstrap（见 §1.3），然后：
const app = await agent.computerUse.getApp("Notes");   // 绑定即观察（不展示）
await app.click(42);                                    // 动作：元素索引，静默
await app.typeText("hello");
await app.pressKey("Return");
await app.getAXState();                                 // 收尾观察：自动展示
```

- 原则："Observe once, act, then observe again before deciding the next step."；把确定性动作 + 一次收尾观察放进同一 cell，减少往返。【代码】SKILL:128-152、docs:337-356
- "Success means the API accepted an action, not that the app acted."；输入可能被受理却无效果（尤其 web-content editor），必须重新观察确认文字落地。【代码】SKILL:63-65
- 元素索引属于"该 app 最新一次观察"，同一 cell 内可复用索引连续动作（click→type 同一索引），观察会重编号；元素消失 → `ELEMENT_UNAVAILABLE`（fail-closed）。【代码】SKILL:154-157、docs:324-327；client:621-638
- 观察已在内部等待 UI settle：**不要 setTimeout/polling**；独立观察报告"无变化"时，没有中间动作就不要立刻重复。【代码】SKILL:232-235、docs:461-464

### 4.2 何时必须重新观察

| 触发 | 动作 |
|---|---|
| 任何动作之后（尤其非幂等/输入类） | `getAXState()` 验证可见结果 |
| 动作报错且 `actionSent=true`/`possibly_sent` | 先观察，禁止盲重试 |
| 报 `ELEMENT_UNAVAILABLE`/`STALE_STATE`/`STRUCTURED_STATE_UNAVAILABLE` | `retry:"reobserve"`，重新观察 |
| 观察的 `window` 与预期不符（弹窗出现/窗口被替换） | 读新窗口、操作或关闭后再观察 |
| 坐标被拒/报 stale frame/owner 意外 | 重新观察拿新栅格，或改元素索引 |
| 独立观察"无变化" | 不要立即重复；先做动作或换路径 |
| 只截图观察之后要用 AX 索引 | 必须先取整树（SDK 已强制 `disable_diffing`） |

【代码】SKILL:154-209、docs:318-405、client:587-619

### 4.3 失败重试与 pacing（模型侧文案）

- 重试决策用 `retry` 字段：`"never"`（CONTROLLER_BUSY/CONTROL_STOPPED/权限类/VERSION_MISMATCH/ACTION_UNAVAILABLE/NOT_SETTABLE/NOT_SELECTABLE）、`"reobserve"`、`"retry"`（普通可重试）。`CONTROLLER_BUSY` 表示另一个 live ZCode CUA 会话拥有输入，"never retryable"，报告 error 里的 owner 并让用户关闭那个会话。`VERSION_MISMATCH` 不重试、提示升级（Helper 与 producer 版本必须同源安装）。【代码】SKILL:237-254、docs:369-405、client:96-131；【规格】cua-receipt-observation-contract.md:61-68
- 禁止用睡眠"等"：不写 `setTimeout`、不写 polling loop；观察方法自带 settle。设计规格里的实现口径：连续动作间 `postActionMs` 默认 100ms；观察前基础等待约 1s，有 loading/busy 迹象递增至最多 5s（ActionSettler）。【代码】SKILL:232-235；【规格】v3-api:591-597（实现口径属 task-3）
- 到达"visible 结果"才结束：若状态不变/只到中间页，换方法再试；否则说明具体阻塞。"Persist until the request is actually complete. Attempting an action is not completion."【代码】SKILL:260-263、docs:466-471
- 坐标点击"受理但无效"不重复（见 §3.2）；失败后不要用另一套 UI 自动化技术绕过权限拒绝（明确禁止）。【代码】SKILL:203-209、255-258

### 4.4 反模式（SDK 注释里记录的真实事故，可直接当"模型易犯错误清单"）

1. 把 `getApp` 的"顺带展示"当省一次观察——结果每 cell 多吐一棵注定作废的树、同 cell 两个 `[35]` 指向不同元素；已改为"只绑定不展示"。【代码】client:1048-1063
2. 动作后无条件清 `stateId` 逼重新观察——与文档示例 `click(42); setValue(42,…)` 打架；改为信任 Helper 的索引冻结与 fail-closed。【代码】client:621-638
3. 坐标必须与截图同 cell——每 cell 新 Worker 下模型白撞一次错误再补截图；已放宽为"最近可动作栅格"。【代码】client:342-357
4. `list_apps` 返回裸 JSON 数组被解析器当非对象丢弃 → 恒返回 `[]`，模型误判"应用列表为空"；修复为保真解析（空数组 vs 拿不到可区分）。【代码】client:143-159
5. 观察结果 `structuredContent` 带整棵元素表 → 单次输出 140KB 被迫落盘、模型用 shell grep 自己的结果；修复为对 UI 投影时把 elements 换成 `element_count`。【代码】client:490-507
6. Helper 冷启动 not-ready 信封被静默丢弃 → 模型永远走不过冷启动，转而 `request_access`+shell 排查；修复为机器可读契约 + 退避重试。【代码】client:240-274
7. 截图失败只回一句无信息的 "Screenshot unavailable" → 模型去抢焦点/查环境；修复为带上原因并指回 AX 路径。【代码】client:650-674
8. 带图观察的 `structuredContent` 被无条件丢弃 → `has_image` 恒 false、window/state_id 是漂移前的探针那次；修复为投影后保留。【代码】client:449-471

## 5. app / 窗口定位与前后台模式（问题 E）

### 5.1 模型如何指定目标

- `AppRef = {name?, bundle_id?, pid?, window_id?}`；`getApp`/`app_ref` 均接受该对象；**裸字符串在 tool 层一律当 bundle_id**，要 display name 必须写 `{name:"Notes"}`。tool 的 `app_ref` 没有 path 字段（无法按路径指定 app）。【代码】SKILL:108、docs:182-193、449-456
- 绑定 API 的字符串重试策略（SDK 层）：含点且不含空格/斜杠 → 先 `{bundle_id}`，否则先 `{name}`；首次 `APP_NOT_FOUND`（匹配 `target app is not running` 语义核心）时用另一字段重试一次；对象形态不做猜测。绑定成功后把 `app_ref` 收敛成已解析身份 `{pid, bundle_id?, window_id?}`，后续路径用无歧义身份（解决"地图"这类本地化名观察成功、输入失败的分叉）。【代码】client:368-414、947-1032
- 模型必须**逐字复制**用户给的应用名，不翻译/本地化/缩短/去后缀（`网易云音乐app` ≠ `网易云音乐`，`日历` ≠ `Calendar`），改写会解析到别的 app 或 "app not found"；解析失败就调一次 `listApps()` 挑匹配标识符。Windows 上 display name 用 OS 列出的名字（开始菜单名），不是窗口标题。【代码】SKILL:134-142、274-277；docs:184-186
- `getApp(...)` **绑定即启动**：app 没运行会在后台冷启动（没有单独 launch 工具）；绑定不展示状态。`open_application` 已整体删除，`activate/url/new_instance` 三项子能力一并消失（评审接受）。【代码】SKILL:132-135、docs:449-456；【规格】cua-14-tool-e2e-validity.md:42
- 窗口定位：`list_windows({app_ref})` 拿稳定 `window_id` → 放进 `app_ref`（`getApp({pid, window_id})`）或未文档化的 `getWindow(name, windowId)`；钉定后该 cell 内所有调用都留在该窗口。绑定失败/窗口无法解析 → `STALE_STATE`，而非静默换窗口（修复了 Helper 静默降级到最前窗口只回 `window_id_fallback:true` 的坑：SDK 现在直接抛错）。【代码】SKILL:170-177、docs:250-316、client:1033-1047；【规格】v3-api:396-416
- 不给 `window_id` 时每次观察重解析主/键窗口，故新 modal 会接管捕获；元素索引是**按窗口**的，窗口换了索引可能指向无关控件。【代码】docs:302-316

### 5.2 前台 / 后台输入模式如何体现在参数里

- 唯一显式开关是 `strategy`：`auto`（默认，优先后台 AX，失败时才回落事件合成）、`a11y`（强制 AX，无可用动作即 fail-closed）、`event`（全局输入，要求目标（及 window_id）**已在前台**；不激活任何东西，后台调用直接 `FOREGROUND_REQUIRED` 且什么都不发）。【代码】docs:198-202、SKILL:74-87
- 模型纪律："Bind keyboard input to an app or element; never send it unbound."；"Do not run both the accessibility and visual paths for the same action."；`FOREGROUND_REQUIRED` 只在 `strategy:"event"`（以及后台 app 上的 paste）出现，不可重试、不要找激活 API——改元素 target/`setValue`/语义动作。【代码】SKILL:211-218、docs:382-386
- `modifiers` 在按下瞬间生效（cmd+click/shift+click 必须走事件路径）；`hold_seconds` 按住指定时长；`repeat` 重复发送而不是循环调用。【代码】SKILL:211-223、docs:206-209；【规格】v3-api:387-392
- Windows 真实派发是"后台优先升级链"（2026-10 规格，14 工具签名不变）：`strategy=event` 或带修饰键 → 直接进物理 lane（激活+SendInput）；auto 在 UIA 未命中/不支持且"确定未发送"时才向 PostMessage/前台升级；**升级是驱动侧自主决策、模型无感**，成功回执不披露升级（`method` 投影为中性值），delivery 元数据只走 broker 元数据不进模型文本；已确认后台不支持会返回 `background_unavailable`，不自动前台重试。模型可见错误文本禁止出现内部原语名（PostMessage/double Invoke 等）。【规格】windows-input-delivery-spec.md:79-127、209-236
- 焦点副作用（模型可见 warning）：Windows 上"启动非打包 app"会抢焦点；`include_screenshot=true` 会把**最小化**窗口恢复（un-minimize）——最小化窗口的树完全可用，所以默认不要截图。其余路径不抢用户焦点。【代码】SKILL:274-277、docs:457-460
- 平台分支：`agent.computerUse.computer.target` 暴露 `"mac"|"windows"|"linux"`（enumerable 只读属性），供模型按平台分支；能力缺失走类型化错误（Linux 无 AX 元素→ELEMENT_UNAVAILABLE；Linux/Windows 键盘无 pid 定向→FOREGROUND_REQUIRED），非 macOS 不直接抛错。【代码】client:1138-1150；【规格】v3-api:802

## 6. SDK 脚本结构与 bridge（问题 F）

### 6.1 文件、版本与分发契约

- 包名 `@zcode/zcode-cua-plugin`，version `0.6.5`（与 plugin.json 同版本），`"type":"module"`，`"main":"./scripts/computer-use-client.mjs"`；build/test/typecheck 全指向 `node scripts/check-sdk.mjs`（该脚本只 `access` 目标文件是否存在，不编译）。【代码】package.json:1-24、check-sdk.mjs:1-4
- 插件 manifest：`name:"computer-use"`、`version:"0.6.5"`、`skills:"skills"`，没有 MCP server 段。【代码】plugin.json:1-10
- 开发同步链（`sync-cache.mjs`）：把 `skills/docs/scripts/computer-use-client.mjs/.zcode-plugin/package.json/node_modules` 复制进 CLI 的 `~/.zcode/cli/plugins/cache`，并用产品同款 staging 函数注入 `sharp`（截图/缩放原生依赖）与 `koffi`；cache 目录名（版本号）只是安装期 key，可以落后于 plugin.json 版本。这解释了为什么"只同步 JS 不同步 native"会让截图失效。【代码】sync-cache.mjs:1-13、27-37、94-116
- 依赖 `@zcode/zcode-cua`（workspace:*）只用于契约/常量占位（本快照 producer 为 placeholder，README:1-7）。【代码】package.json:17-19

### 6.2 分层（模型代码 → shared host → Helper）

```text
Worker（模型写的 JS）
  globalThis.agent.computerUse               ← setupComputerUseRuntime 注入
       │  bridge.call(method, input)         ← Symbol("zcode.node-repl.computer-use-bridge")
       ▼
node-repl-host 主线程
  CuaRuntimePool（按 workspaceKey+sessionId+remoteSessionId 隔离）
    └─ AccessibilitySession + KillSwitch + InputHoldRegistry
         └─ BrokerClient → signed Computer Use Helper
```
【代码】client:892-932、cua-bridge.ts:54-76、cua-broker.ts:70-128；【规格】migration:88-99、v3-api:542-559

### 6.3 bridge 请求如何封装

- Worker 侧 bridge 由 host 注入：`{ [Symbol.for("zcode.node-repl.computer-use-bridge")]: { call, assertAvailable, documentationRoot } }`；SDK 的 `setupComputerUseRuntime` 检查 bridge 存在、调 `assertAvailable()`，否则抛 "Computer Use runtime bridge is unavailable. Use Computer Use from a ZCode desktop or shared-host session."。【代码】client:892-901、cua-bridge.ts:29-76
- `bridge.call(method, input)` 走**私有 socket**（不是 MCP）：Windows 命名管道 `\\.\pipe\zcode-node-repl-cua-<uuid>`，其它平台 `$TMPDIR/znrc-<uuid>.sock`；每请求一行 JSON `{id, token, method, input}`；响应一行 `{id, ok, result, responseMeta?}`；请求上限 1 MiB、响应上限 32 MiB；token 由主线程 call-ledger 每次调用签发，broker 校验 `ledger.resolve(token)` 后才执行，**不采信** Worker 发来的 context；subagent 在 bridge 和 broker 两层都被拒。【代码】cua-bridge.ts:78-132、cua-broker.ts:17-128
- host 侧把 capability method 适配到 staging runtime：`runtime.execute({ toolName: payload.method, arguments: payload.input, context, signal })`，context 由 `requestMeta` 重建（sessionId/workspaceKey/clientMode/deliveryKind/actionTitle/trace…；缺 session_id 或 workspaceKey 直接报错）。【代码】cua-broker.ts:96-128、157-236
- 结果回流：runtime 返回 MCP `CallToolResult`；broker 从 `_meta["zcode.cua/app-associations-v1"].primary` 取目标 app 身份写账本（用于工具卡图标/名称，不进模型可见结果）；host 的 `toMcpRunResult` 把 SDK 经 `nodeRepl.emitStructuredResult` 送来的 structured 结果组装为最终 tool result（image 前置、保留 image_ref、structuredContent 取最后一个、丢弃 provider 可伪造的 meta）。【代码】cua-broker.ts:130-155、result.ts:85-213、client:449-488

### 6.4 SDK 这一层做了哪些校验/规范化/包装（模型契约的实际收口点）

| 事项 | 位置/行为 | 证据 |
|---|---|---|
| 参数→wire 键映射（camelCase→snake_case） | click→`left_click{mouse_button,click_count}`、pressKey→`key{hold_seconds}`、drag→`left_click_drag{from_target,to}`、typeText→`type`、performSecondaryAction→`perform_action` | client:695-808 |
| target 绑定 | number→`{type:"element",index}`（要求已有观察，否则 STALE_STATE）；[x,y]→`{type:"coordinate",x,y[,frame_id]}`；非法值→INTERNAL | client:299-366 |
| 元素索引前置条件 | 无 `stateId` 时抛 STALE_STATE，错误文案给"可直接粘贴的调用"（不拼假代码） | client:321-331 |
| scroll 方向/按钮/键位归一化 | 方向 trim+lower；u/d/l/r→长名；鼠标 l/r/m→left/right/middle；键位别名表 | client:416-418、741-888 |
| app 字符串→bundle_id/name 猜测 + 一次换字段重试 + 绑定后身份收敛 | 见 §5.1 | client:368-414、947-1032 |
| selectText 内容→range 本地计算（不额外交互） | value 来自上次观察；0/多匹配→NOT_SELECTABLE | client:818-855 |
| 动作结果归一 | assertOk：`isError` 或 `dispatch_status==="possibly_sent"` → 抛 ComputerUseError（code 映射、actionSent、dispatchStatus、details） | client:278-297 |
| not-ready 冷启动 | 识别 `{kind:"CUA_NOT_READY"}`，按 retryable 退避重试（上限 6 次） | client:240-274、903-932 |
| 观察结果校验 | 缺 state_id/elements/app/window → STRUCTURED_STATE_UNAVAILABLE（消息列出缺哪项） | client:515-553 |
| 展示与投影 | 观察自动 emitText / structured sink 保留图+image_ref；元素全表在 UI 投影里换成 element_count | client:420-507、641-693 |
| 文档加载 | 覆盖 `agent.documentation.get`：只处理 `"computer-use"`（从 documentationRoot/computer-use.md 读），其它名交还原 loader | client:1188-1205 |
| 未注入裸 `cua` 全局 | 入口是 `agent.computerUse`（与 agent.documentation/agent.browsers 并列） | client:1182-1186 |

- **schema 逐字校验不在 SDK**：SDK 只做"便利映射 + 前置拒绝"；真正 strict 校验（`.strict()`、`unrecognized_keys`、`expected object, received undefined`）在 producer 工具层（文档明写；源码不在快照）。【代码+未证实】docs:138-157、client:590-592
- **安全凭证不经过 SDK**：SDK 不能访问 broker socket/token/native module；frame authority/possibly-sent/controller lease/kill switch 都在 host runtime/Helper 内，SDK 只保留"不删语义"（错误字段+内部 binding）。【规格】migration:42-45、v3-api:73-76

### 6.5 与 node_repl 的关系（一句话：CUA 是 node_repl 的"租客"，node_repl 只负责执行 JS 与投递结果）

- 每次 `js` 调用是 fresh Worker（新全局、新模块缓存）；host 在 Worker 里注入 bridge globals 与 `nodeRepl.*`，其中 `nodeRepl.write`（文本）、`nodeRepl.emitImage`（图片）、`nodeRepl.emitStructuredResult`（结构化/官方图片通道）是模型可见输出通道。【代码】migration:47-52、client:420-488、result.ts:85-213
- CUA 的 session/frame registry/action-state/kill switch 由 shared host runtime 持有；`js_reset`（已删）只会重置 Worker，不销毁 CUA runtime。【规格】migration:97-99
- 外层 `js` 只做**一次高风险审批**；内层复用 Helper/TCC、kill switch、frame freshness、possibly-sent、input-hold cleanup；不新增逐动作审批。stale frame/Helper unavailable/权限拒绝/possibly_sent/attachment 丢失都必须 fail closed 并以结构化结果返回。subagent 请求直接返回 CUA unavailable。【规格】migration:111-118
- 副产物：Windows 指示器/session 标签按"cell 是否含 `setupComputerUseRuntime`（`computerUse` 布尔事实）"驱动，sideband 不携带源码/参数/凭据；只调 `agent.browsers.*` 不触发 CUA 指示器。【代码】computer-use-operation-event.ts:14-32、65-67；【规格】migration:135-143

## 7. 工具面/文案设计经验与 AstrBot 草案（问题 G）

### 7.1 命名与分层（可抄）

- 底层工具名 = **动词_名词 snake_case**（list_apps/get_app_state/left_click/left_click_drag/set_value/select_text/perform_action/paste/...）；绑定 API = **camelCase 短方法**（getApp/click/setValue/performSecondaryAction）。两套名字不是随意：底层名字是冻结协议（producer manifest 为准），SDK 名是模型日常面；R1 要求"同名同签、名字/位置参数顺序/选项键名/返回类型保持冻结"，R2 要求新增能力只能进可选选项键或尾部可选参数（`Target` 附加成员数必须为 0）。【规格】v3-api:65-82、191-205；client:1-22
- 选项袋（不是新工具）：`modifiers/strategy/holdSeconds/clickCount/mouseButton/prefix/suffix/selectionType/format/emit/disableDiffing`。删除的工具全部有"参数化替代"（双击→click_count、hold_key→hold_seconds、右键→mouse_button、write_clipboard→paste、wait→内建 settle）——**少工具、多选项**是代码执行面下的正确取舍：每多一个工具要养 schema/handler/tier/e2e oracle/三平台场景。【规格】v3-api:77-82、283-327、cua-14-tool-e2e-validity.md:22-45
- 坐标与元素统一成 `target` 一个参数（number 或 [x,y]），模型不需要记"点元素工具/点坐标工具"两个名字。【代码】SKILL:89-104

### 7.2 文案写法（原文摘录 + 手法）

- **决策指南与参考手册分离**：SKILL 只回答"该用哪条路径、何时观察、失败怎么办"；参数形状/返回字段放 `docs/computer-use.md` 按需取。"Read it only for an argument shape or response field this page does not give."（SKILL:125-126）
- **禁令写成命令句 + 理由**："Do not run both the accessibility and visual paths for the same action."；"Never move, resize or close a window to make a coordinate land."；"Do not replace an available element action with a keyboard shortcut just because the shortcut is shorter."（SKILL:54-56、190-209）
- **给可判定的成功定义**："Success means the API accepted an action, not that the app acted."；"Persist until the request is actually complete. Attempting an action is not completion: verify the returned state visibly shows the result."（SKILL:63-65、260-263；docs:466-471）
- **错误语义结构化而非文案化**：错误对象给 `code/actionSent/retry/details`，模型据字段分支；禁止靠错误文本做流程判断（"Never parse error text"同类原则）。CONTROLLER_BUSY 的"never retryable + 报 owner + 请用户关会话"是唯一指定话术。（SKILL:237-254、docs:369-405）
- **机器可读错误信封优于自然语言**：producer 冷启动返回 `{kind:"CUA_NOT_READY", reasonCode, retryable}`，注释明说目的是"宿主不必解析错误字符串"。（client:247-263）
- **token 不外泄**：frame_id 只作内部凭证；错误恢复文案"never instruct the model to copy image_ref.frame_id"；成功路径不披露内部路径名（Windows 规格 §3.4）。（frame-coordinate:81-85、windows-input:209-236）
- **工具 description 承担边界声明**：`js` description 明说"只用于 Browser Use / Computer Use，不做通用 JS/文件/ shell/数据处理"，并给 timeout 的硬性建议（>30s 必须显式设、上限 120s）。这比在 skill 里重复更有效——它是被模型当"工具能力边界"读的。（tool-contract.ts:26-58）
- **一处权威、多处引用**：工具名/参数事实源是 producer `"src/tools/manifest.ts"`，SDK 的 `COMPUTER_METHOD_NAMES` 与之对齐，SKILL/docs 再引用；写文档时明说"以哪份为准"。（client:29-45、SKILL:265-307）

### 7.3 反面教训（ZCode 自己踩过的，AstrBot 可直接避坑）

1. **同一字符串在仓库两侧各写一遍 → 漂移**：`isAppNotFound` 曾匹配长文案，producer 改了一个逗号导致备用查询一次都没执行（47 次报错/23 例）。（client:399-407）
2. **文档与描述分层不一致**：`js` description 仍教 `nodeRepl.write(state.text)`、`state.state_id`（v1 语义），而 SKILL/docs 是"观察自动展示、模型不碰 state_id"（v3）；测试还把旧文案钉死。（tool-contract.ts:43-52、mcp-server.test.ts:167-172）→ **发布前要跑"模型面三方一致性"检查，而不是只跑单测**。
3. **空结果 vs 获取失败不可区分**：`list_apps` 解析 bug 让模型以为"应用列表为空，需要初始化/请求权限"，白绕三个 cell。（client:143-159）→ 返回结构必须能表达"空"与"不可用"。
4. **静默丢图/丢字段**：截图没回传只给一句无信息错误；带图观察丢 structuredContent 导致 window/state_id 漂移。→ 凡是"请求了但没拿到"都要显式说明原因与是否可重试。（client:449-471、650-693）
5. **帮助文本给假代码**：错误里拼 `await 地图.getAXState()` 这种非法 JS，模型无法照抄；已改成散文或可直接粘贴的调用。（client:321-331）
6. **默认档藏起必需数据**：`detail` 默认 compact 不带 actions，而 performSecondaryAction 只能用这些 actions；删除 detail、compact 无条件带 actions。（v3-api:266-281）

### 7.4 可借鉴到 AstrBot 插件（容器内模型 → Windows 执行器）的工具面草案

前提差异：AstrBot 侧没有 node_repl/JS Worker，模型是"一次一个 tool call"的函数调用；因此**不能**照抄"单 cell 批量动作 + 末尾观察"的 JS 范式，应把批量能力做成显式参数（batch）或接受多次调用。建议 MCP server 暴露 13 个工具（12 个基础 + 可选 batch）：

| 工具名 | 参数（建议 JSON Schema） | 返回 | 设计要点 |
|---|---|---|---|
| `computer_list_apps` | `{}` | `{apps:[{app_id,name,title?,pid,active}]}` | app_id 用执行器侧稳定身份（Windows: AUMID/exe+codepath），name 只作展示 |
| `computer_list_windows` | `{app_id}` | `{windows:[{window_id,title,subrole?,bounds,main,focused,onscreen}]}` | bounds 标注 diagnostic-only；窗口标题不可当 app 名 |
| `computer_observe` | `{app_id?, window_id?, include_screenshot?:bool=false, full?:bool}` | **MCP image content + text**：`{tree, app, window, has_image, effect_evidence?, state_id}` | 观察自动"返回图+树"，一次调用完成"看"；state_id 仅供插件内部，不要求模型回传 |
| `computer_click` | `{target: 元素索引(number) 或 [x,y], button?:"left", click_count?:1, modifiers?:string, strategy?:"auto"}` | `{ok:true, action_sent, dispatch_status, retry}` | 元素索引优先；坐标=**最新返回栅格**像素；不暴露 frame_id |
| `computer_type` | `{text, target?}` | 同上回执 | 非幂等 |
| `computer_key` | `{keys:string, hold_ms?:int, repeat?:int}` | 回执 | keys 用"ctrl+shift+t"/"Return"形态，平台差异由执行器归一 |
| `computer_scroll` | `{target, direction:"up"|"down"|"left"|"right", pages?:1}` | 回执 | pages 单位，clamp 0–100 |
| `computer_drag` | `{from, to}` | 回执 | 只有 down→observe→up 的需求时再考虑加 hold 原语 |
| `computer_set_value` | `{target, value}` | 回执 | 可设置元素首选（替代 type/paste） |
| `computer_select_text` | `{target, text?, range?:[start,length], selection?:"text"|"cursor_before"|"cursor_after"}` | 回执 | 文本匹配歧义要拒绝并报候选数 |
| `computer_paste` | `{text, format?:"text"|"md"|"html"}` | 回执 | 剪贴板保存/恢复放执行器侧，避免污染用户剪贴板 |
| `computer_stop` | `{reason?}` | `{ok:true}` | kill switch；之后拒绝一切动作 |
| （可选）`computer_batch` | `{steps:[...] }` | `{results:[...], final_observation?}` | 补偿 AstrBot 无 JS 批处理；必须支持"任一步失败即停、标 action_sent" |

返回/错误约定（建议直接照抄 ZCode 的思想）：
1. 回执字段 `{action_sent:boolean, dispatch_status:"accepted"|"possibly_sent"|"not_sent", retry:"never"|"reobserve"|"retry", effect?}`；成功=无异常，失败=MCP isError + 结构化 `{code, message, action_sent, retry}`；
2. 错误码收敛为 13 个并用文档固定：`PERMISSION_DENIED / NOT_AUTHORIZED / APP_NOT_FOUND / AMBIGUOUS_APP / ELEMENT_UNAVAILABLE / STALE_STATE / NOT_SETTABLE / NOT_SELECTABLE / ACTION_UNAVAILABLE / FOREGROUND_REQUIRED / CONTROLLER_BUSY / TIMEOUT / INTERNAL`（ZCode 20 个里去掉客户端专属的 STRUCTURED_STATE_UNAVAILABLE/HELPER_UNAVAILABLE/VERSION_MISMATCH/SCREEN_LOCKED/CONTROL_STOPPED，或保留但确保**映射表覆盖**，别让未知码落 INTERNAL）；
3. 图片用 MCP image content 返回（base64 + mimeType），**一个结果只放一张图**，文字与图分开；坐标永远解释为"最近一次返回图片的像素"；
4. 工具名统一 `computer_*` 前缀防污染；工具 description 里写清"只用于桌面 UI 操作、web 页面用浏览器工具、不用于 shell/文件"；
5. 会话/并发：AstrBot 一个会话一个执行器 lease（对应 CONTROLLER_BUSY），kill switch 独立工具；
6. 不照抄：node_repl 的 per-cell bootstrap、JS 代码执行面、`agent.documentation.get` 动态文档（AstrBot 用系统提示/工具 description 即可）、ZCode 的 macOS 特化文案。

## 8. 对本项目（PCCtrl / Windows MCP 服务）的可复用结论与坑

1. **模型面要做"唯一入口 + 少量粗工具"**：入口收敛（ZCode 甚至收成一个 js）能大幅降低路由错误；对 AstrBot 这类函数调用宿主，收敛到 10–12 个 `computer_*` 工具 + 一份常驻 skill 文档即可，避免 30 个工具时代的爆炸。
2. **观察与动作必须一次工具调用内闭合**：AstrBot 若"点一下再观察"要两次 call，不如让 `computer_click` 回执可选带 `return_state:"compact"`，或在执行器侧自动附 post-state（ZCode 规格里 `return_state=compact/full` 正是为此，默认 none 是给 JS 批处理面用的）。
3. **坐标契约照抄"最新帧内部绑定"**：不把 frame_id/二进制 token 暴露给模型，避免模型复制失败；错误恢复统一"重新观察拿新图"。
4. **图片是最大成本项**：inline base64、单结果单图、按需截图（默认只给树）是 ZCode 用真实事故换来的；AstrBot 侧还要考虑 token 成本，建议 `include_screenshot` 默认 false、仅在坐标/视觉需求时 true。
5. **typed receipt 必须实现**：`action_sent/possibly_sent/retry` 是防"盲重试导致双击/重复输入"的核心；Windows 执行器的网络不确定性（超时但已下发）正是该字段的用武之地。
6. **应用/窗口身份由执行器解析**：不要信任模型给的 name（本地化名、窗口标题、大小写都不可靠）；执行器返回 stable app_id，模型后续只传 app_id。ZCode 的"逐字复制 + 只在失败时 listApps"是过渡方案，AstrBot 可以直接用 ID。
7. **失败语义要可诊断**：错误消息必须说明"缺哪个字段/哪一步/是否可能已发送/是否可重试"，ZCode 为此专门加了字段级 missing 列表与 not-ready 信封——否则模型会去 shell/权限排查绕圈。
8. **不要把所有安全都交给模型**：ZCode 把 state_id/frame/lease/kill switch 全部藏在内部，只把"决策所需的信息"给模型；AstrBot 插件同样应把 token/帧凭证/窗口几何留在执行器侧。
9. **坑：文档与代码漂移**。ZCode 的 `js` description、设计文档（14 工具含 open_application、三工具 js_reset）都与当前代码不一致；发布前应把"模型可见文案"纳入契约测试（本笔记 §7.3-2）。
10. **坑：权限只读快照 ≠ 触发授权**。`request_access` 设计为只读、不弹系统框；AstrBot 侧"请求权限"必须是独立、显式、有 UI 确认的动作，不能由失败自动触发。

## 9. 未解问题 / 边界发现（不在本任务深挖）

1. **producer 源码缺失**：工具 schema 的逐字定义、错误码字符串、观察文本 DSL、`has_image` 的位置、图片 MIME/尺寸、`background_unavailable` 等，均无法在快照核实；本笔记的"schema 细节"以 docs/SDK 为准，需 task-3 或后续拿到 producer 源码后复核。
2. **错误码映射缺口**：docs 列 20 码、SDK 映射可达 14 码（APP_NOT_FOUND/AMBIGUOUS_APP/CONTROL_STOPPED/SCREEN_LOCKED 不在映射表；另有 Windows `background_unavailable`）。若 producer 真发这些码，模型会看到 INTERNAL——建议 Lead 在主报告标为"契约风险"。
3. **`js` tool description 与 SKILL 冲突**：description 仍要求 `nodeRepl.write(state.text)` 与 `state.state_id`/`state.elements[*].index`（v1 面），并使用 `screenshot_display.bounds` 概念；与 v3 文档相反，且被 `mcp-server.test.ts:167-186` 钉死。是"有意兼容旧模型"还是"漏改"，未证实。
4. **版本叙述漂移**：迁移规格说"工具面固定为 js/js_add_node_module_dir/js_reset"（migration:14-15），host 0.6.0 已删后两者；设计文档 §3.7 的 14 工具列表仍含 open_application 而无 paste（v3-api:363-371），而 14-tool normative 文档与当前代码以 paste 取代 open_application。以**代码+SKILL/docs 三方**为准=含 paste、不含 open_application。
5. **文档化边界漂移**：v3-api 设计说 `getWindow` 不进 documentation.get()（v3-api:404-406），但当前 docs/computer-use.md:298-300 写了它；SKILL 未提。
6. **SKILL 与 docs 重复度高**（docs 自述 "It largely repeats the resident Computer Use skill"）：维护成本与漂移风险；本笔记只把 docs 当参数手册、SKILL 当决策指南。
7. **模型面的图像缩放/TTL 未核实**：frame authoritative 的 TTL/LRU/最新帧保留期限在 producer 侧；对模型可见行为（多久后坐标失效）只有"重观察"建议，无明确数字。
8. **`request_access` 的 capabilities 取值未证实**；`pause/resume` 类控制不存在（只有 stop，不可恢复）。
9. **Windows 输入升级链**属 task-3 深挖范围：本笔记只记录"模型无感、回执不披露、错误文案不提内部原语"三条模型面结论（windows-input:79-127、209-236）。
10. **AstrBot 侧协议选型**（MCP streamable HTTP/stdio、是否走 SSE、容器↔Windows 执行器的认证）不在本任务范围，建议由负责架构的成员补。

## 证据索引（文件:行号 + 一句话）

- `SKILL.md:1-15` — skill frontmatter 与三条总禁令（Main agent only / 不委派 / 不用其它 UI 自动化）
- `SKILL.md:17-37` — 每 cell bootstrap 模板与"同一 cell"要求
- `SKILL.md:39-65` — AX 优先、坐标兜底、后台观察、成功≠已生效
- `SKILL.md:67-123` — 模型可见 TypeScript API（Target 12 成员、AppRef、State、agent.computerUse）
- `SKILL.md:128-177` — observe-act-verify、绑定即启动、索引复用、diff、窗口作用域
- `SKILL.md:179-209` — 输出/one-raster/坐标与 bounds 纪律
- `SKILL.md:211-263` — 键盘、selectText、等待、错误码/actionSent/retry/CONTROLLER_BUSY
- `SKILL.md:265-307` — 14 个底层工具名单与参数提示、documentation.get 提示
- `docs/computer-use.md:18-44` — 按需参考的 bootstrap 与 fresh Worker 说明
- `docs/computer-use.md:46-133` — 完整 API/AppInfo/AccessStatus/Computer
- `docs/computer-use.md:138-211` — 单对象参数、strict 校验、14 工具参数与默认值
- `docs/computer-use.md:212-248` — computer.* 三种返回形态（解析 payload/字符串/envelope）
- `docs/computer-use.md:250-316` — Windows/窗口字段、window_id、modal、getWindow/STALE_STATE
- `docs/computer-use.md:318-405` — workflow、错误字段、FOREGROUND_REQUIRED、CONTROLLER_BUSY
- `docs/computer-use.md:407-471` — elements()、树裁剪上限、坐标/diff/paste/settle 细节
- `computer-use-client.mjs:30-45` — COMPUTER_METHOD_NAMES（14，事实源注释）
- `computer-use-client.mjs:75-131` — 错误码映射、NEVER/REOBSERVE 集合、retry 计算
- `computer-use-client.mjs:143-159` — list_apps 裸数组解析 bug 与修复原则
- `computer-use-client.mjs:240-297` — not-ready 信封、assertOk、possibly_sent 语义
- `computer-use-client.mjs:299-414` — target 绑定、bundle id 猜测、APP_NOT_FOUND 语义匹配
- `computer-use-client.mjs:420-553` — emit/projectToHost/forDisplay/imageBytes/appStateOf
- `computer-use-client.mjs:584-810` — 12 个 Target 方法实现（参数映射与前置校验）
- `computer-use-client.mjs:818-888` — selectText 内容匹配、键位别名规范化
- `computer-use-client.mjs:892-1207` — bridge 校验、call 重试循环、bindApp、computer 14、cua 根、documentation loader
- `package.json:1-24` / `.zcode-plugin/plugin.json:1-10` — 版本 0.6.5、main 脚本、skills 分发、无 MCP server
- `scripts/sync-cache.mjs:1-116` — 开发缓存同步链与 sharp/koffi native staging
- `docs/cua/2026-08-24-cua-node-repl-sdk-migration-spec.md:9-52` — 收敛背景、模型可见工具面、bootstrap 契约
- `...migration-spec.md:60-99` — 结构化结果/输出契约、one-raster、runtime 边界
- `...migration-spec.md:111-188` — 审批/错误/fail-closed、动作后观察边界、验收
- `docs/cua/producer/2026-09-10-cua-sdk-v3-api.md:27-82` — 三处数量、R1/R2/R3 原则
- `...v3-api.md:86-222` — 完整模型可见 API 与成员差异
- `...v3-api.md:226-392` — 30→14 瘦身判据、删除清单、strategy/modifiers/窗口寻址保留理由
- `...v3-api.md:396-458` — 窗口寻址、elements() 翻盘、冻结契约解冻
- `...v3-api.md:462-538` — fresh Worker、possibly_sent、frame 内部化、索引绑定
- `...v3-api.md:540-646` — 分层、withActionPolicy、selectText、settler、paste、键位别名
- `...v3-api.md:806-886` — 模型用例与 30 工具处置表
- `docs/cua/producer/cua-14-tool-e2e-validity.md:9-48` — 14 工具 normative 基线、17 个删除项与替代路径
- `docs/cua/producer/frame-coordinate-internal-binding.md:16-85` — 坐标只传 x/y、内部绑定最新帧、错误恢复不教复制 frame_id
- `docs/cua/producer/cua-receipt-observation-contract.md:6-68` — 动作回执形状、dispatch_status、return_state、版本边界
- `docs/cua/cua-tool-app-identity-summary.md:8-51` — node_repl 时代 app 身份投影（host-only _meta，模型不消费）
- `docs/cua/2026-08-25-cua-request-access-onboarding-spec.md:7-25` — request_access 只读快照、不触发系统 prompt
- `docs/cua/2026-08-25-cua-permission-error-passthrough.md:7-65` — 权限错误透传、不自动重试/不自动弹窗
- `docs/cua/2026-10-13-windows-input-delivery-spec.md:79-127` — Windows 后台优先升级链、strategy 边界、模型无感
- `...windows-input-delivery-spec.md:209-236` — 回执按受众分通道、成功不披露升级、错误文本不含内部原语
- `docs/cua/2026-08-18-frame-pixel-zoom-regression.md:50-63` — Skill 精简与 zoom 边界（模型不做像素换算/zoom 非默认）
- `...frame-pixel-zoom-regression.md:99-154` — 旧坐标协议负例、契约与时序
- `docs/cua/2026-08-25-cua-authority-verdict-audit.md:39-58` — image 与 image_ref 邻接/孤儿 image_ref 拒绝
- `docs/cua/2026-08-19-cua-target-app-display-metadata-spec.md:5-48` — 目标 app 身份由 runtime 解析、模型输入 bundle_id 仅待验证引用
- `apps/zcode-cli/packages/node-repl-host/src/cua-bridge.ts:22-132` — bridge 接口、私有 socket 协议、1MiB/32MiB、token、subagent 拒绝
- `.../node-repl-host/src/cua-broker.ts:17-155` — named pipe/socket、ledger token、runtime.execute、app 身份账本
- `.../node-repl-host/src/result.ts:53-213` — one-raster 保留最新帧、image 前置、structuredContent/meta 组装
- `.../node-repl-host/src/tool-contract.ts:13-58` — 0.6.0 删两工具、js 工具描述（注意与 SKILL 冲突）
- `.../node-repl-host/src/server.ts:66-98` — js schema（code/title/timeout_ms）与唯一工具注册
- `.../packages/contracts/src/tools/node-repl.ts:4-63` — 模型态/执行态 schema 差异、输出形状
- `.../packages/bootstrap/src/zcode-protocol/computer-use-operation-event.ts:14-32` — 以 setupComputerUseRuntime 为锚的布尔判定
- `packages/zcode-cua/frame-contract.js:1-6` — official-frame-integrity meta key、200KiB inline 上限
- `packages/zcode-cua/README.md:1-7` — producer 占位包声明（不可执行，仅 API 契约）
- `packages/zcode-cua/request-access-contract.d.ts:3-9` — AccessStatus 契约
- `docs/cua/producer/ax-tree-optimization-design.md:186-226` — 模型可见 DSL 设计稿（未证实为当前实现）

