# WhaleGo 协议契约 v1（MCP 工具面）

> 状态：**v1.2（审计 02 + 场景走查修订版，实现基线）**。实现基线：执行器 = C#/.NET 10 + MCP C# SDK 2.3.0；
> 插件 = AstrBot 4.29.3（容器自带 mcp 1.30.x）。协议标识：`protocol_version = 1`（整数；本次修订在冻结前完成，不升主版本）。
> 修订依据：`docs/reviews/design-review-01*.md`、`docs/reviews/design-review-02.md`、`docs/reviews/workflow-trace-01.md`；
> 逐条裁决见 `docs/reviews/design-review-02-response.md`（D1–D20）。
> 约定：**字段名一律 snake_case**；坐标系一律**该次观察返回图片的像素、左上原点**；所有工具返回 `CallToolResult`；
> **模型可见文本 = 简体中文 + 固定英文前缀（`OK:` / `ERROR[CODE]:`），错误码保持英文**。

---

## 0. 设计要点（v1 修订后）

1. **7 个必需工具**：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`。
   不设 `go_session`（会话由 `session_id` 参数承载），不设可选项。
2. **观察/动作分离**：`go_observe` 是唯一 UI 事实来源；`go_act` 只返回回执，不隐式观察。
3. **单失败通道（v1 硬规则）**：
   - **动作类工具（go_act）成功** = `isError=false` **且** `dispatch_status="accepted"`；
     只读/查询类工具（health/apps/windows/observe/launch/stop）成功 = `isError=false` 且文本首行 `OK:`；
   - `not_sent` / `possibly_sent` **一律走错误通道**（`isError=true`，错误对象内嵌完整 receipt）；
   - 因为 **AstrBot 不读 `isError`**（源码全树零处理），**文本首行必须给出机器可辨的成败前缀**：
     成功 `OK:`；失败 `ERROR[<code>]:`。
4. **新鲜度 fail-closed**：模型只回传 `state_id + index`（元素）或 `x,y`（坐标）；`frame_id` 由服务端内部绑定，
   模型**不传**。任何动作后该会话当前观察状态立即失效（需重新 observe）。
5. **回执三态**：`action_sent` + `dispatch_status(not_sent|accepted|possibly_sent)` + `retry_action`；
   `possibly_sent` 时调用方**禁止自动重放**，必须重新观察。
6. **隐私 day-1**：密码控件值一律脱敏；敏感窗口截图抑制；**截图默认开启（用户拍板，可一键关闭）**；
   自身进程窗口永不截图；审计不含明文与截图字节。
7. **能力收窄**：v1 只声明已验证动作子集；未实现的组合返回 `UNSUPPORTED_ACTION`（never）。
8. **三态止血**：只读（配置，`READONLY_MODE`）/ 暂停（本机 GUI，`PAUSED`）/ 停止（kill switch，`SESSION_STOPPED`）。
   动作工具全部拒绝，观察类工具仍可用；暂停/停止都会释放租约、取消排队动作；**恢复只允许在本机 GUI/托盘完成**。
9. **模型面最小权限**：`session_id`/`request_id`/`scope` 由插件按 umo 注入，**模型不可见、不可传**；`go_stop` 只停本会话；
   模型没有恢复能力。
10. **模型使用规则**由插件通过 `on_llm_request` 注入（约 15 行，见 §12）；tool description 只放最硬规则。

---

## 1. 传输与认证

- 端点：`POST/GET/DELETE /mcp`（MCP Streamable HTTP）；`GET /healthz`（免认证，最小面）。**仅这两个 HTTP 路由**；
  GUI/托盘通过进程内端口（`IExecutorControl`）控制内核，不新增 HTTP 路由。
- healthz 载荷固定：`{"ok":true,"version":"0.1.0","protocol_version":1,"uptime_s":123,
  "paused":false,"stopped":false,"accepting_actions":true}`；配置 `listen.healthzRequireToken=true` 时同样要求 Bearer（默认 false）。
- 认证：`Authorization: Bearer <token>`（每次请求）；缺失/错误 → HTTP 401 + `WWW-Authenticate: Bearer`。
- 限流：HTTP 429 + `Retry-After`；业务动作另有服务端速率上限（`RATE_LIMITED` + `retry_after_ms`）。
- MCP 协议版本：由 MCP SDK 协商（服务端记录协商结果并在 `go_health.mcp_protocol_version` 暴露）；
  应用协议版本用 `go_health.protocol_version = 1`。**请求不携带 `schema_version`**。
- 工具调用无自定义 envelope；参数即 `properties`。

---

## 2. 通用结果约定

### 2.1 成功（唯一判据）

- **动作类工具（go_act）**：`isError=false` 且 `action_receipt.dispatch_status="accepted"`；
- **只读/查询类工具**（go_health/go_apps/go_windows/go_observe/go_stop）：`isError=false` 且文本首行 `OK:`。
- **`go_launch` 是写动作**（审计 fail-closed，返回前落盘），但没有输入派发/`dispatch_status`，因此成功判据同查询类：
  `isError=false` 且文本首行 `OK:`；失败一律走错误通道。

```jsonc
// CallToolResult（content 顺序固定）
{
  "isError": false,
  "content": [
    { "type": "image", "data": "<base64 PNG>", "mimeType": "image/png" },   // 可选；图片必须在最前
    { "type": "text", "text": "OK: 观察完成 state_id=st-8f21cd04 ..." },
    { "type": "text", "text": "{...结构化 JSON...}" },
    { "type": "text", "text": "<渲染文本树>" }
  ]
}
```

- **首行文本**（无图时是第一个 TextContent）固定前缀 `OK:`（英文冒号），随后一句话摘要。
- 图片（若有）放 content 数组**最前**（部分网关按位置读取图片）；AstrBot 会缓存图片并在下一步注入模型。

### 2.2 失败（唯一失败通道）

触发条件（任一）：`dispatch_status != "accepted"`；参数/状态/权限/限流错误；连接层之上的业务异常。
**不允许**用 HTTP 5xx 表达业务失败。

```jsonc
{
  "isError": true,
  "content": [
    { "type": "text",
      "text": "ERROR[STALE_STATE]: 观察状态已过期，请先重新调用 go_observe。" },
    { "type": "text", "text": "{... whalego-error-v1 JSON，内嵌完整 action_receipt（若涉及动作）...}" }
  ]
}
```

### 2.3 错误对象（`whalego-error-v1`）

```jsonc
{
  "schema_version": "whalego-error-v1",
  "code": "STALE_STATE",
  "message": "观察状态已过期，请先重新调用 go_observe。",
  "retry": "reobserve",                 // never | retry | reobserve
  "retry_after_ms": null,               // retry=retry 时给出建议等待
  "details": { "session_id": "wg-8f21cd04f00dbeef", "state_id": "st-8f21cd04" },
  "action_receipt": null,               // not_sent/possibly_sent 时内嵌完整回执
  "diagnostic": { "phase": "validate", "elapsed_ms": 2 }
}
```

> `message` 面向模型：**简体中文**，一句原因 + 下一步动作；不出现内部派发术语（PostMessage/Invoke/升级）；错误码保持英文。

---

## 3. 工具定义

### 3.1 总表（7 个必需）

| 工具 | 用途 |
|---|---|
| `go_health` | 健康/能力/版本/环境事实/协商结果 |
| `go_apps` | 列出可操控应用（可含窗口） |
| `go_windows` | 列出窗口 |
| `go_observe` | 唯一观察入口：UIA 树 + 可选截图，生成 `state_id`（+内部 frame） |
| `go_act` | 唯一动作通道：单次派发 → 回执（成功）或错误（失败，内嵌回执） |
| `go_launch` | 启动 allowlist 内应用 / 激活已有应用（P0；写动作，审计 fail-closed） |
| `go_stop` | 停止：模型调用只停**本会话**；全局停止由本机热键/托盘/管理员命令触发（恢复仅本机） |

### 3.2 `go_health`

参数：无。

```jsonc
{
  "schema_version": "whalego-health-v1",
  "ok": true,
  "version": "0.1.0",
  "protocol_version": 1,
  "mcp_protocol_version": "2025-06-18",
  "os": { "windows_version": "10.0.26300", "windows_session_id": 1, "interactive": true,
          "locked": false, "integrity": "medium", "dpi_awareness": "per_monitor_v2" },
  "capabilities": {
    "observation": ["uia_tree"],
    "actions": ["click","set_value","type","key","scroll"],
    "targets": ["element","coordinate"],
    "launch": ["allowlist","activate"],
    "launch_allowlist": ["notepad","calc","mspaint","explorer"]
  },
  "paused": false,
  "pause_reason": null,
  "paused_since_ms": null,
  "stopped": false,
  "session_stopped": false,
  "accepting_actions": true,
  "uptime_s": 1234,
  "sessions": 2,
  "controller": { "held": true, "mine": false, "expires_in_ms": 43000 },
  "limits": { "max_observe_elements": 300, "max_observe_chars": 20000,
              "max_image_bytes": 200000, "act_rate_per_minute": 120,
              "state_ttl_seconds": 120, "frame_ttl_seconds": 60, "tool_timeout_seconds": 40 }
}
```

- `mcp_protocol_version` 是运行时协商值（示例值仅占位）。
- `controller` 无租约时为 `null`；只给 `held/mine/expires_in_ms`，**不下发业务会话 id**（模型无从跨会话抢租约）。
- `capabilities.observation` 只在截图启用时含 `screenshot_printwindow`；`capabilities.actions` 在只读/暂停/停止时仍列出能力
  （“现在能不能动”看 `accepting_actions`，不要根据 capabilities 猜）。
- `ok` 只表示服务健康；`accepting_actions = !paused && !stopped && !session_stopped`（只读是另一维度，用 `READONLY_MODE` 表达）。

### 3.3 会话与 `session_id`

- 业务 `session_id` 由插件按 umo 派生注入：`"wg-" + sha256(umo)[:16]`（如 `wg-8f21cd04f00dbeef`），**模型不可见、不可传**；
  executor 解析顺序：内部注入参数 → MCP 连接身份 → `"default"`。
- 会话状态：当前目标窗口、当前 state/frame、观察 TTL、速率计数、控制权租约、`paused`/`stopped`/`session_stopped`。
- 会话 epoch：执行器进程重启/MCP 连接重建后 epoch 递增，旧 `state_id` 一律 `STALE_STATE`；插件重连后必须提示模型重新 observe。
- 会话空闲 10 分钟回收；MCP 连接关闭时释放该连接绑定的会话与租约。

### 3.4 `go_apps`

```json
{ "type": "object", "properties": {
    "include_windows": { "type": "boolean", "default": false }
} }
```

返回：

```jsonc
{
  "schema_version": "whalego-apps-v1",
  "apps": [
    { "app_id": "win-exe:3f6a1c9d2b04", "name": "Notepad", "pid": 1234,
      "exe": "C:/Windows/System32/notepad.exe", "aumid": null, "is_packaged": false,
      "foreground": true, "windows_count": 2 }
  ],
  "omitted_apps": 0
}
```

**app_id 规则（v1 定稿）**：

| 应用类型 | app_id | 说明 |
|---|---|---|
| 普通 Win32 | `win-exe:<sha1(规范化 exe 绝对路径小写)[:12]>`（如 `win-exe:3f6a1c9d2b04`） | 稳定、跨重启；`exe` 字段给绝对路径用于人读 |
| 打包应用（UWP/MSIX/WinUI3） | `win-aumid:<aumid>` | `ApplicationFrameHost` 不作为身份；必要时按 UIA root 的 ProcessId 解析真实应用 |
| 同一 exe 多窗口 | app_id 相同 | 用 `window_id` 区分；`go_observe(app_id)` 多窗口 → `AMBIGUOUS_APP`（提示改用 window_id） |

标题**不参与身份判定**（浏览器/记事本标题会变）；`go_apps` 返回把 `foreground=true` 的应用置前，并附“推荐下一步 = window_id”。

### 3.5 `go_windows`

```json
{ "type": "object", "properties": { "app_id": { "type": "string" } } }
```

返回：

```jsonc
{
  "schema_version": "whalego-windows-v1",
  "windows": [
    { "window_id": "w-00000000006AF2", "app_id": "win-exe:notepad.exe", "title": "无标题 - 记事本",
      "class_name": "Notepad", "pid": 1234, "onscreen": true, "minimized": false, "foreground": true,
      "enabled": true, "bounds": [100,100,900,700], "geometry_source": "dwm", "dpi": 120,
      "captured_at_ms": 1793435400000 }
  ]
}
```

> `window_id` 是不透明稳定标识（HWND + 序号）；**不作为身份信任来源**，服务端内部还会校验 `pid/class/root`。

### 3.6 `go_observe`

```json
{
  "type": "object",
  "properties": {
    "window_id": { "type": "string", "description": "目标窗口 id（与 app_id 二选一）" },
    "app_id":    { "type": "string", "description": "目标应用 id；多窗口歧义时报 AMBIGUOUS_APP" },
    "include_screenshot": { "type": "boolean", "default": false,
                            "description": "是否返回截图（坐标动作前必须做一次带截图观察）" },
    "max_value_chars": { "type": "integer", "minimum": 1, "maximum": 4000, "default": 48,
                         "description": "elements[].value 单值上限；读长文用 4000" },
    "settle_ms": { "type": "integer", "minimum": 0, "maximum": 2000, "default": 250,
                   "description": "等待 UI 结构稳定的上限；0 表示不等待" },
    "max_elements": { "type": "integer", "minimum": 1, "maximum": 2000, "default": 300 }
  },
  "anyOf": [ { "required": ["window_id"] }, { "required": ["app_id"] } ]
}
```

> `session_id` 是插件注入的内部参数（contracts 标 `"internal": true`），模型面 schema 不出现；
> 模型调用插件时插件按 umo 注入，模型无法伪造或跨会话传参。

返回 `content`：

1. 可选 `ImageContent`（PNG，**最前**）；
2. `TextContent` 首行摘要：`OK: 观察完成 window=... state_id=st-... elements=123 sparse=no screenshot=yes`
   （无图时 `screenshot=no` / `no(disabled)` / `no(suppressed)`；有图时首行**最前面**加隐私提示）；
3. `TextContent` `whalego-observe-v1` JSON 元数据；
4. `TextContent` 渲染文本树（§4）。

元数据 JSON：

```jsonc
{
  "schema_version": "whalego-observe-v1",
  "session_id": "wg-8f21cd04f00dbeef",
  "state_id": "st-8f21cd04",
  "frame": { "image_width": 800, "image_height": 600, "client_width": 800, "client_height": 600,
             "scale_x": 1.0, "scale_y": 1.0, "capture_region": "client",
             "actionable": true, "capture_method": "printwindow",
             "signature": "nonblank_ok", "captured_at_ms": 1793435400123 },
  "target": {
    "app":    { "app_id": "win-exe:3f6a1c9d2b04", "pid": 1234, "name": "Notepad" },
    "window": { "window_id": "w-00000000006AF2", "title": "无标题 - 记事本",
                "onscreen": true, "foreground": true, "bounds": [100,100,900,700],
                "dpi": 120, "geometry_source": "dwm" },
    "resolution": "exact"
  },
  "elements": [
    { "index": 3, "kind": "edit", "title": "文本编辑器", "value": "hello", "value_truncated": false,
      "actions": ["set_value","scroll"], "enabled": true, "selected": false,
      "automation_id": "15", "bounds_screen_diag": [120,140,860,680], "depth": 2 }
  ],
  "omitted": { "count": 42, "by_kind": {"container": 30, "text": 12}, "reason": "budget", "sparse_indices": true },
  "advisories": ["tree_budget_exceeded"],
  "captured_at_ms": 1793435400123
}
```

**advisories 闭集（v1）**：`tree_budget_exceeded`、`screenshot_failed`、`capture_degraded`、`not_settled`、
`main_fallback`、`uia_unavailable`、`screenshot_disabled`、`screenshot_suppressed`。新增值必须升协议版本。

**截图语义（三开关 AND）**：执行器 `screenshot.enabled` 且（模型 `include_screenshot=true` 或执行器
`screenshot.defaultInclude=true`）且插件 `allow_screenshot=true`，才返回图片。
- 执行器未启用截图 / 插件不允许：`go_observe` 仍返回树，`advisories=["screenshot_disabled"]`，首行 `screenshot=no(disabled)`；
- 敏感窗口命中（`privacy.sensitiveWindows`）：返回树，不返回图片，`advisories=["screenshot_suppressed"]`（坐标动作随后 `FRAME_NOT_ACTIONABLE`）；
- **自身进程窗口**（`WhaleGo.*`，包括设置窗口/诊断页）永不枚举、不观察、不截图。

**frame 判定（v1）**：采集成功 且 窗口未最小化 且 窗口几何/DPI 与采集时一致 且 年龄 ≤ `frame_ttl_seconds`
且 截图通过非空/非纯黑校验 且 采集方法在已验证宿主清单内 → `actionable=true`；否则 `actionable=false` + `capture_degraded`
（PrintWindow 对未知 GPU/Chromium 宿主默认判不可动作）。截图只截**客户区**。

**frame 生命周期**：同一窗口的“无截图 observe”**不清除**既有 frame（仍校验 TTL/几何/DPI/actionable）；
切换窗口、任何 `go_act` 进入派发、`go_stop`/暂停 → frame 失效。

**读全文**：`max_value_chars` 控制 `elements[].value` 上限（默认 48），文本树仍固定 48；
`value_truncated` 标注单值是否被截断；总预算受 `max_observe_chars` 约束，超出给 `tree_budget_exceeded`。

> `elements[].bounds_screen_diag` 是**诊断信息**（屏幕坐标），**禁止**当作动作坐标；元素动作只认 `index`。
> 坐标动作只认 `frame` 的图片像素（`0,0` = 图片左上角）；**frame_id 只存在于服务端内部**（日志/诊断），
> 不下发模型，模型也不回传。

### 3.7 `go_act`（v1：单动作通道 + oneOf）

```json
{
  "type": "object",
  "required": ["op"],
  "properties": {
    "op": { "type": "string", "enum": ["click","set_value","type","key","scroll"] },
    "target": {
      "oneOf": [
        { "type": "object", "required": ["kind","index","state_id"],
          "properties": { "kind": {"const":"element"}, "index": {"type":"integer","minimum":0},
                          "state_id": {"type":"string","pattern":"^st-[0-9a-f]{8}$"} },
          "additionalProperties": false },
        { "type": "object", "required": ["kind","x","y"],
          "properties": { "kind": {"const":"coordinate"}, "x": {"type":"integer","minimum":0},
                          "y": {"type":"integer","minimum":0} },
          "additionalProperties": false }
      ]
    },
    "args":  { "type": "object", "description": "按 op 定义见下表（服务端逐 op 校验）" }
  },
  "allOf": [
    { "if": {"properties":{"op":{"const":"click"}}}, "then": {"required":["target"],
      "properties":{"args":{"type":"object","required":["button","count"],
        "properties":{
          "button":{"type":"string","enum":["left"]},
          "count":{"type":"integer","enum":[1]},
          "modifiers":{"type":"string","enum":[""]}},
        "additionalProperties": false}}} },
    { "if": {"properties":{"op":{"const":"set_value"}}}, "then": {"required":["target"],
      "properties":{"args":{"type":"object","required":["value"],
        "properties":{"value":{"type":"string","maxLength":4000}}, "additionalProperties": false}}} },
    { "if": {"properties":{"op":{"const":"type"}}}, "then": {"required":["target","args"],
      "properties":{"target":{"type":"object","required":["kind"],"properties":{"kind":{"const":"element"}}},
        "args":{"type":"object","required":["text"],
        "properties":{"text":{"type":"string","maxLength":1500}}, "additionalProperties": false}}} },
    { "if": {"properties":{"op":{"const":"key"}}}, "then": {"required":["target","args"],
      "properties":{"target":{"type":"object","required":["kind"],"properties":{"kind":{"const":"element"}}},
        "args":{"type":"object","required":["keys"],
        "properties":{"keys":{"type":"string","enum":["enter","tab","escape","backspace","delete","up","down","left","right","pageup","pagedown","home","end","space"]}},
        "additionalProperties": false}}} },
    { "if": {"properties":{"op":{"const":"scroll"}}}, "then": {"required":["target"],
      "properties":{"args":{"type":"object","required":["direction"],
        "properties":{"direction":{"type":"string","enum":["up","down"]},
                      "pages":{"type":"number","minimum":0.25,"maximum":20,"default":1}},
        "additionalProperties": false}}} }
  ]
}
```

> `session_id`/`request_id` 是插件注入的内部参数（contracts 标 `internal`），模型面 schema 不出现；
> executor 仍接受并用于会话绑定/日志关联。

**v1 能力边界（重要）**：`click` 仅 `left + 单击`；`key` 仅单键（组合键/修饰键返回 `UNSUPPORTED_ACTION`）；
`scroll` 仅上下、页码 0.25..20；`type` 不含组合键；无 `drag/paste/perform_action/select_text/双击/右键`（P2）。
**所有 op 必须带 `target`**；`type`/`key` 只接受 `kind=element`（v1 不向“当前焦点窗口”盲发按键；无 target → `INVALID_ARGUMENT`）。
坐标目标：使用会话**最近一次带截图观察**绑定的 frame（无截图 observe 不清除同窗口既有 frame）；过期/不可动作
→ `STALE_STATE`/`FRAME_NOT_ACTIONABLE`；坐标是**图片像素**（换算见 §6）。
元素目标：使用会话**当前 state**；动作后 state 立即失效。element 路径需要目标前台而无法安全激活 → `FOREGROUND_REQUIRED`（零发送）。

成功返回（仅 accepted）：

```jsonc
{
  "schema_version": "whalego-act-v1",
  "session_id": "wg-8f21cd04f00dbeef",
  "request_id": "req-01H…",
  "op": "click",
  "action_receipt": {
    "action_sent": true,
    "dispatch_status": "accepted",
    "retry_action": false,
    "target_kind": "element",
    "escalated": false,
    "verification": { "status": "unverified", "reason": "click_no_readback" },
    "state_available": false
  }
}
```

失败返回（`not_sent`/`possibly_sent` 一律 `isError=true`）：

```jsonc
{
  "schema_version": "whalego-error-v1",
  "code": "DISPATCH_UNCERTAIN",
  "message": "动作可能已发送，禁止重试；请先重新观察。",
  "retry": "reobserve",
  "retry_after_ms": null,
  "details": { "session_id": "wg-8f21cd04f00dbeef" },
  "action_receipt": {
    "action_sent": true,
    "dispatch_status": "possibly_sent",
    "retry_action": false,
    "target_kind": "coordinate",
    "escalated": true,
    "verification": { "status": "unverified", "reason": "timeout_after_dispatch" },
    "state_available": false
  },
  "diagnostic": { "phase": "dispatch", "elapsed_ms": 5012 }
}
```

**调用方规则（插件/模型）**：

| 场景 | 模型可见文本 | 允许行为 |
|---|---|---|
| `isError=false, accepted` | `OK: 动作已发送；state 已失效，下一步请先 go_observe` | 可再 observe 验证；不重放 |
| `isError=true, not_sent` | `ERROR[...]: 动作未发送...` | 按 `retry`（retry/reobserve/never） |
| `isError=true, possibly_sent` | `ERROR[..]: 动作可能已发送，禁止重试，请先 observe` | **仅 reobserve** |
| `isError=true, PAUSED` | `ERROR[PAUSED]: 用户已在本机暂停，等待恢复` | 不重试，提示用户到 Windows 恢复 |
| `isError=true, SESSION_STOPPED` | `ERROR[SESSION_STOPPED]: 已停止，需本机恢复` | 不重试，提示用户到 Windows 托盘恢复 |
| `isError=true, READONLY_MODE` | `ERROR[READONLY_MODE]: 当前只读` | 不重试，请用户关闭只读 |
| `isError=true, 参数/状态错误` | `ERROR[...]: ...` | 按 `retry` |

**verification 契约（v1）**：`verification.status ∈ {verified, mismatched, unverified}`：
- `set_value`（元素）：ValuePattern 读回 == 请求值 → `verified`，否则 `mismatched`；
- `type`（元素）：ValuePattern/TextPattern 读回包含请求文本 → `verified`，否则 `mismatched`；
- `click`/`key`/`scroll`：无读回 → `unverified`。

`mismatched` 只说明“写后读不一致”，**不改变 `dispatch_status=accepted`**；调用方只能重新观察，**不得自动重放**。

### 3.8 `go_launch`

```json
{
  "type": "object",
  "properties": {
    "app":    { "type": "string", "description": "启动 allowlist 内应用（go_health.capabilities.launch_allowlist 可查）" },
    "app_id": { "type": "string", "description": "激活已有应用的主窗口" },
    "wait_ms":{ "type": "integer", "minimum": 0, "maximum": 10000, "default": 5000,
                "description": "等待主窗口出现并稳定的上限" }
  },
  "oneOf": [ { "required": ["app"] }, { "required": ["app_id"] } ]
}
```

- **安全边界**：模型只能按 allowlist 名称启动（配置 `launch.allowlist`，默认 notepad/calc/mspaint/explorer；
  GUI 可编辑）；**不接受 PATH 名/相对路径/arguments**（P2 再扩模型面）。
- **写动作**：审计 fail-closed（返回前落盘）；需要激活已有窗口时获取 controller lease。

返回：

```jsonc
{
  "schema_version": "whalego-launch-v1",
  "launched": true,
  "activated": false,
  "app": { "app_id": "win-exe:3f6a1c9d2b04", "pid": 4321 },
  "window": { "window_id": "w-00000000009AF1", "title": "无标题 - 记事本", "onscreen": true },
  "settled": true
}
```

错误：`APP_NOT_FOUND`、`AMBIGUOUS_APP`、`LAUNCH_FAILED`、`LAUNCH_TIMEOUT`。
**`LAUNCH_TIMEOUT` 一律 `retry=never`**：进程可能已启动，禁止重试，请用 `go_apps` 确认。**P0 交付**。

### 3.9 `go_stop`

模型面参数：

```json
{ "type": "object", "properties": {
    "reason": { "type": "string", "maxLength": 200 }
} }
```

> `scope`/`session_id` 是插件注入的内部参数，模型面不出现（模型调用时 scope 固定为 `session`）。

返回：`{ "schema_version": "whalego-stop-v1", "ok": true, "stopped": true, "scope": "session", "sessions_affected": 1 }`

语义：
- **模型调用只停本会话**：该会话后续 `go_act`/`go_launch` → `SESSION_STOPPED`；取消排队动作、释放租约；
  `go_observe`/`go_health` 仍可用。**模型无法恢复**（不存在 resume 工具）。
- **全局停止（scope=all）** 只能由本机热键/托盘「紧急停止」或管理员命令触发；全局停止**持久化**到
  `%LOCALAPPDATA%\WhaleGo\control-state.json`，重启后仍停止，必须**本机人工恢复**（二次确认）。
- 暂停/停止都会清空租约、取消排队动作；在途派发不撤销（走 `possibly_sent`）。
- 状态对模型可见：`go_health.paused/stopped/session_stopped/accepting_actions`。

---

## 4. 文本树渲染格式

- 每行：`{缩进}[{index}] {kind} "{title}"{ value="..."}{ flags}`；缩进 2 空格 × depth；
- **转义**：title/value 内的 `"`、`\`、换行、制表、控制字符按 JSON 风格转义（`\"`、`\\`、`\n`、`\t`、`\uXXXX`）；
- **截断按 Unicode 码点**（不拆代理对/emoji）：title 64、value 48；完整值走 `elements[].value` + `max_value_chars`；
- 密码控件（UIA IsPassword）value 恒为 `***`；
- flags：`actions=[invoke,set_value]`、`disabled`、`selected`；不输出 bounds（诊断字段在 JSON 里叫 `bounds_screen_diag`）；
- 裁剪提示行：`… (omitted N elements: container=30, text=12)`；
- 末行窗口摘要：`window w-... "title" app=... dpi=120 onscreen=true frame=800x600`；
- 首行固定：`OK: 观察完成 window=... state_id=st-... elements=123 sparse=no screenshot=no`；有图时**首行最前面**附隐私提示。

