# CUA 底层 SDK v3 契约、回执/观察/验证、坐标与 AX 体系

> **研究范围**：ZCode Computer Use 的模型可见 SDK 面（v3.1）、action receipt / observation 契约、act-verify 判定流程、frame 像素坐标内部绑定、AX 树裁剪与元素引用、capture 超时诊断、错误透传、macOS 后台输入机制（作为 Windows 派发的对照）。
> **不作为重点**：helper 鉴权（M1）、helper 生命周期（M2）、AX 规范化（M3）、broker 权限模型、叠加层 UI 细节（只在影响观察/验证处引用）。
> **阅读材料**：见文末「证据索引」，全部来自 `ZCode-main/docs/cua/**` 与少量源码交叉核对。
> **核实基线**：ZCode-main 仓库快照（2026-10 检出）；SDK v3 API 文档标注 Draft / 日期 2026-09-10。
> **证据标注**：【代码】= 可运行源码；【规格】= docs 设计规格（真实产品文档，非本快照可执行代码）；【契约】= 占位包/类型定义中的 API 契约；【推断】= 基于材料的推论。

## 0. 结论速览

1. **模型可见面不是 MCP 工具列表，而是一个 camelCase 的 `cua` 全局对象**：固定 19 个 SDK 成员 = 17 个交互面成员 + `requestAccess` + `stop`；`Target` 的附加成员数必须为 **0**，新增能力只能落在「可选选项键 / 尾部可选参数」白名单里，否则 surface 测试失败（`2026-09-10-cua-sdk-v3-api.md:32-54, 732-743`）【规格】。
2. **工具面 30 → 14 是一次有意的契约解冻**：删 16 个工具 + 删 15 个 broker 方法 + 新增原生 `paste`，broker 方法 63 → 49，IPC 协议版本 1 → 2（走既有 `version_mismatch` fail-closed 路径）；normative 文档里必须写明解冻理由（`...sdk-v3-api.md:16, 226-361, 448-458`；`cua-14-tool-e2e-validity.md:11-45`）【规格】。
3. **动作成功 ≠ 工具返回成功**。动作方法公开签名是 `Promise<void>`，成功时无返回值；失败抛 `ComputerUseError`，其中 `actionSent` / `dispatchStatus` / `retry` 三个字段承载「动作是否可能已下发」——这是防重放（双击）的核心机制（`...sdk-v3-api.md:474-503`）【规格】。
4. **回执是默认返回物，观察不是**。变更类工具默认 `return_state="none"`，返回 `action_receipt`（`schema_version: zcode-cua-action-receipt-v1`、`action_sent`、`dispatch_status`、`retry_action`）；只有显式 `return_state="compact"|"full"` 才额外做**恰好一次** `capture_app`（`include_screenshot:false, force_full:true`）（`cua-receipt-observation-contract.md:8-42`）【规格】。
5. **`dispatch_status` 三态**：`accepted` / `possibly_sent` / `not_sent`。`possibly_sent` 时调用方**不得**自动重放非幂等动作；被 Helper 证明「未派发」的 stale 元素返回 `action_sent=false` + `dispatch_status="not_sent"`，且**不做自动补救**（`cua-receipt-observation-contract.md:22-26`；`cua-action-receipt-state-simplification.md:5-8`）【规格】。
6. **坐标只接受「像素 x/y」**：模型从刚观察到的这张 raster 里抄整数像素，不做 Retina/DPI/crop/resize/显示器原点换算；`frame_id` 由 CUA 在 transport session 内**内部绑定**，模型看不到也不需要传（`frame-coordinate-internal-binding.md:18-47`）【规格】。
7. **无 raster 则无坐标动作**：当前 transport 没有 actionable raster，坐标动作在派发前就失败；state-only 观察既不推进也不吊销隐式像素授权；AX `bounds` 是全局屏幕点，**永远不能**当作 raster `x/y` 提交（`frame-coordinate-internal-binding.md:49-55, 63-85`）【规格】。
8. **元素索引由 `state_id` 解析，stale 一律 fail-closed**：模型面不再暴露 `state_id`，索引解析用该 target 当前 `stateId`；缺失即 `STALE_STATE`，且**不自动补观察**（自动重观察会把「想点 A 实际点 B」变成静默错误）（`...sdk-v3-api.md:441-446, 525-536`）【规格】。
9. **act-verify 的判定是「模型确实动作 + 独立验证通过」双条件**，独立验证走模型无法影响的通道（AppleScript 读 app 数据模型 / `pbpaste` / 文件系统 / `defaults read`）；「模型说成功了」「工具返回 `isError=false`」都不算证据（`cua-act-verify-scenarios.md:11-25, 469-486`；`cua-14-tool-e2e-validity.md:50-68`）【规格】。
10. **set_value / AX value 写入不等于提交到文档模型**：Notes 富文本 body 上 `set_value` 被 AX 接受并回显但**不落盘**，必须走真实键盘输入；这是「动作回执成功但目标未生效」的典型反例，工具用 `keyboard_hints._non_committal_rich_text_warning` 提示（`cua-act-verify-scenarios.md:352-384`）【规格】。

## 1. 模型可见面：`cua` 全局对象 + 14 工具逃逸口（对应问题 A）

### 1.1 根对象与 `Target` 接口（逐字）

来源：`ZCode-main/docs/cua/producer/2026-09-10-cua-sdk-v3-api.md:92-187`。【规格】

```ts
type Vec2 = [x: number, y: number];
type Direction = "up" | "down" | "left" | "right" | "u" | "d" | "l" | "r";
type MouseButton = "left" | "right" | "middle" | "l" | "r" | "m";
type SelectionType = "text" | "cursor_before" | "cursor_after";
```

`Target` 的 12 个成员（`...sdk-v3-api.md:136-158`）：

| 成员 | 签名 | 返回 | 备注 |
|---|---|---|---|
| `getAXState` | `(options?: StateOptions)` | `Promise<string>` | 展示并返回 AX 状态文本；默认 diff |
| `getScreenshot` | `(options?: ObservationOptions)` | `Promise<Uint8Array>` | per-app 截图；建立内部 `frameId` |
| `getAXStateAndScreenshot` | `(options?: StateOptions)` | `Promise<StateAndScreenshot>` | `{state, screenshot?}` |
| `paste` | `(text, options?: PasteOptions)` | `Promise<void>` | `format?: "text" | "md" | "html"` |
| `click` | `(target: number | Vec2, options?: ClickOptions)` | `Promise<void>` | 元素索引或像素坐标；`mouseButton`/`clickCount`/`modifiers`/`strategy` |
| `drag` | `(from: Vec2, to: Vec2, options?: DragOptions)` | `Promise<void>` | 唯一拖拽入口；两端各自绑定 |
| `pressKey` | `(key: string, options?: PressKeyOptions)` | `Promise<void>` | `holdSeconds`/`strategy` |
| `scroll` | `(target: number | Vec2, direction: Direction, pages?: number, options?: ScrollOptions)` | `Promise<void>` | `amount` 已删 |
| `selectText` | `(elementIndex: number, text: string, options?: SelectTextOptions)` | `Promise<void>` | 按内容匹配 → 内部换 `[start,length]` |
| `setValue` | `(elementIndex: number, value: string)` | `Promise<void>` | 无选项袋 |
| `typeText` | `(text: string)` | `Promise<void>` | 逐字符；多行/格式化优先 `paste` |
| `performSecondaryAction` | `(elementIndex: number, action: string)` | `Promise<void>` | `action` 只能取自元素声明的 `actions` |

根对象（`...sdk-v3-api.md:176-187`）：

```ts
declare const cua: {
  getState(options?: ObservationOptions): Promise<State>;
  getApp(target: string): Promise<App>;             // 绑定 + 展示初始全量 AX 树
  listApps(options?: ObservationOptions): Promise<AppInfo[]>;
  computer: Computer;                               // 14 个底层工具，§1.4
  requestAccess(capabilities?: string[]): Promise<AccessStatus>;
  stop(reason?: string): Promise<void>;
};
interface App extends Target {}                     // 零额外文档化成员
```

**计数口径**：文档 §0.2 说「19 个 SDK 成员 = 17 个交互面成员 + `requestAccess` + `stop`」。逐项核对：`Target` 12 + 根对象 4（`getState/getApp/listApps/computer`）= 16；加上 §4.2 在 S4 提升为文档化成员的 `app.elements()` = 17。【推断】文档没有给出 17 的逐项清单，此处按 `...sdk-v3-api.md:428-434` 的 S4 结论补齐；未文档化成员 `cua.getWindow(app, windowId)` 不计入模型文档面（`:405-406`）。

