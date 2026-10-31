# PCCtrl 设计评审 01（独立红队）

> 评审对象：`docs/DESIGN.md`（759 行）、`docs/ARCHITECTURE.md`（756）、`docs/PROTOCOL.md`（420），基线 2026-10-31。
> 方法：三文档逐条对照 research/zcode 主报告 §0/§2/§3/§4/§5/§6/§9/§11、notes/04 §7、research/astrbot、ref/AstrBot 4.29.3 源码、`.spike` 实测产物；对每条断言先找反证。
> 局限：未通读 ZCode-main/（按约束）；"容器内 mcp 1.30" 在工作区只有主张、无版本日志；未复跑容器/宿主 E2E。
> 已核实成立（不展开）：MCP C# SDK 2.3.0 + MapMcp/Streamable HTTP、FlaUI 5.0.0、MCP image content 回传（.spike/PcCtrlSpike、UiaProbe）；AstrBot 4.29.3 对 CallToolResult 图片落盘缓存并注入下一步（tool_loop_agent_runner.py:1107-1142、1294-1357）；add_llm_tools(*tools) 与 ToolExecResult=str|CallToolResult 存在（context.py:738、agent/tool.py:17）；新版 mcp 客户端 2 元组 vs legacy 3 元组（.spike/venv mcp 2.3.0）。
> **结论：需修订后实现**（3 个 blocker 必须先改）。

## Blocker

### B1 插件以 `handler=绑定方法` 注册，会在 AstrBot 加载期被错误重绑定，工具调用必失败
- 位置：DESIGN §6.2:417-424；ARCHITECTURE §4.2:594-605（`FunctionTool(..., handler=self._pc_observe)`，handler 为插件实例绑定方法）。
- 证据：ref/AstrBot/star/star_manager.py:1281-1306 对 llm_tools 中 `ft.handler.__module__ == metadata.module_path` 的工具执行 `ft.handler = functools.partial(raw_handler, metadata.star_cls)`；绑定方法的 `__module__` 就是插件 main 模块（已用 Python 验证），重绑后调用 `handler(event)` 变成 `bound(star_cls, event)` → TypeError（参数错位），所有 pc_* 工具报错。另 context.py:109-122、738-754 的 handler_module_path 取 `tool.__module__`，普通 FunctionTool 实例恒为 `astrbot.core.agent.tool`；若注册放在 initialize()（star_manager.py:1424，晚于重绑循环）则归属错误，插件禁用/卸载后工具仍 active（star_manager.py:905-928、1307-1311）。
- 建议：改用官方文档方式 `@dataclass class XTool(FunctionTool[AstrAgentContext])` + `async def call(self, context, **kwargs)`（ref/AstrBot/docs-dev/star/guides/ai.md:45-66）；或 handler 用 main.py 内**未绑定函数**并显式 `handler_module_path=__name__`。不要用绑定方法；DESIGN §6.2、ARCHITECTURE §4.2 示例同步重写，并加一个真实容器冒烟测试（mock 不足）。

### B2 Executor 的 net10.0 TFM 无法引用 net10.0-windows 的 Platform：按文档建库即编译失败
- 位置：ARCHITECTURE §3.1:108-121（Executor=net10.0 → 引用 Platform=net10.0-windows ✔）、:117-121（组合根 Program.cs 在 Executor）；DESIGN §5.1:213-218、§5.2:240-258。
- 证据：.NET 项目引用要求 TFM 兼容，net10.0 不能引用 net10.0-windows；§3.1 矩阵要求 Executor→Platform 项目引用（仅 DI 注册/组合根），因此 TFM 必须兼容，文档现状不可实现。
- 建议：Executor 改 `net10.0-windows`；或拆 `PcCtrl.Host`（net10.0-windows，仅 Program.cs/DI/FlaUI）与 `PcCtrl.Executor`（net10.0 库，供 WebApplicationFactory 单测）；同步改 §2.1 分层图、§3.1 矩阵、§6 目录、§3.11 测试 seam。

