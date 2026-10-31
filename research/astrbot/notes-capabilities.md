# AstrBot 插件「能力边界」笔记（task-2）

> 出处缩写（均相对仓库 `D:\tmp\AstrBot-src`）：
> `plugin.md` = `docs/zh/dev/star/plugin.md`（旧版全量指南，1896 行）；`g/xxx.md` = `docs/zh/dev/star/guides/xxx.md`；`cfg.md` = `docs/zh/dev/astrbot-config.md`；`use/xxx.md` = `docs/zh/use/xxx.md`；`pp.md` = `docs/zh/dev/star/guides/plugin-pages.md`。
> `[src]` = `astrbot/` 源码，仅用于补充「文档未公开但源码存在」的内部机制，**不是插件公开契约**。
> 版本基线：master@2026-10-31。旧指南自 v4.5.7 起停止详尽维护，新指南为准（plugin.md:9-12）。

## 0. 能力边界全景（速查）

- 【能力】插件 = 继承 `Star` 的类 + `main.py`，`__init__(self, context: Context, config: AstrBotConfig)`；`terminate()` 卸载/停用时调用（plugin.md:73-88、1016-1027）。
- 【能力】消息侧：命令/指令组/别名/优先级、事件类型与平台过滤、管理员权限、10 类事件钩子、消息链全段型、被动+主动发送、按钮交互（plugin.md:78、340-749；g/send-message.md:90-147）。
- 【能力】AI 侧：取 Provider 调 `text_chat`/`llm_generate`、注册 FunctionTool（类式/装饰器）、`tool_loop_agent` 单/多 Agent、会话历史与 Persona、`decision` 结构化判断（plugin.md:1236-1271；g/ai.md:25、110-382）。
- 【能力】状态侧：插件级 KV、`_conf_schema.json` 插件配置、`data/plugin_data/<plugin>/` 大文件目录、会话级配置读取（g/storage.md:5-33；plugin.md:1016-1036、1814-1821）。
- 【能力】交互侧：`@session_waiter` 多轮会话、`html_render`/`text_to_image` 文转图、`register_web_api` + `views/` WebUI 页面、插件内 `skills/`（g/session-control.md:44-100；plugin.md:1044-1080；pp.md:4-10、30-36）。
- 【限制】核心硬边界：无插件级 Cron 调度 API、无插件级限流/并发 API、无插件级 SessionLock API、无 token 用量 API、无流式输出 API（见 §6、§7 逐条出处）。
- 【限制】平台差异是最大不确定项：非所有消息段/主动消息/按钮/流式都受支持（plugin.md:186-198；g/send-message.md:96-104；cfg.md:375）。
- 【注意】钩子内不能 `yield`，只能 `event.send()`（plugin.md:581、601、622、647、675、702、722、737）。

## 1. 事件注册：@filter.* 装饰器与语义