### 1.2 数据结构（wire 形状）

`...sdk-v3-api.md:120-174`。【规格】

```ts
interface StateAndScreenshot { state: string; screenshot?: Uint8Array; }

interface AppInfo {
  // listApps 直通 producer 的 list_applications wire 形状（工具层不做映射）
  pid: number;
  name: string | null;
  bundle_id: string | null;   // macOS bundle id / Windows AUMID
  active: boolean;            // 是否 frontmost
}
interface State {
  apps: AppInfo[];
  // ZCode 不融合 Browser Use：该键不存在（§5.3）
}
```

要点：
- `AppInfo` 是**直通**形状（不做 camelCase↔snake_case 映射），`bundle_id` 在 Windows 上语义是 **AUMID**——这是跨平台 app 身份的关键字段（详见 §5）。
- `State` **不含** `browsers` 键；`cua` 上不存在 `browsers/getBrowser/createBrowserTab/getTab/listBrowsers/listTabs`（`...sdk-v3-api.md:204-206, 510-517`）。
- 截图以裸 `Uint8Array` 返回，没有 `{mime, width, height, frame_id}` 这类信封；`frameId` 记在 SDK 的 target 上（`...sdk-v3-api.md:505-508`）。【推断】对外契约里 frame 尺寸/DPR 不作为字段暴露，只通过像素栅格本身 + 内部 frame descriptor 传递。

### 1.3 选项袋（全部可选，且是唯一允许的扩展点）

`...sdk-v3-api.md:99-134`。【规格】

| 选项类型 | 字段 | 归属工具 |
|---|---|---|
| `ObservationOptions` | `emit?: boolean`（默认 true） | 所有观察方法 |
| `StateOptions` | `emit?`, `disableDiffing?: boolean` | `getAXState` / `getAXStateAndScreenshot` |
| `ClickOptions` | `mouseButton?: MouseButton`, `clickCount?: number`, `modifiers?: string`（如 `"cmd+shift"`）, `strategy?: "auto" | "a11y" | "event"`（默认 auto） | `click` |
| `SelectTextOptions` | `prefix?: string`, `suffix?: string`, `selectionType?: SelectionType` | `selectText` |
| `PasteOptions` | `format?: "text" | "md" | "html"`（S0c 只做 text；md/html 抛 `ACTION_UNAVAILABLE`） | `paste` |
| `PressKeyOptions` | `holdSeconds?: number`（>0 走 `hold_key` 原语）, `strategy?` | `pressKey` |
| `ScrollOptions` | `strategy?`（`amount` 已删） | `scroll` |
| `DragOptions` | `modifiers?: string`, `strategy?` | `drag` |

**已删除的选项/参数**：`detail`（`StateOptions` 与工具层都删；compact 无条件带 `actions`、永不带 `bounds`）、`repeat`（key）、`amount`（scroll）、`restoreClipboard`（paste）、`zoom`（Target 成员）、`Target` 上任何附加成员（`...sdk-v3-api.md:105, 119, 127, 130, 158, 266-281, 293-300`）。

### 1.4 `cua.computer`：存活的 14 个工具

`...sdk-v3-api.md:363-381`；`cua-14-tool-e2e-validity.md:5-45`。【规格】

```text
list_apps  open_application  list_windows  get_app_state
left_click  scroll  left_click_drag
type  set_value  select_text  key
perform_action
request_access  stop_computer_control
```

- `cua.computer` 的定位是**平台特定底层 computer API**，模型文档「提一句、不展开」；schema 与返回 envelope 逐字保留（`...sdk-v3-api.md:373-375, 683`）。
- 唯一住在这里的 ZCode 独有能力：**窗口寻址**（`list_windows` + `app_ref.window_id`）、**pid 精确选择**、`open_application` 的 `activate`/`confirm_focus_steal`/`new_instance`/`urls`、`select_text` 的 **range 形态**（`...sdk-v3-api.md:377-381`）。
- 缺失能力的类型化错误：Linux 无 AX 元素 → `ELEMENT_UNAVAILABLE`；Linux/Windows 键盘无 pid 定向 → `FOREGROUND_REQUIRED`（`...sdk-v3-api.md:802`）。

### 1.5 错误类型与错误码

`...sdk-v3-api.md:474-503`。【规格】

```ts
declare class ComputerUseError extends Error {
  readonly code: ComputerUseErrorCode;   // 字符串码，映射自 BrokerErrorCode
  /** 动作是否已下发。false 才可以安全重试同一个非幂等动作。 */
  readonly actionSent: boolean;
  readonly dispatchStatus?: "accepted" | "possibly_sent";
  readonly retry: "reobserve" | "retry" | "never";
  readonly details: Readonly<Record<string, unknown>>;
}
```

**20 个错误码（逐字）**：`PERMISSION_DENIED` `NOT_AUTHORIZED` `APP_NOT_FOUND` `AMBIGUOUS_APP` `LAUNCH_FAILED` `INVALID_APP` `ELEMENT_UNAVAILABLE` `STALE_STATE` `NOT_SETTABLE` `NOT_SELECTABLE` `ACTION_UNAVAILABLE` `FOREGROUND_REQUIRED` `CONTROLLER_BUSY` `CONTROL_STOPPED` `SCREEN_LOCKED` `HELPER_UNAVAILABLE` `VERSION_MISMATCH` `TIMEOUT` `STRUCTURED_STATE_UNAVAILABLE` `INTERNAL`。

**错误 → retry 语义**（`...sdk-v3-api.md:493-498`；附录 A 用例 `:834-845`）：

| runtime receipt | v3.1 表现 |
|---|---|
| `action_sent:true, dispatch_status:"accepted"` | **resolve（void）**，不报错 |
| `action_sent:true, dispatch_status:"possibly_sent"` | **reject**，`actionSent:true`、`retry:"reobserve"` |
| `action_sent:false` | reject，`actionSent:false`，`retry` 按 code |


### 1.6 规格 vs 实现：已核实的 5 处差异（主报告必须区分）

| 点 | v3.1 spec | 真实代码 / 模型文档 |
|---|---|---|
| `open_application` | 仍列在 14 工具（`...sdk-v3-api.md:363-371`） | 已整体删除，启动并入 `get_app_state` 透明拉起（`cua-14-tool-e2e-validity.md:42`；`computer-use-client.mjs:52-55`） |
| `getApp` 入参 | `getApp(target: string)`（`:179`） | 接受 `string \| AppRef{name\|bundle_id\|pid}`（`computer-use-client.mjs:960-975`；`computer-use.md:125`） |
| `getApp` 是否展示树 | 「绑定即观察、展示全量树」（`:213`） | **只观察不展示**；代码里「绑定即观察」那行注释已过时（`computer-use-client.mjs:977, 1048-1063`；`computer-use.md:340-341`） |
| `drag` 目标 | 只接受 `Vec2`（`:148`） | `number \| Vec2`，元素索引与坐标可混用（`computer-use.md:86`；`computer-use-client.mjs:727-739`） |
| `key.repeat` | 删除（`:295-297`） | 模型文档仍列出 `repeat`（`computer-use.md:174, 208`），broker 是否接受未核实 |

## 2. 回执、观察与 act-verify（对应问题 B）

### 2.1 action receipt：默认返回物

`ZCode-main/docs/cua/producer/cua-receipt-observation-contract.md:8-42`。【规格】原文：

> "Mutating tools return an action receipt by default. The default `return_state` is `none` for pointer, keyboard/text, and semantic actions."

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

- `return_state="none"`：返回 action note + receipt，**不做任何 `capture_app` 调用**（`:28-29`）。
- `return_state="compact"|"full"`：**恰好一次** post-action `capture_app`，参数固定 `{"include_screenshot": false, "force_full": true}`；结果仍是**完整** CUA observation，compact/full 只控制「格式化给模型多少」（`:31-42`）。
- **post-action 观察失败不回滚 receipt**，也不得重放动作；state 报 unavailable（`:44-45`）。
- **回执块必须排在 AX 树之前**，这是安全契约的一部分：宿主截断长树时必须保留 `dispatch_status` 与 `retry_action=false`（`:47-50`）。
- `get_app_state` **永远**是 model-visible full state，工具层永远发 `force_full=true`；broker 内部 `delta`/`no_change` 不作为可执行模型状态暴露，不得发布复用的 `state_id`、局部 patch 或 `repeat_observation` 提示；**空 AX 树是合法观察**，不做空树 warmup 重试（`:70-80`）。