```text
OK: 观察完成 window=w-00000000006AF2 state_id=st-8f21cd04 elements=123 sparse=no screenshot=no
[window w-00000000006AF2] "无标题 - 记事本" app=win-exe:3f6a1c9d2b04 dpi=120 onscreen=true
  [0] window "无标题 - 记事本"
    [2] edit "文本编辑器" value="hello" actions=[set_value,scroll]
    [3] button "关闭" actions=[invoke]
… (omitted 42 elements: container=30, text=12)
```

---

## 5. 状态、新鲜度与控制权

### 5.1 会话状态

`GoSession` = { `current_window_id`, `state_id + elements`, `frame`, `observed_at`, `input_lease`, `rate`,
`paused`, `stopped`, `session_stopped`, `epoch` }。
会话**只保留最近一个窗口的 state/frame**；切换窗口即丢弃旧窗口的 state/frame。

### 5.2 新鲜度规则（v1 定稿）

| 事件 | 元素 state | frame |
|---|---|---|
| 新 `go_observe`（同一窗口，带截图） | 顶替 | 顶替 |
| 新 `go_observe`（同一窗口，不带截图） | 顶替 | **保留**（仍校验 TTL/几何/DPI/actionable） |
| 新 `go_observe`（其它窗口） | 丢弃旧窗口 state | 丢弃旧窗口 frame |
| 任何 `go_act` 进入派发（accepted/possibly_sent） | **立即失效** | **立即失效** |
| `go_act` 确定 not_sent（如 UIPI 拒绝） | 保持有效 | 保持有效 |
| state TTL（默认 120s） | 失效 | — |
| frame TTL（默认 60s） | — | 失效 |
| 窗口销毁/回收（HWND 或 owner pid/class 变化） | 失效 | 失效 |
| 窗口几何/DPI 变化 | 保持（元素动作不依赖几何） | 失效 |
| 窗口标题变化 | **不失效**（标题不是身份） | 不失效 |
| 会话切换/`go_stop`/暂停/全局停止 | 失效 | 失效 |
| 执行器重启/连接重建（epoch+1） | 失效 | 失效 |

