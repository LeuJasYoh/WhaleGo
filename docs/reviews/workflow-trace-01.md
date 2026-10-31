# WhaleGo 端到端场景走查 01（规格缺口清单）

> 走查对象：`docs/DESIGN.md`（788 行；标题仍写 v0.2，而 README/reviews 称 v0.3，属文档版本口径不一致，见 §10.5 R-5）、`docs/ARCHITECTURE.md`（896 行）、`docs/PROTOCOL.md`（协议 v1.1，560 行）、`contracts/whalego.tools.v1.json`（588 行）、`docs/NAMING.md`（196 行）、`docs/reviews/design-review-01*.md`；AstrBot 4.29.3 参考源码 `ref/AstrBot/`。
> 方法：把上述文档当作实现规格，沿“用户一句话 → AstrBot 模型 → 插件 → MCP → 执行器 → 桌面”逐场景推演。只认文档写下的规则；文档没写、或两处写法不一致的地方，记为“实现者只能猜 / 模型实际做不到”。
> 边界：本机尚无执行器与插件实现，本次只读文档与源码，不跑 E2E；结论按“P0+P1 实现完成后，文档承诺的验收能否达成”判断。
> 结论：**架构可以进入实现（P0 骨架可开工），但按当前文档，P0 的“读记事本正文”与 P1 的“输入并保存”两个验收承诺达不成；另有 8 项最严重缺口（§0.1）必须在动工前逐条定性，否则两端实现会各自猜一套语义。** 逐条落点见 §10。

---

## 0. 结论速览

### 0.1 最严重的 8 个缺口

| # | 缺口 | 影响阶段 | 主要证据 |
|---|------|---------|---------|
| 1 | 观察结果只保证 48 字符的 `value`，没有“读全文”的参数或工具 | P0 | PROTOCOL §4:424；ARCHITECTURE §3.2:184 |
| 2 | Ctrl+S 在 v1 明确 `UNSUPPORTED_ACTION`，但没有给出“保存”的可执行替代路径 | P1 | PROTOCOL:317-318；DESIGN:354/554/751 |
| 3 | 截图缩放后“图片像素 → 窗口物理像素”的换算没定义（`frame.width/height` 语义有歧义、无 scale 字段） | P1 | PROTOCOL:6/229-236/473；DESIGN:324/386 |
| 4 | `type`/`key` 的目标窗口与焦点契约缺失（v1 没有“激活指定窗口”的动作，只有坐标路径提“前台”） | P1 | PROTOCOL:301-307；ARCHITECTURE:463-469 |
| 5 | 弹窗/菜单/保存对话框落在“单窗观察 + 无 owner 字段”的窗口模型之外 | P1 | PROTOCOL §3.5:179-199；§3.6:201-218 |
| 6 | 超时链数值倒置：插件 45s < 执行器整工具 60s；插件侧超时/连接错误没有错误码与模型文案 | P1 | DESIGN:501；PROTOCOL:492-495；ARCHITECTURE:738-741 |
| 7 | 控制器租约没有主动释放、`retry_after_ms` 计算、自身归属查询；“全局/session 级”措辞冲突 | P1 | PROTOCOL:462-467；DESIGN:360；ARCHITECTURE:568 |
| 8 | 弱 UIA/GPU 窗口的 frame 默认 `actionable=false`，而能救它的 WGC 排在 P2——坐标回退在最需要它的宿主上系统性不可用 | P1 | PROTOCOL:258-260；DESIGN:323-325/692 |

### 0.2 是否可进入实现

- **P0 骨架**（health/apps/windows/observe/launch/stop + 认证 + kill switch）：**可以开工**。但 P0 验收里“观察记事本、看到正文”（DESIGN §10.3:690、§11:711-712）需要先补 §10 清单第 1 条，否则只能验收“看到前 48 字”。
- **P1 动作闭环**（click/set_value/type/key/scroll + 租约 + 审计）：**有条件进入**。先补 §0.1 的 3/4/5/6/7（坐标换算、焦点、弹窗、超时、租约），否则两端会各写一套“猜的”语义，集成时必然互相冲突。
- **“在记事本里输入‘你好世界’并保存”这个用户级验收**：按 v1 当前契约**无法保证**。要么降级为“输入成功，保存由用户完成”，要么把弹窗/菜单/保存按钮规格补进 P1，并用真实记事本做冒烟。
- 另外，本文发现的所有“模型应该……”规则目前只写在设计文档里，**模型看不到**（见 MX-13）；要么落进 tool description，要么用插件 `on_llm_request` 注入，否则写再多也只约束实现者、不约束模型。

---

## 1. 走查方法、基线与代号

### 1.1 前提假设

1. 插件按 DESIGN §6 / ARCHITECTURE §4 实现：子类化 `FunctionTool`、`handler=None`、每个 umo 派生 `session_id` 并注入；AstrBot 4.29.3 不处理 `isError`。
2. 模型能看到的只有 7 个工具的名字/schema/description 与工具返回文本；看不到 DESIGN/ARCHITECTURE/PROTOCOL。
3. 模型有视觉能力时能看到 `go_observe(include_screenshot=true)` 的截图（AstrBot 在 provider modalities 支持 image 时于下一步注入，`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:1109-1142`）；文本模型看不到图。
4. 用户示例中的“记事本”按 Windows 11 自带 Notepad 理解（打包应用，窗口类/进程身份可能不是直觉的 `notepad.exe`）。

### 1.2 缺口分级

| 级别 | 定义 |
|---|---|
| **P0-blocker** | 不补就无法完成 P0 验收，或实现者只能发明语义 |
| **P1-blocker** | 不补就无法完成 P1 验收（尤其 §10.3 P1 与 §11 的验收项） |
| **major** | 不补会稳定产生“模型误用/实现分歧”，但可先按本文建议实现 |
| **minor** | 一致性问题，随手可改 |

### 1.3 文件行号约定与版本口径

> 版本口径：`docs/DESIGN.md:1` 标题仍写 v0.2，`README.md:3` 与 `docs/reviews/design-review-01-response.md:4` 称 DESIGN/ARCHITECTURE v0.3、PROTOCOL v1.1；本文按工作区文件实际内容走查，版本号不影响缺口结论，但建议开工前统一（§10.5 R-5）。

行号对应当前工作区版本：PROTOCOL 560 行、DESIGN 788 行、ARCHITECTURE 896 行、契约 588 行。AstrBot 证据路径相对 `ref/AstrBot/`。

---

## 2. S1（P0）观察闭环：“看看记事本里写了什么”

### 2.1 期望 tool 调用序列

**A. 记事本未打开（cold start）**

```jsonc
// 1) 启动（模型不知道 exe 是否在 PATH；notepad.exe 在 Windows 的 PATH 内）
go_launch  {"exe_path":"notepad.exe","wait_ms":5000}
// → OK: launched=true settled=true app={app_id:"...",pid:4321} window={window_id:"w-00000000009AF1",...}

// 2) 观察目标窗口（launch 返回的 window_id 直接用；坐标动作才需要 include_screenshot=true）
go_observe {"window_id":"w-00000000009AF1","include_screenshot":false,"settle_ms":250,"max_elements":300}
// → OK: 观察完成 window=... state_id=st-xxxxxxxx elements=N screenshot=no
//   text: "OK: ..." + whalego-observe-v1 JSON + 文本树（[2] edit "文本编辑器" value="..."）
```

**B. 记事本已打开（warm start）**

```jsonc
go_apps    {}
// → apps[] 里找 Notepad 的 app_id（可能是 win-exe:notepad.exe，也可能是 win-aumid:...）

go_windows {"app_id":"win-exe:notepad.exe"}
// → windows[] 里按窗口标题/前台标志选 window_id

go_observe {"window_id":"w-...","include_screenshot":false,"settle_ms":250,"max_elements":300}
```

**C. 需要看图（弱 UIA 或想“看”一下）**

```jsonc
go_observe {"window_id":"w-...","include_screenshot":true,"settle_ms":250,"max_elements":300}
// → [ImageContent, "OK: ...", observe JSON, 文本树]
```

### 2.2 文档依据

| 步骤 | 文档规定 | 依据 |
|---|---|---|
| 启动 | `go_launch` 启动 exe / 激活已有应用，有界等待主窗口 | PROTOCOL §3.8:374-402；DESIGN §7.1:548 |
| 观察入口 | `go_observe` 是唯一观察入口，返回 `state_id` + elements + 树 + 可选图 | PROTOCOL §3.6:201-264；DESIGN §4.2:195-201 |
| 返回格式 | content 固定顺序：Image（最前）、OK 摘要、元数据 JSON、文本树 | PROTOCOL §2.1:49-63、§3.6:220-225；DESIGN §7.3:557-561 |
| 文本树 | 每行 `[index] kind "title" value="..."`；title 截 64、**value 截 48**；密码 `***` | PROTOCOL §4:421-427 |
| 模型看到的数据 | `elements[]` 同时返回结构化表；`bounds` 仅诊断，禁止当坐标 | PROTOCOL §3.6:244-248、:262-264；ARCHITECTURE §3.2:180-190 |
| 预算 | observe 默认 ≤300 元素、≤20k 字符；硬上限 2000 节点/2s | PROTOCOL §3.2:135-137、§3.6:213；DESIGN §5.4:322；ARCHITECTURE §3.4:380 |
| 新鲜度 | 返回 `state_id`；动作后失效；同窗口新观察顶替 | PROTOCOL §5.1-5.2:442-460 |

### 2.3 缺口 / 冲突 / 风险

**S1-1（P0-blocker）观察结果只保证 48 字符的正文，模型无法“读出记事本内容”**
- 证据：PROTOCOL §4:424 `value 截断 48 字符`；ARCHITECTURE §3.2:184 `Value // ValuePattern/TextPattern 提取（截断）`；PROTOCOL §3.6:244-248 的 `elements[]` 示例只展示 `value:"hello"`，从未声明 JSON 里的 `value` 是全文还是 48 字截断；7 个工具里没有读取全文的接口（PROTOCOL §3.1:102-112）。
- 影响：一条超过 48 字的笔记，P0 的“看看写了什么”只能答出前 48 字；实现者也会在两处自相矛盾的信号中猜（文本树明确截断，JSON 未写）。这是**用户需求的直接落空**，不是边界情况。
- 建议落点：PROTOCOL §3.6 元数据定义 + §4 渲染规则；DESIGN §5.4 观察引擎；contracts `go_observe` 说明；DESIGN §10.3/§11 验收。
- 建议补的规格文本：
  > `elements[].value` 返回完整文本（单元素上限 4000 字符、总字符预算 `max_observe_chars`）；超过时置 `value_truncated:true` 并把 `omitted.value_chars` 计入 `omitted`。文本树只渲染前 48 字符（保留现有格式），并追加 `… (value truncated, N chars omitted)` 提示。模型要读全文时应使用 `elements[].value`，不要依赖文本树。
  > （二选一：或新增 `go_observe` 参数 `detail:"tree"|"text"`，`text` 时把目标元素的完整 value 作为独立 TextContent 返回，且总预算仍受限。）

**S1-2（P0-major）启动→观察的交接没有“唯一推荐路径”，`app_id` 的三种形态把选择丢给模型**
- 证据：`go_launch` 返回 `window_id`（PROTOCOL §3.8:391-399）；但 `go_observe` 要求 `window_id/app_id` 二选一（PROTOCOL §3.6:203-217；contracts:94-105）；`app_id` 规则是 `win-exe:<basename>`、冲突加 `#2`、打包应用 `win-aumid:`（PROTOCOL §3.4:169-177），而 Windows 11 的 Notepad 恰好是打包应用——契约示例却写死 `win-exe:notepad.exe`（PROTOCOL §3.4:161、§3.6:238）。模型没有可靠办法在调用前知道该用哪种。
- 影响：模型可能用 `go_windows(app_id="win-exe:notepad.exe")` 得到 `APP_NOT_FOUND`，或对打包应用猜错 `aumid`。文档没有规定“launch 返回的 window_id 就是推荐下一步输入”。
- 建议落点：PROTOCOL §3.8 返回说明 + §3.4 app_id 规则；contracts `go_launch`/`go_observe` 描述；DESIGN §5.4。
- 建议补的规格文本：
  > `go_launch` 返回的 `window_id` 是该应用“可立即 observe 的目标窗口”，模型应直接用 `go_observe(window_id=...)`，不得用猜测的 `app_id` 重新定位；`app_id` 只用于 `go_apps`/`go_windows` 过滤与 `go_launch` 激活。测试要求：打包记事本与 Win32 记事本两例都能走通。

**S1-3（P0-major）UIA 不可用/空树与“窗口真的没有内容”不可区分，且没有对应 advisory**
- 证据：advisories 是闭集 `tree_budget_exceeded / screenshot_failed / capture_degraded / not_settled / main_fallback`（PROTOCOL §3.6:255-256），没有 `uia_unavailable`；§3.6 没写“UIA 遍历失败时返回什么”（空 elements 还是错误码）；§8 错误码表也没有对应项（PROTOCOL:500-525）。
- 影响：实现者只能在“报错 / 返回空树+advisory / 返回空树不吭声”里选一个；模型看到空树时无法区分“应用没内容”与“读不到”。这与 S3 的弱 UIA 场景是同一类问题。
- 建议落点：PROTOCOL §3.6（advisory 闭集增加 `uia_unavailable`）+ §8（或明确“UIA 失败不算错误，用 advisory 表达”）；contracts `advisories` 数组；DESIGN §5.4。
- 建议补的规格文本：
  > UIA 树遍历失败（COM 错误/根元素不可得/完全无树）时，`go_observe` 仍返回成功结果：`elements=[]`、`advisories=["uia_unavailable"]`，若请求了截图则照常返回截图与 frame；`target.resolution="exact"` 不变。模型应改用截图 + 坐标（若 frame.actionable=true）。v1 冻结前把该值加入闭集，避免日后必须升协议版本。