### 2.2 `dispatch_status` 三态与「可能已发送」

- `accepted`：动作已被接受（成功路径，v3.1 直接 resolve void）。
- `possibly_sent`：请求**可能已经到达原生派发**，调用方**不得自动重放非幂等动作**（`cua-receipt-observation-contract.md:22-24`）。
- `not_sent`：Helper 证明未派发（例如 stale 元素），此时 `action_sent=false`，并且**没有自动 heal**——不重新抓树、不按指纹找相似元素重试（`cua-receipt-observation-contract.md:24-26`；`cua-action-receipt-state-simplification.md:5-8`）。

设计动机（原文）："ZCode 的 `action_sent` / `dispatch_status` 是事故驱动的：一次点击可能已经落地，盲重试会双击。"（`2026-09-10-cua-sdk-v3-api.md:478-480`）

灰度开关：Electron/Chromium 可能高频触发 `possibly_sent`，S1 先只**统计**（照旧 resolve + 打点频率），确认基线后再翻成 throw；保留 `ZCODE_CUA_POSSIBLY_SENT=throw|warn`（`...sdk-v3-api.md:797`）。

### 2.3 状态机（由契约拼出的动作生命周期）

【推断】依据 `cua-receipt-observation-contract.md` + `cua-action-receipt-state-simplification.md` + `...sdk-v3-api.md:493-498` 拼装；原文档未画状态图。

```text
observe(getAXState) → state_id S，（截图时）frame_id F
        │ 模型提交 target = index | [x,y]
        ▼
bindTarget(t, session):  number → {type:"element", state_id:S, index}    S 缺失 → STALE_STATE
                         [x,y]  → {type:"coordinate", frame_id:F, x,y}   F 缺失 → STALE_STATE
        ▼
withActionPolicy: killSwitch → app display sideband → controller lease(CONTROLLER_BUSY) → settler.wait
        ▼
handler → broker action RPC（单次派发；不做 capture 重试 / 不做指纹重试）
        ▼
receipt: accepted → resolve(void)
         possibly_sent → reject {actionSent:true, retry:"reobserve"}（禁止自动重放）
         not_sent / 其它错误 → reject {actionSent:false, retry:按 code}
        ▼
消费：accepted/possibly_sent 只消费「提供目标元素的那个 pre_state_id」；同 app/window 的其他缓存 state 仍可用，没有 app/window 级 refresh_required 写锁
```

### 2.4 单派发、单次消费、无全局写锁

`ZCode-main/docs/cua/producer/cua-action-receipt-state-simplification.md:5-39`。【规格】原文：

> "Mutating element actions are single-dispatch operations. A stale native element must return `action_sent=false` and ask the model to observe again; the tool must not capture a fresh tree, find a similar element by fingerprint, and retry the action."

- 只有 `pre_state_id`（提供目标元素的那个 state）被消费；同 app/window 的其它 `state_id` 不受影响，**不存在 app/window 级 `refresh_required` 写锁**（`:10-13`）。
- 由新鲜坐标 hit-test 得到的原生元素**没有 `pre_state_id`**，派发不消费任何缓存 AX 状态；但 app scope 仍在 action RPC 前独立校验（`:15-17`）。
- `strategy="auto"` **只作用于当前调用**，不会根据 AX 树质量推断未来的 event fallback；从语义 `AXPress` 降级到坐标/event 点击，**只允许在 broker 证明语义能力检查未派发（`action_sent=false`）时**发生（`:19-22, 38-39`）。
- 指纹匹配只保留给「视觉同捕获消费者」（`get_app_state` + `include_screenshot` 的 per-app raster）——在已捕获栅格内匹配元素，不发第二次变更型原生动作（`:24-29`）。

### 2.5 目标回读验证（target verification）——最容易误判的一节

`cua-receipt-observation-contract.md:52-59`。【规格】原文：

> "Target read-back must verify the intended effect of the exact control action. `AXSelected` is a valid postcondition for selecting a row or toggling a selection-bearing control, but it is not a generic `AXPress` success signal. In particular, a menu-bearing button can expose `AXSelected=false` before and after opening its menu. That unchanged value must not produce `target_verification_status="mismatched"`; menu visibility needs its own observable postcondition, otherwise the receipt remains accepted without a target-verification claim."

结论：**回读验证必须按「具体控件动作」选后置条件**；选错后置条件会产生 false mismatch。找不到合适后置条件时，receipt 保持 `accepted`，但**不带 target-verification claim**（即「接受」≠「已验证生效」）。

### 2.6 act-verify：什么算成功

两套语义要区分：

**（a）工具层（模型可见）**：动作 resolve = 已被接受（`accepted`），**不是**「UI 已变更」。文档反复强调 "Attempting an action is not completion: verify that the returned UI state visibly shows the requested result"（`...sdk-v3-api.md:221-222`）。

**（b）e2e 验收层（act-verify）**：PASS 需要 `model_acted AND verify_passed`，验证器读模型无法影响的通道（`cua-act-verify-scenarios.md:15, 469-486`）：

| 验证通道 | 读什么 | 例 |
|---|---|---|
| AppleScript | app 自己的数据模型 | `text of document 1`、`selection`、`name of every note`、terminal `contents` |
| `pbpaste` | 系统剪贴板 | 剪贴板类场景 |
| 文件系统 | 落盘事实 | Finder 拖拽/重命名、RTF 控制字 `\b`/`\i`/`\ul` |
| `defaults read` | 用户偏好（含三态恢复） | Dock `magnification`/`autohide`、Finder `ShowPathbar` |
| System Events | 无 AppleScript 字典的 app（计算器显示） | `display reads 12` |
| `wait_for` ≤8s 轮询 | 吸收动作→提交之间的异步延迟 | 所有场景 |

**必须外部 oracle 的证据边界**（`cua-14-tool-e2e-validity.md:50-68`）：指针动作需要可信 DOM/原生事件（含 button/event family/target region）；键盘与语义动作需要 DOM/AX 状态变化；依赖 responder chain 的 macOS 原生键盘工作流必须显式 `strategy=event` + 已核验的 `window_id`，并以文件系统/RTF 为 oracle——**返回 "Key pressed" 字符串永远不够**；per-app raster 需要确定性栅格内容 + actionable `image_ref`；生命周期工具需要顺序（`request_access` 首、`stop_computer_control` 末）。**缺 oracle 时即使 `isError=false` 也必须判 fail**（`:66-68`）。

已知「回执成功但目标不生效」的真实反例（`cua-act-verify-scenarios.md:352-384`）：
- Notes 富文本 body：`set_value` / 默认元素 value-set 被 AX 接受并回显，但**不提交**到 note 文档模型；必须 `element_focus` + 真实键盘输入才落盘（AV37 走 committal 真实键输入；AV57/AV72 是**故意**的非提交反例——工具正确 ACK 并 WARN，不落盘属预期）。
- Notes 没有独立 title 元素（title = body 第一行），AV58/AV73 被判 `KNOWN_LIMITATION`/xfail（结构性事实，静态分类，不消耗模型调用，也不算 PASS）。
- TextEdit 的 `NSTextView` 上同样的 `set_value` **会**提交——差异来自 app，而非工具。

**fresh authority 规则**（`cua-14-tool-e2e-validity.md:77-87`）：`state_id` 与当前像素 frame 是单权威快照；动作后想再做元素动作，**必须显式重新观察**（工具默认不再隐式 `get_app_state`）；像素动作后必须取得新的 actionable raster 才能再做坐标动作；复用 stale 视觉证据是**测试 bug，不是产品失败**。

### 2.7 producer/Helper 版本边界

`cua-receipt-observation-contract.md:61-68`。【规格】原文：

> "Mixed producer/Helper versions are unsupported. Installing a new producer must install or replace the bundled Helper from that same build."

- plugin launcher 把默认 server spec 钉到 plugin 版本；生产 Helper 启动时校验内嵌 build id 后才复用；stale Helper 在安装/启动时被替换。
- 运行时动作分类**不携带**旧 Helper wire format 的兼容分支；协议增删方法必须 bump IPC 版本，错配走 `version_mismatch`（不重试，提示升级）（`...sdk-v3-api.md:357-361`）。


