# AstrBot 插件运行机制与硬性限制（源码核实）

- 核实对象：/d/tmp/AstrBot-src（AstrBot 官方仓库稀疏克隆，master@2026-10-31）
- 版本基线：`__version__ = "4.29.3"` — astrbot/__init__.py:3；`VERSION = __version__` — astrbot/core/config/default.py:36；pyproject.toml:3
- 证据路径均相对 /d/tmp/AstrBot-src；行号为该工作副本实测行号
- 强度标注：【硬性】= 代码路径强制，违规即失败；【软性】= 仅解析/展示，运行时不校验；【未证实】= 未找到强制或完整路径

---

## 1 插件加载

### 1.1 目录扫描与入口文件约定
- 插件根目录 data/plugins（astrbot/core/star/star_manager.py:204-205；get_astrbot_plugin_path 见 astrbot/core/utils/astrbot_path.py:49-51）；保留插件根目录 astrbot/builtin_stars（star_manager.py:208-211）
- 【硬性】扫描规则：目录内必须存在 main.py；否则必须存在同名文件 <dir>.py；两者皆无则跳过（star_manager.py:291-320）
- 暂存目录前缀 .plugin-install- / .plugin-upload- / .plugin-backup- 不被当作插件（star_manager.py:297-299）
- 模块路径固定拼为 data.plugins.<dir>.<main|dir>；保留插件为 astrbot.builtin_stars.<dir>...（star_manager.py:1122-1123）；归属判定见 star_manager.py:707-739

### 1.2 metadata.yaml 解析与校验
- 文件名元组 ("metadata.yaml", "metadata.yml")（astrbot/core/star/updater.py:28）
- 【硬性】必填字段 ("name", "desc", "version", "author")，且必须是非空字符串；description 可作 desc 别名（updater.py:29、updater.py:360-396）
- 【硬性】文件 ≤1MB、必须 UTF-8（updater.py:416-421）
- 字段映射：_load_plugin_metadata() 生成 StarMetadata（star_manager.py:491-565）；short_desc / display_name / support_platforms / astrbot_version / views（pages 为别名）/ i18n 均为可选（star_manager.py:534-562）
- 【硬性】插件名 → 安装目录名：取 metadata 的 name，禁止含 / 或反斜杠，必须是合法 Python 标识符且非关键字（star_manager.py:626-671）
- 【软性】未知字段不校验：validate_plugin_metadata 只检查必填项（updater.py:377-396）
- 【软性/易错】新式加载路径下 metadata.yaml 缺失不会导致加载失败：metadata_yaml 为 None 时沿用 Star.__init_subclass__ 建立的 StarMetadata（star_manager.py:1176-1201），解析异常也只 logger.warning 后继续（star_manager.py:1197-1201）；此时 plugin_id 退化为 unknown/unknown（astrbot/core/star/star.py:78-82）。旧式（无 star_map 注册）路径才强制要求 metadata（star_manager.py:1346-1351）

### 1.3 支持平台 support_platforms —— 不强制
- 仅解析为 list[str] 存入元数据（star_manager.py:542-550、1193），并在面板与失败记录中展示（dashboard/services/plugin_service.py:659；star_manager.py:848）
- 全仓 grep 仅 4 处赋值/展示，无任何运行时校验或拦截（【软性】）
- 结论：平台适配必须由插件自己用 @filter.platform_adapter_type(...) 约束（astrbot/core/star/register/star_handler.py:301-318）

### 1.4 插件类约束
- 【硬性】新式：继承 astrbot.api.star.Star 即被 Star.__init_subclass__ 自动登记进 star_map / star_registry（astrbot/core/star/base.py:53-64）
- 基类签名 def __init__(self, context: Context, config: dict | None = None)（base.py:27）；实例化先试 (context, config)，捕获 TypeError 后退回 (context,)（star_manager.py:1230-1241）→ 插件 __init__ 可只收 context
- 旧式回退：类名以 plugin 结尾（忽略大小写）或等于 main（star_manager.py:280-288）；都找不到则抛「未通过 Star 注册…请确认插件主类继承 astrbot.api.star.Star」（star_manager.py:1319-1324）
- 实例属性注入：name / author / plugin_id 在实例化之前写到类上（star_manager.py:1220-1224、1243-1246）
- Handler 绑定：装饰器在 import 阶段登记裸函数，随后 loader 用 functools.partial(raw_handler, star_cls) 绑定实例（star_manager.py:1263-1279）；LLM 工具同理（star_manager.py:1282-1311）→ 装饰器须在模块/类体层级，handler 首参为 self