### B3 失败/回执双通道语义冲突，模型可能把 possibly_sent 当成功并重放
- 位置：DESIGN §7.4:543-568、PROTOCOL §3.7:273-280（回执三态含 not_sent/possibly_sent）vs PROTOCOL §8:386-388（ACTION_TIMEOUT/DISPATCH_UNCERTAIN 用 isError 错误对象携带 possibly_sent）；错误对象（PROTOCOL §2.3:44-55）有 action_sent/dispatch_status 却无 retry_action，回执有 retry_action。
- 证据：ZCode 的唯一成功判据是 `!isError && dispatch_status!="possibly_sent"`，possibly_sent 必须走错误通道且 actionSent=true（主报告 §5.4:868-871）。AstrBot 侧 ref/AstrBot 全树 `isError` 零处理，tool_loop_agent_runner.py:1294-1364 只拼接 content 文本 → 模型根本看不到 isError 标志。
- 建议：v1 写死单规则：成功仅 `dispatch_status=accepted && isError=false`；not_sent/possibly_sent/verification=mismatched 全部 `isError=true`，错误对象内嵌完整 receipt（action_sent/dispatch_status/retry_action/verification），并在文本首句强制失败语义（AstrBot 丢 isError）；删除回执三态表中 not_sent/possibly_sent 两行，避免"回执=成功"误读。

## Major

### M1 新鲜度模型与"照抄 ZCode 契约"不符：动作后状态仍有效、标题变化即失效、全 session 一把顶替
- 位置：PROTOCOL §5:332-341（新观察顶替全部、标题变化失效、动作后不失效）；DESIGN §2.1:76、§4.3:201 称 state/frame 由服务端内部绑定、模型不传 frame_id（§7.1:499 只写生成），但 PROTOCOL §3.7:226、:250-251 要求模型传 frame_id——两份文档直接矛盾。
- 证据：ZCode 模型面不暴露 frame_id，由服务端绑定最新可动作 frame（主报告 §4.2:600-609、§4.4:666-668）；动作后必须重新观察才能再做元素动作（§4.5:678-681）；标题/枚举顺序不作身份判据（§6.6:1230-1232；notes/04 §7.2 #7:428）；state 按 app 最近观察解析（§4.5:682-683）。
- 影响：模型可拿同一 state_id 连点两次或跨窗口复用索引；标题改变（浏览器、记事本 *）引发 STALE_STATE 风暴；多窗口交替需反复 observe。
- 建议：dispatch 后立即把 state 标记 dirty/失效；element 与 frame 分开新鲜度（element 不因几何移动失效，frame 依赖几何/DPI/采集时刻）；删除"标题变化→失效"，改用 window_generation/HWND+owner_pid+class+几何；每窗口保留最近 state 或明确声明单窗口单 state；三文档统一"模型只传 index 或 x/y，frame_id 内部绑定"。

### M2 坐标链漏掉 UIA hit-test 首级；frame.actionable 无判定标准，PrintWindow 黑帧可被当可点
- 位置：DESIGN §5.5:316-327；ARCHITECTURE §3.6:435-446（坐标=宿主分类→PostMessage→SendInput）；ARCHITECTURE:196 FrameInfo.Actionable、PROTOCOL:184-185/:351 只有字段无规则。
- 证据：ZCode 坐标点击首级是 UIA `ElementFromPoint` 窗口域命中测试，之后才进丢弃矩阵/PostMessage/SendInput（notes/04 §1.2:40；主报告 §6.1:957、§7.3:435）。PrintWindow 对 Chromium/GPU 窗口可能黑屏/过期（ARCHITECTURE §8 #1:720），但设计未将"截图不可信"与 actionable=false 绑定。
- 影响：后台精确路径缺失（更多前台抢焦点）；模型可能对黑帧/过期帧点坐标，产生"点了没点中"。
- 建议：坐标链补 UIA hit-test 级并写入 PROTOCOL；定义 frame.actionable 判据（采集方法+黑帧/内容检测+最小化/遮挡/几何/DPI/TTL），未验证的 PrintWindow 宿主一律 actionable=false 或强制 WGC；advisories 增加 `capture_degraded`。

### M3 P0/P1 工具面未闭环：无启动/激活应用、无观察结算、advisories 无词表、app_id 身份规则不完整
- 位置：DESIGN §7.1:494-503（无 launch/activate）；PROTOCOL §3.6:157-169（observe 无 settle/wait）、:201 advisories:[] 未定义；DESIGN §5.4:305 app_id 规则模糊；PROTOCOL §3.4:124 示例 `win-exe:notepad`（basename）。
- 证据：ZCode 用 get_app_state 隐式拉起应用（含 10s 等窗与焦点语义），没有 launch/activate 就无法"打开计算器"（主报告 §6.6:1236-1249）；观察自带 UI settle，模型不应轮询等待（§4.5:676-677）；app 身份应优先 AUMID、普通应用用 canonical exe 绝对路径、ApplicationFrameHost 不得作身份、标题不作判据（§6.6:1228-1234）；关联错误码 APP_NOT_FOUND/AMBIGUOUS_APP 在 PROTOCOL §8 缺失。
- 建议：P1 增加启动/激活能力（并入 pc_apps 的 action 或独立工具），或把"不启动应用"写入非目标并给出模型替代路径；observe 增加有界 `settle_ms`/结构静止检测 + advisories 闭集（`not_settled`/`effect_evidence_unchanged`/`capture_degraded`）；app_id 定为 `win-exe:<canonical absolute path>`（有 AUMID 时优先 `win-aumid:`），补 APP_NOT_FOUND/AMBIGUOUS_APP。