- 【能力】`@filter.command("helloworld")`：注册指令，默认需唤醒前缀（默认 `/`，cfg.md:558-560）；handler 前两参必须 `self, event`（plugin.md:78-84、101-103）。
- 【能力】带参指令自动解析并转型：`async def add(self, event, a: int, b: int)`，`/add 1 2`（plugin.md:356-364）。
- 【能力】指令组 `@filter.command_group("math")` + `@math.command("add")`；子组用 `@math.group("calc")`（注意是 `group` 不是 `command_group`），理论可无限嵌套；缺子指令时报错并渲染树（plugin.md:372-435）。
- 【能力】别名 `@filter.command("help", alias={"帮助", "helpme"})`（v3.4.28+，plugin.md:437-447）。
- 【能力】事件类型过滤 `@filter.event_message_type(filter.EventMessageType.ALL / PRIVATE_MESSAGE / GROUP_MESSAGE)`（plugin.md:455-470）。
- 【能力】平台过滤 `@filter.platform_adapter_type(filter.PlatformAdapterType.AIOCQHTTP | ...QQOFFICIAL)`，支持位或；完整枚举含 AIOCQHTTP、QQOFFICIAL、TELEGRAM、WECOM、LARK、DINGTALK、DISCORD、SLACK、KOOK、SATORI、WEBCHAT 等（plugin.md:474-483；g/listen-message-event.md:224）。
- 【能力】权限 `@filter.permission_type(filter.PermissionType.ADMIN)` 仅管理员可用（plugin.md:487-494）。
- 【能力】多过滤器叠加为 **AND**：全部通过才执行（plugin.md:496-505）。
- 【能力】优先级 `@filter.command("x", priority=1)`（v3.4.21+），指令/监听器/钩子均支持，默认 `0`，大者先执行（plugin.md:739-749）。
- 【能力】按钮回调 `@filter.button_action("my_plugin:confirm")`；不填 ID 匹配所有回调，用 `event.get_button_interaction()` 取 `ButtonInteraction`（g/send-message.md:135-147）。
- 【能力】LLM 工具 `@filter.llm_tool(name="get_weather")`（g/ai.md:176-187）。
- 【能力】事件钩子（10 种）：`on_astrbot_loaded`(plugin.md:520)、`on_waiting_llm_request`(g/listen-message-event.md:276)、`on_llm_request`(plugin.md:538)、`on_llm_response`(plugin.md:594)、`on_agent_begin`(plugin.md:615)、`on_using_llm_tool`(plugin.md:637)、`on_llm_tool_respond`(plugin.md:664)、`on_agent_done`(plugin.md:692)、`on_decorating_result`(plugin.md:714)、`after_message_sent`(plugin.md:732)；`on_agent_*`/`on_using_llm_tool`/`on_llm_tool_respond` 需 > v4.23.1（plugin.md:605、626、650、679）。
- 【限制】钩子**不能**与 `@filter.command`/`command_group`/`event_message_type`/`platform_adapter_type`/`permission_type` 混用（plugin.md:507-510）。
- 【限制】钩子内不能 `yield`；`on_decorating_result` 只能改 `event.get_result().chain`（plugin.md:714-722）。
- 【限制】指令名不能带空格，否则被解析为第二个参数（plugin.md:348-349）。
- 【注意】`from astrbot.api.event import filter` 必须显式导入，否则与内置 `filter` 冲突（plugin.md:323-327）。
- 【注意】`on_llm_request` 可改 `ProviderRequest`；追加到 `system_prompt` 会破坏提示词缓存，成本约 +7~20 倍，动态内容应走 `req.extra_user_content_parts`（`TextPart`、`.mark_as_temp()`，v4.24.0+）（plugin.md:538-579；g/listen-message-event.md:303-338）。

## 2. 事件对象与拦截/放行/中止

- 【能力】`event.message_str` 纯文本；`event.message_obj` 为 `AstrBotMessage`（plugin.md:82、105-131；g/listen-message-event.md:23-41）。
- 【能力】`AstrBotMessage` 字段：`type/self_id/session_id/message_id/group_id/sender/message: List[BaseMessageComponent]/message_str/raw_message/timestamp`；`session_id` 取决于 `unique_session` 设置（plugin.md:113-131）。
- 【能力】常用取值：`get_sender_name()`(plugin.md:81)、`get_sender_id()`(plugin.md:803)、`get_group_id()`(plugin.md:1220)、`get_platform_name()`(plugin.md:1845)、`get_platform_id()`(g/other.md:18)、`unified_msg_origin`(plugin.md:773)。
- 【能力】结果构造：`plain_result / image_result / chain_result / make_result()`（plugin.md:84、763、809、1165）。
- 【能力】发送：`await event.send(...)`（钩子/waiter 内唯一合法方式）、`event.get_result()`（plugin.md:581、716、1153）。
- 【能力】中止传播：`event.stop_event()`，之后其它插件 handler 与 LLM 请求都不再执行（plugin.md:901-914；g/listen-message-event.md:507-520）。
- 【注意】`event.message_obj.raw_message` 是平台适配器原始对象，跨平台不可移植；取平台专有能力前应 `assert isinstance(event, AiocqhttpMessageEvent)`（plugin.md:131、1845-1856）。
- 【注意】插件之前的核心管线已做白名单、会话开关、限流、内容安全检查与预处理，插件拿到的已是过滤后事件（g/session-control.md:19；cfg.md:184-196）。