## 3. 坐标体系：像素契约、内部帧绑定与新鲜度（对应问题 C）

### 3.1 模型可见坐标契约：整数像素，不做任何换算

`frame-coordinate-internal-binding.md:18-26`。【规格】

> "The model copies the integer pixel indices from the exact raster it just observed. It performs no Retina, DPI, crop, resize, display-origin, or native pointer conversion."

- 坐标目标形态：`{"type":"coordinate","x":38,"y":105}`，`x/y` 必须是**非负整数**（旧契约 `frame-pixel-zoom-regression.md:150-151` 明确要求整数；SDK 代码在 `computer-use-client.mjs:334-341` 对非整数/负数抛 `INTERNAL`）。
- **明确拒绝的旧坐标格式**：`space:"screen"`、`space:"screenshot"`、`state_id/index` 作为坐标兼容形式，全部在 schema 阶段拒绝（`2026-08-18-frame-pixel-zoom-regression.md:150-151`）。【规格】
- AX `bounds` 是**全局屏幕点**，只作诊断，**永远不能**复制成坐标目标（`...sdk-v3-api.md:266-281`；`computer-use.md:279-280, 425-428`）。【代码+规格】
- 归一化坐标不存在；没有任何 `normX/normY`、缩放因子或 DPR 字段进入模型面。

### 3.2 内部帧绑定：隐式像素授权的流水线

`frame-coordinate-internal-binding.md:28-47`。【规格】

```text
model-visible raster (1280 x 645)
             │ x/y only
             ▼
bind latest actionable frame in this transport
             │ immutable frame descriptor
             ▼
validate actionable + TTL + app/window ownership
             │
             ▼
project raster pixel center ──► native pointer unit ──► dispatch
```

关键不变量（`frame-coordinate-internal-binding.md:44-85`）：
- **绑定发生在 app 解析或 broker I/O 之前，同步完成**；队列期复核使用内部绑定的 id，并发观察不能重新解释已提交的 `x/y`，也不能仅因「出现了更新的 frame」而拒绝（`:44-48, 69-72`）。
- 绑定的 target 在 retained-frame 生命周期内保持有效，**永不重绑到新几何**（`:44-47`）。
- **state-only 观察发布不了 raster ⇒ 既不推进也不吊销隐式像素授权**；最新保留的可动作栅格继续是绑定源，直到发布新栅格或其 TTL/LRU/actionability 检查失败（`:48-55`）。
- 显式 `frame_id` 仍被接受且**权威**：unknown / expired / evicted / cross-session / non-actionable 全部 fail closed，**绝不回退到 latest frame**（`:57-61`）。
- 当前 transport 没有 actionable raster ⇒ 坐标动作在派发前失败（`:65`）；隐式绑定是 transport-local，不能选到另一个 MCP session 的 frame（`:66-68`）。
- 每个栅格观察以规范的相邻 `[image, image_ref]` 对开头，任务状态/紧凑元数据排在其后；`image_ref` 只用于完整性与诊断，模型不需要读或重复（`:75-80`）。
- 恢复错误**不得**教模型去抄 `image_ref.frame_id`；过期/像素缺失/非可动作/live-owner 变化后，要求重新观察并取新的 raster `x/y`（`:81-85`）。

【代码】SDK 实现与契约一致但更宽松：本 cell 取过栅格才附带 `frame_id`，否则只发裸 `{type:"coordinate", x, y}` 由 broker 走 implicit latest-actionable（`computer-use-client.mjs:342-360`，注释记录 2026-09-14 真机放宽原因：per-cell Worker 下「必须同 cell 截图」会白烧一轮）。安全边界不变：帧过期/被替换/非可动作、`app_ref` 与帧真实 owner 不一致（`frame_dispatch_identity_mismatch`）仍 fail-closed（同处注释）。

### 3.3 frame 生命周期、新鲜度与早期契约

【规格】30 工具时代 frame 契约（`2026-08-18-frame-pixel-zoom-regression.md:128-154`）：模型可见 `frame_id` 是**短不透明 token**（如 `frame-a1b2c3d4`，不公开可猜代次）；私有 session/generation、latest-supersession、TTL、digest 均不公开；返回顺序 `image_ref -> image -> frame hint -> AX tree`，frame hint **不签发 authority**，唯一权威是紧邻最终 raster 的结构化 `image_ref`；派发前依次 `registry live/bounds → Helper provenance → actuate once`，stale/越界/窗口-owner/topology 变化必须在原生动作前 fail closed（`broker calls=0`）；成功以夹具业务标记变化为准，不以 CGEvent/AX 返回为准。

【规格】v3.1 已把 frame 完全内部化：`getScreenshot()` 返回裸 `Uint8Array`，`frameId` 由 SDK 记在 target 上（`...sdk-v3-api.md:505-508`）；host 侧 `TargetSessionStore` 持有 `{ appRef, stateId, frameId, diffBaseline, appInstructionsShown, settleDeadline }`（`:599-605`）。

**frame 尺寸与缩放**（可证实的实测值）：macOS 真机窗口 raster `1280×900`，模型提交整数像素 `(641,496)`，DOM oracle 返回 `Marker placed at 450, 280`（`frame-pixel-zoom-regression.md:210-217`）；C07 marker-grid 覆盖 Retina、奇数尺寸、负屏幕原点、部分越界裁剪，全部要求**像素中心投影**正确（`:163-166`）。**frame 尺寸没有出现在模型可见协议里**，只能从栅格本身推断（【推断】：`getScreenshot` 返回 `Uint8Array`，无 width/height 字段）。

### 3.4 DPR、多显示器与坐标系换算

- **所有换算在 ZCode 内部完成**：resize、byte-budget、Retina/DPI、crop、window/display origin、native dispatch 投影都由 `@zcode/zcode-cua` 处理，不得要求模型计算（`frame-pixel-zoom-regression.md:57-59`）。【规格】
- macOS 坐标翻转：CG 全局坐标（左上原点）↔ NS 全局坐标（左下原点）的翻转基准**必须用主显示器 frame（PrimaryScreenFrame）**；误用 `NSScreen.mainScreen`（= key window 所在屏）会造成整体偏移（实测 458px）（`ghost-cursor-multi-display-spaces.md:32-42`）。
- 多显示器：overlay 单窗在 macOS 默认 Spaces 下只上「面积最大的屏」，改为跟随「反馈点所在屏」；代价是跨屏 glide 边界跳变（`ghost-cursor-multi-display-spaces.md:4-30`）。多 session 版再改为**每屏一窗**、`contentsScale = 该屏 backingScaleFactor`、跳过镜像屏（`multi-session-cursor-overlay-spec.md:130-147, 477`）。
- Windows 侧约定（对 PCCtrl 直接有用）：进程 **Per-Monitor-V2、坐标全物理像素**（与 `ReadWindowFrameBounds`/WGC 位图 1:1），overlay 按光标热点所在屏 scale 绘制；native 层用虚拟屏指标（`GetSystemMetrics(SM_X/CXVIRTUALSCREEN)` 族）作为坐标语言（`multi-session-cursor-overlay-spec.md:166-170, 199-204`）。

## 4. AX 树：采集、裁剪、元素引用与状态一致性（对应问题 D）

### 4.1 快照身份：full / delta / no_change

【规格】目标架构（`ax-tree-optimization-design.md:100-101, 136-139`）：
- **只有 full 观察生成并向模型暴露新的 session `state_id`**；delta/no_change **不 mint 新 id**，模型复用上一次 full 持有的 state_id。
- delta **必须声明 `base_state_id`**，patch 只有在调用方持有 base 时才能应用；compaction、重连、进程恢复、跨页面导航、base 丢失时**强制 full**；连续 delta 达到阈值时发 full checkpoint。
- 触发 full 的条件：首次观察、base 不可用、window identity 变化、页面/文档导航、大比例节点重建、schema 版本变化（`:136`）。

【规格】真机实测基线（`:9-17`）：Calculator full ≈ **9,435 字符 / 58 元素**；TextEdit full 3,636 字符 / 29 元素；TextEdit no-change 仍返回约 3.6–4 KB 全树；同一会话 11 个带 `state_id` 结果合计 112,279 字符、平均约 10,207 字符/次。