### M4 AstrBot 工具权限默认值写反，pc_stop(resume) 模型可自解，且无用户可见止血手段
- 位置：DESIGN §6.6:463 `tool_permission` 默认 "admin"；ARCHITECTURE §4.4:627 "默认 admin"；DESIGN §5.6:342、PROTOCOL §3.8:286-291 把 resume 放在模型可调工具参数里；DESIGN §5.10:386、§10.3:681 托盘/指示器排到 P2。
- 证据：ref/AstrBot/provider/func_tool_manager.py:453-458 `_default_permission` 对非内置工具返回 `"member"`；:460-494 仅当 SharedPreferences `tool_permissions._default.<name>="admin"` 才校验——插件工具**默认任何群员可用**，README"不给普通群员"不会自动成立。research §11.6:2209-2210 要求用户可见"正在操作"提示与停止键；notes/04 §2.5 表明前台动作/焦点风险需要用户随时止血。
- 建议：删除插件 `tool_permission` 配置键，改为启动自检 + README 指引管理员在 WebUI 把 6 个工具配 admin，并在 handler 内以 `event.is_admin()` 兜底；pc_stop 对模型只保留"停"，resume 仅 `/pcctrl resume`（ADMIN）或执行器本地热键/托盘；P1 就要有托盘/常驻指示+全局热键（如 Ctrl+Alt+Shift+F12），不要延后。

### M5 观察即把窗口文本/截图交给第三方模型，但无密码屏蔽、无敏感窗口黑名单；截图还会被 AstrBot 提示可转发
- 位置：ARCHITECTURE ElementNode.Value:174-184；PROTOCOL:196-198；DESIGN §8.1:596 隐私对策标 P3。
- 证据：tool_loop_agent_runner.py:1310-1323 对 tool 返回的 ImageContent 缓存后生成文本 "Use send_message_to_user to send it to the user if satisfied" → 模型可把桌面截图发到聊天平台；设计未禁止。设计三份文档也未提及 UIA IsPassword 脱敏，ValuePattern/TextPattern 文本会原样进树/元素表与第三方模型。
- 建议：day-1 规则——IsPassword 控件值一律脱敏 `***`；默认敏感窗口/进程黑名单（密码管理器、银行、凭据 UI）可配；截图默认关闭，插件在 convert.py 固定注入"不得转发截图"提示；observe 审计只记尺寸/元素数；把"截图可能被模型外发"写入威胁模型与 README。

### M6 审计完整性自相矛盾：drop-oldest 队列与"每个写动作必须有审计"不变量冲突
- 位置：ARCHITECTURE §1.2 #9:34（每写动作必有审计）vs §3.8:489-490（Channel drop-oldest、不阻塞请求）。
- 影响：高负载/攻击下动作发生而审计丢失，事故无法复盘，与"全自动+审计"的确认决策兜底承诺不符。
- 建议：写动作审计改同步/背压（队列满 → `AUDIT_UNAVAILABLE` 且 not_sent）；或至少 drop 计数进 pc_health 并告警；审计文件滚动/上限/启动校验可写，禁止静默丢。

### M7 取消/超时语义过度承诺：UIA COM 与已派发 SendInput 不可撤销，超时后必须 possibly_sent 并恢复 shield
- 位置：ARCHITECTURE §1.2 #8:33（所有调用可取消）；DESIGN §6.8:478、§3.1:91。
- 证据：notes/04 §2.5:144-148（不杀线程；single-flight 超时后仍占用；需 AdoptAbandonedShields）；SendInput 入队后无法撤回。AstrBot 外层还用 `asyncio.wait_for`（ref/AstrBot/astr_agent_tool_exec.py:721-745）以 `run_context.tool_call_timeout`（默认 120，ref/AstrBot/agent/run_context.py:19、ref/AstrBot/docs-dev/astrbot-config.md:83）取消插件 handler；插件自设 45s 未说明如何读取/低于该值。
- 建议：PROTOCOL 增加取消契约：派发前取消→not_sent；派发后→让原生调用完成、返回 possibly_sent；RPC 超时不等于动作取消；执行器超时/退出路径做 shield 接管与前台恢复；插件超时后连接标记 unclean、下次调用前重连；插件 timeout 从 AstrBot provider_settings 读取并 clamp；删除"可取消"泛化表述。