## 3. 消息模型与发送

- 【能力】消息链 = `List[BaseMessageComponent]`，有序、保序发送（plugin.md:133-145、802-812）。
- 【能力】消息段：`Plain/text、Face、Record、Video、At、Music、Image、Reply、Forward、Node、Nodes、Poke`（plugin.md:151-167）。
- 【能力】构造方式：`MessageChain().message("Hello!").file_image("path")`（plugin.md:782）；或 `[Comp.At(...), Comp.Plain(...), Comp.Image.fromURL(...)]` + `event.chain_result(chain)`（plugin.md:802-809；g/send-message.md:52-59）；或 `event.make_result().chain = [...]`（plugin.md:1165-1169）。
- 【能力】被动回复：`yield event.plain_result(...)` / `yield event.image_result("path" | "https://...")`，可多次 yield 多条（plugin.md:757-767）。
- 【能力】主动推送：存 `event.unified_msg_origin`，之后 `await self.context.send_message(umo, message_chain)`；UMO 形如 `platform_name:message_type:session_id`（plugin.md:769-790、1577；g/send-message.md:19-40）。
- 【能力】按钮：`Comp.ActionRow(buttons=[Comp.Button(id, label, action=Comp.Button.CallbackAction(data={...}) | UrlAction(url), style=...)], action_timeout=60, fallback_text=...)`（g/send-message.md:106-141）。
- 【限制】消息段平台差异（plugin.md:186-198 表格）：QQ 个人号支持全部段型；`At` 不支持：QQ 官方、企业微信、钉钉；`Record` 不支持：QQ 官方、钉钉；`Video`/`Reply` 不支持：QQ 官方、企业微信、钉钉；主动消息不支持：QQ 官方、企业微信、钉钉；QQ 官方/钉钉发送时被平台强制带 `At`；钉钉图片仅支持 http 链接。
- 【限制】`Comp.File` 部分平台不支持（plugin.md:819；g/send-message.md:72）。
- 【限制】`Comp.Record` 目前只接受 wav 格式，其它需自行转码（plugin.md:825；g/send-message.md:78）。
- 【限制】`Video.fromFileSystem` 要求协议端与机器人同机/同系统，跨机请用 `fromURL`（plugin.md:873；g/send-message.md:159）。
- 【限制】群合并转发 `Node` 仅 aiocqhttp（OneBot v11）可用，大多数平台不支持（plugin.md:837-857；g/send-message.md:168-170）。
- 【限制】`Face`（QQ 表情）仅 aiocqhttp（plugin.md:882-897）。
- 【限制】aiocqhttp 下 `plain` 发送会被 `strip()` 去空格/换行，可用零宽空格 `\u200b` 规避（plugin.md:178-181；g/send-message.md:64-66）。
- 【限制】按钮：支持平台见 g/send-message.md:96-104（OneBot v11/Misskey/微信公众号/weixin_oc 仅文字链接回退，不触发回调）；Satori 取决于所连平台；Mattermost 回调需可达回调地址。
- 【限制】`action_timeout` 必须是有限正数，同一行共享截止时间；到期点击提示已过期、不执行 handler；**复用同一 ActionRow 实例不会刷新有效期**，需新建实例（g/send-message.md:143-144）。
- 【限制】`CallbackAction.data` 必须可 JSON 序列化；`UrlAction` 只开链接、不回调、不受 `action_timeout` 限制（g/send-message.md:145-146）。
- 【注意】`image_result` 的 URL 必须以 `http`/`https` 开头（plugin.md:763-766）。