**S1-4（P0-major）观察结果的 token 预算没有闭环，长树会被 AstrBot 从中间截掉**
- 证据：执行器侧 `maxObserveChars=20000`（PROTOCOL §3.2:135-137；DESIGN §5.8:386），元素上限 300/2000（§3.6:213），但 AstrBot 对工具结果的预览阈值是 **7000 tokens**、硬阈值 **27.5k tokens**，超限后把内容写到容器临时文件并只给“截断预览 + 文件路径”（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:112-113、402-446、448-465`）；而该溢出机制**只在 AstrBot 提供 `astrbot_file_read_tool` 时才启用**（`star/context.py:342-348`）。DESIGN 只说“插件对齐 ≈7000 tokens”（DESIGN §6.5:487），没给截断规则；PROTOCOL 没有 token 预算。
- 影响：模型可能只看到“OK 摘要 + 一半 JSON + 少量树”，且 JSON 被从中间截断后不是合法 JSON；若 AstrBot 未挂文件读取工具，大结果会原样进上下文，可能撑爆上下文窗口。S1 的“看内容”因此在长文本窗口上直接失真。
- 建议落点：PROTOCOL §3.6/§4（明确输出预算与截断顺序）；DESIGN §6.5 + ARCHITECTURE §4.5（插件转换规则）；验收加“长树”用例。
- 建议补的规格文本：
  > 观察结果预算按“图片（独立）→ OK 摘要（≤1 行）→ 元数据 JSON（**必须完整合法，不得截断**）→ 文本树（可截断）”顺序分配；执行器总字符预算默认 20000，插件再按 AstrBot 约 7000 tokens 做二次上限。文本树截断必须保留 `… (omitted N elements…)` 行；JSON 若装不下则整体省略 elements 明细并置 `omitted.reason="budget"`，禁止输出半截 JSON。

**S1-5（minor）`go_observe` 必填 window_id/app_id 与“会话当前窗口”概念不一致**
- 证据：PROTOCOL §5.1:444-445 说会话有 `current_window_id`；ARCHITECTURE §3.4:378 观察状态机写“解析窗口（app_id/window_id/**前台主窗**）”；但 PROTOCOL §3.6 + contracts 用 `anyOf` 强制二选一（PROTOCOL:216；contracts:94-105）。
- 影响：模型每次 re-observe 都被迫重复传 `window_id`（它得记住），而实现者不知道“省略时是否可以用会话当前窗口”；跨窗口多步操作时容易出现“忘了传”的 INVALID_ARGUMENT。保留 anyOf 也可，但要把“必须显式传”写进 tool description，或允许省略=会话当前窗口。
- 建议落点：PROTOCOL §3.6 参数段 + contracts；DESIGN §7.1。
- 建议补的规格文本：`window_id` 与 `app_id` 均可省略；省略时使用会话当前窗口（上一成功 observe 的窗口），无当前窗口则返回 `WINDOW_NOT_FOUND`；两者同时给时返回 `INVALID_ARGUMENT`。

### 2.4 S1 小结

P0 的“看”在**短文本、UIA 正常、单窗**的条件下是通的；一旦正文超过 48 字（几乎必然）或窗口无 UIA，就退化为“看个大概”。S1-1/S1-4 是 P0 验收必须补的；S1-2/S1-3 属于补了才能让实现者不猜的契约。

## 3. S2（P1）动作闭环：“在记事本里输入‘你好世界’并保存”

### 3.0 先回答任务里的问题：Ctrl+S 是真实缺口还是文档已说清？

**文档已说清“不支持”**：`key` 只接受 14 个单键（不含字母、数字、Ctrl/Alt/Win/F1 等），组合键一律 `UNSUPPORTED_ACTION`（PROTOCOL §3.7:304-307、:317-318；DESIGN §5.5:354、§7.2:554、§12.3:751；contracts:333-350）。

**但文档没说清“那怎么保存”**，也没说清“需要快捷键的任务在 v1 属于不可完成、模型应当如何向用户交代”。按 v1 能力，保存只能走 UI 点击（工具栏按钮 / 菜单项 / 保存对话框），而这条路径撞上本文 S2-2～S2-5 的空白（`type`/`key` 的焦点契约、菜单/对话框窗口模型、等待新窗口）。因此结论是：**“Ctrl+S 不可用”是已声明的能力限制；‘保存’这一用户任务缺的是任务级替代路径与验收口径，是真实缺口（P1-blocker）。**

### 3.1 期望 tool 调用序列

**A. 输入（推荐 `set_value` 替换 / `type` 插入两种意图）**

```jsonc
// 0) 若 Notepad 已开：go_windows 找 window_id；未开：go_launch(exe_path="notepad.exe")
go_launch   {"exe_path":"notepad.exe","wait_ms":5000}

// 1) 观察，拿到 edit 元素的 index 与 state_id
go_observe {"window_id":"w-...","include_screenshot":false,"settle_ms":250,"max_elements":300}

// 2a) 替换式写入（set_value 会替换该元素现有全部内容）
go_act {"op":"set_value",
        "target":{"kind":"element","index":2,"state_id":"st-8f21cd04"},
        "args":{"value":"你好世界"}}
// 2b) 或插入式输入（type 在光标/焦点处插入；见缺口 S2-2/S2-3）
go_act {"op":"type","args":{"text":"你好世界"}}

// 3) 动作后 state/frame 立即失效（PROTOCOL §5.2:453），必须重新观察
go_observe {"window_id":"w-...","include_screenshot":false,"settle_ms":250}
```

**B. 保存（v1 唯一可行的路径：点 UI，但该路径目前规格不足）**

```jsonc
// 4) 点“保存”按钮/菜单项（前提：observe 的树里存在带 actions=[invoke] 的保存元素）
go_act {"op":"click",
        "target":{"kind":"element","index":N,"state_id":"st-<new>"},
        "args":{"button":"left","count":1}}

// 5) 若无“保存”控件，只能走菜单：点“文件/File” → 菜单弹出（新窗口/新 surface）→ 再点“保存”
//    但菜单在 v1 窗口模型里没有规格（S2-4/S2-5），当前无法可靠编排。

// 6) 无标题文档会弹保存对话框（新的顶层窗口）
go_windows {}                        // 找一个新出现的保存对话框窗口
// → 期望有 owner/归属信息（当前契约没有，S2-4）
go_observe {"window_id":"w-对话框","include_screenshot":false,"settle_ms":250}
go_act {"op":"set_value","target":{"kind":"element","index":?, "state_id":"st-..."},
        "args":{"value":"note.txt"}}   // 或直接点默认文件名+“保存”
go_act {"op":"click","target":{"kind":"element","index":?,"state_id":"st-..."},
        "args":{"button":"left","count":1}}
// 7) 验证：重新观察主窗口标题从“无标题”变为“note.txt - 记事本”（标题只作效果证据，不作身份）
```

### 3.2 文档依据

| 环节 | 文档规定 | 依据 |
|---|---|---|
| 输入文本 | `type(text)` 前台 Unicode / WM_CHAR；`set_value` 走 ValuePattern | DESIGN §5.5:352-353；ARCHITECTURE §3.6:465-469 |
| 单键 | 14 个键；组合键不支持 | PROTOCOL §3.7:304-307、:317-318 |
| 动作语义 | 成功只回 accepted，不读回；要状态再 observe | PROTOCOL §0:14-24、§3.7:322-340；DESIGN §4.2:201 |
| 读回 | `set_value` 读回一致才算 verified；`type` 元素路径 contains；前台路径恒 unverified | ARCHITECTURE §3.12:592-603 |
| 窗口/对话框 | `go_windows` 列顶层窗口（title/class/pid/bounds/foreground…） | PROTOCOL §3.5:179-199 |
| 观察 | 一次 observe 只针对一个 `window_id/app_id`；`elements[].bounds` 禁止当坐标 | PROTOCOL §3.6:201-218、:262-264 |
| 预算 | 键入 5s+20ms/字符；工具整体 60s | PROTOCOL §7:492-493 |

### 3.3 缺口 / 冲突 / 风险

**S2-1（P1-blocker）`UNSUPPORTED_ACTION` 是唯一答案，但“保存”的替代路径与任务级结论没写**
- 证据：PROTOCOL §3.7:317-318 只写“组合键返回 `UNSUPPORTED_ACTION`”；`go_act` 的 tool description 只有“单次动作…动作后必须重新 observe”（contracts:112），没有任何“需要组合键时怎么办”的指引；DESIGN §10.3 P1 与 §11 的验收里也没有“输入并保存”这一条（DESIGN:691、709-719），即该项其实**没有验收标准**。
- 影响：模型拿到 `UNSUPPORTED_ACTION` 后会重试/换参数/编造成功；实现者只能自行决定“要不要提示用户手动保存”。
- 建议落点：PROTOCOL §3.7（v1 能力边界）+ contracts `go_act` 描述；DESIGN §10.3 P1/§11 验收；README 的 v1 限制。
- 建议补的规格文本：
  > v1 不支持组合键（含 Ctrl+S / Ctrl+C / Ctrl+V / Alt+F4）。当用户要求“保存/另存为/关闭并保存”时，模型必须：(a) 用 `go_observe` 找带 `invoke` 的“保存”按钮/菜单项并点击；(b) 若弹出保存对话框，用 `go_windows` 找到该对话框窗口，`go_observe(window_id=...)` 后填写文件名并点“保存”；(c) 若找不到可点击的保存控件，必须明确告诉用户“v1 无法发送 Ctrl+S，请手动保存”，**不得声称已保存**。P1 验收增加：真实记事本“输入‘你好世界’→ 点保存控件/菜单 → 保存对话框 → 重新观察标题变化”的用例；若无保存控件，验收为“如实报告无法保存”。

**S2-2（P1-blocker）`type`/`key` 的目标窗口、焦点、前台契约缺失；也没有“激活指定窗口”的动作**
- 证据：`go_act` 的 `type`/`key` 在 schema 里**不需要 target**（PROTOCOL §3.7:301-307；contracts:285-395 的 if/then 只要求 args），但全文没有说“文本/按键送到哪个窗口、焦点在谁身上”；只有坐标路径写了“前台 SendInput（需前台）”（ARCHITECTURE §3.6:463-465；PROTOCOL §6:476-480）；`go_launch` 只能按 `app_id` 激活“主窗口”（PROTOCOL §3.8:380），不能按 `window_id` 激活指定窗口；`type` 元素路径的 ValuePattern 与 WM_CHAR 选择规则也只有半句（ARCHITECTURE:466）。
- 影响：用户切走窗口后，`type` 可能把“你好世界”打进另一个应用；实现者不知道应先校验/激活哪个窗口；模型也不知道需要先点一下 edit 来抢焦点（而点击又会失效 state）。
- 建议落点：PROTOCOL §3.7 增加 op 语义/前置条件；contracts `go_act` schema 增加可选 `window_id`；ARCHITECTURE §3.6 输入链；DESIGN §5.5；P1 验收。
- 建议补的规格文本：
  > `type`/`key` 作用于“会话当前窗口”。执行器派发前校验：① 目标窗口 `window_id`（可选参数 `go_act.window_id`，缺省=会话当前窗口）存在且身份未变；② 该窗口为前台且焦点元素属于该窗口；③ 不满足时执行器按 `window_id` 直接激活目标窗口（内部能力，不新增公开工具），仍失败返回 `FOREGROUND_REQUIRED`（not_sent）。`type` 带 element target 时按“插入”语义（WM_CHAR / 前台 Unicode），**不替换**；要替换用 `set_value`。`key` 只发单键到焦点元素。
  > 附带：`go_act` 的 schema 把 `window_id` 列为可选字段（新增可选字段不升协议版本，PROTOCOL §9:531）。

**S2-3（P1-major）每个 op 的语义没有规格，模型无法在 `set_value` 与 `type` 之间做正确选择**
- 证据：PROTOCOL §3.7:266-320 只有 JSON Schema（`args` 一句话：“按 op 定义见下表”，但“表”其实只是 schema），没有“替换/插入/焦点/读回/非客户区”等语义；ARCHITECTURE §3.12:592-603 只有 verify 口径，没有选择建议；DESIGN §5.5:352-354 只有一行。
- 影响：模型可能在已有内容的记事本里用 `set_value` 把用户原文覆盖掉；或对不支持 ValuePattern 的控件反复 `set_value`。实现者也会对“type 带 target 时怎么办”各写一套。
- 建议落点：PROTOCOL §3.7 增加“op 语义表”；contracts 的 `go_act` 描述/args 描述；DESIGN §7.2。
- 建议补的规格文本（可直接做成表）：
  > | op | target | 语义 | 读回 | 建议 |
  > |---|---|---|---|---|
  > | `click` | element / coordinate | 元素走 UIA 模式梯；坐标走 hit-test 链；left 单击 | 无（恒 unverified） | 元素可分时优先元素 |
  > | `set_value` | element | **替换**元素全部值；元素不支持 ValuePattern/TextPattern 时 `ELEMENT_ACTION_UNAVAILABLE` | 读回 == 请求值否则 `mismatched`（仍 accepted） | “改名/整段替换”用它 |
  > | `type` | 可省略；带 element 时表示“先聚焦该元素” | 在光标处**插入**文本；不替换 | 元素路径读回 contains；前台路径 unverified | “追加/续写”用它；替换必须显式 set_value |
  > | `key` | 无 | 单键发到当前焦点元素 | 无 | 只支持 enter/tab/esc/方向键等 14 键 |
  > | `scroll` | element / coordinate | element 走 UIA ScrollPattern；坐标走宿主分类 | 无 | 先元素后坐标 |

**S2-4（P1-blocker）保存依赖菜单/对话框，但 v1 没有“非主窗、owned popup、菜单、模态框”的窗口模型**
- 证据：`go_windows` 只列“顶层窗口”的字段（PROTOCOL §3.5:179-199），没有 `owner_window_id/parent/is_modal/kind`；`go_observe` 一次只观察一个窗口（PROTOCOL §3.6:201-218）；截图是“窗口截图”（DESIGN §5.4:323），没有说明是否合成 owned popup / 菜单；ZCode 的研究正好证明这类 surface 是独立 HWND 且需要显式归属链（`research/zcode/notes/04-windows-input-and-frames.md:267-276`，作为外部参考）。
- 影响：保存对话框/菜单弹层要么找不到（模型不知道如何归到记事本），要么找到了也拍不到菜单像素、点不中菜单项（菜单 popup 不在目标窗口截图内）；这是“保存”不可执行的根因之一。
- 建议落点：PROTOCOL §3.5/`go_windows` 返回字段 + §3.6 观察对象定义；DESIGN §5.4 截图范围；P1 验收。
- 建议补的规格文本：
  > `go_windows` 返回的每个窗口增加 `owner_window_id`（可为 null）、`parent_window_id`（可为 null）、`kind`（`window|dialog|menu|tooltip`，v1 只区分前三）与 `is_modal`。`go_observe` 的目标必须是 `kind=window|dialog` 的窗口；`kind=menu` 的弹层在 v1 **不支持**被 observe（返回 `UNSUPPORTED_ACTION`/或 `WINDOW_NOT_FOUND`，明确写死），因此 v1 的保存路径只保证“保存按钮（在目标窗口树内）”与“保存对话框（kind=dialog）”，不保证菜单弹层。实现者按此实现，模型按此选择。
  > （如果要把菜单也纳入 P1，则必须补 popup surface 的截图合成/坐标属主规则，工作量接近 ZCode 的 frame-surface 专项，不建议塞进 P1。）

**S2-5（P1-major）动作后“新窗口出现”的等待/发现没有原语，模型没有 sleep**
- 证据：`settle_ms` 只描述“等待 UI 结构稳定的上限”（PROTOCOL §3.6:211-213），`not_settled` 是 advisory（:255-256）；没有任何“等待新顶层窗口出现/等待窗口关闭”的参数；`go_launch` 的 `wait_ms` 只用于启动（PROTOCOL:383-385）；模型侧没有 sleep/wait 工具。
- 影响：点击“保存”后，模型可能在对话框出现前就 `go_windows`，从而什么都看不到；它唯一能做的是重复 observe（触发 AstrBot 的“重复调用”提示，见 §7.10），或者反复 go_windows。保存流程因此不稳定。
- 建议落点：PROTOCOL §3.6 `settle_ms` 语义 + §3.5 `go_windows`（可选 `wait_ms`）；DESIGN §5.4；模型使用说明。
- 建议补的规格文本：
  > `go_observe.settle_ms` 的语义扩展为：最多等待 `settle_ms`，直到目标窗口 UIA 结构稳定；若本会话上一动作可能触发新顶层窗口（dialog/menu），同时等待“同 pid/owner 的新窗口出现或 `settle_ms` 耗尽”。仍无新窗口时返回 `advisories=["not_settled"]`。或者：`go_windows` 增加可选 `wait_ms`（0..2000），语义为“最多等待 window_id 集合发生变化”。

**S2-6（P1-major）`type` 的长度上限与超时预算自相矛盾**
- 证据：`type.text` `maxLength:4000`（PROTOCOL §3.7:301-303；contracts:293-312）；键入预算是“5s + 20ms/字符”、工具整体 60s（PROTOCOL §7:492-493）；插件默认超时 45s（DESIGN §6.6:501）。4000 字符 = 5s + 80s = **85s**，必然先撞执行器 60s / 插件 45s。
- 影响：实现者无法判断“超长文本”该拒绝、截断还是分片；模型可能传 4000 字然后拿到 `ACTION_TIMEOUT/possibly_sent`（部分已输入、禁止重试），接下来只能靠 observe 猜输入了多少。
- 建议落点：PROTOCOL §3.7 `type.text` `maxLength` + §7 预算；contracts；DESIGN §6.6 超时。
- 建议补的规格文本：
  > v1 `type.text` 上限 1500 字符（与 45s 插件预算、60s 工具预算对齐；1500×20ms+5s=35s）；超限返回 `INVALID_ARGUMENT`。或保留 4000 但明确“超过 1500 字符的输入按分片执行，每片一次 go_act，单片失败即停止”，且规定部分发送语义（已发送部分 → `possibly_sent`，禁止自动重放）。

**S2-7（P1-major）保存成功的判定没有 oracle；“标题变化”只被定义为“不失效”，没被定义为“可作证据”**
- 证据：PROTOCOL §5.2:459 只写“窗口标题变化不失效（标题不是身份）”；ARCHITECTURE §3.12:592-603 只定义 set_value/type 的 verification；没有“保存是否成功”的判定规则；DESIGN §4.2:201 只说“再次 observe（读回/二次观察），而不是相信工具返回 ok”。
- 影响：模型可能把 `OK: 动作已发送` 当成“已保存”，直接回复用户“已保存”。
- 建议落点：PROTOCOL §3.7/§4（模型使用规则）；plugin 的模型使用说明；DESIGN §4.2。
- 建议补的规格文本：
  > 保存/关闭类任务的成功判定只能来自重新观察：主窗口标题从“无标题”变为“<文件名> - 记事本”、或保存对话框消失且主窗口内容仍在，可认为保存成功；仅 `OK: 动作已发送`、或对话框消失但未重新观察，不得声称保存成功。标题只作效果证据、不作窗口/应用身份（见 §5.2）。

**S2-8（P1-major）中文/emoji/IME 输入没有验收口径**
- 证据：`type.text` 是普通 string（PROTOCOL §3.7:301-303），没有 UTF-16/代理对/IME 组合态的说明；DESIGN 验收只写“set_value/type 读回一致”（DESIGN §11:713），夹具是 WinForms（DESIGN §10.2:681），没有中文/emoji 用例。
- 影响：中文经 SendInput/ValuePattern 一般可用，但 IME 组合、代理对（emoji）、富文本换行（`\n`）在不同宿主行为不同；读回 contains 可能因归一化不一致误报。
- 建议落点：PROTOCOL §3.7 `type` 说明；DESIGN §10.3/§11 验收；ARCHITECTURE §3.12 读回规则。
- 建议补的规格文本：
  > v1 `type` 按 Unicode 码点逐字符注入（不经 IME 组合），文本按 UTF-16 代理对完整发送，不得截断半个代理对；`\n` 映射为 Enter 键。P1 验收加中文（“你好世界”）、emoji（“🙂”）、中英混合、换行四类用例，断言读回内容 contains 请求文本（不做归一化比较）。

### 3.4 S2 小结

“输入”在 `set_value` 可用时基本成立；“保存”在 v1 只有“点 UI”一条路，而这条路缺 4 块规格（焦点、菜单/对话框、等待新窗口、成功判定）。**建议把 S2 的用户级验收在 P1 拆成两个用例**：“输入成功（元素读回一致）”与“保存：有保存控件时点控件并验证；无控件时如实报告 v1 限制”。

---

## 4. S3（P1）弱 UIA / 无 UIA 窗口的坐标回退

### 4.1 期望 tool 调用序列

```jsonc
// 1) 带截图观察；必须包含 include_screenshot=true，否则坐标动作必然 STALE_STATE/FRAME_NOT_ACTIONABLE
go_observe {"window_id":"w-...","include_screenshot":true,"settle_ms":250,"max_elements":300}
// → [ImageContent（最前）, "OK: ...", observe JSON（frame.actionable=true, width/height/dpr, capture_method）, 文本树]
//   模型在图片上目测目标位置（例如计算器按钮“7”在图片 (412,233)）