### 5.3 控制权租约（多会话）

- 同一台桌面同一时刻只允许一个会话的**输入动作**（click/type/key/scroll 与需要激活窗口的 launch）执行；观察可并发。
- 获取：动作**进入派发前**获取 `controller lease`（TTL 60s，每次动作续期）；同一调用内最多等待
  `policy.controllerWaitMs`（默认 2000ms）。非持有者 → `CONTROLLER_BUSY`（`retry=retry`，
  `retry_after_ms = min(剩余租约, 5000)`，`controller.mine=false`）；**不使 state/frame 失效**。
- 释放：会话关闭/空闲回收、`go_stop`、暂停、全局停止、执行器退出。
- `go_health.controller` 暴露 `{held, mine, expires_in_ms}`（**不下发**持有者会话 id）。

---

## 6. 坐标与命中测试

- 坐标 = **最近一次带截图观察返回图片的像素**（左上原点）。模型只传 `x,y`，不传 frame_id/DPR/屏幕偏移。
- 截图只截**客户区**；`frame` 含 `image_width/image_height/client_width/client_height/scale_x/scale_y`。
  内部换算：`client_x = round(image_x * client_width / image_width)`（y 同理），再按窗口客户区原点换算到屏幕物理像素。
- 服务端内部：frame 绑定 `{window_id, geometry, dpi, captured_at, capture_method, actionable}`；派发前校验新鲜度与
  `actionable`；换算到屏幕物理像素（多屏/负坐标 P2 再验证，v1 单屏）。