## 4. 调用 LLM（Provider / 工具 / Agent / 流式）

- 【能力】新方式（>= v4.5.7）：`provider_id = await self.context.get_current_chat_provider_id(umo=umo)`，再 `await self.context.llm_generate(chat_provider_id=..., prompt=... | contexts=[...])`；同时给 `prompt` 与 `contexts` 时 prompt 追加到最后（g/ai.md:14-29、437-442）。
- 【能力】旧方式：`await self.context.get_using_provider_async(umo=event.unified_msg_origin)` 后 `await prov.text_chat(prompt=, context=, system_prompt=)`（plugin.md:1238-1263）；同步版 `get_using_provider()` 已弃用（plugin.md:1244）。
- 【能力】`text_chat()` 可选参数：`func_tool: ToolSet`、`image_urls: List[str]`（支持文件路径）、`model: str`（强制指定模型）、`tool_calls_result: dict`（plugin.md:1266-1271）。
- 【能力】`LLMResponse` 字段：`role/result_chain/completion_text/tools_call_args/tools_call_name/tools_call_ids/raw_completion/is_chunk`、`to_openai_tool_calls()`（plugin.md:1273-1369）。
- 【能力】其它提供商：`get_provider_by_id(provider_id=)`、`get_all_providers()`、`get_using_stt_provider_async`/`get_using_tts_provider_async`、`get_all_embedding_providers()`（plugin.md:1240-1242、1373-1383）。
- 【限制】嵌入/重排序提供商**没有**「当前使用」概念，只用于知识库（plugin.md:1373-1375）。
- 【能力】Tool 类式定义：`@dataclass class X(FunctionTool[AstrAgentContext])`，字段 `name/description/parameters`，`async def call(self, context, **kwargs) -> ToolExecResult`（g/ai.md:114-144）；旧式 `async def run(self, event, greeting)`（plugin.md:1452-1475）。
- 【能力】注册：`self.context.add_llm_tools(ToolA(), ToolB())`（>= v4.5.1）；旧版 `self.context.provider_manager.llm_tools.func_list.append(...)`（plugin.md:1477-1492；g/ai.md:150-160）。
- 【限制】`context.register_llm_tool()` 已弃用；若沿用，`func_args` 必须是字典列表，传字符串列表会 `AttributeError`（g/ai.md:162-169）。
- 【限制】`@filter.llm_tool` 通过 docstring 生成参数 schema，**不读**签名类型注解；缺 `Args:` 或格式不对会让 schema 为空、参数被静默丢弃；**不支持** `parameters=...`（g/ai.md:193-198）。
- 【限制】装饰器工具参数类型仅 `string/number/object/boolean`（plugin.md:1516），v4.5.7+ 增加 `array` 及 `array[string]` 子类型（g/ai.md:191）。
- 【能力】Agent：`await self.context.tool_loop_agent(event=event, chat_provider_id=..., prompt=..., tools=ToolSet([...]), max_steps=30, tool_call_timeout=60, system_prompt=...)`，自动循环工具调用直到不再调用或达 `max_steps`（g/ai.md:200-221、363-382）。
- 【能力】Multi-Agent：`agent-as-tool` 模式，子 Agent 本身是 `FunctionTool`，内部再调 `ctx.tool_loop_agent`（g/ai.md:223-361）。
- 【能力】对话上下文：`self.context.conversation_manager` → `get_curr_conversation_id / get_conversation / new_conversation / switch_conversation / delete_conversation / get_conversations / get_filtered_conversations / update_conversation / get_human_readable_context`；`Conversation.history` 为 JSON 字符串（plugin.md:1529-1677；g/ai.md:384-528）。
- 【能力】写入对话：`conv_mgr.add_message_pair(cid=, user_message=UserMessageSegment(...), assistant_message=AssistantMessageSegment(...))`（g/ai.md:424-450）。
- 【能力】人格：`self.context.persona_manager` 的 `get_persona/get_all_personas/create_persona/update_persona/delete_persona/get_default_persona_v3`；`tools=None` 表示全部工具、`[]` 禁用全部（plugin.md:1679-1751；g/ai.md:530-604）。
- 【能力】结构化判断（另配「判断」提供商）：`self.context.decision.ask({...})` / `.choose()` / `.yes_no(threshold=0.7)`，题型 `DecisionChoice/Boolean/ScoreQuestion`（g/ai.md:32-106）。
- 【限制】`decision` 在提供商未配置、超时、连接失败、HTTP 错误、非法 JSON 或违反选项契约时统一抛 `DecisionError`，插件必须捕获并降级（g/ai.md:108）。
- 【限制】插件文档**未提供流式输出 API**；流式是核心配置项，仅 WebChat、Telegram、飞书生效，不支持时可选降级分段回复（cfg.md:375、383）。源码有 `event.send_streaming`（`[src] astrbot/core/platform/astr_message_event.py:355`）但**未在文档中作为插件契约公开**。
- 【限制】文档未公开 token 计数/用量接口；模型选择靠 `model` 参数或会话默认提供商（plugin.md:1270、g/ai.md:16）。
- 【注意】长期记忆/知识库/外部查询不宜全塞提示词，优先注册 `llm_tool` 让模型按需调用（plugin.md:579）。