// 2) 坐标单击（元素不可用/树里没有目标时）
go_act {"op":"click","target":{"kind":"coordinate","x":412,"y":233},
        "args":{"button":"left","count":1}}
// 执行器内部：命中测试 → 宿主分类 → PostMessage → 前台 SendInput；模型只看到 receipt（escalated:bool）

// 3) 重新观察验证（动作后 frame 必然失效，必须重新截图）
go_observe {"window_id":"w-...","include_screenshot":true,"settle_ms":250}
```

### 4.2 文档依据

| 环节 | 文档规定 | 依据 |
|---|---|---|
| 坐标定义 | 最近一次带截图观察返回**图片的像素**、左上原点；模型只传 x,y | PROTOCOL §1:6、§6:471-475 |
| frame 元数据 | `frame{width,height,dpr,actionable,capture_method,signature,captured_at_ms}` | PROTOCOL §3.6:229-236 |
| 截图预算 | 缩放至 ≤1024 宽、base64 ≤200KiB（自定预算） | DESIGN §5.4:323-324；DESIGN §5.8:386 |
| actionable 判据 | 采集成功/未最小化/几何DPI一致/TTL/非黑帧/已验证宿主；否则 false + capture_degraded | PROTOCOL §3.6:258-260 |
| 命中测试 | UIA ElementFromPoint 主级 + 窗口域枚举次级 → 宿主分类 → PostMessage → 前台 SendInput | PROTOCOL §6:476-480；ARCHITECTURE §3.6:452-475 |
| 非客户端 | 坐标落在非客户区/标题栏 → UNSUPPORTED_ACTION | PROTOCOL §6:479-480 |
| 多屏 | 未验证项（负坐标/多屏） | DESIGN §12 附录:780；ARCHITECTURE §8:858 |
| 不支持宿主 | PrintWindow 对 Chromium/GPU 可能黑屏；P2 换 WGC | DESIGN §5.4:323-325；DESIGN §10.3:692 |

### 4.3 缺口 / 冲突 / 风险

**S3-1（P1-blocker）截图缩放后的“图片像素 → 窗口物理像素”换算没有定义，`frame.width/height` 语义歧义、没有 scale 字段**
- 证据：PROTOCOL §1:6 说坐标是“该次观察返回图片的像素”；§6:473-475 说执行器“换算到屏幕物理像素”；但 frame 只有 `width/height/dpr`（PROTOCOL §3.6:229-236），示例里 `width=800,height=600` 正好等于 `window.bounds` 的 800×600，而截图可能被缩到 ≤1024 宽（DESIGN §5.4:324；§5.8:386 `maxImageWidth:1024`）——当窗口 1600 宽时，图片 1024 宽，`frame.width` 到底是 1600 还是 1024？没有 `scale/source_width` 字段，也没有换算公式；`dpr` 的语义（物理/逻辑）只在 ARCHITECTURE 注释里出现（§3.2:200-201）。
- 影响：实现者会自己对“宽高指的是图片还是窗口”做假设；假设错了，坐标点击稳定偏移（尤其 125%/200% DPI、>1024 宽窗口、多屏）。这是 P1 坐标路径的头号风险。
- 建议落点：PROTOCOL §3.6 `frame` 字段 + §6 坐标规则；DESIGN §5.4；contracts `go_observe` 说明。
- 建议补的规格文本：
  > `frame` 字段改为：`image_width/image_height`（实际返回 PNG 的像素尺寸，= 模型坐标系）、`window_bounds`（DWM 物理屏幕矩形，同 `target.window.bounds`）、`scale_x = image_width / window_bounds.width`、`scale_y = image_height / window_bounds.height`、`dpr`（仅诊断，不参与模型坐标换算）。坐标换算固定为：`screen_x = window_bounds.left + round(x / scale_x)`，`screen_y = window_bounds.top + round(y / scale_y)`；越界（x<0 或 x≥image_width）→ `INVALID_ARGUMENT`。
  > 验收：100%/125%/200% DPI、窗口宽 >1024（触发缩放）、单屏三组夹具点位误差 ≤1 物理像素。

**S3-2（P1-blocker）坐标回退在最需要它的宿主上系统性关闭：弱 UIA 窗口往往就是 Chromium/GPU/自绘，而 PrintWindow 帧默认不可动作、WGC 在 P2**
- 证据：PROTOCOL §3.6:258-260 “未验证宿主（PrintWindow 对未知 GPU/Chromium 默认判不可动作）”；DESIGN §5.4:323-325 明确“未验证宿主（Chromium/GPU）→ actionable=false + capture_degraded”；WGC 排在 P2（DESIGN §10.3:692）；P1 验收只有 WinForms 夹具（DESIGN:691），没有 Chromium/Edge 坐标用例。
- 影响：需要坐标回退的场景（浏览器、Electron、自绘控件）恰好是 PrintWindow 不可信的宿主；结果 `frame.actionable=false` → `FRAME_NOT_ACTIONABLE`，模型再怎么 observe 也点不了坐标。文档没有说 P1 的“已验证宿主清单”是什么、哪些弱 UIA 宿主必须走元素/键盘而非坐标。
- 建议落点：PROTOCOL §3.6 actionable 判据（明确 P1 已验证宿主清单）+ DESIGN §10.3 P1 验收；DESIGN §5.4 截图策略。
- 建议补的规格文本：
  > v1（P1）已验证可坐标动作的宿主：WinForms、WPF（非 Chromium 内嵌）、经典 Win32（Notepad/计算器主窗）、Edge/Chromium 需先通过 `capture_probe` 验证。其它宿主一律 `actionable=false`+ `capture_degraded`，模型应改用元素动作或告知用户。P1 验收必须包含“Edge/Chromium 页面内一个坐标目标”或明确把 Chromium 坐标移到 P2 并在 README 写明。
  > 若启用 WGC 提前到 P1，则 Chromium/GPU 的 `capture_method="wgc"` 帧可 actionable；否则不要承诺“弱 UIA 窗口可坐标点击”。

**S3-3（P1-major）截图区域与客户区/标题栏关系没定义，模型无法知道“哪里能点”**
- 证据：DESIGN §5.4:323 是“窗口截图（PrintWindow + BitBlt）”，PROTOCOL §6:479-480 说“坐标落在窗口非客户区/标题栏 → UNSUPPORTED_ACTION”，但 frame 里没有 `client_rect`/标题栏高度；文本树也不输出 bounds（PROTOCOL §4:425“不输出 bounds”），模型无法知道截图中哪一段是标题栏。
- 影响：模型在图片上按视觉选“关闭/最小化”按钮或标题栏区域时，会收到 `UNSUPPORTED_ACTION`，但错误信息不会告诉它“这是非客户区”，它会继续试。
- 建议落点：PROTOCOL §3.6 frame 字段 + §6；DESIGN §5.4 截图范围。
- 建议补的规格文本：
  > 二选一并在 v1 写死：① 截图只捕获窗口**客户区**（`PrintWindow` 后按 `GetClientRect` 裁剪），模型坐标系与客户区一致，非客户区根本不在图里；或 ② 截图含非客户区，但 `frame` 增加 `client_rect`（图片坐标系中的客户区矩形），模型对 `client_rect` 之外的坐标动作会得到 `INVALID_ARGUMENT`（文本说明“该区域是窗口边框/标题栏”）。推荐 ①，能省掉一类模型错误。

**S3-4（P1-major）多屏/负坐标/虚拟桌面原点未定义且未验证；P1 却在验收里承诺坐标路径**
- 证据：DESIGN 附录 A 尾注:780 明说“多屏负坐标”**未验证**；ARCHITECTURE §8:858 风险 #5“高 DPI/多显示器坐标”只有“Per-Monitor V2 + 夹具测试”的缓解，没写实现规则；PROTOCOL §6:473-475 只说“换算到屏幕物理像素”，没有 `SM_XVIRTUALSCREEN`/虚拟桌面原点/负 bounds 的处理；证据只有单屏 125%（DESIGN:772）。
- 影响：窗口在左/上副屏（负坐标）或混合 DPI 时，前台 SendInput 的绝对坐标必须做虚拟桌面归一化（研究笔记已证明这是真机坑，`research/zcode/notes/04-windows-input-and-frames.md:283-284`）；文档没写，实现者可能直接 `SetCursorPos(screen_x, screen_y)`，在副屏上点偏或点不动。
- 建议落点：PROTOCOL §6 坐标换算；DESIGN §5.4/§10.3；ARCHITECTURE §8 风险表。
- 建议补的规格文本：
  > 屏幕坐标换算固定为虚拟桌面坐标：`virtual_x = screen_x - SM_XVIRTUALSCREEN`、`virtual_y = screen_y - SM_YVIRTUALSCREEN`，SendInput 绝对坐标用 `MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK` 并除以 `SM_CXVIRTUALSCREEN/SM_CYVIRTUALSCREEN`。P1 验收：主屏右侧副屏（正坐标）+ 主屏左侧副屏（负坐标）+ 125%/200% 混合 DPI 三组；若不做，README 明确“v1 仅保证单屏”。

**S3-5（P1-major）文本模型拿不到截图时，坐标动作能力没有降级规则**
- 证据：AstrBot 只在 provider `modalities` 为空或含 `image` 时注入图片（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:1109-1114`）；DESIGN §6.5:488-489 要求插件“截图前检查 provider modalities，不支持则文本降级并提示”，但没规定降级后模型该怎么完成原本要坐标的任务；`go_observe` 的 tool description 仍要求“坐标动作前必须做一次 include_screenshot=true 的观察”（contracts:57）。
- 影响：文本模型会照做 `include_screenshot=true`，但根本看不到图；它只能在瞎猜坐标、或向用户报告之间选择。文档没有把“无视觉能力”映射为“坐标动作不可用”。
- 建议落点：PROTOCOL §3.6/`go_observe` 返回说明；DESIGN §6.5；ARCHITECTURE §4.5 转换表。
- 建议补的规格文本：
  > 当当前 provider 不支持 image 时，插件对 `include_screenshot=true` 的 observe 结果附加固定首行提示：`NOTE: 当前模型无法接收截图，坐标动作不可用；请只用元素动作或向用户说明限制。`；同时 `go_observe` 仍返回 frame 元数据与树（执行器不知道模型能力）。`go_act` 的 coordinate 目标在该模型下由插件直接拒绝（`ERROR[UNSUPPORTED_ACTION]`），不发到执行器，避免模型猜坐标。