【代码】当前实现采用「树文本 + 结构化 elements」双轨：`get_app_state` 参数 `disable_diffing`（工具层翻成 broker `force_full`）（`computer-use.md:162`）；SDK 用 `treeSeen` 强制「delta 只在基线是模型看过的树时才允许」，首次绑定后与只截图观察后的第一次观察都强制整树（`computer-use-client.mjs:592-610`）。生产文档明确：diff 只针对「本 cell 已展示给模型的树」，omitted 行未变、索引仍有效（`computer-use.md:329-335`）。

### 4.2 裁剪与预算：按语义价值，而不是出现顺序

【规格】裁剪顺序（`ax-tree-optimization-design.md:140-185`）：先保留 focused/editable/actionable、selected/checked/error/status；再留有 name/value 的 text/link/button/menuitem；**然后为保留节点补最短父链**；纯布局容器折叠成 section/container summary，只有承担消歧时才保留。预算从「固定 node count」改为**字符/token 预算 + 语义配额**并用。

【规格】实测问题：Chrome 搜索结果页 80 个元素里 **56 个 AXGroup**、75 个无有效 name/value 且 `truncated=true`——无信息容器先占满 node cap，真正的 link/text/button 可能被截掉（`:71-73`）。必须输出 `omitted_counts_by_kind`、`truncated_reason`，并提供可调用的 `get_element_subtree(node_id)` / `get_more_elements(cursor)`（`:185`）。

【代码+规格】实现侧现状：`targeting/format.ts` 超限时按优先级选元素并补回祖先链，注记写着 `indices are sparse; N hidden`（真机 **736 → 300**）（`...sdk-v3-api.md:428-434`）；compact **无条件带 `actions`、永不带 `bounds`**（`:266-281`）。模型文档补充两种不同的缺失：稀疏索引可用 `elements()` 恢复；容器自身只报部分子节点时 header 显示 `showing A-B of N items`，这些行**从未进入观察**，`elements()` 也救不回，只能滚动/移动 app 自己的选区（`computer-use.md:409-420`）。

### 4.3 元素引用（ref/index）如何生成与失效

- **引用就是 snapshot 内短别名 `index`**；元素表行形状：`{index, kind, title|null, value|null, actions[]}`（`computer-use.md:70-76`）；macOS 的 `AppState.window` 另加 `selected` 与 `default_action`（`macos-app-frame-surface-provenance.md:150`）。
- 索引按**该 app 最近一次观察**解析，动作必须带 `app_ref` 说明作用域，否则多 app 会话里索引会退到「全局最近一次观察」（`computer-use-client.mjs:301-332, 715-724`；`computer-use.md:187-193`）。索引是 **per-window** 的，每次观察重编号（`computer-use.md:302-306`）。
- **失效判定在持有原生元素的一侧**：Helper 在观察时把索引冻结成 native token，元素消失时本来就 fail-closed；SDK 不再在客户端提前否决（`computer-use-client.mjs:624-638`）。
- stale 元素 → `action_sent=false` + `dispatch_status="not_sent"`，**不自动补救**（不重抓树、不按指纹重试）（`cua-receipt-observation-contract.md:24-26`）；`ELEMENT_UNAVAILABLE`/`STALE_STATE`/`STRUCTURED_STATE_UNAVAILABLE` 的 `retry` 是 `reobserve`（`computer-use-client.mjs:96-101`）。
- 窗口寻址：`window_id` 解析失败时 Helper 会**静默降级到最前窗口**（只回 `window_id_fallback: true`），SDK 现在把降级改成抛 `STALE_STATE`（`computer-use-client.mjs:1040-1047`；`...sdk-v3-api.md:408-410`）。未绑 `window_id` 时每次观察都重新解析 main/key window，**动作后新开的 modal 会成为被捕获窗口**，旧索引可能指向无关控件（`computer-use.md:302-316`）。

### 4.4 动作后的状态一致性校验

- **consumer 不维护 post-state 屏障**：只消费 `action_receipt`，不合成 `refresh_required`；需要 UI 事实时由模型显式 `get_app_state` 并用新 `state_id`（`2026-08-18-ax-action-state-consistency.md:14-51`）。
- 工具层只有 `return_state=compact|full` 才抓**一次** `force_full` 观察，且 receipt 必须排在长 AX 树之前（防止宿主截断丢掉防重放证据）（`cua-receipt-observation-contract.md:31-50`）。
- **目标回读验证按控件动作选后置条件**：`AXSelected` 可用于选行/带选择控件，但不是通用 `AXPress` 成功信号；菜单类按钮菜单打开前后都可能 `AXSelected=false`，这不能判 `mismatched`（`cua-receipt-observation-contract.md:52-59`）。
- 【代码】`[effect_evidence unchanged]`：producer 为「AX 受理但界面逐字节未变」提供的**控件类型无关**证据位，观察响应里以独立 text block 下发；SDK 必须把它附在树文本后，否则模型对「点了没反应」零感知（`computer-use-client.mjs:555-574`）。模型文档要求出现该提示时**不要重复坐标**，改走元素/键盘（`computer-use.md:429-435`）。

### 4.5 AX 与像素/视觉如何配合

- **AX-first**：默认 `include_screenshot=false`；只有视觉读取、坐标目标、AX sparse 或显式截图才捕获（`ax-tree-optimization-design.md:224-226`）。
- 对 canvas / AX sparse 页面，目标架构要求显式返回 `state_quality=visual_required`，**不伪造空树可用**（`:221-223`）。
- 截图时 snapshot 应记录 window、display provenance、**device pixel ratio**、image region 与 media ref；AX delta 与图片 dirty crop 共用同一 `state_id/base_state_id`，任一方缺失不得让另一方不可解析（`:226`）。【规格/设计，未证实已实现】
- 无「AI 视觉模型读 AX」的设计描述；视觉仅指 raster 走 media block（不放进 AX JSON 或日志，`:225`）。


## 5. 目标 app 身份与 frame provenance（对应问题 E）

### 5.1 app 身份字段

`AppInfo = { pid, name, bundle_id, active }`，`bundle_id` 在 macOS 是 bundle id、**在 Windows 是 AUMID**（`...sdk-v3-api.md:164-170`）。窗口层（`list_windows` 行，`computer-use.md:250-289`）：

| 字段 | 含义 / 陷阱 |
|---|---|
| `window_id` | 稳定整数；放回 `app_ref.window_id` 绑定该窗口 |
| `title` / `subrole` | `""` = AXTitle 为空（Qt/自绘面板常见）；`null` = 读不到（AX 失败或 CoreGraphics 合并行）。**`subrole` 存在性 = 可绑定 AX 窗口 vs 残留 surface**（30 app/34 窗实测 31 个 `AXStandardWindow`） |
| `text_preview` / `bounds` | 无标题窗口内容采样（**绝不是标识符**，可能含用户数据）/ `[x,y,w,h]` 全局屏幕点，**仅供诊断** |
| `main` / `focused` / `onscreen` / `index` | AX main / AX key 窗口（**都不等于系统前台 app**，后者看 `listApps().active`）/ `false` = app 关窗后 CoreGraphics 残留、不可操作 / 行号（非元素索引、非 window id） |

**平台差异（代码）**：Windows 上「display name」是 OS 列出的名字（开始菜单名），**不是窗口标题**；`getApp("Notes")` 这种裸字符串按「含点且无空格/斜杠 → bundle_id，否则 name」猜，猜错时换字段重试一次（`computer-use-client.mjs:368-393`；`computer-use.md:182-186, 449-456`）。【代码】

### 5.2 macOS frame surface provenance：像素归属是证据，不是几何启发式

`macos-app-frame-surface-provenance.md`（implementation contract 2026-08-23）。【规格】

> "Surface membership is evidence, not a geometry heuristic."

一个 surface 属于某 app frame 必须**同时**满足 4 条（:22-34）：① macOS AX 能从该逻辑应用的 focused/main/top-level window 经**有界关系walk** 到达；② `_AXUIElementGetWindow` 返回真实 `CGWindowID`；③ WindowServer 在截图前后报告同一窗口，owner PID/bundle/layer/bounds 稳定；④ 截图几何与 display topology 稳定。walk 上界 **8 层 / 64 节点**；**绝不用标题或重叠 bounds 推断归属**。

每个 frame 记录一个**有序 front-to-back 的 `capture_surfaces` 集合**，每项含：`actual_window_id`、`presentation_window_id`、中性 `surface_kind` 与 AX 证明的 `relation`、真实派发 owner PID 与 canonical bundle ID、截图时 bounds 与 WindowServer layer（`:53-58`）。