## 5. 存储：隔离范围与适配场景

| 机制 | API | 隔离/落盘范围 | 适用 | 出处 |
| --- | --- | --- | --- | --- |
| 简单 KV | `await self.put_kv_data(k, v)` / `get_kv_data(k, default)` / `delete_kv_data(k)` | **插件维度**独立空间，互不干扰；需 AstrBot >= 4.9.2 | 少量配置/临时状态 | g/storage.md:5-18 |
| 插件配置 | `_conf_schema.json` + `AstrBotConfig`（继承 dict，`save_config()`） | `data/config/<plugin_name>_config.json`，Schema 驱动、WebUI 可视化 | 用户可调配置 | plugin.md:928-1036 |
| 大文件 | `get_astrbot_data_path()/plugin_data/<self.name>/` | 插件独立子目录（`self.name` >= 4.9.2） | 缓存、素材、导出文件 | g/storage.md:21-33 |
| 会话级配置 | `self.context.get_config(umo=umo)` | 按 UMO 读取多配置文件（v4.0.0+） | 读取会话生效配置 | plugin.md:1814-1821 |
| 默认全局配置 | `self.context.get_config()` | 全局，**文档建议只读** | 读取全局开关 | plugin.md:1806-1812 |

- 【限制】`secret: true` 只遮罩 WebUI 显示，**不加密**配置文件，插件不应记录/回显该值（plugin.md:974、1002）。
- 【限制】Schema 升级只补缺失项、**不删除**多余项（plugin.md:1036）。
- 【注意】持久化数据必须放 `data` 目录，不能放插件自身目录（更新/重装会覆盖）（plugin.md:208）。
- 【限制】文档未公开「用户偏好设置」类插件 API，也未公开数据库 ORM 作为插件契约；需要结构化数据只能自建 SQLite/文件（文档无对应章节）。
- 【注意】插件可通过 `skills/` 目录随插件分发 Skill，来源只读，可启用/禁用但不能在本地 Skills 页删除或编辑（plugin.md:244-269）。

## 6. 会话控制、并发与限流