### M8 多会话/租约/停止作用域未定义，多 umo 可同时驱动同一桌面
- 位置：PROTOCOL §3.3:106-107（kill switch 写成会话状态）vs §3.8:290（sessions_affected 全局）；ARCHITECTURE §3.9:497-502（每会话串行+跨会话物理串行+全局 kill switch）；DESIGN §5.6:340-343。
- 证据：ZCode 全局唯一输入写者/controller lease，其他 session 得 CONTROLLER_BUSY（主报告 §5.6:924-926）；设计只有串行、没有控制权归属，两个聊天会话的动作仍会交错到同一焦点/剪贴板/前台。
- 建议：引入全局 controller lease（owner+过期/心跳），非 owner 的 pc_act 返回 `CONTROLLER_BUSY`（never 或 admin 抢占）；明确 pc_stop=全局 kill switch（可选 per-session pause）；补多会话并发/抢占测试。

### M9 版本事实链不完整：mcp 1.30 无证据、requirements 无上界、MCP 协议 revision 未记录
- 位置：DESIGN §1.2:55、§3.3:132-133、附录 A #5/#6:738-739；ARCHITECTURE:540 `mcp>=1.30`。
- 证据：工作区唯一 mcp 产物是宿主机 .spike/venv 的 **mcp 2.3.0**：`streamable_http_client` 返回 2 元组（.spike/venv/Lib/site-packages/mcp/client/streamable_http.py:713-744、788）、旧 `streamablehttp_client` 已不存在、HTTP 客户端是 `httpx2`（不能传 headers=，需 http_client=）。而 AstrBot 4.29.3 自带 MCP 客户端按 3 元组解包（ref/AstrBot/agent/mcp_client.py:601）→ 容器必须 mcp<2 才不破坏方案 A；`mcp>=1.30` 无上界且无容器实测记录，属证据缺口。MCP C# SDK 2.3.0 还按协议 revision 校验 header/per-request metadata（NuGet XML `McpProtocolVersions.*`、`StreamableHttpHandler.ValidateProtocolVersionHeader`），设计只有 app 级 `pcctrl/1`，未记录协商出的 MCP revision。
- 建议：requirements 固定 `mcp==1.30.*`（至少 `>=1.30,<2`）并在 initialize 断言版本；文档贴容器 `pip show mcp` 原始输出；compat.py 覆盖 legacy headers= 与新版 http_client(httpx2) 两条构造路径及 2/3 元组；pc_health 增加 mcp_protocol_version；CI 用容器同版本客户端跑 E2E（宿主 2.3.0 通过不代表容器通过）。

### M10 P1 输入矩阵与验收面不匹配：schema 暴露了未实现的按钮/双击/修饰/键盘能力
- 位置：PROTOCOL §3.7:243-248（button/count/modifiers/key 和弦/scroll）；DESIGN §10.3:680 P1 验收仅 WinForms 夹具；ARCHITECTURE §3.6:435-446。
- 证据：ZCode 真机结论：Chromium 双击/右键/修饰、WPF 非前台键盘/文本、组合键、后台滚轮均有确定丢弃/限制（notes/04 §1.2:41-49、§2.2:94-111、§7.3:435-438）；WinForms 单夹具通过不能代表这些路径。
- 建议：P1 schema 收窄为已验证子集（click(left,count=1)、单键、type、scroll），其余返回稳定错误码 `UNSUPPORTED_ACTION`（never）；P1 验收加 WPF + Edge/Chromium + 高完整性窗口 + 用户切走 + noop 矩阵记录（主报告 §11.8:2244-2256）；按钮/双击/修饰/和弦延到 P2。