### 1.5 加载流程与失败处理
- load() 主流程（star_manager.py:1076-1482）：读 inactivated_plugins → 扫描 → import（含依赖恢复 1134-1141）→ 载入 _conf_schema.json → 版本校验 → 实例化 → 绑定 handler → 应用 alter_cmd 权限覆盖（1390-1418）→ await initialize()（1422-1424）→ 触发 on_plugin_loaded（1426-1437）
- 失败处理：import 失败或任意 BaseException 均记入 failed_plugin_dict 并 _cleanup_plugin_state()（star_manager.py:1142-1157、1439-1455）；load() 返回 (False, failed_plugin_info)（1479-1482）
- 清理范围：sys.modules、star_map、star_registry、star_handlers_registry、llm_tools、平台适配器（star_manager.py:776-819）
- 失败记录含 traceback，供面板重试（star_manager.py:821-859）；reload_failed_plugin(dir_name) 可重试单个失败插件（star_manager.py:990-1016）
- 重载：reload() 先 _terminate_plugin() 再 _unbind_plugin()；全量重载清空三个注册表（star_manager.py:1039-1056），随后重新 load()（1072）
- 终止：terminate() 被 await；若插件类自己定义了 __del__ 则改用 loop.run_in_executor(None, __del__) 在线程池执行（star_manager.py:1947-1964）；之后触发 on_plugin_unloaded（1968-1979）
- 启停：禁用写 shared_preferences 的 inactivated_plugins（按 module_path）（star_manager.py:1905-1929）；启用触发 reload()（1981-1999）
- 【硬性】保留插件（builtin）不可卸载、不可更新（star_manager.py:1699-1700、1888-1889、2073-2074）
- 热重载仅在环境变量 ASTRBOT_RELOAD=1 且安装 watchfiles 时启用，递归监听两个插件目录（star_manager.py:222-240）→ 默认关闭

### 1.6 依赖安装（运行时边界重点）
- 插件目录 requirements.txt 会被自动安装：_ensure_plugin_requirements()（star_manager.py:353-379）→ 缺失依赖预检 → pip_installer.install()（star_manager.py:139-190）
- pip 在 AstrBot 自身进程内执行（from pip._internal.cli.main import main，astrbot/core/utils/pip_installer.py:83-97、1104-1119、1125-1126）→ 插件依赖直接改动宿主 Python 环境，仅靠 core-constraints 锁保护核心依赖版本（pip_installer.py:1019-1028）
- import 失败还有依赖恢复/重装路径（star_manager.py:435-490，模式枚举 89-99）→ 加载插件本身会触发代码安装

---

## 2 事件与钩子

### 2.1 EventType 清单（astrbot/core/star/star_handler.py:219-241）
OnAstrBotLoadedEvent:225、OnPlatformLoadedEvent:226、AdapterMessageEvent:228、OnWaitingLLMRequestEvent:229、OnLLMRequestEvent:230、OnLLMResponseEvent:231、OnAgentBeginEvent:232、OnAgentDoneEvent:233、OnDecoratingResultEvent:234、OnCallingFuncToolEvent:235、OnUsingLLMToolEvent:236、OnLLMToolRespondEvent:237、OnAfterMessageSentEvent:238、OnPluginErrorEvent:239、OnPluginLoadedEvent:240、OnPluginUnloadedEvent:241