- **命中测试（v1 必做）**：坐标动作先做 UIA `ElementFromPoint`（主级）+ 窗口域枚举（次级），
  结果用于**选择派发路径**（宿主分类/丢弃矩阵），不是把坐标动作改写成元素动作：
  `hit-test → 宿主分类 → PostMessage（可投）→ 前台 SendInput（需前台）`。
- 出界 → `INVALID_ARGUMENT`；frame 不可动作 → `FRAME_NOT_ACTIONABLE`；坐标落在窗口非客户区/标题栏 →
  `UNSUPPORTED_ACTION`（v1 不支持非客户区拖拽/点击）。

---

## 7. 超时、取消与重试（v1 定稿）

| 阶段 | 取消语义 |
|---|---|
| 派发前（校验/排队/等待租约） | 可取消 → `not_sent` |
| 原生调用进行中（UIA COM / PostMessage / SendInput） | **不可撤销**：让调用完成或超时；结果不确定 → `possibly_sent` |
| 已返回 accepted/possibly_sent | 不可回收；只能重新观察 |

**超时链（必须单调递增）**：执行器单工具内部预算 **≤40s**（observe 树 2s + 截图 3s；act 5s；launch 等待 ≤10s）
< 插件调用超时（默认 **45s**，从 AstrBot `provider_settings.tool_call_timeout` 读取并 clamp 到 ≤`tool_call_timeout-5s`）
< AstrBot 工具调用超时（默认 120s）。插件启动自检必须校验“插件 timeout > 执行器预算 + 5s”。
- 插件超时/断线：
  - **动作类**调用超时或连接断开 → 按 `DISPATCH_UNCERTAIN` 呈现（“可能已发送，禁止重试，先 go_observe”）；
  - **只读类**调用超时 → 重连后重试一次，仍失败返回插件侧 `EXECUTOR_TIMEOUT`；