**S3-6（P1-major）坐标路径的失败原因对模型不可操作（escalated 只有 bool、hit-test 结果不暴露）**
- 证据：模型面回执只有 `escalated`（PROTOCOL §3.7:322-340；ARCHITECTURE §1.2 #7:32 要求不暴露内部术语）；`UNSUPPORTED_ACTION`/`FRAME_NOT_ACTIONABLE` 的 message 是通用一句（PROTOCOL §2.3:96），没有“该坐标落在标题栏/被遮挡/宿主不可投递”的可操作提示。
- 影响：模型收到 `UNSUPPORTED_ACTION` 后只能盲试其它坐标；对“非客户区”这类完全可以避免的错误，应该一句话告诉它“改点内容区”或“先用元素动作”。
- 建议落点：PROTOCOL §2.3（`message` 面向模型的可操作要求）+ §6；`go_act` 描述。
- 建议补的规格文本：
  > 坐标动作失败时，`message` 必须给出下一步动作（不暴露内部术语）：非客户区 → “该位置是窗口边框/标题栏，请点击内容区”；frame 不可动作 → “当前截图不可用，请重新 include_screenshot=true 观察，或改用元素动作”；被遮挡/前台失败 → “目标窗口未在前台，请先激活或重新观察”。允许在 `details.hint` 给结构化提示，模型面文本保持中性。

**S3-7（minor）坐标动作使 frame 失效导致“每次点击都要重新截图”，成本与轮次没有说明**
- 证据：PROTOCOL §5.2:453 任何 act 进入派发后 frame 立即失效；§3.7:319 坐标目标使用“最近一次带截图观察”的 frame。文档没有告诉模型“一次坐标点击 = 一次截图 + 一次点击”，也没给出多步连续点击的批量方式（v1 无多点点击）。
- 影响：模型若想连续点几个位置，会不断 observe（截图）→ click，轮次/延迟高；恰好会触发 AstrBot 的重复调用提示（见 MX-10）。
- 建议落点：PROTOCOL §3.7/§5.2 模型使用说明；DESIGN §7.3。
- 建议补的规格文本：
  > 模型使用规则：一次坐标动作必须重新 observe 一次（include_screenshot=true）；v1 不支持一次操作多个坐标。模型应在任务开始时告知用户“可能需要多轮观察”，不要对同一坐标重复点击超过 2 次。

### 4.4 S3 小结

坐标链的“内部规则”（hit-test → 宿主分类 → PostMessage → SendInput）是清楚的；缺的是**模型面/坐标面**的三件事：图片像素怎么换算（S3-1）、哪些宿主可用（S3-2）、截图里客户区在哪（S3-3）。这三件不补，实现者只能猜，模型只能盲点。多屏（S3-4）可先降级为“v1 单屏”并在 README 写死，但必须写。

## 5. S4（P1）多会话 / 控制器租约冲突

### 5.1 期望 tool 调用序列

```jsonc
// 聊天 A（插件派生 session_id=astrbot-aaaa...）
go_observe {"window_id":"w-A"}
go_act     {"op":"click","target":{"kind":"element","index":2,"state_id":"st-aaaa"},
            "args":{"button":"left","count":1}}
// → OK: accepted；租约由 A 持有（TTL 60s，动作续期）

// 聊天 B（session_id=astrbot-bbbb...）几乎同时
go_observe {"window_id":"w-B"}
go_act     {"op":"click","target":{"kind":"element","index":5,"state_id":"st-bbbb"},
            "args":{"button":"left","count":1}}
// → 执行器等待 controller_wait_ms（默认 2000ms）后：
//   isError=true, ERROR[CONTROLLER_BUSY]: 其它会话持有控制权；retry=retry；retry_after_ms=<未定义>
// A 的租约仍有效；B 的 state（st-bbbb）未被动作失效，仍可用（PROTOCOL §5.2:454）

// B 的恢复（模型能做的只有“再调一次”）
go_act     {"op":"click", ...同参数...}      // 重试同一动作；若超过 state TTL 120s 则 STALE_STATE
// 可选：go_health 查 controller（但 go_health 没有 session_id 参数，模型看不出是不是自己持有）
```

### 5.2 文档依据

| 规则 | 文档规定 | 依据 |
|---|---|---|
| 租约范围 | 同一台桌面同一时刻只允许一个会话的输入动作 | PROTOCOL §5.3:462-467；DESIGN §5.6:360-361 |
| 获取/续期 | 首次 `go_act`/`go_launch` 获取，TTL 60s，每次动作续期 | PROTOCOL:465；DESIGN:360 |
| 冲突返回 | 等待 `controller_wait_ms`（默认 2000）后 `CONTROLLER_BUSY`（retry=retry + retry_after_ms） | PROTOCOL:466、§8:509；DESIGN §5.8:384 |
| 持有者可见 | `go_health.controller` | PROTOCOL §3.2:134 |
| 动作不失效状态 | 确定 not_sent 时 state/frame 保持有效 | PROTOCOL §5.2:454 |
| stop 清租约 | `go_stop` 清空租约；scope=session/all | PROTOCOL §3.9:404-417；ARCHITECTURE §3.9:538 |

### 5.3 缺口 / 冲突 / 风险

**S4-1（P1-blocker）租约“只能等到过期”：没有主动释放、没有心跳/续期失败、`retry_after_ms` 无计算规则；模型也没有 sleep**
- 证据：全文只写“TTL 60s，每次动作续期”（PROTOCOL:465；DESIGN:360），没有“动作结束后 X 秒自动释放”；`retry_after_ms` 在错误码表里只写“附”（PROTOCOL:466/509），没有算法；模型侧没有等待/睡眠工具，只能反复重试；`go_health` 只能看到持有者 id（PROTOCOL:134），看不出是不是自己。
- 影响：A 做完一个动作后，B 最长会被阻塞 60s；B 的模型只能以“每次调用都等 2s + 失败”的节奏重试，既慢又会触发 AstrBot 的“重复调用”提示（MX-10）。实现者不知道 `retry_after_ms` 填什么，可能填 0，模型就会立刻重试。
- 建议落点：PROTOCOL §5.3（租约生命周期）+ §8（CONTROLLER_BUSY 的 `retry_after_ms` 定义）；DESIGN §5.6；P1 验收“多会话并发”。
- 建议补的规格文本：
  > 租约在“动作派发结束”后保留 `lease_grace_ms`（默认 2000ms）以允许连续动作，之后自动释放；TTL 60s 只是崩溃兜底上限。`CONTROLLER_BUSY` 的 `retry_after_ms` = 当前持有者租约剩余毫秒（最小 100、最大 5000）；若剩余 >5000，则填 5000，模型据此最多重试。模型不得在 `retry_after_ms` 之前重试同一动作；连续 2 次 `CONTROLLER_BUSY` 后应向用户说明“另一个会话正在操作桌面”。

**S4-2（P1-major）“全局租约”与“session 级租约”两种写法冲突；`go_launch` 同时被定义为“查询类成功”和“租约获取者”；`go_stop` 的 scope 语义不清**
- 证据：DESIGN §5.6:360 写 `controller lease（全局）`；ARCHITECTURE §3.11:568 写“动作前必须获取 **session 级** controller lease”；PROTOCOL §5.3:464 写“同一台桌面…只允许一个会话”。PROTOCOL §2.1:46-47 说“`go_launch` 不是派发动作，按查询类处理”（成功 = OK 前缀），但 §5.3:465 又说“首次 `go_act`/`go_launch` 时获取租约”。`go_stop` 写“停止…释放控制权租约”（PROTOCOL:415），但 scope 只定义“停止范围”，没定义“是否清全局租约/是否可被其它 session 立即抢占”。
- 影响：实现者会做一个 per-session 的锁（结果多会话仍能交错到同一焦点），或把 launch 当动作类（成功判据写错）；`go_stop(scope="session")` 可能把别的会话租约也清了。
- 建议落点：PROTOCOL §5.3 + §2.1 + §3.9；ARCHITECTURE §3.9/§3.11；DESIGN §5.6。
- 建议补的规格文本：
  > 全篇统一称“**全局 controller lease**”（不是 per-session）：同一时刻最多一个 `session_id` 持有；其它会话的 `go_act`（含 type/key/scroll/launch 的前台部分）一律 `CONTROLLER_BUSY`。`go_launch` 的“查询类成功”只描述成功判据（`OK:`），**当且仅当**它需要激活/置前窗口时才参与租约；纯进程启动不占租约。`go_stop(scope="session")`：仅置该 session 的 stopped 标志并释放它持有的租约（若持有）；不影响其它 session。`go_stop(scope="all")`：置全局 kill switch、停止所有会话、清空租约。

**S4-3（P1-blocker）模型无法判断“租约是不是我持有 / 我现在能不能动作”**
- 证据：`go_health` 参数为空（PROTOCOL §3.2:114-116），返回里 `controller:{session_id,expires_at_ms}` 只是一个“当前持有者”，模型不知道自己插件的 `session_id`（插件内部派生），也没有字段说明“本次调用所属会话”。`go_health` 示例里 `sessions:2` 也没有每会话状态。
- 影响：B 的模型看到 `go_health.controller.session_id="astrbot-aaaa"`，无法判断这是不是自己；它只能反复试 `go_act`。遇到 `CONTROLLER_BUSY`/状态矛盾时无法自诊断。
- 建议落点：PROTOCOL §3.2 `go_health` 参数/返回；或 `CONTROLLER_BUSY` 错误对象 details；DESIGN §5.6。
- 建议补的规格文本：
  > `go_health` 增加可选参数 `session_id`；返回增加 `session:{session_id, stopped, readonly, controller_holder:boolean, state_id, state_age_ms}`。当调用方不传 `session_id` 时该字段为 null。`CONTROLLER_BUSY` 的 `details` 必须包含 `retry_after_ms` 与 `holder_is_self:false`。