### 2.2 Filter 类型清单（astrbot/core/star/filter/）
- CommandFilter（命令名 + alias + 参数解析）：filter/command.py:31-220；装饰器 register/star_handler.py:76-127
- CommandGroupFilter（指令组）：filter/command_group.py:14；register/star_handler.py:209-249
- RegexFilter：filter/regex.py:10-18；register/star_handler.py:321-333
- EventMessageTypeFilter（GROUP / PRIVATE / OTHER / ALL）：filter/event_message_type.py:10-33；register/star_handler.py:263-275
- PermissionTypeFilter（ADMIN / MEMBER / GROUP_ADMIN / SHARED_GROUP_ADMIN）：filter/permission.py:9-53；register/star_handler.py:336-358
- PlatformAdapterTypeFilter（20 个平台 + ALL）：filter/platform_adapter_type.py:9-74；register/star_handler.py:301-318
- ButtonActionFilter（按钮回调）：filter/button_action.py:7-26；register/star_handler.py:278-298
- CustomFilter / CustomFilterAnd / CustomFilterOr（支持 & 与 | 组合）：filter/custom_filter.py:21-62；register/star_handler.py:130-206

过滤语义（astrbot/core/pipeline/waking_check/stage.py）：
- 同一 handler 的多个 filter 为 AND；len(event_filters)==0 的 handler 永不激活（waking_check/stage.py:196-211）
- PermissionTypeFilter 不参与 AND 短路，单独记录 raise_error 后决定「跳过 / 回显无权限 + stop_event」（waking_check/stage.py:205-208、224-241）
- 任一 filter 抛异常 → 向用户回显「插件 X: 错误」并 stop_event()（waking_check/stage.py:212-222）
- CommandFilter 只在 event.is_at_or_wake_command 为真时匹配（filter/command.py:191-193）；解析出的参数写入 event.set_extra("parsed_params", params)（filter/command.py:218），再由 handler 调用时以 kwargs 传入（waking_check/stage.py:251-254；process_stage/method/star_request.py:42、74）
- RegexFilter 不受 wake_prefix 约束（filter/regex.py:9-18）
- 会话级插件启停另有一层过滤（astrbot/core/star/session_plugin_manager.py:48-89）

### 2.3 Handler 优先级
- 优先级存于 StarHandlerMetadata.extras_configs["priority"]，缺省 0（star_handler.py:19-23）
- 注册表 append() 后按 -priority 排序 → 数值越大越先执行（star_handler.py:24-26）
- 装饰器的 **kwargs 原样写入 extras_configs（register/star_handler.py:48-73，md.extras_configs = kwargs）→ 任意事件装饰器都可传 priority=
- 执行顺序沿用注册表顺序：waking_check 收集 activated_handlers（waking_check/stage.py:180-183、250），star_request 顺序遍历执行（star_request.py:39-49）
- enabled 字段为外部开关（star_handler.py:275；过滤逻辑 161-162）

### 2.4 中断机制
- event.stop_event() 生效点：stage 边界检查 event.is_stopped()（pipeline/scheduler.py:58-62、68-72、78-82）；同批 handler 循环内检查（star_request.py:40-41、88-89、123）
- handler 正常返回后 event.clear_result() 清空上一条结果（star_request.py:90）→ 多 handler 的结果默认不累积，除非 stop_event
- 钩子中 stop_event() 会中止后续钩子并返回 True（pipeline/context_utils.py:148-152）

### 2.5 ON_* 钩子调用点（参数契约）
- on_astrbot_loaded → core/core_lifecycle.py:373-383，无参数（await handler.handler()）
- on_platform_loaded → core/platform/manager.py:247-257，无参数（每加载一个平台触发一次）
- on_plugin_loaded → star_manager.py:1426-1437，参数 metadata
- on_plugin_unloaded → star_manager.py:1968-1979，参数 metadata
- on_plugin_error → process_stage/method/star_request.py:108-115，参数 (event, plugin_name, handler_name, error, traceback_text)
- on_waiting_llm_request → process_stage/method/agent_sub_stages/internal.py:226，参数 event
- on_llm_request → internal.py:307（内置 Agent）、third_party.py:333（第三方 Agent），参数 (event, req)
- on_llm_response → core/astr_agent_hooks.py:31-35，参数 (event, llm_response)
- on_agent_begin → astr_agent_hooks.py:14-21，参数 (event, run_context)
- on_agent_done → astr_agent_hooks.py:36-41，参数 (event, run_context, llm_response)
- on_using_llm_tool → astr_agent_hooks.py:43-54，参数 (event, tool, tool_args)
- on_llm_tool_respond → astr_agent_hooks.py:56-70，参数 (event, tool, tool_args, tool_result)
- on_decorating_result → pipeline/result_decorate/stage.py:160，参数 event
- after_message_sent → pipeline/respond/stage.py:406，参数 event