- 插件超时后**不重发动作**、连接标记 unclean、下次调用前重连。
- `retry=retry` 仅允许出现在 `not_sent`；`possibly_sent` 一律 `reobserve` 且 `retry_action=false`（服务端强制）。

---

## 8. 错误码表（v1 唯一事实源）

| code | 场景 | retry | dispatch_status |
|---|---|---|---|
| `INVALID_ARGUMENT` | schema/参数/坐标越界/枚举非法 | never | not_sent |
| `UNSUPPORTED_PROTOCOL` | protocol_version 不匹配 | never | not_sent |
| `UNSUPPORTED_ACTION` | v1 未实现的动作/组合（组合键、右键、双击、拖拽等） | never | not_sent |
| `SESSION_NOT_FOUND` / `SESSION_EXPIRED` | 会话不存在/过期 | never | not_sent |
| `SESSION_STOPPED` | 本会话/全局停止生效（需本机恢复） | never | not_sent |
| `PAUSED` | 用户在本机暂停（GUI/托盘；恢复仅本机） | never | not_sent |
| `SESSION_LOCKED` | Windows 锁屏/安全桌面，无法安全操作 | never（解锁后） | not_sent |
| `CONTROLLER_BUSY` | 其它会话持有输入控制权 | retry（`retry_after_ms`） | not_sent |
| `READONLY_MODE` | 只读模式（配置），动作被禁用 | never | not_sent |
| `RATE_LIMITED` | 超速率 | retry（`retry_after_ms`） | not_sent |
| `AUDIT_UNAVAILABLE` | 写动作审计无法落盘（fail-closed） | retry | not_sent |
| `STALE_STATE` | state/frame 过期/不匹配/动作后未重观察 | reobserve | not_sent |
| `FRAME_NOT_ACTIONABLE` | frame 不可动作（最小化/黑帧/宿主未验证） | reobserve | not_sent |
| `ELEMENT_UNAVAILABLE` | 元素消失/不可动作 | reobserve | not_sent |
| `ELEMENT_ACTION_UNAVAILABLE` | 元素不支持请求的 UIA 动作 | reobserve | not_sent |
| `WINDOW_NOT_FOUND` / `WINDOW_CHANGED` | 窗口不存在/身份变化 | reobserve | not_sent |
| `APP_NOT_FOUND` / `AMBIGUOUS_APP` | 应用不存在/歧义 | reobserve / never | not_sent |
| `LAUNCH_FAILED` / `LAUNCH_TIMEOUT` | 启动失败/窗口未出现（进程可能已启动） | never / never（禁止重试） | not_sent |
| `FOREGROUND_REQUIRED` | 前台路径需要目标前台但无法安全激活 | reobserve | not_sent |
| `UIPI_BLOCKED` | 目标完整性级别更高 | never | not_sent |
| `ACTION_TIMEOUT` | 动作超时且**未进入派发** | retry | not_sent |
| `DISPATCH_UNCERTAIN` | 原生调用后异常/超时 | reobserve | **possibly_sent** |
| `INTERNAL` | 未预期异常（可能发生在派发前或派发后） | never | not_sent / possibly_sent |