- 只有 presentation root、且 WindowServer surface ABI 不可用时**不签发 surface record**，保留原 root window 校验路径，不得让整个观察失败（`:60-62`）。
- AX 已证明 attached surface 但其 WindowServer 身份不可用/截图期间变化 → **仍返回 observation，但栅格标 non-actionable**（`capture_surfaces_unavailable` / `capture_surfaces_changed`）：树可用、panel 像素 fail-closed（`:63-67`）。
- 坐标派发：front-to-back 命中 `capture_surfaces` → 无可证明 surface → `action_sent=false`；WindowServer 身份校验发现 closed/replaced/moved/owner changed → `surface_replaced`、`action_sent=false`；只有精确匹配才派发（`:82-98`）。无关遮挡窗**不属于该 app frame，因此偷不走坐标授权**（`:99-104`）。
- **模型面**：`AppState.window` 增加中性字段 `surface_kind`（`window`/`attached_dialog`/`open_panel`/`save_panel`/`popover`）、`actual_window_id`、`presentation_window_id`、`surface_lifecycle`（`stable`/`replaced`/`closed`）；元素增加 `selected`/`default_action`（`:142-157`）。
- **mutation 验证按目标类型**：选择需要 `selected=true` 或确认控件变化；打开/确认需要 panel 关闭且控制回到同一 presentation root；**通用 AX 树 digest 变化本身永远不够**（`:159-179`）。即使 `return_state="none"` 也要报告实际派发 surface、是否可重试、以及效果为何未验证（`:169-173`）。

### 5.3 防伪造 / 权威链

- **唯一栅格权威 = 图片紧邻的结构化 `image_ref`**：`image` 与 `image_ref` 必须直接相邻；中间有空白/任意 block → 整个结果错误化且**不暴露 frame_id**；无关图片不能替 orphan `image_ref` 授权（`2026-08-25-cua-authority-verdict-audit.md:42-44, 56-58`）。
- **one-raster 规则**：一个结果里出现第二张栅格 → 整帧移除，一张图都拿不到（`2026-08-24-cua-node-repl-sdk-migration-spec.md:76-79`）。
- official 保护需 producer 签发的 `zcode.cua/official-frame-integrity-v1` + 合法相邻 `image_ref` + raster 三者同结果才动态启用（`:81-86`）。
- **沙箱不可写权威元数据**：`nodeRepl.setResponseMeta` / `emitStructuredResult` 送来的 `zcode.cua/app-associations-v1` 一律丢弃（挂在模型可见 globals 上，否则模型能让工具卡声称操作了别的 app）；可信记录点只在 node-repl-host CUA bridge（`2026-08-19-cua-target-app-display-metadata-spec.md:139-160`）。
- app 展示身份键 `appKey`：`darwin:<bundleId>` / `windows-aumid:<aumid>` / `windows-exe:<canonical path>` / `linux-exe:<path>`；provider 自带的 32×32 PNG 不被宿主投影，图标由 `IPlatformService.getApplicationIcon` 解析（`:121-137`）。
- Windows 身份纪律：普通应用用 **canonical exe 绝对路径**，有 AUMID 时优先 AUMID，**`ApplicationFrameHost.exe` 不得作为身份**；packaged app 必须用 `IApplicationActivationManager` 激活并读 `PKEY_AppUserModel_ID` 精确解析 HWND/PID，多候选 fail closed（`2026-07-29-windows-...design.md:53-64, 146-160`）。【规格】

## 6. 超时、诊断与错误透传（对应问题 F）

### 6.1 `capture_app` 超时诊断

`capture-app-timeout-diagnostics.md`。【规格】macOS `capture_app` 在 Helper 异步 worker 中遍历 AX 树，目标无响应/树过大/worker 排队时被 **8 秒 wall-clock fence** 截断；过去只有一句 deadline 错误，无法定位卡点。

`captureAppDiagnostic(pid, windowId?)` 是 **additive native ABI**，返回最近一次匹配采集的（:18-28）：

| 字段 | 含义 |
|---|---|
| `phase` | `capture_lock` / `window_resolve` / `tree_traversal` / `menu_traversal` / `native_complete` |
| `last_ax_operation` / `nodes_visited` | 进入可能阻塞 AX API 前的操作名 / 已开始读取的节点数 |
| `elapsed_ms` / `deadline_exceeded` | 从 N-API admission 起的单调时钟耗时（含 libuv 排队）/ 读取诊断时是否已越过 native 绝对 deadline |
| `pid` / `window_id` | 只用于把诊断绑定到本次请求，不含授权材料 |

- **隐私约束**：native 诊断只记录阶段、最后一次 AX 操作、节点数、耗时、PID、window id；**不得记录控件文本、剪贴板、token、socket、窗口标题或其他用户内容**（:14-15）。
- 正常快速 capture 不写日志，只在超时时写一行 `[capture-app-timeout] ...` 到受控 `--exit-log`，同时把同一组字段摘要附加到 MCP 错误文本（:33-37）。
- 诊断 ABI 缺失/形状非法/抛错时，**原超时错误仍须正常返回**：诊断路径不得改变 `capture_app` 的失败语义（:30-31）。

### 6.2 冷启动 / not-ready：`CUA_NOT_READY`

- Helper 懒启动：第一次调用触发拉起，同时工具层返回**非 error** 结果，单个文本块装 `{kind:"CUA_NOT_READY", reasonCode:"broker_not_accepting", retryable:true, message:"...is starting up..."}`——producer 刻意设计的机器可读契约（`possibly_sent ⇒ retryable=false，绝不重放；marker 在途 ⇒ 可重试`）（`computer-use-client.mjs:247-262`）。
- 【代码】SDK 现在识别该 envelope，最多重试 **6** 次，退避 `[250, 500, 750, 1000, 1500]` ms（`computer-use-client.mjs:263-265`）。
- `broker_unavailable` / `stale_socket` → `HELPER_UNAVAILABLE`，并继续沿用 `CUA_NOT_READY` 语义（`cua-permission-error-passthrough.md:49-50`；`computer-use-client.mjs:87-90`）。

### 6.3 权限错误透传：不翻译、不自动弹框

`cua-permission-error-passthrough.md:3-51`；`2026-08-25-cua-permission-error-passthrough.md:7-65`。【规格】

> "CUA 不再返回或翻译 `CUA_PERMISSION_REQUIRED`。Helper/Broker 的失败统一作为普通 MCP tool error 返回"

- 原因：`permission_denied` **不等价于 macOS TCC 缺失**——前台窗口不匹配、可信 Host 的 `prevent_activation`、**Windows UIPI**、Linux XTest、无效目标、native capability 失败都可能用该码；producer 无法仅凭通用码判断权限种类，consumer 再根据错误文本自动弹权限框，会把一次不可靠推断扩散成产品行为（:11-14）。
- 模型侧恢复：错误明确提到 Accessibility / Screen Recording 时可调用一次**只读** `request_access`；明确缺权则告知用户并结束本轮，不得继续 CUA、自动重试或承诺授权后自动继续（:53-59）。
- 只有 macOS 的 official `request_access` 成功结果在 `_meta["zcode.cua/request-access-status-v1"]` 带严格版本化快照；`accessibility=denied|stale`、`screenRecording=denied` 是确定缺权，**`unknown` 不足以触发引导**（:39-45）。
- 三平台同一透传规则，**不按 `process.platform` 改写错误含义**（:51）。

### 6.4 错误码映射与重试语义（代码实证）

【代码】broker 码 → SDK 码映射（`computer-use-client.mjs:76-94`）：`permission_denied→PERMISSION_DENIED`、`not_authorized→NOT_AUTHORIZED`、`launch_failed→LAUNCH_FAILED`、`invalid_request→INVALID_APP`、`element_unavailable→ELEMENT_UNAVAILABLE`、`not_settable→NOT_SETTABLE`、`not_selectable→NOT_SELECTABLE`、`action_unavailable→ACTION_UNAVAILABLE`、`foreground_required→FOREGROUND_REQUIRED`、`controller_busy→CONTROLLER_BUSY`、`broker_unavailable/stale_socket→HELPER_UNAVAILABLE`、`version_mismatch→VERSION_MISMATCH`、`timeout→TIMEOUT`、`unimplemented→ACTION_UNAVAILABLE`、`method_not_found/internal→INTERNAL`。**未知码归 `INTERNAL`，绝不静默成功**。