钩子契约【硬性】：
- 必须声明为 async def：assert inspect.iscoroutinefunction(handler.handler)（pipeline/context_utils.py:114-115）→ 异步生成器不能当钩子
- 顺序调用（注册表即 priority 序）；单个钩子抛 BaseException 只记日志、不中断（context_utils.py:95-146）
- 钩子返回后若 event.is_stopped() 立即中止并返回 True（context_utils.py:148-154）

### 2.6 Handler 返回契约（pipeline/context_utils.py:12-75）
- handler 可为协程或异步生成器（分别处理：inspect.isasyncgen :47-67、inspect.iscoroutine :68-75）
- 协程返回 MessageEventResult 或 CommandResult → event.set_result() 并 yield；其他值透传给下一 stage（:68-75）
- 异步生成器每个 yield 都交回控制权并进入后续 stage（洋葱模型）→ 流式输出（:47-67；scheduler.py:53-72）
- 调用签名不匹配时 TypeError 只记日志、handler 被静默跳过（context_utils.py:36-45）

### 2.7 Pipeline 阶段顺序（9 段，固定）
WakingCheckStage → WhitelistCheckStage → SessionStatusCheckStage → RateLimitStage → ContentSafetyCheckStage → PreProcessStage → ProcessStage → ResultDecorateStage → RespondStage
- 顺序表 astrbot/core/pipeline/stage_order.py:3-13；按 STAGES_ORDER.index 排序（scheduler.py:25-27）；内置阶段模块清单 pipeline/bootstrap.py:7-29
- 插件 handler 只在 ProcessStage 内被调用（process_stage/stage.py:43-60 → star_request.py）；LLM 调用在其后的 AgentRequestSubStage（process_stage/stage.py:62-76）
- 洋葱模型：stage 返回异步生成器时，后续 stage 在其 yield 点之后递归执行（scheduler.py:38-82）

---

## 3 消息模型