### 8.1 插件侧错误码（非执行器契约）

插件在连接层产生、执行器不会返回；模型可见文本仍为 `ERROR[<code>]: ...`，映射在插件 `convert.py`。

| code | 场景 | retry |
|---|---|---|
| `EXECUTOR_UNREACHABLE` | 连不上执行器（未启动/防火墙/地址错） | retry |
| `EXECUTOR_TIMEOUT` | 只读类调用超时（动作类按 `DISPATCH_UNCERTAIN` 呈现） | reobserve |
| `AUTH_FAILED` | 401（token 错误/已被轮换） | never |
| `PROTOCOL_MISMATCH` | `protocol_version` 不兼容 | never |
| `CONNECTION_LOST` | 调用中断线（动作类按 `possibly_sent` 呈现） | reobserve |

---

## 9. 版本与兼容

- `protocol_version = 1`（整数，go_health 暴露）；载荷各自带 `schema_version`（`whalego-*-v1`）。
- 新增可选字段/枚举值 → 不升主版本；删除字段/改语义/改工具名 → `protocol_version + 1`，服务端拒绝旧版。
- 插件启动时检查 `go_health.protocol_version`；不一致报明确错误，不静默降级。
- 实例：v1.2 新增的 `go_health.paused/pause_reason/paused_since_ms/stopped/session_stopped/accepting_actions`、
  `go_observe.max_value_chars`、`advisories` 新值（`uia_unavailable`/`screenshot_disabled`/`screenshot_suppressed`）、
  `PAUSED`/`SESSION_LOCKED` 错误码、`frame` 新增像素映射字段，均属“新增可选字段/枚举值”，**不升主版本**。