### M11 pc_act schema 不自治、strategy/perform_action 语义未定义，模型必误用
- 位置：PROTOCOL §3.7:211-237（args 自由 object、仅 prose 描述、无 oneOf/条件 required）；:233 strategy 仅 auto|event 且全文未定义行为；:217 含 perform_action；DESIGN §5.5:334 MVP 不列 perform_action；ARCHITECTURE:218 注释"本期忽略 a11y"与 enum 不符。
- 证据：ZCode 三策略 auto/a11y/event 有明确定义，event 要求目标已前台否则 FOREGROUND_REQUIRED（notes/04 §2.1:79-85）；perform_action 只是 UIA pattern 逃生口（notes/04 §1.2:51），与自动模式梯重复。
- 建议：用 `oneOf` 按 op 定义 target/args 子 schema（button/keys/direction/value/text 全部 typed/enum + 条件 required）；明确 strategy 语义（建议 v1 删除该字段，服务端全自动选路）；perform_action 移出 v1 或从 `elements[].actions` 派生并定义闭集。

### M12 工具数量 6/7 与里程碑不一致（pc_session 可选、pc_stop 未排期）
- 位置：DESIGN §3.5:150、§4.1:165,176、§6.1:407、§6.2:428、§7.1:492、§11:698 均按"7 个"；PROTOCOL §0:11"6 必需+1 可选"、§3.9:303"插件 MVP 不用"；DESIGN §10.3 P0/P1 未把 pc_stop 写进里程碑。
- 建议：v1 定 6 个必需工具（health/apps/windows/observe/act/stop），pc_session 延后并从 FunctionTool 列表及"7 个工具可见"验收中移除；pc_stop 放进 P0；全库统一"6"。

### M13 AstrBot 平台事实：isError 被丢弃、图片仅在 provider 支持 image 时注入——转换层需兜底
- 位置：DESIGN §6.5:445-450、§3.1:90、§9.3:646；PROTOCOL §2.2:38-40。
- 证据：ref/AstrBot 全树 `isError` 零命中，tool_loop 只取 content 文本（tool_loop_agent_runner.py:1294-1364）；图片缓存后仅在 provider `modalities` 为空或含 "image" 时注入（同文件:1107-1142），text-only 模型会静默看不到截图；插件若把 isError 文本写成纯 JSON，模型可能当成功。
- 建议：convert.py 把错误渲染成以"error:/失败"开头的文本 + 结构化 JSON；pc_observe 前检查当前 provider modalities 并在不支持图像时文本降级/提示；README 写明 AstrBot 丢 isError、方案 A 更弱。

## Minor