**S4-4（P1-major）租约检查在动作状态机里的位置没定义，错误优先级会决定模型该“重观察还是等租约”**
- 证据：ARCHITECTURE §3.4:358-373 的动作状态机顺序是 `Validate → CheckSession → CheckReadOnly/RateLimit → ResolveTarget → Dispatch`，**完全没有租约获取步骤**；DESIGN §5.5:335-344 的派发链是 `校验 → 获取 controller lease → 目标解析`；PROTOCOL §8 只列错误码，没有优先级。
- 影响：如果实现者把 `STALE_STATE` 放在 `CONTROLLER_BUSY` 前面，B 在等 2s 租约前就会先被告知“状态过期”，从而去重新观察（而其实租约马上能拿到）；顺序反了则模型会一直等租约、永远不知道自己的 state 已经过期。模型侧恢复动作完全不同。
- 建议落点：ARCHITECTURE §3.4 状态机（补租约步骤）+ PROTOCOL §3.7/§8（错误优先级）；DESIGN §5.5。
- 建议补的规格文本：
  > 动作校验顺序固定为：`INVALID_ARGUMENT` → `SESSION_NOT_FOUND/EXPIRED/STOPPED` → `READONLY_MODE`/`RATE_LIMITED` → 获取 controller lease（等待 `controller_wait_ms`；失败 `CONTROLLER_BUSY`）→ 目标/新鲜度（`STALE_STATE`/`FRAME_NOT_ACTIONABLE`/`ELEMENT_UNAVAILABLE`）→ `UIPI_BLOCKED`/`FOREGROUND_REQUIRED` → 派发。先返回的错误不产生任何副作用；`CONTROLLER_BUSY` 不使 state/frame 失效。

**S4-5（minor）模型/用户人工操作与租约的关系没写**
- 证据：租约只管“会话之间”；用户本人移动鼠标、切窗口不在模型内。P1 验收已写“用户中途切走”用例（DESIGN §10.3:691），但文档没有告诉模型“动作后必须重新观察来发现用户干扰”。
- 影响：模型可能假设“accepted 就生效”，而用户已经切走导致前台路径失败。
- 建议落点：PROTOCOL §3.7 模型使用规则；DESIGN §4.2。
- 建议补的规格文本：`accepted` 只代表“已派发”，用户可能已切换前台或改变窗口；任何多步任务在每步后都要重新 `go_observe`，不得跨步骤复用 state/frame。

### 5.4 S4 小结

租约机制“有名字、有 TTL、有错误码”，但没有生命周期细节（释放/续期/归属/顺序），模型无法据此恢复，实现者只能猜。S4-1/2/3/4 都应在 P1 动工前写死；否则多会话验收（DESIGN §11:718）只能靠人工点两下聊天窗口，无法判断行为是否正确。

---

## 6. S5（P1）失败谱

### 6.1 失败矩阵（当前文档给出的、与缺失的）

| # | 失败 | 文档规定 | 现状 | 依据 |
|---|---|---|---|---|
| 1 | 执行器内部动作超时（未派发） | `ACTION_TIMEOUT` not_sent retry | 清楚 | PROTOCOL §8:522 |
| 2 | 执行器内部动作超时（已派发） | `DISPATCH_UNCERTAIN` + 内嵌 receipt possibly_sent | 清楚 | PROTOCOL §2.2:70-93、§8:523 |
| 3 | 插件调用超时（45s/120s） | “不重发、连接标记 unclean、下次重连、返回明确错误” | **模型文案/错误码/possibly_sent 判定缺失** | DESIGN §6.3:471-474；ARCHITECTURE §4.3:738-741 |
| 4 | AstrBot 外层超时 | 抛异常 → 模型看到 `error: tool ... execution timeout after N seconds.` | **没写插件必须内层兜住** | `ref/AstrBot/astr_agent_tool_exec.py:774-777`、`tool_loop_agent_runner.py:1410-1420` |
| 5 | token 错误（401） | HTTP 401 + WWW-Authenticate；插件无规定 | **插件行为/模型文案缺失** | PROTOCOL §1:32-36；DESIGN §9.3:653 |
| 6 | 容器重启 / 连接断开 | 指数退避重连；断开释放会话与租约 | **重连后 state/租约/在途动作语义缺失** | DESIGN §6.3:468-476；ARCHITECTURE §3.11:566 |
| 7 | kill switch | `SESSION_STOPPED`；恢复仅管理员 | 清楚（但 pause 与 readonly 混淆） | PROTOCOL §3.9:404-417、§8:508 |
| 8 | 只读/暂停 | `READONLY_MODE` “只读/暂停模式” | **pause 无定义；与 readonly/stopped 三态混淆** | PROTOCOL:144、:510；DESIGN:408 |
| 9 | AstrBot 忽略 isError | 插件用文本前缀兜底 | 部分清楚（缺“异常必须被内层捕获”的硬要求） | PROTOCOL §10:547；DESIGN §6.5:490-491 |
| 10 | 工具结果被截断 | 插件对齐 ≈7000 tokens，执行器 ≤20k 字符 | **数值不闭环、截断规则缺失、JSON 可能半截** | DESIGN:487；ARCHITECTURE:484；`ref`:112-113、402-465 |
| 11 | 锁屏 | `go_health.locked` 只上报 | **无错误码/无行为** | PROTOCOL §3.2:125 |
| 12 | 限流 429 / RATE_LIMITED | HTTP 429 + Retry-After；`RATE_LIMITED` retry | 插件映射缺失（并入 3/5） | PROTOCOL §1:34、§8:511 |

### 6.2 缺口 / 冲突 / 风险

**S5-1（P1-blocker）超时链的数值倒置；且“从哪里读 AstrBot 超时”写错了**
- 证据：执行器“工具整体 60s”（PROTOCOL §7:492-493）；插件默认 `timeout=45`（DESIGN §6.6:501）；DESIGN §6.3:471-473 说从 AstrBot `provider_settings.tool_call_timeout`（默认 120s）读取并 clamp；ARCHITECTURE §4.3:738 写死 `timeout=45s`；但 AstrBot 代码里真正传进插件的是 `ContextWrapper.tool_call_timeout`（`ref/AstrBot/agent/run_context.py:12-19`，默认 120），在 `astr_agent_tool_exec.py:721-745` 用 `asyncio.wait_for` 生效；`provider_settings.tool_call_timeout` 的文档默认值是 **60s**（`ref/AstrBot/docs-dev/astrbot-config.md:389-393`），而 DESIGN 附录 #17:775 写“默认 120s”——同一事实三处不一致。且在 `ref/AstrBot` 现有树中 grep 不到“从 provider_settings 读取 tool_call_timeout 再构造 run_context”的代码路径（参考树可能不完整，需实测确认）。
- 影响：45s 插件超时会先于 60s 执行器超时触发，于是“执行器本来能返回 `DISPATCH_UNCERTAIN` 的情况”变成“插件侧超时、没有 receipt、模型不知道该不该重试”。这是**失败语义最危险的一处**。
- 建议落点：PROTOCOL §7（超时预算表）+ DESIGN §6.3/§6.6 + ARCHITECTURE §4.3；实现时以 `context.tool_call_timeout` 为准。
- 建议补的规格文本：
  > 插件超时取 `context.tool_call_timeout`（即 AstrBot 实际生效值），再取 `min(插件配置 timeout, context.tool_call_timeout - 5)`。默认预算链：执行器整工具 ≤60s > 单动作 5s；插件必须 > 执行器整工具（建议 70s，且 ≤ 实际 `tool_call_timeout`-5）。若实测 AstrBot 默认是 60s，则执行器整工具必须降到 ≤45s 或把插件值调到 50s；**不允许插件超时早于执行器整工具超时**。P1 验收加：人为让执行器 sleep 到超时，确认模型收到的是 `DISPATCH_UNCERTAIN` 而不是插件超时。