- 本文件 v1.2 对应 `contract_version = "1.2"`（`contracts/whalego.tools.v1.json`）。

---

## 10. AstrBot 内置 MCP 兼容（备选路径）

```json
{ "mcpServers": { "whalego": {
    "url": "http://host.docker.internal:8765/mcp",
    "transport": "streamable_http",
    "headers": { "Authorization": "Bearer <TOKEN>" },
    "timeout": 60
} } }
```

注意（已核实）：AstrBot 不处理 `isError`、工具名会被 sanitize 且无前缀、非内置工具默认权限是 `member`、
图片仅在 provider 支持 image 时注入。因此备选路径下模型看到的只是原始文本 JSON，**不推荐**；插件是主路径。

---

## 11. 隐私与脱敏规则（day-1）

1. UIA `IsPassword` 控件的 `value` 一律 `***`；`type/set_value` 的输入文本不写日志（只记长度+哈希）。
2. 敏感窗口黑名单（配置 `privacy.sensitiveWindows`，wire 名 `privacy.sensitive_windows`）：命中时 `go_observe` 返回树，
   **不返回任何截图**，`advisories=["screenshot_suppressed"]`；坐标动作 → `FRAME_NOT_ACTIONABLE`。
3. **截图默认开启**（执行器 `screenshot.enabled=true`、插件 `allow_screenshot=true`，用户拍板）；三开关 **AND**：
   执行器 `screenshot.enabled` 且（模型 `include_screenshot=true` 或执行器 `screenshot.defaultInclude=true`）且插件
   `allow_screenshot=true`；任一关闭即无图。默认不自动附图（`defaultInclude=false`），模型按需请求。
