# AstrBot 插件开发约束与能力边界

> **研究基线**：AstrBot v4.29.3（官方仓库 `AstrBotDevs/AstrBot` master @ 2026-10-31），文档站 [docs.astrbot.app](https://docs.astrbot.app/dev/star/plugin-new.html) 与仓库 `docs/zh/dev/` 同源。
> **方法**：官方文档（12 篇 dev 文档）+ 源码交叉核实。文中 `xxx.py:行号` 为 AstrBot 仓库相对路径；文档引用标注为「文档:行号」。
> **结论强度**：源码强制的标【硬】，仅展示/约定而无强制的标【软】。

---

## 0. 十条结论速览

1. **插件是宿主进程内的 Python 代码**：没有沙箱、没有权限声明、没有 CPU/内存限额、**handler 没有超时**；`os`/`subprocess`/`ctypes`、任意文件、任意出站网络、AstrBot 数据库全部可达。
2. **硬骨架只有四样**：插件目录 + `metadata.yaml`（name/desc/version/author）+ 入口 `main.py`（或 `<目录名>.py`）+ 主类继承 `astrbot.api.star.Star`。
3. **隐性命名红线**：`metadata.name` 必须是合法 Python 标识符且非关键字（不能有空格、连字符、斜杠），安装目录名 = `metadata.name`；市场身份 `plugin_id = author/name`。
4. **唯一会阻止插件加载的元数据是 `astrbot_version`**（PEP 440）；`support_platforms` 只是展示信息，**不参与任何运行时路由/过滤**【软】。
5. **全异步、单事件循环**：任何同步阻塞（`requests`、`time.sleep`、CPU 重活、阻塞 IO）都会卡住整个 AstrBot；不同会话并发，同一事件的多 handler 按 priority 降序串行。
6. **消息管线固定 9 段**，插件 handler 只在 `ProcessStage` 执行：白名单 → 会话开关 → 限流 → 内容安全 → 预处理 → **插件/LLM** → 结果装饰 → 发送。插件拿到的已是过滤后的消息。
7. **能力面很宽**：23 个注册装饰器（含 `regex`、`custom_filter`、13 类生命周期/LLM 钩子）、27 种消息段、Provider/FunctionTool/Agent/decision、KV/配置/DB、多轮会话、HTML 转图、WebUI 自定义页面与 SSE。
8. **明确的能力缺口**：无插件级定时调度（无公开 cron API）、无限流/并发控制 API、无流式输出公开 API、无 token 用量 API、无插件间隔离。
9. **平台差异是最大不确定项**：At/Record/Video/Reply/主动消息/按钮 在各适配器覆盖不同；`Record` 只收 wav；合并转发只有 OneBot v11；QQ 个人号适配器会 `strip()` 纯文本。
10. **插件市场不是安全边界**：无签名/哈希校验（MD5 只用于缓存新鲜度），安装 = 在宿主进程执行任意第三方代码；浏览器侧唯一的隔离是 WebUI 页面的受限 iframe。

---

## 1. 运行模型：后面所有约束都由它推导

### 1.1 进程内执行，无沙箱【硬】

- 插件模块在 AstrBot 主进程内 `import` 并实例化（`star_manager.py:1122-1123、1230-1246`）。
- `core/star` 与 `core/pipeline` 中检索不到 sandbox / seccomp / bubblewrap / setrlimit 等隔离手段；`default.py` 的 `allow_execution`/`allow_network` 只被 LLM computer 工具消费，**插件加载与执行路径不引用**。
- `requirements.txt` 由**进程内 pip** 安装（`pip_installer.py:83-97、1104-1126`），直接改动宿主 Python 环境，仅靠 core-constraints 锁住核心依赖版本。
- 含义：插件能读 `data/` 下所有配置与数据库、能调 `context.get_db()`、能 import 其他插件模块、能起子进程。**安全假设是「作者可信 + 用户自行审阅源码」**。

### 1.2 全异步单事件循环【硬】

- 每条消息事件派发为一个独立 `asyncio.Task`（`event_bus.py:73`），dispatch 本身是常驻 Task（`core_lifecycle.py:303-310`）。
- handler 必须是 `async def` 或异步生成器；**钩子必须是 `async def`**（源码断言 `inspect.iscoroutinefunction`，`pipeline/context_utils.py:114-115`）。
- 同步阻塞 = 全局阻塞；且**插件 handler 没有任何超时**（`asyncio.wait_for/timeout` 在插件路径无命中）。
- 会话串行只存在于 LLM 请求：`session_lock` 仅包住 Agent 调用（`internal.py:239`、`utils/session_lock.py:8-52`），**插件 handler 不按会话串行**，同一会话的并发需要自己加锁。

### 1.3 消息管线 9 段（`pipeline/stage_order.py:3-13`）

| # | Stage | 作用 | 对插件的含义 |
|---|---|---|---|
| 1 | WakingCheck | 唤醒判定、收集并过滤 handler、解析命令参数 | 命令默认需唤醒前缀（`/`）；`regex` 过滤器不受前缀限制 |
| 2 | WhitelistCheck | 群/私聊白名单 | 进不到插件的消息不会触发任何 handler |
| 3 | SessionStatusCheck | 会话是否启用 | 禁用会话直接丢弃 |
| 4 | RateLimit | 核心限流（stall/discard） | 限流发生在插件之前，插件无法感知被丢弃的消息 |
| 5 | ContentSafetyCheck | 内容安全 | 同上 |
| 6 | PreProcess | 预处理（含会话等待回调分发） | `session_waiter` 接管的消息不再进入普通插件 |
| 7 | Process | 先插件 handler，后 LLM/Agent | 插件在这里；`stop_event()` 可阻止后续 LLM |
| 8 | ResultDecorate | 前缀、T2I、转语音等结果装饰 | `on_decorating_result` 钩子在此 |
| 9 | Respond | 真正发往平台 | `after_message_sent` 钩子在此 |

### 1.4 加载 / 重载 / 卸载

- **扫描**：`data/plugins/<dir>` 内必须存在 `main.py`，否则退化为 `<dir>.py`；两者皆无则跳过（`star_manager.py:291-320`）。`.plugin-install-`、`.plugin-upload-`、`.plugin-backup-` 前缀目录不视为插件。
- **顺序**：解析 metadata → 校验 `astrbot_version` → 装依赖 → import → 载入 `_conf_schema.json` → 实例化 → 绑定 handler → `await initialize()` → 触发 `on_plugin_loaded`。
- **失败**：任何异常都会进 `failed_plugin_dict`，并清理 `sys.modules`/star 注册表/handler 表/LLM 工具/平台适配器（`star_manager.py:1142-1157、1439-1455、776-819`）；WebUI「加载失败插件」列表可单独重载。
- **卸载/重载**：`await terminate()`；若插件自定义了 `__del__` 则丢线程池执行。保留插件（builtin）不可卸载、不可更新。
- **热重载**：仅当环境变量 `ASTRBOT_RELOAD=1` 且安装了 `watchfiles` 才启用（默认关闭），否则只能手动点 WebUI 的「重载插件」。

### 1.5 异常处理契约【硬】

- handler 抛异常 → 记日志 → 触发 `on_plugin_error`（参数 `event, plugin_name, handler_name, error, traceback_text`）→ 若该消息是 `/` 命令或 @ 唤醒，则回显「调用插件 X 的处理函数 Y 时出现异常」→ `stop_event()`（`star_request.py:88-123`）。
- 过滤器抛异常 → 回显错误并 `stop_event()`（`waking_check/stage.py:212-222`）。
- 钩子抛异常只记日志、不中断主流程（`context_utils.py:95-146`）。
- 一次事件中，先执行的 handler 结束后会 `clear_result()`；**每个 `yield` 都会立即沿管线发送**，不会攒到下一个 handler。

---

## 2. 工程结构：加载阶段就会校验的硬约束

| 文件/目录 | 必需 | 约束与失败后果 |
|---|---|---|
| `metadata.yaml`（或 `.yml`） | 【硬】文档要求必须 | 必填 `name`/`desc`/`version`/`author`（非空字符串）；文件 ≤1MB、UTF-8。缺字段 → 「插件元数据校验失败」 |
| `main.py` 或 `<目录名>.py` | 【硬】 | 二者皆无 → 目录被当作「非插件」跳过 |
| 插件主类 | 【硬】 | 必须继承 `astrbot.api.star.Star`；未注册时会回退「类名以 Plugin 结尾 / 叫 Main」，否则报「未通过 Star 注册」 |
| `_conf_schema.json` | 可选 | 存在即解析并生成 `data/config/<插件名>_config.json`，注入 `__init__(context, config)`；JSON 非法直接加载失败 |
| `requirements.txt` | 可选但强烈必需 | 缺了会在用户侧 `ModuleNotFoundError`；由进程内 pip 安装，与核心依赖冲突会报 dependency conflict |
| `logo.png` | 可选 | 文件名固定，建议 1:1 / 256×256 |
| `skills/` | 可选 | `skills/<skill>/SKILL.md` 或 `skills/SKILL.md`；加载进 Skill Manager，WebUI 中只读来源（可启停，不可改删） |
| `.astrbot-plugin/i18n/<locale>.json` | 可选 | 唯一被读取的 i18n 路径；文件 ≤1MB、locale ≤32 字符 |
| `views/<view_name>/index.html` | 可选 | 面板可视化页面入口（旧 `pages/` 为别名），无 index.html 的目录被忽略 |

### 2.1 metadata.yaml 字段

| 字段 | 必填 | 语义 / 约束 |
|---|---|---|
| `name` | 【硬】 | 插件标识；**必须是合法 Python 标识符**（无空格/连字符/斜杠、非关键字）；安装目录名取此值 |
| `desc` | 【硬】 | 描述；缺失时可用 `description` 回退 |
| `version` | 【硬】 | 语义化版本；市场记录的 version 必须与之相同 |
| `author` | 【硬】 | 作者；与 name 组成市场唯一身份 `author/name` |
| `repo` | 加载非必填 / **上架必填** | 插件仓库地址 |
| `display_name` | 可选 | WebUI/市场展示名（支持 i18n） |
| `short_desc` | 可选 | 市场卡片短描述，缺省回退 desc |
| `astrbot_version` | 可选 | PEP 440 版本范围，如 `">=4.16,<5"`；不满足会**阻止加载** |
| `support_platforms` | 可选 | `list[str]`，取值须是 `ADAPTER_NAME_2_TYPE` 的 key；**只展示，不生效** |
| `social_link`/`tags` | 可选 | 展示用 |
| `views`/`pages` | 可选 | 面板视图元数据 |

> **文档与源码的差异（值得注意）**：文档写「务必修改 metadata.yaml，AstrBot 识别插件元数据依赖此文件」；而源码在新式加载路径下**允许缺失**（回退到代码里 `Star` 子类自动登记的空元数据），后果是 `plugin_id` 退化为 `unknown/unknown`、版本校验与市场三项一致性校验全部失效。**结论：实际开发必须提供 metadata.yaml**。

### 2.2 命名与身份【硬】

- 建议（文档）：`astrbot_plugin_` 前缀、全小写、无空格、尽量短。
- 强制（源码）：`metadata.name` 能通过 `str.isidentifier()` 且非关键字，不含 `/`、`\`。
- 市场身份：`plugin_id = <author>/<name>`（小写归一化后仍唯一）；安装后会用已装修复的 `author/name/version` 与市场记录逐项比对，不一致则安装失败。

### 2.3 `astrbot_version`（唯一阻止加载的元数据）

- 格式 PEP 440，**不要加 `v` 前缀**；示例 `>=4.17.0`、`>=4.16,<5`、`~=4.17`。
- 校验：`SpecifierSet.contains(Version(VERSION), prereleases=True)`（`star_manager.py:673-705`），3 个强制点：加载、安装覆盖已有目录前、更新前。
- 写错格式（如 `v4.17`、`> =4.17`）不会忽略，而是直接判定不兼容 → 加载失败。
- WebUI 安装时可勾选「无视警告，继续安装」绕过（`ignore_version_check`）。

### 2.4 `support_platforms`（展示型，【软】）

- 源码全链路只有 4 处赋值/展示，**没有任何运行时校验或拦截**。
- 平台过滤必须自己写 `@filter.platform_adapter_type(...)` 或判断 `event.get_platform_name()`。
- 自建平台适配器（新平台名）无法用它表达，因为取值被限制为内置 `ADAPTER_NAME_2_TYPE` 的 key（文档列 19 个，源码还多 `webchat`）。

### 2.5 数据持久化

- KV：`await self.put_kv_data / get_kv_data / delete_kv_data`，按插件维度隔离（>= 4.9.2）。
- 大文件：`data/plugin_data/<plugin_name>/`（`StarTools.get_data_dir()` 会自动建目录）。
- **禁止**写插件自身目录：重装/更新会被覆盖（文档「开发原则」）。

### 2.6 调试与依赖

- 必须启动 AstrBot 本体调试；改代码后在 WebUI 插件卡片点「重载插件」。
- 依赖写 `requirements.txt`；不要在插件里 `pip install` 核心依赖的升降级。
- 网络请求用 `aiohttp`/`httpx`，**不要用 `requests`**（同步阻塞，卡事件循环）。
- 提交前跑 `ruff`。

---

## 3. 配置、i18n、WebUI 页面、Skills

### 3.1 `_conf_schema.json`（配置 schema）

- `type` **必填**：`string`、`text`、`int`、`float`、`bool`、`object`、`list`、`dict`、`template_list`；v4.13.0+ 增加 `file`（带 `file_types` 后缀白名单与默认 `[]`）。
- `object` 必须带 `items` 子 schema；默认值约定 int 0 / float 0.0 / bool False / string "" / object {} / list []。
- 其他字段：`description`、`hint`、`obvious_hint`、`invisible`、`secret`、`options`/`labels`/`subtitles`、`slider`、`editor_mode`/`editor_language`/`editor_theme`、`_special`、`template_schema`。
- **`secret: true` 只遮罩 WebUI 显示，不加密存储**；插件不得记录/回显这些值。
- `_special`（v4.0.0+）：`select_provider`、`select_provider_tts`、`select_provider_stt`、`select_persona`、`select_knowledgebase`、`sender_id`、`umo`；核心内部的 `provider_pool`、`persona_pool`、`t2i_template` 等**禁止插件使用**（随时变动）。
- 升级 schema 时核心会「补缺失项、删已移除项」，插件侧读取要点防御性写法。

### 3.2 i18n

- 只读 `.astrbot-plugin/i18n/<locale>.json`（`zh-CN`、`en-US`）；必须是 JSON object，**必须嵌套结构，不支持点号扁平 key**。
- 顶层：`metadata`（display_name/short_desc/desc）、`config`（按配置项名嵌套）、`views`（视图名 → title/description 等）。
- 缺语言/缺字段自动回退到 metadata.yaml 与 schema 原文；改 locale 文件后需重载插件。

### 3.3 WebUI 可视化页面（`views/`）

- 后端：`context.register_web_api(route, handler, methods, desc)`，handler 用 `astrbot.api.web` 的 `request`/`json_response`/`error_response`/`file_response`/`stream_response`。
- **路由必须以插件名为前缀**：注册 `/<PLUGIN_NAME>/items/<id>`，前端 endpoint 写 `items/123`。
- 前端通过 bridge（`window.AstrBotPluginView`）：`apiGet/apiPost/upload/download/subscribeSSE/t()/onContext()/getContext()`。
- **安全边界（硬）**：页面运行在 `allow-scripts allow-forms allow-downloads` 的受限 iframe，不能访问 Dashboard cookie/LocalStorage/父页面 DOM，不能绕过 bridge 复用面板鉴权；资源走 path token；旧 `/api/plugin/page/content/...?asset_token=` 已移除；禁止原生 `EventSource`（带不了 Authorization）；后端必须自行校验输入、文件名白名单。
- 鉴权：扩展路由依赖 API Key 的 `plugin` scope，视图资源走 JWT path token（限定插件+视图）。

### 3.4 随插件分发 Skills

- `skills/<skill>/SKILL.md`（多 Skill）或 `skills/SKILL.md`（单 Skill，名称用插件目录名）。
- 加载后进入 Skill Manager，来源标记为该插件；可启用/禁用，但在 WebUI 里**只读**，随插件更新而变化。

### 3.5 打包与发布

- 发布到 [cloud.astrbot.app/publish](https://cloud.astrbot.app/publish)（需 AstrBot Cloud 账号），GitHub 托管源码。
- **zip ≤ 16MB**，超限 CI 直接拒绝；清理 `.git`/`__pycache__`/`node_modules`，压缩静态资源。
- 市场记录必填 `author/name/version/repo/desc`；`repo` 只接受三种 GitHub HTTPS 形式（`/repo`、`/repo.git`、`/tree/<branch>`）；保留字段（`plugin_id`、`install_method` 等）不得出现在记录里。

---

## 4. 能力边界：插件能做什么

### 4.1 事件注册（23 个装饰器 + 13+ 事件类型）

**指令类**：`command`（可 `alias=`、`priority=`）、`command_group`/`group`（可无限嵌套）、`regex`、`button_action`、`llm_tool`。

**过滤类**：`event_message_type`（ALL/PRIVATE/GROUP/OTHER）、`platform_adapter_type`（20 个平台位或）、`permission_type`（ADMIN/MEMBER/GROUP_ADMIN/SHARED_GROUP_ADMIN）、`custom_filter`（支持 `&`/`|` 组合）。

**钩子类（13 类）**：`on_astrbot_loaded`、`on_platform_loaded`、`on_plugin_loaded`、`on_plugin_unloaded`、`on_plugin_error`、`on_waiting_llm_request`、`on_llm_request`、`on_llm_response`、`on_agent_begin`、`on_agent_done`、`on_using_llm_tool`、`on_llm_tool_respond`、`on_decorating_result`、`after_message_sent`。

**语义要点：**

- 同一 handler 多个过滤器之间是 **AND**；没有任何 filter 的 handler 永不激活。
- `permission_type` 不参与 AND 短路：不满足时回显「无权限」并 `stop_event()`。
- 优先级数值越大越先执行（默认 0）；同一事件内 handler 按注册表顺序串行。
- `event.stop_event()` 停止事件传播（后续 handler、LLM 请求都不再执行）；钩子里 `stop_event()` 会中止后续钩子。
- 钩子**不能**与 `command`/`event_message_type`/`platform_adapter_type`/`permission_type` 混用，**钩子内不能 `yield`**，只能用 `await event.send(...)`。
- `on_plugin_loaded/unloaded/error`、`on_platform_loaded`、`regex`、`custom_filter` 在**官方文档里没有章节**，属源码可用但未公开契约，慎用（升级可能变）。

### 4.2 消息能力

- **发送三条路**：`yield event.plain_result(...)` / `await event.send(...)`（钩子与 waiter 内唯一方式）/ `await self.context.send_message(umo, chain)`（主动推送，需平台支持）。
- **消息段 27 种**：Plain、Image、Record、Video、File、At、AtAll、Face、Poke、Reply、Forward、Node/Nodes、Music、RPS、Dice、Shake、Share、Contact、Location、Json、Unknown、ActionRow、Button、ButtonInteraction 等。
- **平台能力矩阵（文档，v4.x）**：

| 平台 | At | Plain | Image | Record | Video | Reply | 主动消息 |
|---|---|---|---|---|---|---|---|
| QQ 个人号(aiocqhttp) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Telegram | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| QQ 官方接口 | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| 飞书 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 企业微信 | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| 钉钉 | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |

- 其他限制：`Record` 只接受 wav；合并转发 `Node` 仅 OneBot v11；`Face` 仅 aiocqhttp；`Video.fromFileSystem` 要求协议端与机器人同机；aiocqhttp 发送时会 `strip()` 纯文本（可用零宽空格 `\u200b`）；QQ 官方/钉钉发送时平台强制带 `At`；钉钉图片只接受 http 链接。
- **交互按钮**：`Comp.ActionRow` + `Comp.Button`，`@filter.button_action("id")` 接收回调；`action_timeout` 必须有限正数、同一行共享截止时间、复用实例不刷新；不支持按钮的平台走 `fallback_text` 文字回退；`UrlAction` 不触发回调。

### 4.3 AI 能力

- **取当前 Provider**：`await self.context.get_current_chat_provider_id(umo)`；调用 `await self.context.llm_generate(chat_provider_id=..., prompt=...)`（>=4.5.7 推荐），或旧式 `get_using_provider_async()` + `text_chat(...)`。
- **Tool**：① `@dataclass` 继承 `FunctionTool[AstrAgentContext]` + `self.context.add_llm_tools(ToolA(), ...)`（可完全控制 schema）；② `@filter.llm_tool(name=...)` 装饰器——**只解析 docstring 的 `Args:` 段**，不读类型注解，缺 `Args:` 会导致参数 schema 为空、参数被静默丢弃；**不支持 `parameters=`**。
- **Agent**：`await self.context.tool_loop_agent(event=..., chat_provider_id=..., prompt=..., tools=ToolSet([...]), max_steps=128, tool_call_timeout=120)`；多 Agent 用 agent-as-tool 模式。
- **结构化判断**：`self.context.decision.ask({...})` / `.choose()` / `.yes_no(threshold=)`，需在 WebUI 另配「判断」提供商；异常统一 `DecisionError`，必须捕获降级。
- **提示词钩子**：`on_llm_request` 可改 `ProviderRequest`；**每轮变化的内容放 `extra_user_content_parts`（`.mark_as_temp()`），不要改 `system_prompt`**，否则破坏提示词缓存、成本约 ×7–20。
- **会话/人格/知识库**：`context.conversation_manager`、`context.persona_manager`、`context.kb_manager` 均可编程访问。

### 4.4 状态能力

| 机制 | API | 隔离粒度 | 用途 |
|---|---|---|---|
| KV | `put_kv_data/get_kv_data/delete_kv_data` | 插件维度 | 少量配置/状态 |
| 配置 | `_conf_schema.json` + `AstrBotConfig`（可 `save_config()`） | `data/config/<plugin>_config.json` | 用户可调参数 |
| 数据目录 | `StarTools.get_data_dir()` | `data/plugin_data/<plugin>/` | 缓存、素材、导出文件 |
| 数据库 | `context.get_db()` | 全局共享 | 需要结构化数据时可只用其驱动，但**无文档化 schema 契约** |
| 会话配置 | `context.get_config(umo=)` | 按会话 | 读取生效配置 |

### 4.5 交互与其他能力

- **多轮会话**：`@session_waiter(timeout=60, record_history_chains=False)` + `SessionController.keep/stop/get_history_chains`；可自定义 `SessionFilter` 把「一个群」当一个会话。接管期间该用户/群消息不再进普通 handler。waiter 内不能 `yield`，只能 `await event.send()`；超时抛 `TimeoutError`，应 `stop_event()`。
- **文转图**：`await self.text_to_image(text)` / `await self.html_render(tmpl, data, options=...)`，options 即 Playwright `page.screenshot` 参数。
- **Web 页面/API/SSE**：见 §3.3。
- **常驻任务**：在 `initialize()` 或 `__init__` 里 `asyncio.create_task()`，在 `terminate()` 里取消（`context.register_task` 已废弃）。
- **平台专有能力**：`context.get_platform_inst(platform_id)` 拿适配器实例；`event.bot.api.call_action(...)` 可直接调 OneBot 协议端 API（删消息、禁言等）；`event.message_obj.raw_message` 拿原始消息对象（跨平台不可移植）。

---

## 5. 明确「做不到 / 没有公开 API」的清单

| 想做的事 | 现实 | 可行替代 |
|---|---|---|
| 插件级定时/周期任务 | **无公开 Cron API**（`Context` 内部持有 `cron_manager`，但属未文档化内部实现；「未来任务」是实验性功能且需平台支持主动消息） | `initialize()` 里 `asyncio.create_task` + 自己判重；或用核心「未来任务」页面 |
| 插件级限流 / 并发控制 | 无限流 API；核心 `platform_settings.rate_limit` 对所有消息生效 | 自己用 `asyncio.Semaphore`/`asyncio.Lock` 按 umo 限流 |
| 会话串行执行 | `session_lock` 只锁 LLM 请求，插件 handler 并发 | 自己按 `event.unified_msg_origin` 加锁 |
| 流式输出 | 文档无公开 API；源码有 `event.send_streaming` 但未作为契约公开 | 分段 `yield` 回复（受平台差异影响） |
| token 用量统计 | 无 API | 自算或用 Provider 原始响应（不稳定） |
| 沙箱 / 权限声明 / 资源限额 | **不存在**，插件 = 宿主进程任意代码 | 无（只能自律 + 用户审阅） |
| 修改 Dashboard 鉴权 / 跨插件路由 | iframe + path token + 路由前缀三重要求 | 走 bridge API，路由带自己插件名前缀 |
| 钩子里 `yield` | 不允许 | `await event.send()` |
| 在 `support_platforms` 里声明新平台 | 取值受限于内置 `ADAPTER_NAME_2_TYPE` | 用 `@filter.platform_adapter_type` 或自行判断 |
| 卸载/更新内置插件 | 保留插件（builtin）不可卸载、不可更新 | 无 |

---

## 6. 安全与信任边界

- **插件市场只负责发现与分发**：清单 MD5 只用于缓存新鲜度，无签名、无哈希校验；GitHub zip / git clone / 本地 zip 安装后即在宿主进程执行（模块顶层代码 + pip 依赖）。**安装第三方插件 = 授予其完整宿主权限**。
- **配置里的 `secret` 不加密**：只是 WebUI 遮罩。
- **页面侧唯一隔离**是受限 iframe + path token + CSP；后端 handler 仍需自行校验路径/文件名/数值。
- **协议端 API 是高权限面**：`event.bot.api.call_action` 可删消息、禁言、踢人等，跨平台不通用且随协议端版本变化。
- **建议**：固定插件版本、审阅源码、最小化依赖、给 `astrbot_version` 设下限、日志里不打印密钥。

---

## 7. 高频坑 Top 13

1. `metadata.name` 写成 `my-plugin`（连字符）→ `isidentifier()` 失败，装不上。
2. 忘了 `requirements.txt` → 用户侧 `ModuleNotFoundError`。
3. handler 写成同步 `def`、或漏掉 `self, event` 前两参 → 静默不执行/调用报错。
4. 钩子写成 `async def ... yield`（异步生成器）→ 断言失败，钩子不生效。
5. `@filter.llm_tool` 的 docstring 缺 `Args:` → 参数 schema 为空，模型传来的参数被静默丢弃。
6. 每轮往 `system_prompt` 追加动态内容 → 破坏提示词缓存，成本 ×7–20。
7. 把数据写在插件目录 → 更新/重装被覆盖。
8. 以为 `support_platforms` 会过滤平台 → 实际不生效。
9. 指令名带空格（会被解析成第二个参数）；忘记唤醒前缀。
10. aiocqhttp 下纯文本被 `strip()`，需要零宽空格。
11. 在不支持主动消息的平台（QQ 官方/企微/钉钉）发主动消息 → 失败或静默丢弃。
12. 以为改代码自动热重载 → 需要 `ASTRBOT_RELOAD=1` + `watchfiles`。
13. 在 handler 里做同步 HTTP/大计算 → 卡死整个机器人（没有超时保护）。

---

## 8. 合规开发 checklist

- [ ] 目录：`data/plugins/<name>/`，`metadata.yaml` + `main.py` + `_conf_schema.json` + `requirements.txt`
- [ ] `metadata.name` 是合法 Python 标识符、全局唯一；`author/name/version` 与市场记录一致
- [ ] `astrbot_version` 写明下限（PEP 440，无 `v`）
- [ ] 主类继承 `Star`，`__init__(self, context, config=None)`；`initialize()` 起任务、`terminate()` 收尾
- [ ] 所有 handler 是 `async def`/异步生成器；钩子是 `async def` 且不 `yield`
- [ ] 网络用 `aiohttp/httpx`；重活丢线程池/子进程；按 umo 自行加锁/限流
- [ ] 数据写 `data/plugin_data/<plugin>/` 或 KV；配置走 schema，密钥 `secret: true`
- [ ] 多平台场景显式 `@filter.platform_adapter_type`；对不支持的段做降级
- [ ] 页面路由带插件名前缀；后端校验输入；不碰 Dashboard cookie
- [ ] `ruff` 格式化；日志用 `astrbot.api.logger` 或 `self.logger`
- [ ] 打包前清理 `.git`/`__pycache__`，zip ≤ 16MB

---

## 附录 A：证据索引

**文档**（`docs/zh/dev/`，与 docs.astrbot.app 同源）

- `star/plugin-new.md`：工程准备、metadata、skills、support_platforms、astrbot_version、开发原则
- `star/plugin.md`：旧版全量指南（平台矩阵、消息发送、插件配置、AI、杂项 API）
- `star/plugin-publish.md`：发布流程、metadata 示例、16MB 限制
- `star/guides/`：simple、listen-message-event、send-message、plugin-config、plugin-i18n、plugin-pages、ai、storage、session-control、html-to-pic、other
- `dev/plugin-market/index.md`、`2026-06-27.md`：市场 JSON schema 与身份校验
- `dev/plugin-platform-adapter.md`：平台适配器插件

**源码**（相对仓库根）

- `astrbot/core/star/star_manager.py`：扫描/加载/元数据/版本校验/失败清理（291-320、491-565、673-705、1076-1482、776-819、1947-1979）
- `astrbot/core/star/base.py`：`Star` 基类、自动登记、`text_to_image`/`html_render`
- `astrbot/core/star/context.py`：暴露给插件的 Context API（184-963）
- `astrbot/core/star/star_handler.py:219-241`：`EventType` 全量
- `astrbot/api/event/filter/__init__.py`：装饰器导出全量
- `astrbot/core/pipeline/stage_order.py:3-13`：9 段管线顺序
- `astrbot/core/pipeline/process_stage/method/star_request.py:88-123`：handler 异常处理
- `astrbot/core/pipeline/context_utils.py:47-75、114-115`：handler 返回契约、钩子 async 断言
- `astrbot/core/event_bus.py:73`：每事件一个 Task
- `astrbot/core/utils/session_lock.py`：会话锁（仅 LLM）
- `astrbot/core/message/components.py`：27 种消息段
- `astrbot/core/utils/pip_installer.py:83-97、1104-1126`：进程内 pip
- `astrbot/dashboard/services/plugin_service.py`：市场清单、安装链路、鉴权 scope

## 附录 B：参考链接

- 插件开发指南：<https://docs.astrbot.app/dev/star/plugin-new.html>
- 插件发布：<https://docs.astrbot.app/dev/star/plugin-publish.html>
- 插件市场发布入口：<https://cloud.astrbot.app/publish>
- 源码仓库：<https://github.com/AstrBotDevs/AstrBot>
- 插件模板 helloworld：<https://github.com/Soulter/helloworld>
- 文转图 Playground：<https://t2i-playground.astrbot.app/>

> 本报告由文档（3 份笔记）+ 源码交叉核对汇总；三条队友笔记保留在 `research/astrbot/notes-*.md` 供追溯。