**S5-2（P1-blocker）插件层的错误码/模型文案不存在；AstrBot 的兜底文本会把失败显示成小写 `error:`，前缀规则失效**
- 证据：PROTOCOL §8 错误码表是**执行器**的唯一事实源（PROTOCOL:500），没有任何插件侧错误（连接失败/超时/auth）；DESIGN §6.3:471-474、ARCHITECTURE §4.3:738-741 只说“返回友好错误”，没有码/文案；AstrBot 对 Tool handler 抛异常的兜底是 `_append_tool_call_result(func_tool_id, f"error: {e!s}")`（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:1410-1420`），而超时被转成普通 `Exception("tool X execution timeout after N seconds.")`（`ref/AstrBot/astr_agent_tool_exec.py:774-777`）——**都不以 `ERROR[` 开头**，模型会把它们当普通文本。插件若抛异常，B3 定的前缀规则直接失效。
- 影响：实现者只按文档写“返回友好错误”，很可能 `raise` 或返回普通字符串；模型侧就无法区分失败/成功，B3 的整个失败通道在插件这一层漏掉。
- 建议落点：PROTOCOL 新增 §（或明确 §8 扩展）插件侧错误码表；DESIGN §6.5；ARCHITECTURE §4.5；P1 验收。
- 建议补的规格文本：
  > 插件必须捕获所有 `Exception` 并返回 `CallToolResult`，不允许异常穿出到 AstrBot（`CancelledError` 可记录后重新抛出，但插件自身超时应短于 AstrBot 的 `tool_call_timeout`，让失败在插件层被塑形，而不是落进 AstrBot 的兜底文本）。插件侧错误码（与执行器错误码同一闭集，模型可见文本首行 `ERROR[CODE]:`）：
  > `EXECUTOR_UNREACHABLE`（连接失败，retry=retry）、`EXECUTOR_TIMEOUT`（插件等待超时；若请求是 `go_act`，文本必须采用 `DISPATCH_UNCERTAIN` 的语义：“可能已发送，禁止重试，请先 observe”）、`AUTH_FAILED`（401/403，retry=never，提示管理员检查 token）、`PROTOCOL_MISMATCH`（health.protocol_version 不一致，never）、`CONNECTION_LOST`（断线，retry=retry，先重连）。
  > 每个插件错误都必须带 `retry` 语义并内嵌 `details`；成功/失败文本前缀规则与执行器一致。

**S5-3（P1-blocker）插件超时/断线发生在 `go_act` 请求发出之后时，模型可见语义必须是“可能已发送”，但文档只写了“不重发”**
- 证据：DESIGN §6.3:471-474 只写“超时后不重发动作、连接标记 unclean、下次调用前重连”；PROTOCOL §7:488-490 的取消语义只覆盖**执行器内部**阶段，没有覆盖“插件→执行器”的 HTTP 层超时；ARCHITECTURE §4.3:738-741 的超时只说“返回可操作错误（建议先 observe 确认状态）”，没定义错误码与是否禁止重试。
- 影响：插件超时后，执行器可能已经派发（动作可能已发生）。如果模型看到“执行器超时”并按普通 retry 处理，就会重复动作——违背 B3 的 `possibly_sent` 红线，而这条红线只在执行器的错误对象里成立。
- 建议落点：PROTOCOL §7（补“客户端侧超时”行）；DESIGN §6.3；ARCHITECTURE §4.3；P1 验收（模拟插件超时后重复动作必须被插件拒绝）。
- 建议补的规格文本：
  > 插件对 `go_act`/`go_launch` 的请求一旦写出，任何本地超时/断线都视为 `possibly_sent`：返回 `ERROR[DISPATCH_UNCERTAIN]: 动作可能已发送，禁止自动重试；请先 go_observe 确认。`，并设置插件内部标记 `uncertain=true`；在该标记清除（下一次观察成功）前，插件拒绝任何新的 `go_act`（返回同一错误）。只读工具超时可安全重试一次。

**S5-4（P1-major）kill switch / 暂停 / 只读三态没有定义清楚，`READONLY_MODE` 把两个概念混在一个码里**
- 证据：PROTOCOL §3.3:144 的会话状态含“暂停/停止标志”，但全篇没有“暂停”定义；`READONLY_MODE` 注释是“只读/暂停模式”（PROTOCOL:510）；DESIGN §5.10:408 托盘有“暂停/恢复/退出”，§8.3:617 只讲 `readonly` 开关；`go_stop` 是第三种状态（stopped）。`go_stop` 的“取消排队动作”（PROTOCOL:415）对**已在原生调用中**的动作无效（§7:489-490），但措辞容易让用户/模型以为 stop 能撤回全部动作。
- 影响：实现者不知道该做 1 个还是 3 个状态；模型收到 `READONLY_MODE` 时分不清“用户开了只读”还是“用户按了暂停”（恢复方式不同：只读要 /whalego readonly off，暂停/stop 要 resume）；用户按了托盘“暂停”后模型看到什么码没有定义。
- 建议落点：PROTOCOL §3.3/§3.9/§8；DESIGN §5.10/§8.3；GUI 文档（gui-designer 的 docs/GUI.md 也应引用）。
- 建议补的规格文本：
  > v1 三态：① `readonly`（配置开关，持久；错误码 `READONLY_MODE`，恢复 /whalego readonly off）；② `paused`（托盘/热键的临时暂停，进程内状态；错误码新增 `PAUSED`，恢复 /whalego resume 或托盘；observe/health 仍可用）；③ `stopped`（kill switch；错误码 `SESSION_STOPPED`；恢复仅管理员 resume）。三态都不影响 `go_health`/`go_observe`。`go_stop` 只保证“拒绝后续动作 + 取消排队动作”；已进入原生派发的动作可能完成（`possibly_sent`），`go_stop` 的返回文本必须提示这一点。

**S5-5（P1-major）token 错误、容器/执行器重启的恢复契约缺失**
- 证据：认证失败只写 HTTP 401（PROTOCOL §1:33），插件如何处理、模型看到什么没写；`/healthz` 免认证（PROTOCOL:32），所以 401 只会在工具调用时暴露；DESIGN §6.3:468-476 只写重连退避，没有“401 不重连/不重试”规则；MCP 连接关闭释放会话与租约（ARCHITECTURE §3.11:566），但重连后同一 `session_id` 的旧 state 是失效、是复用、还是新会话，没有规定；没有 session generation/epoch。
- 影响：token 写错时，模型可能收到“执行器不可达”并无限重试（被当成网络问题），用户得不到“请检查 token”的可操作提示。容器重启后，模型上下文里还留着旧的 `state_id`；如果执行器把同 `session_id` 的新会话当作旧会话，可能误用旧 state，或收到 `STALE_STATE` 但不知道原因。
- 建议落点：PROTOCOL §3.3（会话生命周期/epoch）+ §1（认证错误）；DESIGN §9.3 排障；ARCHITECTURE §3.11。
- 建议补的规格文本：
  > 401/403 不得重试：插件返回 `ERROR[AUTH_FAILED]: WhaleGo 执行器拒绝认证，请在 AstrBot 插件配置中检查 token，并确认执行器已重新生成 token。`（retry=never）。执行器对每个 `session_id` 维护 `session_epoch`：MCP 连接关闭/执行器重启后旧 epoch 作废；新连接的同名 session 是全新会话（state/frame/租约为空），旧 `state_id` 一律 `STALE_STATE`。插件在检测到重连后，下一次工具结果的 OK 摘要后追加一行 `NOTE: 执行器已重连，请重新 go_observe。`

**S5-6（P1-major）锁屏/无交互会话没有失败路径**
- 证据：`go_health.os.locked` 只上报（PROTOCOL §3.2:125）；错误码表没有锁屏项（PROTOCOL:500-525）；DESIGN 没写锁屏时 observe/act 行为。
- 影响：锁屏后截图/UIA 可能失败或返回黑帧，模型会看到 `screenshot_failed/capture_degraded` 或莫名的 `FOREGROUND_REQUIRED`，而不知道“用户锁屏了，需要用户解锁”。这是 P1 真机最常见的“突然全失败”。
- 建议落点：PROTOCOL §3.2 health + §8 错误码；DESIGN §5.6/§9.3；P1 验收。
- 建议补的规格文本：
  > `go_health.os.locked=true` 时，`go_act`/`go_launch` 直接返回 `SESSION_LOCKED`（retry=never，message：“Windows 已锁屏，请先解锁再继续”）；`go_observe` 仍返回树/截图（可能为黑帧）但不生成可动作 frame（`actionable=false` + `capture_degraded`）。锁屏状态变化不自动恢复动作。

**S5-7（P1-blocker）结果截断规则缺失：可能把 JSON 截成半截、把树截掉一半，模型既不知道也不可靠**
- 证据：AstrBot 阈值 7000/27500（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:112-113`），超限时保留**前缀一半**并追加“Truncated tool output preview… Use astrbot_file_read_tool…”（`ref:171-176、448-465、440-446`）；该机制仅在工具集含 `astrbot_file_read_tool` 时启用（`ref/AstrBot/star/context.py:342-348`）；DESIGN 只说“插件对齐 ≈7000 tokens”（DESIGN §6.5:487）；ARCHITECTURE §3.7:484 说“工具结果超限截断并标注”，没有标注格式；执行器上限是 20000 字符（PROTOCOL §3.2:135），中文 20k 字符很可能 >7000 tokens。
- 影响：模型可能看到“一半的 JSON + 一半的树”，既无法解析 JSON，也不知道树缺了哪些元素；在未挂文件读取工具时，AstrBot 甚至不截断，直接把大结果塞进上下文。
- 建议落点：PROTOCOL §3.6/§4（输出预算与截断格式）；DESIGN §6.5；ARCHITECTURE §4.5；contracts `go_observe` 描述；P1 验收“长树+中文”。
- 建议补的规格文本：
  > 插件对每个工具结果做硬上限：估算 tokens ≤6000（安全低于 AstrBot 7000 预览阈值）。超限时按“先删文本树尾部、保留完整 JSON 与省略行、再删 elements 明细”的顺序降级，并在 OK 摘要行追加 `truncated=true omitted=N`；**永远不输出不完整 JSON**。执行器侧 `max_observe_chars` 默认从 20000 下调到与 6000 tokens 等价的字符数（或由插件按 UTF-8 字节重算并二次截断）。P1 验收：300 元素 × 中文树、20k 字符文档两例，模型侧看到的 JSON 可解析、树有省略提示。

**S5-8（minor）`INTERNAL` 的 dispatch_status 两个源不一致**
- 证据：PROTOCOL §8:524 写 `not_sent / possibly_sent`；contracts:573-576 写 `not_sent_or_possibly_sent`；错误对象的 `action_receipt.dispatch_status` 枚举只有三值（PROTOCOL:23）。同表其他错误都只给一个值。
- 影响：契约一致性检查（ARCHITECTURE §3.10、contracts/README:4）会因为枚举值不同而失败；实现者不知道 `INTERNAL` 该不该带 receipt。
- 建议落点：PROTOCOL §8 与 contracts 同步；`INTERNAL` 改为“not_sent；若已进入派发则按 `DISPATCH_UNCERTAIN` 处理”，或从契约表删除并只在错误码表引用。

**S5-9（minor）暂停/只读恢复命令的“管理员”判定与模型可见性没写**
- 证据：恢复仅 `/whalego resume` 或托盘（DESIGN §6.7:512、§8.3:621）；模型永远看不到恢复动作；但文档没说“stop 之后模型该说什么”——只说错误码 `SESSION_STOPPED`（retry=never）。
- 影响：模型可能不断重试 act；或编造“已恢复”。
- 建议落点：DESIGN §6.7/§8.3；`go_stop` 描述/系统提示。
- 建议补的规格文本：收到 `SESSION_STOPPED` 时，模型必须停止调用动作类工具，并告知用户“桌面控制已被停止，需要管理员在 AstrBot 发送 /whalego resume（或在 Windows 托盘/全局热键恢复）”，不得声称已恢复。

### 6.3 S5 小结

执行器内部的失败语义（not_sent/accepted/possibly_sent）设计得不错；**断点全部在“插件→AstrBot”这一段**：超时链倒置、插件错误码缺失、异常兜底文本破坏前缀、截断规则缺失。P1 验收（DESIGN §11:715-717）只覆盖执行器侧错误，建议增加“插件层 5 个失败用例”。

## 7. 模型可见文本里可能让 LLM 困惑的点

> 这一节只列“模型实际能看到、且会因此做错决定”的点。设计文档里的规则模型看不到，所以每条都建议落到 tool description / 返回文本 / 插件注入说明里（见 MX-13）。

**MX-1（高）index 不是跨观察稳定的**
- 现象：`index` 是每次观察时分配的（ARCHITECTURE §3.2:180-190），文本树与 `elements[]` 共用；树可能按预算裁剪并 `sparse_indices:true`（PROTOCOL §3.6:248-249、§4:426）——模型会看到 `[0] [2] [3] … [120]` 这类跳号，容易误以为“index 是位置/顺序”，也可能把两棵不同观察的 index 混用。
- 建议：在 `go_observe` 的 OK 摘要固定输出 `state_id + elements + sparse=true/false`；在 tool description / 系统提示写死“index 只在本次 state_id 内有效；跳号是裁剪，不是错误”。

**MX-2（高）state_id 失效时机比模型直觉更激进**
- 现象：任何 `go_act` **进入派发**（accepted/possibly_sent）后 state/frame 立即失效（PROTOCOL §5.2:453）；`type`/`key` 不带 target 也算一次 act；`not_sent` 才保留（:454）。模型常见错误是“点一下 → 再点一下同一个元素”（第二次必 `STALE_STATE`），或“点完直接 type”（type 本身不需要 state，但如果它想再点别的元素就会踩坑）。
- 建议：`go_act` 成功文本固定附一句“state 已失效，下一步请先 go_observe”；`STALE_STATE` 的 message 固定为“请重新 go_observe 后重试；不要复用旧 state_id”。

**MX-3（高）`elements[].bounds` 是屏幕坐标，但看起来最像“可点的坐标”**
- 现象：`bounds` 在 elements JSON 里以 `[left,top,right,bottom]` 出现（PROTOCOL §3.6:244-248；ARCHITECTURE §3.2:189 `BoundsDiagnostic`），而文档又禁止它当坐标（PROTOCOL:262-264）。模型很容易把 bounds 的中心当 x,y 填入 coordinate target，但坐标必须是**截图图片像素**（frame-local），两者在窗口不在 (0,0) 时完全不同。
- 建议：v1 模型面直接去掉 `bounds`（诊断信息只进审计/日志），或在字段名写 `bounds_screen_diagnostic` 并在 tool description 明确“不可用于坐标”；统一措辞“坐标=最近一次截图的图片像素”。

**MX-4（高）窗口标题歧义与变化**
- 现象：标题不参与身份（PROTOCOL §3.4:177、§3.5:199、§5.2:459），但 `go_windows` 最主要的可读字段就是标题；两个“无标题 - 记事本”无法区分；标题还会因未保存变成“* 无标题 - 记事本”。模型很可能用标题去挑 window_id 或判断应用。
- 建议：`go_windows` 返回顺序把 `foreground=true` 的窗口置前；tool description 写“标题只用于人读，选择窗口请用 window_id/app_id/pid，不能只用标题”；同标题时在标题后附 pid/窗口 id 短码。

**MX-5（高）`app_id` 三种形态让模型无从下手**
- 现象：`win-exe:<basename>` / 同名 `#2` / `win-aumid:`（PROTOCOL §3.4:169-177）；同一个 Notepad 在“Win32/打包”两种安装下形态不同（契约示例却写死 `win-exe:notepad.exe`，§3.4:161、§3.6:238）。模型没有可靠规则在调用前选对。
- 建议：`go_apps` 返回时把 `app_id` 原样给模型并附“推荐路径”；`go_launch` 返回的 `window_id` 是下一步唯一推荐输入（S1-2）；在 tool description 写“不要猜 app_id，先 go_apps”。

**MX-6（中）中英混排与“成功/失败”的多套表达**
- 现象：模型可见文本同时出现：`OK:`/`ERROR[CODE]:` 英文前缀、中文摘要“观察完成”、英文 `message`（“Observation state is stale; call go_observe again.”，PROTOCOL §2.3:86-88）、中文 tool description、英文 JSON 字段。工具描述又是“中文优先”（NAMING §3:89）。多语言混排会浪费 token，也可能让模型对 `ERROR[..]` 后中文内容理解不一致。
- 建议：v1 统一“模型可见文本 = 简体中文 + 固定英文前缀/错误码”；`message` 也用中文（错误码保持英文）；在 PROTOCOL §2.3 写明语言规则。

**MX-7（高）emoji/换行/引号会破坏“一行一个元素”的渲染**
- 现象：文本树格式是 `[index] kind "title" value="..."`（PROTOCOL §4:423），title 截 64、value 截 48 **按字符**（很可能是 UTF-16 code unit）；如果 title/value 含 `"`、换行、emoji，行结构会被破坏，代理对可能被截一半变成乱码。PROTOCOL 没有转义规则。
- 建议：PROTOCOL §4 增加：title/value 内的 `"`、`\`、换行、制表符做 JSON 风格转义（`\n`、`\"` 等）；截断按 Unicode 码点/字素簇，不拆代理对；尾部用 `\u2026`。验收加含引号、换行、emoji 的窗口。

**MX-8（高）一次 observe 返回多少 token 没有答案，且树与 JSON 内容重复**
- 现象：同一次 observe 同时返回“元数据 JSON 的 elements 表”和“渲染文本树”（PROTOCOL §3.6:220-225），同一批元素的标题/值出现两遍；上限是 300 元素 / 20000 字符（§3.2:135-137），token 数没有任何保证（中文 20k 字符可能 >7000 tokens）；AstrBot 又在 7000 tokens 处截断（S5-7）。模型经常在“树和 JSON 哪个是权威”上摇摆。
- 建议：明确“JSON 是权威，树是给模型读的摘要”；插件按 token 预算裁剪树；`go_observe` 返回的 OK 行附 `tokens≈N/6000`；给模型一个 `max_elements` 的降级建议（大窗口先 100 元素）。

**MX-9（高）截图“有”和“模型能看见”是两件事**
- 现象：`go_observe(include_screenshot=true)` 返回图片，但 AstrBot 只在 provider modalities 支持 image 时注入（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:1109-1114`）；文本模型只会在工具结果里看到“Image returned and cached at path=...”之类的占位文本（`ref:1319-1323`），然后按 tool description 去猜坐标。
- 建议：插件在 image 不可注入时按 S3-5 固定提示；`go_act` coordinate 直接拒绝；`go_health.capabilities` 增加 `model.image_visible:boolean`（由插件填充）。

**MX-10（中）`CONTROLLER_BUSY` 重试会触发 AstrBot 的“重复调用”提示**
- 现象：AstrBot 对“同名同参数连续调用 ≥3 次”会追加 `[SYSTEM NOTICE] ... same tool with the same arguments 3 times ...`（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:147-156`、:1364-1370）。B 会话为了等租约，重试同一 `go_act` 3 次就会收到这条提示，可能因此放弃正确的动作。
- 建议：执行器把 `controller_wait_ms` 内的等待做成“单次调用内阻塞”（默认 2s，可配到 ≤10s），减少模型重试次数；插件在 `CONTROLLER_BUSY` 文本中给 `retry_after_ms`；文档提示“重复提示是框架行为，不是动作失败”。

**MX-11（中）`session_id`/`request_id` 在模型 schema 里可填，但语义是插件内部字段**
- 现象：`go_observe`/`go_act` 都有 `session_id`（PROTOCOL §3.6:214、§3.7:287；contracts:88-92、182-185），描述是“插件传入”；`request_id` 是“插件生成”。模型可能随便填一个 `session_id`，导致执行器把它当成“另一个会话”或在同连接下产生未定义状态；也可能填非法值触发 `INVALID_ARGUMENT`。
- 建议：模型面 schema 删掉这两个字段（插件在内部注入，或执行器从 MCP 会话元数据取）；contracts 保留执行器实际接受的参数时，需要写清“模型不可见/插件注入”的区分（见 S4-3）。

**MX-12（低）`os.session_id` 与业务 `session_id` 同名**
- 现象：`go_health` 返回 `os.session_id:1`（Windows 登录会话，PROTOCOL §3.2:125），而工具参数里的 `session_id` 是 WhaleGo 业务会话（`astrbot-<sha16>`，§3.3:143）。模型可能把两者混为一谈（尤其看到 `os.session_id=1` 后以为会话 id 是 1）。
- 建议：健康返回改名 `os.windows_session_id`；业务会话统一叫 `whalego_session_id`（v1 冻结前改名，避免后面升协议）。

**MX-13（结构性问题）模型实际看不到“规格”，只看到一两句 tool description**
- 现象：所有“动作后必须重新 observe”“possibly_sent 不重试”“先 go_apps 别猜 app_id”“state_id 只在本次有效”等关键规则都写在 DESIGN/PROTOCOL 里；模型可见的是 contracts 里那几句 description（例如 `go_observe` 的 description，contracts:57）和返回文本。NAMING 还规定 tool description “≤120 字”（NAMING §3:89），放不下多条策略。设计文档默认“模型会遵守”，但没有任何机制把策略送进模型上下文。
- 建议：v1 采用“tool description（硬规则，短）+ 插件 `on_llm_request` 注入一段固定的 WhaleGo 使用说明（约 15 行，作为临时 user content）”两层；AstrBot 支持 `on_llm_request` 钩子（研究已核实，`research/astrbot/AstrBot插件开发约束与能力边界.md:183、:218`）；把“使用说明文本”纳入 contracts 或插件单测，保证与 PROTOCOL 同步。NAMING 的 120 字限制需要为“系统提示/使用说明”开例外。

**MX-14（中）截图后的 AstrBot 提示与隐私规则冲突**
- 现象：AstrBot 在工具返回图片后生成文本“Use send_message_to_user to send it to the user if satisfied...”（`ref/AstrBot/agent/runners/tool_loop_agent_runner.py:1319-1323`）；WhaleGo 要求截图结果固定附加“未经用户明确要求不得转发”（DESIGN §6.5:492）。两句话在同一条工具结果里互相矛盾，模型可能只记住前一句。
- 建议：插件在返回图片时把“隐私提示”放在 OK 摘要行开头（而不是末尾），或干脆不返回图片、改成让用户自己在 Windows 上查看；至少在模型使用说明里写“截图不得转发”。

---

## 8. 给非专家的一段解释（约 40 行）

1. 你在聊天里说一句话，这句话先到 AstrBot——一个跑在 Docker 容器里的机器人程序。
2. AstrBot 把这句交给它背后的大模型；模型自己不在你的电脑上，它只会“说话”和“请求调用工具”。
3. WhaleGo 插件是模型和你的 Windows 之间的翻译官。
4. 模型说“我想看记事本”，插件就把它翻译成一条 MCP 协议请求。
5. 请求通过 Docker 的内部网络地址 `host.docker.internal` 发到 Windows。
6. Windows 上有一个常驻的 WhaleGo 执行器（托盘小图标）。
7. 它必须跑在你登录后的桌面会话里——做成后台服务就看不到你的桌面。
8. 请求带着一个长令牌（Bearer Token），相当于门禁卡；令牌不对执行器就拒绝。
9. 执行器用 Windows 自带的“UI 自动化”（UIA）接口读窗口。
10. 它把窗口里的按钮、输入框、文字读成一张清单，每个元素编一个号。
11. 清单连窗口标题一起返回；如果窗口不支持 UIA，还能截一张这个窗口的图。
12. 模型看到的不是真实屏幕，而是这张清单（编号/类型/文字）和可选图片。
13. 模型要“点第 7 个按钮”，就回传状态编号 `state_id` + 编号 7。
14. 模型要“点图片上的某处”，就回传图片像素坐标 `x,y`；前提是上一次观察带截图。
15. 执行器先检查：这份清单/截图是不是本会话最新一次观察、有没有过期。
16. 然后选动作路径：能用 UIA 语义动作就直接调；不行试后台消息；再不行把窗口提到前台，用真实鼠标键盘。
17. 点击/输入发出后，执行器只能说“发出了/可能发出了/没发出”，不能保证业务真的生效。
18. 所以模型必须重新观察一次（再读树/再截图）来确认结果。
19. 这就是为什么一句“帮我点一下”常常要来回好几轮。
20. 输入文字也一样：要么用 UIA 的“设置值”直接替换，要么往光标处“打字”。
21. 中文、emoji 都能发；但 v1 只支持单键（回车/Tab/方向键等），Ctrl+S、Ctrl+C 这类型组合键不支持。
22. 所以“输入并保存”在 v1 只能靠点菜单/工具栏上的保存按钮。
23. 如果窗口里没有可点的保存控件，模型只能如实告诉你“这一步 v1 做不到”，不能假装保存成功。
24. 多个聊天同时用时会抢桌面：全局同一时刻只允许一个会话操作，另一个会收到“控制权忙”。
25. 安全上有几道闸：只读开关（只让看不让动）、暂停、停止（kill switch）、审计日志。
26. 停止之后，只有管理员发 `/whalego resume` 或用 Windows 托盘/热键才能恢复，模型自己不能解除。
27. 截图可能包含你的隐私，所以默认关闭；密码框内容会打码；模型被要求不得转发截图。
28. 观察返回的树可能被裁剪（窗口太大/元素太多），你会看到“omitted”提示和跳号。
29. 每次动作后 `state_id` 都会失效，模型不能拿旧状态再点，否则会报“状态过期”。
30. 窗口标题会变（“无标题”变成“xxx.txt”），所以标题不能当窗口身份。
31. 两个同名窗口（两个“无标题 - 记事本”）只能靠窗口 id 区分。
32. 执行器或容器重启、断线后，之前的观察作废，要重新观察。
33. 插件在中间做保护：文本太长会截断、错误加 `ERROR[CODE]:` 前缀（因为 AstrBot 不读 MCP 的 isError 标志）。
34. 如果执行器超时，动作可能已经发出去了，这时模型被要求“不要重试，先重新观察”。
35. 如果你在它操作时自己动了鼠标/切了窗口，动作可能失败；模型会在下一次观察里发现。
36. 整条链是“模型提议、执行器执行、模型验证”，不是“模型直接拥有你的电脑”。
37. 所以它能做的事有限：UI 可读、窗口可截、动作在支持列表里，三者缺一不可。
38. v1 能做：列应用/窗口、启动程序、读树+截图、单击、设置值、输入文字、单键、滚动、停止。
39. v1 不能做：组合键、右键/双击、拖拽、粘贴、读超长文档、菜单弹层、复杂宿主的坐标点击。
40. 想知道细节，看 `docs/PROTOCOL.md`（工具契约）和 `docs/DESIGN.md`（为什么这样设计）。

---

## 9. 能做什么 / 不能做什么（v1 范围内）

### 9.1 能做什么

| 能力 | v1 状态 | 前置条件 / 说明 | 依据 |
|---|---|---|---|
| 健康检查、版本/能力 | ✅ | `go_health`；文本/图片能力由插件补充 | PROTOCOL §3.2 |
| 列应用 / 列窗口 | ✅ | `go_apps`/`go_windows`；标题不作身份 | PROTOCOL §3.4-3.5 |
| 启动 exe / 激活已有应用 | ✅ | `go_launch`；等待主窗口出现；激活语义有限 | PROTOCOL §3.8 |
| 观察 UIA 树 + 元数据 | ✅ | `go_observe`；预算 300 元素/20k 字符 | PROTOCOL §3.6 |
| 单窗截图 | ✅（条件） | 执行器 `screenshot.enabled` + 模型 `include_screenshot` 双开关；provider 支持 image 才看得见 | PROTOCOL §3.6、§11；DESIGN §6.5 |
| 元素单击（left×1） | ✅ | `go_act.click` + element target；UIA 模式梯 | PROTOCOL §3.7；ARCHITECTURE §3.6 |
| 坐标单击（left×1） | ⚠️ 条件可用 | 最近一次带截图观察、frame.actionable=true、宿主已验证、非标题栏 | PROTOCOL §6；本文 S3 |
| 设置值（整段替换） | ✅（元素支持时） | `go_act.set_value`；ValuePattern/TextPattern 可用 | PROTOCOL §3.7；ARCHITECTURE §3.12 |
| 输入文本（插入） | ✅（条件） | `go_act.type`；目标窗口前台/焦点归属需补规格（S2-2） | PROTOCOL §3.7 |
| 单键（14 键） | ✅ | enter/tab/escape/backspace/delete/方向/page/home/end/space | PROTOCOL §3.7:304-307 |
| 滚动 | ✅ | element 走 ScrollPattern；坐标上/下、0.25-20 页 | PROTOCOL §3.7 |
| 多会话串行 | ✅（机制） | 全局 controller lease；但生命周期细节缺失（S4） | PROTOCOL §5.3 |
| 只读 / 暂停 / 停止 | ⚠️ 部分 | stop 明确；pause/readonly 三态未定义（S5-4） | PROTOCOL §3.9；DEFECT |
| 审计 | ✅ | 写动作返回前落盘；不记录明文/截图字节 | ARCHITECTURE §3.8；DESIGN §5.7 |
| 隐私 | ✅（基础） | 密码 value=`***`；敏感窗口黑名单；截图默认关；不得转发提示 | PROTOCOL §11 |

### 9.2 不能做 / 不可靠（v1）

| 不能做 | 说明 | 依据 |
|---|---|---|
| 组合键（Ctrl+S/C/V、Alt+F4…） | `key` 只收 14 个单键；组合 → `UNSUPPORTED_ACTION`（P2） | PROTOCOL:304-307、:317-318；DESIGN:354/751 |
| 右键、双击、拖拽、粘贴、选中文本、perform_action | v1 枚举里没有；P2 | PROTOCOL:317-318；DESIGN:354 |
| 读取长文档全文 | 文本树 value 截 48 字；JSON 截断规则未定义；无 read_text | PROTOCOL §4:424；ARCHITECTURE §3.2:184（S1-1） |
| 菜单弹层 / 复杂 popup 的观察与点击 | 单窗观察模型没有 popup 归属/截图合成 | PROTOCOL §3.5-3.6；本文 S2-4 |
| 保存对话框的可靠编排 | 依赖窗口发现+等待+焦点，均缺规格 | 本文 S2-4/S2-5 |
| 按 window_id 激活任意窗口 | 只有 `go_launch(app_id)` 激活主窗口 | PROTOCOL §3.8（S2-2） |
| Chromium/GPU 等未验证宿主的坐标点击 | PrintWindow 帧默认不可动作；WGC 在 P2 | PROTOCOL:258-260；DESIGN:323-325/692（S3-2） |
| 多屏/负坐标/混合 DPI 的坐标点击 | 文档明确“未验证”；无虚拟桌面换算规则 | DESIGN:780；ARCHITECTURE:858（S3-4） |
| 文本模型的坐标操作 | 看不到截图，无法选点 | AstrBot 图片注入条件；S3-5 |
| 等新窗口出现 / sleep / 轮询等待 | 没有等待原语；settle 只管树稳定 | PROTOCOL:211-213（S2-5） |
| 打开/保存任意文件（文件对话框） | 登录 v1 没有文件操作，只有对话框点击；未规格 | 本文 S2-4 |
| 锁屏时操作 | 只有 health.locked 上报，无错误码/行为 | PROTOCOL:125（S5-6） |
| 模型自行恢复 stop / resume | 恢复仅管理员命令/托盘/热键 | PROTOCOL §3.9:416-417 |
| 多执行器 / TLS / 确认流程 / 崩溃自恢复 | P3/P2；v1 不做 | DESIGN §10.3/§12.3 |
| 高完整性（UIPI）窗口 | 预检并拒绝 | PROTOCOL §8:521 |
| UWP/ApplicationFrameHost 全矩阵 | 只写规则，未真机验证 | DESIGN:780；ARCHITECTURE:857 |

## 10. 规格修订清单（按文档落点，可逐条勾选）

> 用法：Lead 可直接把本表拆成共享任务；“落点”里的章节号对应当前版本。标 **P0前** 的必须在写 P0 验收前完成，标 **P1前** 的在 T4/T5 联调前完成。

### 10.1 PROTOCOL.md

| # | 落点 | 补什么 | 关联缺口 | 时点 |
|---|---|---|---|---|
| P-1 | §3.6 元数据 + §4 渲染 | `elements[].value` 完整返回规则（单值上限/总预算/`value_truncated`/`omitted.value_chars`）；文本树截断提示 | S1-1 | P0前 |
| P-2 | §4 | 文本树转义规则（引号/反斜杠/换行/制表符/Unicode 截断不拆代理对）；语言规则（中文+英文前缀） | MX-6/MX-7 | P0前 |
| P-3 | §3.6 `frame` | 明确 `image_width/image_height`（模型坐标系）、`scale_x/scale_y`、`client_rect`（或截图只截客户区）、换算公式与取整规则 | S3-1/S3-3 | P1前 |
| P-4 | §3.6 actionable | P1 已验证宿主清单；非清单宿主行为（`actionable=false`+模型指引）；P1 验收宿主范围写死 | S3-2 | P1前 |
| P-5 | §3.6 advisory | 增加 `uia_unavailable`（并说明“冻结前加，不升版本”）；空树语义写死 | S1-3 | P0前 |
| P-6 | §3.7 op 语义 | 增加 op 语义表（click/set_value/type/key/scroll 的语义、target、读回、替换/插入、失败码） | S2-3 | P1前 |
| P-7 | §3.7 type/key | 目标窗口/焦点/前台契约；`go_act` 新增可选 `window_id`；失败码 `FOREGROUND_REQUIRED` 触发条件 | S2-2 | P1前 |
| P-8 | §3.7 `type.text` | `maxLength` 与 45s/60s 预算对齐（建议 1500）或定义分片/部分发送语义 | S2-6 | P1前 |
| P-9 | §3.5 + §3.6 | `go_windows` 增加 `owner_window_id/parent_window_id/kind/is_modal`；`go_observe` 只接受 window/dialog；菜单弹层 v1 不支持写死 | S2-4 | P1前 |
| P-10 | §3.6 `settle_ms` / §3.5 | 等待新窗口/新状态的语义（或 `go_windows.wait_ms`）；`not_settled` 使用规则 | S2-5 | P1前 |
| P-11 | §5.3 + §8 | 租约生命周期：`lease_grace_ms`、主动释放、`retry_after_ms` 公式、`CONTROLLER_BUSY` 不使 state/frame 失效、错误优先级 | S4-1/S4-4 | P1前 |
| P-12 | §2.1/§5.3/§3.9 | 统一“全局 controller lease”；`go_launch` 只在需要激活时参与租约；`go_stop` scope 语义 | S4-2 | P1前 |
| P-13 | §3.2 `go_health` | 可选 `session_id` + 每会话状态（stopped/readonly/controller_holder/state_id）；`controller` 可为 null 的 schema | S4-3 | P1前 |
| P-14 | §7 超时 | 超时链统一表（AstrBot 实际 `tool_call_timeout` → 插件 → 执行器）；插件超时 ≠ 动作取消；`go_act` 客户端超时按 `DISPATCH_UNCERTAIN` 呈现 | S5-1/S5-3 | P1前 |
| P-15 | §8 错误码 | 插件侧错误码（`EXECUTOR_UNREACHABLE/EXECUTOR_TIMEOUT/AUTH_FAILED/PROTOCOL_MISMATCH/CONNECTION_LOST`）与模型文案；`SESSION_LOCKED`/`PAUSED`；修正 `INTERNAL` 的 dispatch 列 | S5-2/S5-4/S5-6/S5-8 | P1前 |
| P-16 | §3.3 + §1 | 会话 epoch：断线/重启后旧 state 一律 STALE；401 不重试；重连后提示重新 observe | S5-5 | P1前 |
| P-17 | §3.9 | 三态定义（readonly/paused/stopped）与各自允许的工具；`go_stop` 对在途动作的说明 | S5-4 | P1前 |
| P-18 | §3.6/§3.7 | 模型面去掉 `session_id`/`request_id` 或标注“插件注入、模型不可填”；`bounds` 是否下发 | MX-3/MX-11 | P1前（可在实现前定） |
| P-19 | §12（新增） | 模型使用规则（reobserve、不要重试 possibly_sent、CONTROLLER_BUSY 等待、保存路径、截图不可见降级） | MX-1..MX-13 | P1前 |

### 10.2 DESIGN.md

| # | 落点 | 补什么 | 关联缺口 | 时点 |
|---|---|---|---|---|
| D-1 | §5.4 观察引擎 | 全文读取/裁剪规则；截图范围（客户区）；host 白名单；多屏策略（或写“v1 单屏”） | S1-1/S1-4/S3-1..S3-4 | P0前 |
| D-2 | §6.3/§6.6 | 插件超时从 `context.tool_call_timeout` 取；默认 70s（或与执行器 60s 对齐）；异常必须内层捕获 | S5-1/S5-2 | P1前 |
| D-3 | §6.5 结果转换 | token 预算/截断顺序/JSON 不可截断；image 不可注入提示；隐私提示位置 | S1-4/S5-7/MX-8/MX-14 | P1前 |
| D-4 | §5.10/§8.3 | 托盘“暂停/恢复”的模型可见语义；与 stop/readonly 的关系 | S5-4 | P1前 |
| D-5 | §10.3 P0/P1 + §11 | 把“读正文”“输入并保存（含失败分支）”“多会话”“插件超时”“长树”纳入验收；删掉无法达成的模糊承诺 | S1/S2/S4/S5 | P0前（P0部分）/P1前 |
| D-6 | §12.3 遗留 | 明确 v1 不支持组合键、菜单弹层、长文本、Chromium 坐标；给出 P2 顺序 | S2-1/S2-4/S3-2 | P0前 |

### 10.3 ARCHITECTURE.md

| # | 落点 | 补什么 | 关联缺口 | 时点 |
|---|---|---|---|---|
| A-1 | §3.4 动作状态机 | 把“获取 controller lease”写进状态机与错误优先级 | S4-4 | P1前 |
| A-2 | §3.11 会话身份 | 会话 epoch/断线释放/重连语义；与 PROTOCOL §3.3 对齐 | S5-5 | P1前 |
| A-3 | §3.6 输入链 | `type`/`key` 的焦点/前台预检；坐标换算模块（`FrameCoordinateMapper`）职责 | S2-2/S3-1 | P1前 |
| A-4 | §4.5 转换表 | 插件异常必须转 CallToolResult；失败文本前缀；截断规则 | S5-2/S5-6/S5-7 | P1前 |
| A-5 | §7 任务分解 | 把 S1-1/S2-2/S3-1/S5-1 的规格修订作为 T1/T4/T5 的前置依赖 | 全部 | P0前 |

### 10.4 contracts/whalego.tools.v1.json

| # | 改什么 | 关联缺口 | 时点 |
|---|---|---|---|
| C-1 | `go_observe` 增加 `detail`/返回字段说明；`session_id` 标注模型不可填 | S1-1/MX-11 | P0前 |
| C-2 | `go_act` 增加可选 `window_id`；description 写入“动作后必须重新 observe、possibly_sent 禁止重试、保存类任务限制” | S2-1/S2-2 | P1前 |
| C-3 | `go_windows` 的 owner/kind 字段说明；`go_health` 的 session/locked 字段说明 | S2-4/S4-3/S5-6 | P1前 |
| C-4 | 错误码/枚举与 PROTOCOL §8 同步；`INTERNAL` 的 dispatch 值统一 | S5-8 | P1前 |
| C-5 | 一致性检查 `tools/list` 比对（ARCHITECTURE §3.10）加“模型可见 schema 不含 session_id/request_id”的断言 | MX-11 | P1前 |

### 10.5 README / NAMING / 模型可见说明

| # | 落点 | 补什么 | 关联缺口 | 时点 |
|---|---|---|---|---|
| R-1 | README | 新增“v1 已知限制”一节（组合键/保存/长文档/菜单/Chromium/多屏/锁屏），用非术语写清 | S1/S2/S3/S5 | P0前 |
| R-2 | NAMING §3 规则 5 | 为“模型使用说明/系统提示”开例外；tool description 只放最硬规则（不超过 N 字），长规则走插件注入 | MX-13 | P0前 |
| R-3 | 插件 | 新增固定的“WhaleGo 使用说明”文本（约 15 行），通过 `on_llm_request` 注入或作为首轮 system 附加；内容从 PROTOCOL §12 的“模型使用规则”同步 | MX-13 | P1前 |
| R-4 | docs/GUI.md（gui-designer 产出） | 暂停/恢复/停止/只读的 UI 与模型可见语义对齐（本文 S5-4） | S5-4 | P1前 |
| R-5 | DESIGN 标题 / README | 统一版本号（DESIGN 标题 v0.2 vs README/reviews 称 v0.3）；避免实现者按错版本文档开工 | 文档一致性 | P0前 |

---

## 11. 建议新增的验收用例（补规格后可直接执行）

### P0（观察闭环）

1. **读正文**：打开一个有 500 字中文的记事本 → `go_observe` 一次 → 断言模型可见数据包含第 100 个字之后的内容（文本树可只显示摘要，但 `elements[]` 或 `detail=text` 必须能拿到全文）。
2. **长树**：打开 300+ 元素的窗口 → 断言返回的 JSON 可被解析、树有 omission 标记、模型侧不超过 AstrBot 预览阈值。
3. **UIA 失败**：用一个无 UIA 的测试窗口（或 mock）→ 断言 `uia_unavailable` advisory + 截图仍返回。
4. **转义**：窗口标题/element value 含引号、换行、emoji → 断言文本树仍是“一行一个元素”，无乱码。
5. **token 负例**：401、`/healthz` 免认证、token 轮换后旧 token 失效。

### P1（动作闭环）

6. **输入并保存（有保存控件）**：记事本 `set_value`/“你好世界” → 重新观察 → 点保存控件 → 保存对话框（go_windows 找窗口 → set_value 文件名 → 点保存）→ 重新观察标题变化。
7. **输入并保存（无保存控件）**：断言模型收到明确“无法保存”的文案，不得返回 OK。
8. **焦点**：目标窗口在前台/不在前台两态；不在前台时断言 `FOREGROUND_REQUIRED` 或自动激活并成功；文本不得打进别的窗口（用两个记事本验证）。
9. **坐标换算**：100%/125%/200% DPI、窗口宽 >1024（触发缩放）→ 点击计数 oracle，误差 ≤1 像素。
10. **非客户区**：点击标题栏 → 断言 `UNSUPPORTED_ACTION`（或 `INVALID_ARGUMENT`），message 给出“点内容区”的指引。
11. **多会话**：A 持租约、B 动作 → B 收到 `CONTROLLER_BUSY` + `retry_after_ms`；A 动作结束后 B 在 `retry_after_ms` 内重试成功；A 的 `go_stop(scope="session")` 不阻塞 B。
12. **插件层失败**：执行器 sleep 到工具超时 → 模型看到 `DISPATCH_UNCERTAIN`（不是插件超时）；插件本地超时 → 禁止重试；401 → `AUTH_FAILED` 不重试；容器重启 → 重连后提示重新 observe。
13. **截断续做**：插件把长 observe 截断后，模型仍能读到完整 JSON 与省略提示。
14. **锁屏**：锁屏时 `go_act` → `SESSION_LOCKED`；解锁后可继续。

---

## 12. 最终判断

- **架构层**：观察/动作分离、state/frame 内部绑定、receipt 三态、hit-test 链、租约方向都是对的；三份文档足以指导实现，不需要推翻架构。
- **契约层**：当前缺口集中在“模型面语义”和“插件↔执行器失败面”，不是架构问题；但每一条都会让实现者只能猜。
- **进入实现的建议**：
  1. 先做 §10.1 的 P-1/P-2/P-5、§10.2 的 D-5（P0 验收）与 §10.4 的 C-1 —— 这些是 P0 验收“读得到”的前提；
  2. P1 动工前做 §10.1 的 P-3/P-4/P-6/P-7/P-8/P-9/P-10/P-11/P-14/P-15/P-17 与 §10.3 的 A-1/A-3/A-4；
  3. 把 §11 的 6-14 用例写进 P1 验收；S2 的“输入并保存”拆成“有保存控件/无保存控件”两支。
- 如果时间紧：**P0 可先做“观察 + 列表 + launch + stop”**（S1-1 的全文读取建议必须一起做，否则 P0 验收空转）；**P1 的“保存”可先声明为 v1 已知限制**，但必须在 README 与 tool description 明确，不能让模型/用户误以为能保存。

---

## 附录 A. 证据快照（可复核）

### A.1 基线

```text
README.md 53 / docs/DESIGN.md 788 / docs/ARCHITECTURE.md 896 / docs/PROTOCOL.md 560
contracts/whalego.tools.v1.json 588 / docs/NAMING.md 196
docs/reviews/design-review-01.md 117 / -response.md 57 / -verdict.md 53
docs/evidence/container-env.md 59
```

### A.2 AstrBot 侧源码核验（ref/AstrBot/，相对路径）

| 事实 | 证据 |
|---|---|
| 全树 `isError` 零处理 | `grep -rn isError / is_error ref/AstrBot` → 0 命中（两种写法分别 grep） |
| 工具结果只拼接 TextContent（isError 不参与） | `agent/runners/tool_loop_agent_runner.py:1294-1364` |
| 图片缓存后注入，仅 provider modalities 空或含 image | `agent/runners/tool_loop_agent_runner.py:1109-1142`；图片占位文本 `1319-1323` |
| 工具异常兜底文本 `error: {e}`（非 `ERROR[`） | `agent/runners/tool_loop_agent_runner.py:1410-1420` |
| 工具调用超时转普通 Exception | `astr_agent_tool_exec.py:721-745、774-777` |
| `ContextWrapper.tool_call_timeout` 默认 120 | `agent/run_context.py:12-19`；`star/context.py:309-312` |
| provider_settings.tool_call_timeout 文档默认 60 | `docs-dev/astrbot-config.md:389-393`；ref 树中未见读取该配置构造 run_context 的代码路径（参考树可能不完整，需容器实测） |
| 结果截断阈值 7000/27500 tokens；只保留前缀一半 + 溢出文件提示 | `agent/runners/tool_loop_agent_runner.py:112-113、402-446、448-465` |
| 溢出机制仅在工具集含 `astrbot_file_read_tool` 时启用 | `star/context.py:342-348` |
| 非内置工具默认权限 = member | `provider/func_tool_manager.py:453-494` |
| 插件工具加载期重绑定（绑定方法会 TypeError） | `star/star_manager.py:1281-1311`；`star/context.py:109-122` |
| legacy mcp 客户端 3 元组解包 | `agent/mcp_client.py:601-603` |
| FunctionTool 的 `call` 以 `(context, **kwargs)` 调用 | `astr_agent_tool_exec.py:813-818` |
| 重复同名同参调用 ≥3 次追加 SYSTEM NOTICE | `agent/runners/tool_loop_agent_runner.py:147-156、1364-1370` |

### A.3 研究侧引用（只作“现实复杂性”佐证，不作为规格）

- `research/zcode/notes/04-windows-input-and-frames.md:267-276`：owned popup/menu/child 四类 surface 与坐标属主；说明 v1“单窗观察”无法覆盖菜单/对话框。
- `research/zcode/notes/04-windows-input-and-frames.md:283-284`：前台 SendInput 绝对坐标需虚拟桌面归一化（负坐标/多屏）。
- `research/zcode/notes/04-windows-input-and-frames.md:169-177`：点击/键入节拍与读回裁决（作为 P1 实现参考，不是 v1 强制）。
- `research/astrbot/AstrBot插件开发约束与能力边界.md:183、:218`：钩子/提示词注入能力（用于 MX-13 的插件使用说明方案）。

### A.4 本文未覆盖

- 执行器 GUI/托盘交互细节（由 `docs/GUI.md` 负责）；
- TLS/网络层、确认流程、多执行器、崩溃恢复（P2/P3）；
- 真实容器内 E2E（尚无实现）；本文所有 AstrBot 结论来自参考源码，最终需容器冒烟复核。

---

> 修订建议：本文件作为 **v1 契约冻结前的最后一份场景走查**；§10 的 P0前 项完成后可关闭 S1 相关缺口，P1前 项完成后可关闭 S2-S5 相关缺口。若某条被判定“v1 不做”，请同时改 README 的“v1 已知限制”并在 tool description 里告诉模型，否则模型仍会尝试。