- `actionSent` 默认 **false** 是刻意的保守方向：只有收据明确说下发过才置 true（反向默认会让模型对从未下发的动作放弃重试）（`:113-130`）。
- `retry` 推导：`actionSent=true → "reobserve"`；否则 `NEVER_RETRY_CODES`（8 个：CONTROLLER_BUSY/CONTROL_STOPPED/PERMISSION_DENIED/NOT_AUTHORIZED/VERSION_MISMATCH/ACTION_UNAVAILABLE/NOT_SETTABLE/NOT_SELECTABLE）→ `"never"`；`REOBSERVE_CODES`（ELEMENT_UNAVAILABLE/STALE_STATE/STRUCTURED_STATE_UNAVAILABLE）→ `"reobserve"`；其余 `"retry"`（`:96-129`）。
- `assertOk` 的唯一成功判据：`!isError && dispatch_status !== "possibly_sent"`；非 error 但 `possibly_sent` → **算失败**抛 `TIMEOUT`（实际是「可能已发送」），并把 `actionSent` 置 true（`:278-297`）。
- `FOREGROUND_REQUIRED` 只来自 `strategy:"event"`（以及后台 app 的 `paste`）；macOS 上这段模型面**没有任何前台化手段**，不要重试、不要找 activate（`computer-use.md:382-385`）。
- `STRUCTURED_STATE_UNAVAILABLE` 消息必须列出缺失字段（state_id/elements/app/window），否则同一故障既不可自愈也不可诊断（`computer-use-client.mjs:515-553`）。


## 7. Windows 对照与 PCCtrl「观察-动作-验证」契约建议（对应问题 G）

### 7.1 macOS 后台输入契约（Windows 派发可直接借用的原则）

`macos-background-input-mode.md:21-104, 106-214`。【规格】

1. **不改变用户环境**：默认不得改变前台 app、key window、窗口层级或物理鼠标位置；证据不足时 fail-closed。
2. **目标必须是 on-screen 且仍属于绑定 PID 的真实窗口**；隐藏/最小化/其他 Space/失效或负 fallback window id 一律 `action_sent=false`。
3. **事件只投递一次**：已写入 PID/window id/窗口内坐标的事件只走 `SLEventPostToPid`，**不得**再调用 `CGEventPostToPid`；双通道会让 Chromium 收到重复 mousedown/mouseup/contextmenu，使「工具成功」掩盖重复业务副作用（`:102-104`）。
4. **进入原生调用后，错误只能表示 `possibly_sent`**：`AXUIElementPerformAction` 等写 API 的返回值不是应用业务状态的事务确认——app 可能已新增条目/打开编辑器/改变选择，随后 AX 仍返回 `kAXError*`；因此不能表示「动作没发生」，也不能触发自动重试（`:106-111`）。
5. **begin 失败发生在派发前 ⇒ `action_sent=false`**；**end 失败发生在派发后 ⇒ 必须按 `action_sent=true` 解读**，绝不能让调用方读成「没送达、可重试」（`:159-214`）。键盘收尾的 `frontmost_changed`/`cursor_moved` 只记 warn 不改结果（这两量本路径改不动；报成失败会让模型重复输入）（`:202-214`）。
6. **失败诊断要结构化**：后台键盘 begin 不能只返回布尔值，detailed ABI 必须给稳定 `reason`、`native_status`、`pid`/`window_id`、预期 bounds、live owner/on-screen/live bounds、缺失符号列表（`:159-191`）。

### 7.2 Windows 侧已证实的事实（供 PCCtrl 选定坐标系与身份）

- 输入路由：Windows 保持 **foreground 验证 + SendInput/UIA 路由**，不消费 macOS AX surface 关系（`macos-app-frame-surface-provenance.md:213-218`）。
- 坐标：进程 **Per-Monitor-V2，坐标全物理像素**，与 `ReadWindowFrameBounds` / WGC 位图 1:1（`multi-session-cursor-overlay-spec.md:199-204`）；截屏走 WGC，overlay 用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 排除自己（`:195-198`）。
- 身份：packaged app 用 AUMID + `PKEY_AppUserModel_ID` 精确解析 HWND/PID；普通 Win32 用完整 executable 身份；`ApplicationFrameHost.exe` 不得当身份；窗口标题/active/枚举顺序**都不作判据**（`2026-07-29-windows-...design.md:53-64, 146-160`）。
- 焦点副作用（与 macOS 不同，必须写进契约）：Windows 启动非 packaged app 会取焦点；对最小化窗口 `include_screenshot=true` 会**恢复**窗口（`computer-use.md:449-460`）。【代码】
- Windows 已有独立的 app frame surface 规格（`2026-10-23-windows-app-frame-surface-spec.md`，873+ 行）：复用 `frame_id`/session/generation/`actual_window_id`/`presentation_window_id`/owner，工具层注入 `frame_provenance`/`element_provenance`、broker 只验证不产生目标；拒绝词 `element_surface_replaced`/`element_geometry_changed`/`element_surface_unavailable`/`pointer_receiver_mismatch`/`invalid_element_provenance`（:239, 848-849）——**细节归 Windows 成员，本笔记只登记存在性**。

### 7.3 给 PCCtrl Windows 执行器的 JSON 契约草案（【推断】，按 ZCode 语义改写）

设计取向：**wire 用 snake_case、SDK 用 camelCase**；`state_id`/`frame_id` **只存在于执行器↔服务内部**，模型只看到「元素索引」和「raster 像素 x/y」；动作默认只返回 receipt，观察是独立调用。

```jsonc
// 1) 观察（observe）——唯一的 UI 事实来源（wire 全部 snake_case）
{ "schema_version": "pcctrl-observe-v1", "session_id": "sess_...", "seq": 12,
  "target": {
    "app":    {"pid": 23192, "aumid": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
               "exe_path": "C:\\...\\Calculator.exe", "name": "Calculator"},
    "window": {"window_id": 27367, "title": "Calculator", "onscreen": true,
               "foreground": false, "bounds": [100,100,400,600]},   // bounds=屏幕点，禁止当坐标
    "state_id": "st-8f21", "window_resolution": "exact" },           // exact | main_fallback(禁止静默降级)
  "state": { "mode": "full|delta|no_change", "base_state_id": "st-7e00",
             "elements": [{"index": 6, "kind": "button", "title": "7", "value": null,
                           "actions": ["press"], "selected": false, "enabled": true}],
             "omitted": {"by_kind": {"container": 4}, "reason": "budget", "sparse_indices": true}},
  "frame": { "frame_id": "fr-4c9a", "width": 400, "height": 600, "dpr": 1.5,
             "actionable": true, "non_actionable_reason": null, "captured_at_ms": 1760000000000 },
  "advisories": ["effect_evidence unchanged"] }

// 2) 动作（act）——单次派发 + 幂等键（frame_id 由服务填充，模型不传）
{ "schema_version": "pcctrl-act-v1", "session_id": "sess_...", "request_id": "req_01H...",
  "op": "click|type|key|scroll|drag|set_value|select_text|perform_action|paste",
  "target": {"kind": "element", "index": 6, "state_id": "st-8f21"},
            // 或 {"kind":"coordinate","x":38,"y":105,"frame_id":"fr-4c9a"}
  "args": {"mouse_button": "left", "click_count": 1, "modifiers": "", "strategy": "auto"},
  "expected_precondition": {"window_id": 27367, "surface": "stable"} }   // TOCTOU 复核输入

// 3) 动作回执（默认返回，不隐含观察；回执块必须排在状态/树之前）
{ "action_receipt": { "schema_version": "pcctrl-action-receipt-v1",
    "action_sent": true, "dispatch_status": "accepted|possibly_sent|not_sent", "retry_action": false,
    "dispatch_surface": {"window_id": 27367, "owner_pid": 23192, "method": "sendinput|uia|wgc_postmessage"},
    "verification": {"status": "verified|unverified|mismatched", "postcondition": "value_changed|selection|panel_closed|null"},
    "state_available": false }}

// 4) 错误（唯一失败通道；不可重放 / 可能已发送的事实必须随错一起走）
{ "code": "PERMISSION_DENIED|FOREGROUND_REQUIRED|STALE_STATE|ELEMENT_UNAVAILABLE|...",
  "action_sent": false, "dispatch_status": "not_sent", "retry": "reobserve|retry|never", "retry_action": false,
  "details": {"brokerCode": "permission_denied", "owner": "sess_..."},
  "diagnostic": {"phase": "tree_traversal", "last_ax_operation": "AXChildren", "nodes_visited": 312,
                 "elapsed_ms": 8012, "deadline_exceeded": true, "pid": 23192, "window_id": 27367} }
```