- m1 **命名/字段/配置键散乱**：click 参数"key"vs"keys"（DESIGN §7.2:508 vs PROTOCOL:246）；pc_observe schema 缺 session_id（PROTOCOL §3.6:157-169 对比 DESIGN §7.1:499）；`state_ttl_seconds` 可配（PROTOCOL:337）但 executor 配置无此项；executor readonly 配置键缺失（DESIGN §8.3:609 声称支持）；`max_screenshot`（插件）/ `screenshot.defaultInclude`（执行器）/ `include_screenshot`（模型）三开关交互未定义；health 字段 os_session/integrity（DESIGN §3.5:150）vs 嵌套 os（PROTOCOL §3.2:87-88）；STOPPED vs SESSION_STOPPED（PROTOCOL:291 vs :376）；错误示例多出 `is_error` 字段（DESIGN §7.5:575）；回执有 `request_id`（PROTOCOL:259）但 pc_act 不收该参数。建议给 PROTOCOL 补一张"唯一字段/键名表"，executor 配置与插件配置各一节，交叉引用。
- m2 **版本标识不统一且 schema_version 未落 wire**：`pcctrl/1`、`protocol_version "1.0"`、`pcctrl-*-v1`、包版本 0.1.0 四套（PROTOCOL §9:396-398、DESIGN §3.5/§7 前注:490）；且所有工具 schema 都不含 schema_version，PROTOCOL §1:28 的"请求里 schema_version 必须匹配"无实现载体。建议 v1 只保留 `protocol_version`（整数 1）+ initialize/health 协商，请求不带 schema_version。
- m3 **mode="diff" 进 v1 但语义空、且实现排 P2**：PROTOCOL §3.6:166 与 DESIGN §7.1:499 vs DESIGN §10.3:681。建议 v1 删 diff，或定义基线/state_id 复用/输出格式。
- m4 **引用与预算不精确**：DESIGN §5.4:310 把"≤1024 宽 + 200KiB"都归为 ZCode 预算，但 ZCode 是 base64≤200KiB、decoded≤153600B、最长边≤2000（主报告 §4.6:699-700；1024 宽是自定值）；图片顺序 PROTOCOL 文本在前（§3.6:171-175），ZCode 实测需 image 在前才能被 Anthropic 网关看到（§3.6:515）；附录 A #14 的"UIA bounds 为物理像素"未被 UiaProbe 代码直接验证（.spike/UiaProbe/Program.cs 只打印 DPI/bounds）。建议改成"自定预算"并补验证记录。
- m5 **分层规则自相矛盾**：ARCHITECTURE §2.2:95 "L0-L3 不 import astrbot"，但 L3 tools.py 要构造 FunctionTool（只能从 `astrbot.api` 导入，ref/AstrBot/api/__init__.py:5,76）；DESIGN §6.1:409 session.py"观察缓存"与 ARCHITECTURE 不变量 4:29"插件不缓存 UI 状态"冲突。建议：工具壳/FunctionTool 构造上移 main.py（L4），L3 只保留 schema 常量与纯转换；删除"观察缓存"改为"最近 state_id 提示"。
- m6 **错误码表两文档漂移**：DESIGN §5.9:367-381 缺 UNSUPPORTED_PROTOCOL、SESSION_STOPPED、FRAME_NOT_ACTIONABLE、ELEMENT_ACTION_UNAVAILABLE、DISPATCH_UNCERTAIN，且 FOREGROUND_REQUIRED/INTERNAL 的 retry 与 PROTOCOL §8:384-388 不一致；PROTOCOL 的 RATE_LIMITED 只给 retry 无 `retry_after_ms`。建议以 PROTOCOL 为单一事实源，DESIGN 只引用；错误对象加 `retry_after_ms`。
- m7 **receipt 泄漏内部术语**：PROTOCOL §3.7:266 模型可见 receipt 带 `dispatch.method:"uia"`，与 DESIGN §1.2:32/ARCHITECTURE §1.2 #7:32"不出现 PostMessage/Invoke/升级路径"及 notes/04 §2.4:123"method 投影为中性值"冲突。建议模型面删 dispatch 或只留 `escalated:bool`，完整 method 只进审计。
- m8 **AstrBot 结果预算/超时表述**：DESIGN §6.5:447 插件截断"≤8k tokens"无依据，AstrBot 预览阈值是 7000 tokens、硬阈值 27.5k（tool_loop_agent_runner.py:112-113、402-414），超限会落文件并只给路径；DESIGN §1.2:54 "handler 无框架级超时"不准确——LLM FunctionTool 有 `asyncio.wait_for`（astr_agent_tool_exec.py:721-745），无超时的是事件 handler。建议插件预算对齐 AstrBot 常量（≤7000），并修正文案区分两类 handler。
- m9 **"SSE 一行开关"未验证**：DESIGN §3.4:138。工作区只验证 Streamable HTTP（.spike/PcCtrlSpike/Program.cs:6-8,28）；MCP C# SDK 2.3.0 ASP.NET 包 XML 中仅见 `StreamableHttpHandler` 与 SSE event-stream 内部类型，未见 legacy SSE server 传输入口。建议删除"一行开关"或补 spike 证据。

## 评审结论

- **需修订后实现**。三份文档的"为什么/怎么分层"总体成立（选型、观察/动作分离、回执三态、窄接口、测试 seam 方向正确），但存在 3 个 blocker 与 13 个 major：插件注册方式会直接跑不起来、Executor 无法编译、失败通道会让模型误判；此外新鲜度/坐标/工具面/安全止血/取消/多会话/版本链均需在动工前闭环。
- 修订成本估计：B1/B2 是文档与示例级（0.5 天），B3/M1/M2/M11 需要协议 v1 重写（1-2 天），其余可随 P0 设计补齐。

### Top5 优先修复项（按顺序）

1. **B1**：重写插件工具注册（subclass+call 或 main.py 未绑定函数 + 显式 handler_module_path），加容器内真实加载/调用冒烟；同时修 DESIGN §6.2 / ARCHITECTURE §4.2 示例。
2. **B3**：v1 单失败通道规则 + 错误对象内嵌 receipt + 失败文本强制前缀（AstrBot 丢 isError）；回执表删除 not_sent/possibly_sent。
3. **M2**：坐标链补 UIA hit-test，定义 frame.actionable（PrintWindow 未验证宿主不可点坐标）；同步 PROTOCOL §3.6/:351。
4. **安全三件套 M4+M5+M6**：工具权限默认 member 的纠正与 admin 配置引导、pc_stop 拆"停/恢复"、密码屏蔽+敏感窗口黑名单+截图不转发提示、审计不失真。
5. **M1**：统一新鲜度契约（动作后失效、element/frame 分离、标题不作判据、模型不传 frame_id），并修 B2 的 TFM/工程骨架（机械改动，随同一次修订）。