### 3.1 消息段类型（astrbot/core/message/components.py:64-91，ComponentType 共 27 种）
- 基础段：Plain:66、Image:67、Record:68、Video:69、File:70、ActionRow:71、Button:72、ButtonInteraction:73
- IM 段：Face:76、At:77、Node:78、Nodes:79、Poke:80、Reply:81、Forward:82、RPS:83、Dice:84、Shake:85、Share:86、Contact:87、Location:88、Music:89、Json:90、Unknown:91
- 组件均为 pydantic BaseModel，type 字段固定（components.py:94-97）；类定义行号：Plain 134、Button 153、ActionRow 218、ButtonInteraction 250、Face 262、Record 270、Video 423、At 545、AtAll 560、RPS 567、Dice 574、Shake 581、Share 588、Contact 599、Location 608、Music 619、Image 636、Reply 725、Poke 755、Forward 788、Node 796、Nodes 850、Json 877、Unknown 887、File 905
- toDict() 默认输出 {"type": type.lower(), "data": {...}}；Plain 特例输出 {"type": "text", "data": {"text": ...}}（components.py:119-145）；to_dict() 为 async 版本，默认回退 toDict()（components.py:129-131）
- 平台原生转换由各适配器自行实现（astrbot/core/platform/sources/*）；逐平台覆盖度未逐一核实（【未证实】）

### 3.2 MessageChain 操作（astrbot/core/message/message_event_result.py:18-193）
derive():36、message():49、at(name, qq):60、at_all():71、error():83、url_image():93、file_image():106、base64_image():118、use_t2i():127、use_markdown():137、get_plain_text():149、squash_plain():170
- 元数据字段：use_t2i_ / use_markdown_（None = 跟随配置或平台默认）、type（message_event_result.py:28-34）
- MessageEventResult(MessageChain)：stop_event():246、continue_event():251、is_stopped():256、set_async_stream():260、set_result_content_type():265、is_llm_result():275；EventResultType:195、ResultContentType:208

---

## 4 插件上下文 API

### 4.1 Context（astrbot/core/star/context.py:132-133，注释即「暴露给插件的接口上下文」）
构造注入的管理器（context.py:143-182）：provider_manager、platform_manager、conversation_manager、message_history_manager、persona_manager、astrbot_config_mgr、kb_manager、cron_manager、subagent_orchestrator、decision；类属性 registered_web_apis、html_renderer（:135-137）

| 方法 | 行号 | 说明 |
|---|---|---|
| llm_generate(*, chat_provider_id, prompt, image_urls, audio_urls, tools, system_prompt, contexts, **kwargs) | 184-226 | 单次 LLM 调用，不自动执行工具 |
| tool_loop_agent(*, event, chat_provider_id, ..., max_steps=128, tool_call_timeout=120) | 228-368 | 带工具循环的 Agent 调用 |
| get_current_chat_provider_id(umo) | 370 | 当前会话 Provider ID |
| get_registered_star(name) / get_all_stars() | 387 / 393 | 插件元数据查询 |
| get_llm_tool_manager() | 397 | 工具管理器 |
| activate_llm_tool(_async) / deactivate_llm_tool(_async) | 402 / 416、434 / 445 | 工具启停 |
| get_provider_by_id / get_all_providers / get_all_tts_providers / get_all_stt_providers / get_all_embedding_providers | 456 / 487 / 491 / 495 / 499 | Provider 访问 |
| get_using_provider(_async)、get_using_tts_provider(_async)、get_using_stt_provider(_async) | 504/529、561/581、605/625 | 按会话取当前 Provider |
| get_config(umo=None) | 648-663 | 默认配置或按会话配置 |
| send_message(session, message_chain) -> bool | 665-746 | 主动发消息；找不到平台返回 False |
| add_llm_tools(*tools) | 748-772 | 动态添加 LLM 工具 |
| register_web_api(route, view_handler, methods, desc) | 774-796 | 注册面板 Web API |
| get_db() | 846-852 | 数据库实例 |
| register_provider(provider) | 854-860 | 注册 Provider |
| get_platform_inst(platform_id) | 830-844 | 按 ID 取平台适配器 |
| 已废弃：get_event_queue、register_llm_tool、unregister_llm_tool、register_commands、register_task、get_platform | 802 / 862 / 895 / 908 / 950 / 806 | 代码内 @deprecated |

### 4.2 Star 基类（astrbot/core/star/base.py）
text_to_image(text, return_url=True, template_name=None, umo=None)（:66-102）、html_render(tmpl, data, ...)（:104-139）、initialize()（:141，激活时调用）、terminate()（:144，禁用/重载时调用）、__del__（:147 已废弃）；self.logger 为插件独立 logger（:39-51，LogManager.get_plugin_logger）
- 存储：put_kv_data / get_kv_data / delete_kv_data（astrbot/core/utils/plugin_kv_store.py:14-27），底层 sp scope="plugin"、scope_id=plugin_id → 按插件隔离的是逻辑命名空间，不是文件系统

### 4.3 StarTools（astrbot/core/star/star_tools.py）
send_message（:35-54）、create_message（:57-101）、create_event（:104 起）、get_data_dir(plugin_name=None) -> Path（:244-297，落在 data/plugin_data/<plugin_name> 并自动创建）

### 4.4 AstrMessageEvent 常用 API（astrbot/core/platform/astr_message_event.py）
send:553、send_streaming:355、send_typing:369、stop_event:418、continue_event:426、is_stopped:434、set_result:389、plain_result:474、image_result:478、chain_result:487、request_llm:495、react:585、get_group:594、set_extra:292、get_extra:296、should_call_llm:442、get_messages:232、get_message_str:192、is_private_chat:330、is_admin:338

### 4.5 插件可 import 的稳定入口
- astrbot.api（astrbot/api/__init__.py:73-83 的 __all__：AstrBotConfig、BaseFunctionToolExecutor、FunctionTool、ToolSet、agent、html_renderer、llm_tool、logger、sp）
- astrbot.api.event（api/event/__init__.py:1-17）；astrbot.api.event.filter（全量装饰器与过滤器，api/event/filter/__init__.py:1-78）
- astrbot.api.star（api/star/__init__.py:1-7：Context、Star、StarTools、register）；astrbot.api.message_components、astrbot.api.web、astrbot.api.util、astrbot.api.provider、astrbot.api.platform

---

## 5 运行时边界

### 5.1 全异步，共享单一事件循环【硬性】
- handler 必须可 await（协程或异步生成器，pipeline/context_utils.py:47-75）；钩子必须 async def（context_utils.py:115）
- 每条消息事件派发为一个独立 asyncio Task：task = asyncio.create_task(scheduler.execute(event))（astrbot/core/event_bus.py:55-75，关键行 73）；dispatch 自身是单个常驻 Task（core_lifecycle.py:303-310）
- 因此插件里的同步阻塞调用（time.sleep、同步 HTTP、重 CPU、阻塞 IO）会阻塞整个 AstrBot 事件循环 → 最强的一条隐性硬约束

### 5.2 超时 / 沙箱 / 权限 —— 均不存在
- core/pipeline 与 core/star 中 asyncio.wait_for / asyncio.timeout 仅命中 core/star/updater.py:92（git clone 超时）→ 插件 handler 无执行超时
- core/star 目录 grep sandbox / seccomp / bubblewrap / seatbelt / setrlimit 无命中 → 插件无进程隔离、无 CPU/内存限额、无权限声明
- default.py:12-33 的 allow_execution / allow_network / filesystem_scope 只被 core/tools/**（LLM computer 工具）消费（core/tools/computer_tools/util.py:83-92、shell.py:118 等），插件加载与执行路径未引用（通读 star_manager.py 未发现相关校验）

### 5.3 并发与「会话锁」session_lock
- 消息级并发：每个事件一个 Task（event_bus.py:73），不同会话完全并发
- 插件 handler 不按会话串行：star_request 顺序执行同一事件的多个 handler（star_request.py:39-49），但不与其他事件的 handler 互斥
- 真正的会话锁只包住 LLM Agent 请求：async with session_lock_manager.acquire_lock(event.unified_msg_origin)（process_stage/method/agent_sub_stages/internal.py:239）
- 锁实现：每事件循环一个 _PerLoopSessionLockManager，按 session_id 维护 asyncio.Lock，引用计数归零即回收（astrbot/core/utils/session_lock.py:8-52）；跨线程用 threading.Lock 保护 loop 映射（:33-46）

### 5.4 插件代码在何处执行
- 消息处理：EventBus.dispatch 创建的 pipeline Task 内（event_bus.py:73）→ ProcessStage → StarRequestSubStage → call_handler
- 加载与 initialize()：在 AstrBotCoreLifecycle.initialize() 主流程里 await（core_lifecycle.py:264-267）→ 插件可在 initialize() 里 asyncio.create_task() 起常驻任务；旧式 context.register_task 注册的协程也被收集执行（core_lifecycle.py:325-328；context.py:950-963）
- terminate() 在事件循环内 await；仅自定义 __del__ 被丢到线程池（star_manager.py:1947-1964）

### 5.5 插件可访问的系统资源 —— 无限制
- 进程内 Python 代码：完整标准库、os / subprocess / ctypes、任意文件路径、任意网络出站；data/plugin_data/<name> 与 KV 只是约定而非强制隔离
- 可注册面板 Web API（context.py:774-796）、注册 Provider 与 LLM 工具（context.py:748-772、854-860）、通过 get_config 拿到并修改 AstrBotConfig 对象
- 加载期副作用：import 模块顶层代码与 initialize() 全部以宿主权限执行；requirements.txt 由进程内 pip 安装（见 1.6）

### 5.6 配置与存储隔离
- 配置 schema：插件目录 _conf_schema.json（star_manager.py:212、1159-1173），解析失败直接抛错（610-624）
- 配置文件：data/config/<root_dir_name>_config.json（star_manager.py:1167-1173），以 config 构造参数注入插件
- 插件数据目录：data/plugin_data/<plugin_name>（star_tools.py:244-287；astrbot_path.py:61-63）
- KV 存储：shared_preferences 的 plugin scope + plugin_id（plugin_kv_store.py:9-27）
- i18n：插件目录 .astrbot-plugin/i18n/<locale>.json，单文件 ≤1MB、locale ≤32 字符（star_manager.py:568-608）

### 5.7 插件市场安装与信任边界
- 市场清单 URL：https://api.soulter.top/astrbot/plugins 与 https://github.com/AstrBotDevs/AstrBot_Plugins_Collection/raw/refs/heads/main/plugin_cache_original.json；MD5 端点 https://api.soulter.top/astrbot/plugins-md5（dashboard/services/plugin_service.py:1070-1089）
- MD5 仅用于缓存新鲜度（plugin_service.py:1105-1141），不是代码完整性校验；core/star 内 grep hashlib / sha256 / signature / gpg 无命中 → 无签名校验
- 安装链路：市场条目给出 repo 与可选 download_url（plugin_service.py:1276-1329）→ PluginManager.install_plugin()（star_manager.py:1620-1656）→ _PluginUpdater.install()（astrbot/core/star/updater.py:203-244）
- 传输：GitHub 走 archive zip（astrbot/core/repository.py:57-123、199-209）；非 GitHub 主机走 git clone（repository.py:210-223；updater.py:92-111）；允许 SSH/SCP 形式，URL 内嵌凭据被拒绝（repository.py:158-169）
- 本地 zip 安装：解压到临时目录后 _install_plugin_from_directory（star_manager.py:2001-2035、2037-2237），带备份与失败回滚（2099-2174）
- 结论：安装即等于在宿主进程内执行任意第三方代码（模块顶层 + pip 依赖）。市场只负责发现与分发，不构成安全边界；信任假设是仓库所有者可信

---

## 6 版本兼容机制

- 声明字段：metadata.yaml 的 astrbot_version（PEP 440 specifier，如 >=4.13.0,<4.17.0）→ StarMetadata.astrbot_version（astrbot/core/star/star.py:69-70）；解析见 star_manager.py:551-555
- 解析与强制：_validate_astrbot_version_specifier(version_spec)（star_manager.py:673-705）
  - 未声明或空白 → 直接通过（677-682）
  - SpecifierSet 解析失败 → 报「Use a PEP 440 range, e.g. >=4.16,<5.」（684-690）
  - 判定式 specifier.contains(Version(VERSION), prereleases=True)（700-704）→ 预发布版本也视为满足
- 强制点共 3 处：加载（新式 star_manager.py:1203-1213；旧式 1353-1363）、安装或更新覆盖已有目录前（2088-2093）
- 失败行为：抛 PluginVersionUnsupportedError（star_manager.py:68-69）→ 落入 failed_plugin_dict、插件不激活；安装/更新时直接失败
- 绕过开关：ignore_version_check（star_manager.py:1080、1624、2002；安装路径 2047）
- 非强制项汇总：support_platforms（见 1.3）、metadata 未知字段（updater.py:377-396）、astrbot_version 未声明即放行

---

## 7 未证实 / 需另行核实
- 各平台适配器对 27 种消息段的实际转换覆盖率（未逐 platform/sources/* 核实）
- views / pages（面板页面）字段的完整消费路径（仅确认被解析进 StarMetadata.views，star_manager.py:556-561）
- 市场条目 download_url 的具体域名与校验（仅确认字段被透传，plugin_service.py:1328；updater.py:232-234）
- 桌面版（packaged desktop runtime）下 pip 目标目录 data/site-packages 的行为细节（pip_installer.py:1045-1059 仅确认分支存在）
- 多插件互相引用时的热重载一致性（_purge_modules / _get_plugin_related_modules，star_manager.py:707-775 未逐情形验证）