### 7.4 落地建议（按优先级）

1. **动作结果与观察分离**：默认只回 receipt；要状态就再观察一次。回执块永远排在状态/树之前（宿主截断时保住 `dispatch_status`/`retry_action`）。
2. **三态派发 + `action_sent`**：`accepted/possibly_sent/not_sent`；进原生调用之后的任何错误一律 `possibly_sent`，禁止自动重放非幂等动作。
3. **显式新鲜度**：服务端持有 `stateId/frameId`，模型只给索引/像素；缺失即 `STALE_STATE`，不自动补观察；新观察不得让队列中旧坐标被重新解释。
4. **验证按控件选后置条件**：UIA 的 `SelectionItem.IsSelected`/`ToggleState`/`ValuePattern` 各自对应动作；**不要用通用「树变了」当成功**；无合适后置条件时只报 `accepted` + `unverified`。
5. **坐标 = 一次性 raster 的物理像素**：所有 DPI/crop/窗口原点换算是执行器内部事；AX bounds 与屏幕坐标严禁混入；无 raster 时坐标动作在派发前拒绝。
6. **Windows 专有坑**：UIPI/前台策略都会表现为 `permission_denied`，**不要翻译成「缺权限」自动弹框**；`SendInput` 会改真实焦点/光标，需要显式声明并尽量用 UIA 元素动作；`PostMessage/PostThreadMessage` 可能被 Chromium 忽略（ZCode 的 SendInput S4 实验背景，细节归 Windows 成员）。
7. **结构化超时诊断**：固定字段 `phase/last_ax_operation/nodes_visited/elapsed_ms/deadline_exceeded/pid/window_id`，不写用户内容；诊断读取失败不得改变原错误语义。
8. **身份与 provenance**：app 身份用 AUMID/exe canonical path + pid，win identity 用 HWND + 窗口 owner 校验；截图 owner 与派发 owner 用**同一权威**（ZCode 0.5.6 的教训：capture 用 WindowServer、dispatch 用 AX hit-test 会错投）。

## 未解问题 / 边界发现（不越界深挖）

1. **两份「14 工具」清单口径不一**：`2026-09-10-cua-sdk-v3-api.md:363-371` 的 14 含 `open_application`；但 normative 的 `cua-14-tool-e2e-validity.md:22-45` 与【代码】`computer-use-client.mjs:30-45` 都是「`open_application` 已删、启动并入 `get_app_state` 透明拉起」。建议主报告以 **代码 + normative 文档**为准，并把 spec 的差异标注为「草案滞后」。
2. **`repeat` 参数**：v3.1 spec 删除 `repeat`（`...sdk-v3-api.md:295-297`），但模型文档 `computer-use.md:174, 208` 仍把它列为 `key` 的参数。未核实 broker schema 是否仍接受——归 broker/工具层成员。
3. **`getApp` 是否「绑定即展示」**：spec §2.3 写「绑定即观察、展示全量树」（`:213`），【代码】已于 2026-09-13 改为**不展示**（`computer-use-client.mjs:1048-1063`），`computer-use.md:340-341, 361-363` 也是「不展示」。主报告应写实现事实。
4. **frame TTL / LRU 的具体数值未找到**（只有「validation 含 actionable + TTL」「TTL/LRU/actionability checks fail」的描述，无毫秒数）；`display-topology`/`display-provenance` 的内部字段也未在本范围读到。
5. **AX 树 DSL / stable node_id / base_state_id 是否落地未证实**：`ax-tree-optimization-design.md` 是目标架构（P1-P5），当前【代码】仍是「树文本 + 结构化 elements + diff」，未见 `@state` DSL 或 `node_id` 的实际实现证据。
6. **Windows 侧**：`2026-10-23-windows-app-frame-surface-spec.md`、`2026-10-13-windows-input-delivery-spec.md`、S3/S4 派发计划只做了存在性登记，细节留给 Windows 成员，避免重复/冲突。

## 证据索引

| 文件（相对 `ZCode-main/`） | 一句话说明 |
|---|---|
| `docs/cua/producer/2026-09-10-cua-sdk-v3-api.md` | v3.1 模型面全量声明、30→14 工具瘦身、错误码、S0-S6 排期 |
| `docs/cua/producer/cua-receipt-observation-contract.md` | `zcode-cua-action-receipt-v1`、return_state、回执顺序、目标回读验证 |
| `docs/cua/producer/cua-action-receipt-state-simplification.md` | 单派发/单次消费 `pre_state_id`、无 app 级写锁、strategy 只作用当前调用 |
| `docs/cua/producer/cua-14-tool-e2e-validity.md` | normative 14 工具契约、解冻理由、证据边界、fresh authority |
| `docs/cua/producer/cua-act-verify-scenarios.md` | 80 场景 act-verify、独立 oracle、Notes 非提交反例、CJK IME/锁屏 preflight |
| `docs/cua/producer/frame-coordinate-internal-binding.md` | 像素坐标契约 + 内部帧绑定不变量 + 显式 frame_id 兼容 |
| `docs/cua/producer/ax-tree-optimization-design.md` | AX 裁剪/预算/node_id 与 material fingerprint/full-delta-no_change/实测基线 |
| `docs/cua/producer/capture-app-timeout-diagnostics.md` | 8s fence 与 capture 诊断字段、隐私与失败语义边界 |
| `docs/cua/producer/macos-app-frame-surface-provenance.md` | surface 归属四条件、capture_surfaces、surface_kind、mutation 验证 |
| `docs/cua/producer/macos-background-input-mode.md` | 后台输入目标约束、SLEventPostToPid 单次、AX 错误 ⇒ possibly_sent、begin/end 诊断 |
| `docs/cua/producer/cua-permission-error-passthrough.md` | 不翻译 `CUA_PERMISSION_REQUIRED`、`permission_denied` 过载、request_access 只读 |
| `docs/cua/2026-08-25-cua-permission-error-passthrough.md` | 同一决策的 consumer 侧根因与不变量 |
| `docs/cua/2026-08-25-cua-authority-verdict-audit.md` | image/image_ref 相邻权威、CUA-AUTH/MEDIA case |
| `docs/cua/2026-08-18-ax-action-state-consistency.md` | consumer 只消费 receipt、不合成 refresh_required/post-state |
| `docs/cua/2026-08-18-frame-pixel-zoom-regression.md` | frame_id token、TTL/generation 不公开、派发前校验顺序、Retina/负原点实测 |
| `docs/cua/2026-08-19-cua-target-app-display-metadata-spec.md` | app 展示身份、`app-associations-v1`、node_repl 可信点、沙箱伪造丢弃 |
| `docs/cua/2026-08-24-cua-node-repl-sdk-migration-spec.md` | SDK+bridge 迁移、one-raster、structured sink 边界、bootstrap 每 cell |
| `docs/cua/producer/multi-session-cursor-overlay-spec.md` | 多 session 光标/颜色/每屏一窗、Windows layered overlay/物理像素/WGC 排除 |
| `docs/cua/producer/ghost-cursor-multi-display-spaces.md` | 多屏 overlay 跟随、CG↔NS 翻转基准必须主屏 |
| `docs/cua/producer/ghost-cursor-turn-end-hide.md` | turn-ended/session-closed 隐藏、20s 租约不缩短 |
| `docs/superpowers/specs/2026-07-29-windows-cua-tool-call-and-app-launch-reliability-design.md` | Windows AUMID 激活/HWND 解析、工具名精确契约、UIPI 边界 |
| `apps/zcode-cli/packages/zcode-cua-plugin/scripts/computer-use-client.mjs` | **真实 SDK 实现**：14 方法、错误映射、receipt 判定、implicit frame、treeSeen |
| `apps/zcode-cli/packages/zcode-cua-plugin/docs/computer-use.md` | 模型可见参考文档：API/坐标/错误/Windows/工作流（471 行） |
| `apps/zcode-cli/packages/zcode-cua-plugin/skills/computer-use/SKILL.md` | 模型决策指南（307 行，未逐段引用） |