4. **自身进程窗口（`WhaleGo.*`，包括设置/诊断窗口）永不枚举、不观察、不截图**；GUI 中 token 只掩码显示，
   复制到剪贴板后 60s 条件清空。
5. 模型可能被 AstrBot 提示“可把图片发给用户”：插件把隐私提示放在 **OK 摘要行最前面**，
   文案固定：`注意：桌面截图包含用户隐私，未经用户明确要求不得转发。`
6. 审计不含：输入明文、剪贴板内容、截图字节、token；token 明文只存 `%LOCALAPPDATA%\WhaleGo\secrets.json`（当前用户 ACL）。

---

## 12. 模型使用规则（插件注入，v1）

插件通过 `on_llm_request` 把以下规则作为固定说明注入（内容与本节同步、单测锁定）；tool description 只放最硬规则：

1. 先 `go_apps`/`go_windows` 找目标，不要猜 `app_id`/`window_id`；标题只用于人读。
2. 动作前必须 `go_observe`；元素动作用 `state_id+index`，坐标动作用“最近一次带截图观察的图片像素”。
3. **任何动作后 state/frame 失效**；下一步先 `go_observe`，不要复用旧 `state_id`。
4. `OK:` = 动作已发送（不等于生效）；`ERROR[...]` = 失败，按 `retry` 提示处理。
5. “动作可能已发送”（`possibly_sent`）时**禁止重试**，只能重新观察。
6. `CONTROLLER_BUSY` 按 `retry_after_ms` 等待后重试；不要连续重复同一调用（AstrBot 会追加重复提示）。
7. v1 不支持组合键（Ctrl+S 等）、右键、双击、拖拽、粘贴；保存类任务只能点击保存控件/菜单，没有可点控件就如实说“做不到”。
8. 截图默认可用（含隐私提示）：未经用户明确要求不得转发；需要看图/坐标操作时传 `include_screenshot=true`；
   模型看不到图（provider 不支持 image）时不要做坐标动作。
9. 用户暂停/停止后（`PAUSED`/`SESSION_STOPPED`）不要重试，提示用户到 Windows 托盘恢复。
10. 读长文用 `go_observe(max_value_chars=…, max_elements=…)`；树被裁剪/跳号是正常现象。