- 【能力】`@session_waiter(timeout=60, record_history_chains=False)` 激活后，同一会话后续消息先交给 waiter 处理，直到 `stop()` 或超时（v3.4.36+；plugin.md:1104-1190；g/session-control.md:4-90）。
- 【能力】`SessionController`：`keep(timeout, reset_timeout)`、`stop()`、`get_history_chains() -> List[List[Comp.BaseMessageComponent]]`（plugin.md:1192-1200；g/session-control.md:92-100）。
- 【能力】自定义会话 ID：继承 `SessionFilter` 实现 `filter(event) -> str`，按群维度合并会话（`event.get_group_id() or event.unified_msg_origin`），可实现群内组队（plugin.md:1202-1232；g/session-control.md:102-130）。
- 【注意】默认会话标识基于 `sender_id`，不自定义则群内每人独立（plugin.md:1204）。
- 【限制】waiter 内不能 `yield`，必须 `await event.send(...)`；超时抛 `TimeoutError`，应捕获并 `event.stop_event()`（plugin.md:1160-1185；g/session-control.md:52-85）。
- 【限制】`keep(reset_timeout=True)` 时 `timeout` 必须 > 0，<=0 立即结束；`reset_timeout=False` 时新超时 = 剩余超时 + timeout（可为负）（plugin.md:1196-1198）。
- 【注意】被 waiter 接管的消息**不会**继续触发普通插件或 Agent；且无需再次 @ 或唤醒前缀（g/session-control.md:19）。
- 【限制】会话隔离由核心 `platform_settings.unique_session`（默认 false）控制，仅支持隔离的渠道生效；`AstrBotMessage.session_id` 随之变化（cfg.md:164-166；plugin.md:119）。
- 【限制】限流是核心平台级配置 `platform_settings.rate_limit`：`time` 时间窗、`count` 条数、`strategy` = `stall`（等待）/ `discard`（丢弃）；**文档未提供插件级限流 API**（cfg.md:168-170）。
- 【限制】文档未提及/未公开插件级并发限制或 SessionLock；源码内部有 per-event-loop 的 `session_lock_manager.acquire_lock(session_id)`（`[src] astrbot/core/utils/session_lock.py:33-55`），插件不应依赖该内部实现。
- 【注意】需要周期性任务时文档推荐的路径是 `asyncio.create_task()`（plugin.md:1872-1888），自行做单例/去重，避免同会话并发堆积。

## 7. 文转图、定时、日志与杂项

- 【能力】`await self.text_to_image(text, return_url=True)`：纯文本渲染为图片；`return_url=False` 时返回本地路径（plugin.md:1038-1050；g/html-to-pic.md:7-17）。
- 【能力】`await self.html_render(TMPL, data, options=options)`：HTML + Jinja2（支持 CSS/循环/条件）渲染为图片 URL（plugin.md:1054-1087；g/html-to-pic.md:21-54）。
- 【能力】`options` 即 Playwright `page.screenshot` 参数：`timeout/type/quality/omit_background/full_page/clip/animations/caret/scale/mask`（plugin.md:1089-1102；g/html-to-pic.md:56-68）。
- 【注意】可先用在线 Playground（https://t2i-playground.astrbot.app/）可视化调试模板（g/html-to-pic.md:5）。
- 【能力】日志：必须 `from astrbot.api import logger`（不要用标准库 `logging`）；每个插件 `__init__` 后另有独立的 `self.logger`，等级可在 WebUI 插件配置弹窗单独设置，不影响核心与其他插件（plugin.md:70、96-97）。
- 【限制】Cron/定时任务：**插件文档没有公开的调度 API**。核心有 `CronJobManager` 与 `future_task` 工具，但面向 Agent/未来任务页面（`[src] astrbot/core/core_lifecycle.py:241`、`astrbot/core/cron/manager.py:103`），非插件契约。
- 【限制】未来任务/主动型能力仍是**实验性功能**（v4.14.0 引入）；需 AstrBot 在线、需平台支持主动消息、关闭配置不会删除已建任务；一次性任务触发后删除、失败也删除；循环任务保留；「间隔」是 Cron 步进而非固定计时器（步进上限 59/23/31，缺日期月份不执行）（use/proactive-agent.md:5-18、84、66）。
- 【限制】Cron 任务不经过消息 Pipeline，只共享事件执行生命周期；异常/取消也会结束并标错（use/webui.md:205）。
- 【能力】杂项：`self.context.get_config()`（plugin.md:1809）、`get_platform_inst(platform_id)`（g/other.md:17-22，旧 `get_platform()` 已废弃）、`event.bot` + `client.api.call_action(...)` 调协议端 API（plugin.md:1840-1858；g/other.md:26-44）、`get_all_stars()`（plugin.md:1866-1870）、`platform_manager.get_insts()`（plugin.md:1890-1896）、`asyncio.create_task`（plugin.md:1872-1888）。
- 【能力】插件 WebUI：`views/<view_name>/index.html` + `context.register_web_api(path, handler, ["GET"], desc)`，前端经 `window.AstrBotPluginView` bridge 调后端（pp.md:4-10、30-60）。
- 【限制】可视化视图运行在受限 iframe `allow-scripts allow-forms allow-downloads`，**不能**访问 Dashboard cookies/LocalStorage/父页面 DOM，也不能绕过 bridge 复用 Dashboard 鉴权；后端仍需校验输入（pp.md:645-662）。
- 【限制】旧版 `/api/plugin/page/content/...?asset_token=...` 已移除；不要手拼内容 URL（pp.md:641）。
- 【能力】插件元数据：`metadata.yaml` 的 `display_name`(plugin.md:230-234)、`short_desc`(236-242)、`support_platforms`（key 需用 `ADAPTER_NAME_2_TYPE`，281-296）、`astrbot_version`（PEP 440，如 `">=4.16,<5"`；不满足会阻止加载，WebUI 可「无视警告继续安装」，298-317）、`logo.png` 1:1 推荐 256x256（222-228）。

## 8. 文档明示的限制/注意事项汇总（便于摘录）

1. 【限制】旧指南 plugin.md 自 v4.5.7 起不再详尽维护，以 plugin-new.md 与 guides/ 为准（plugin.md:9-12）。
2. 【限制】平台非全能力：消息段/主动消息/按钮/流式均按平台适配器而异（plugin.md:186-198；g/send-message.md:96-104；cfg.md:375）。
3. 【限制】钩子不能与过滤器混用，且钩子内不能 yield（plugin.md:507-510、581 等）。
4. 【限制】指令名不能含空格；handler 必须在插件类内、文件名必须 main.py（plugin.md:101-103、349）。
5. 【限制】llm_tool 装饰器只认 docstring `Args:`，不认类型注解，不支持 `parameters=`（g/ai.md:193-198）。
6. 【限制】`register_llm_tool` 已弃用；`get_using_provider()`/`get_using_stt_provider()`/`get_using_tts_provider()` 同步接口已弃用（g/ai.md:162-169；plugin.md:1244、1383）。
7. 【限制】`system_prompt` 每轮变化破坏缓存，成本 +7~20 倍（plugin.md:543-549）。
8. 【限制】Record 仅 wav、File 部分平台不支持、合并转发仅 OneBot v11、Face 仅 aiocqhttp、aiocqhttp 会 strip plain（plugin.md:178-181、819-897）。
9. 【限制】按钮 `action_timeout` 复用实例不刷新；过期后点击不执行 handler；部分平台仅文字回退（g/send-message.md:143-147、100-104）。
10. 【限制】KV 仅插件维度（>=4.9.2）；`secret` 不加密；Schema 不删多余项；持久化禁放插件目录（g/storage.md:5-18；plugin.md:974、1002、1036、208）。
11. 【限制】无插件级 Cron / 限流 / 并发 / SessionLock / token 用量 / 流式公开 API（§6、§7 出处）。
12. 【限制】未来任务是实验性功能，需要 AstrBot 在线、平台支持主动消息、模型支持工具调用（use/proactive-agent.md:5、13-14、86）。
13. 【限制】WebUI 视图受 iframe 与 CSP 限制，不能直连 Dashboard 鉴权（pp.md:645-662）。
14. 【注意】开发原则：必须测试、写注释、错误处理不崩溃、提交前 ruff、网络请求用 aiohttp/httpx 而非 requests、扩展现有插件优先提 PR、借鉴需注明来源与许可（plugin.md:200-214）。
