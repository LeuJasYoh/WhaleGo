# AstrBot 插件「开发约束」笔记

> 来源：AstrBot 官方仓库稀疏克隆 `D:\tmp\AstrBot-src`（master@2026-10-31）。下文路径均相对该目录。
> 图例：【强制】违反会加载失败/安装失败/被市场拒绝；【建议】官方推荐做法；【警告/坑】易踩边界或文档滞后点。
> 覆盖文档（docs/zh/dev/ 下）：star/plugin-new.md、star/plugin-publish.md、star/guides/{plugin-config,plugin-i18n,plugin-pages,simple}.md、star/plugin.md（旧版）、plugin-market/{index,2026-06-27}.md、plugin-platform-adapter.md；关键处用源码交叉核对（源码路径以 `astrbot/` 开头）。

## 0. 一句话结论

- 【强制】插件 = `data/plugins/<name>/` 下的一个目录，根必须有 `metadata.yaml`（name/desc/version/author 非空字符串）+ 入口 `main.py`（或同名 py），主类继承 `astrbot.api.star.Star`。（docs/zh/dev/star/plugin-new.md:41-44；astrbot/core/star/updater.py:29；astrbot/core/star/star_manager.py:301-309）
- 可选但强相关的约束面：`astrbot_version`（会阻止加载）、`support_platforms`（仅展示，不参与路由）、`requirements.txt`（pip 安装且受核心依赖保护）、`_conf_schema.json`（WebUI 配置渲染）、`.astrbot-plugin/i18n/*.json`、`views/`（受限 iframe 页面）。

## 1. 目录结构与入口文件

| 文件/目录 | 约束 | 出处 |
| --- | --- | --- |
| `metadata.yaml` | 必须在插件根（= 仓库根）。允许 `metadata.yml`。缺失即「不是合法的 AstrBot 插件」。安装/仓库校验阶段超过 1MB 直接报错。UTF-8。 | updater.py:28-30、412-426、441-463；plugin-new.md:43-44 |
| `main.py` | 【强制】入口文件名须为 `main.py`；也兼容 `<插件目录名>.py`。二者都没有则跳过加载。 | simple.md:40；star_manager.py:295-319 |
| 主类 | 【强制】须继承 `astrbot.api.star.Star`；未用装饰器注册的旧式插件要求类名以 `Plugin` 结尾或命名为 `Main`，否则报「未通过 Star 注册」。 | simple.md:11、30；star_manager.py:280-288、1319-1324 |
| 实例化 | 有 `_conf_schema.json` 时以 `(context=..., config=...)` 关键字实例化，否则 `(context=...)`。构造函数别写成位置参数专属。 | star_manager.py:1228-1241、1331-1344 |
| `logo.png` | 可选；文件名固定 `logo.png`（源码常量），文档建议 1:1、256x256。v4.5.0+ 生效。 | star_manager.py:213、1174；plugin-new.md:46-48；plugin.md:222-226 |
| `requirements.txt` | 可选；但一旦用到三方库就必须提供，否则用户侧 ModuleNotFound。 | plugin-new.md:156-160 |
| `skills/` | 可选。`skills/<skill>/SKILL.md`（多 Skill）或 `skills/SKILL.md`（单 Skill，名字用插件目录名）。加载后进入 Skill Manager，WebUI 中为只读来源：可启用/禁用，不能删除/编辑；随插件更新变化。 | plugin-new.md:66-91；plugin.md:244-269 |
| `_conf_schema.json` | 可选；存在则自动解析生成配置并注入 `__init__`。 | plugin-config.md:9、266-284；star_manager.py:1159-1173 |
| `.astrbot-plugin/i18n/<locale>.json` | 可选；唯一被读取的 i18n 位置。 | plugin-i18n.md:3、195-197；star_manager.py:568-573 |
| `views/<view_name>/index.html` | 可选；兼容旧 `pages/`。只扫描 `views/<view_name>/index.html`，无 index.html 的目录被忽略。 | plugin-pages.md:4、10 |
| 持久化数据 | 【强制/建议】写 `data` 目录（源码：`data/plugin_data`），不要写插件自身目录，否则更新/重装被覆盖。 | plugin-new.md:168；astrbot/core/utils/astrbot_path.py:61-63 |

- 【强制】main.py 标准形态：`from astrbot.api.star import Context, Star` → `class XPlugin(Star)` → `def __init__(self, context: Context): super().__init__(context)`；Handler 用 `astrbot.api.event.filter` 的装饰器（如 `@filter.command("...")`）在类内注册，用 `yield event.plain_result(...)` 回复。simple.md:6-26
- 【行为】继承 `Star` 的子类会经 `__init_subclass__` 自动登记到 `star_map`；`metadata.yaml` 的字段优先于代码内声明的元数据。star_manager.py:1176-1196
- 【警告/坑】插件配置实体不在插件目录：`data/config/<root_dir_name>_config.json`（root_dir_name 通常是 `metadata.name`）。star_manager.py:1167-1173
- 【警告/坑】安装后落盘目录名 = `metadata.name`（须是合法 Python 标识符），不是仓库名。star_manager.py:642-671、2059-2072

## 2. 命名规范与唯一性

- 【建议】插件名/仓库名：以 `astrbot_plugin_` 开头、无空格、全小写、尽量短。plugin-new.md:21-25
- 【强制】`metadata.name` 必须是合法 Python 标识符且非关键字，不能含 `/`、`\`（否则安装/加载报错）；因此空格、连字符、大写混排都有风险。star_manager.py:631-639、663-671
- 【强制】市场侧插件身份 `plugin_id = metadata.author + "/" + metadata.name`；全局唯一，且「小写归一化后相等」也不允许共存；`repo`、本地目录名、展示名都不得作为身份。plugin-market/2026-06-27.md:92-115
- 【强制】`author`/`name` 必须非空、去首尾空白、不含 `/`、不含 ASCII 控制字符。plugin-market/2026-06-27.md:109-115
- 【强制】市场 JSON 根 key 应等于 `${author}/${name}`（兼容例外：等于 `name`，此时 `name` 可省略在记录内）。plugin-market/2026-06-27.md:98-107、117-128
- 【强制】市场记录字段与已装 `metadata.yaml` 必须一致：`author`/`name`/`version` 三项逐项相等，否则安装失败。plugin-market/2026-06-27.md:266-269、278-280

## 3. metadata.yaml 字段（含必填判定）

【强制】加载必填：`name`、`desc`、`version`、`author`（源码常量 `PLUGIN_METADATA_REQUIRED_FIELDS`），且都必须是「去空白后非空」的字符串。astrbot/core/star/updater.py:29、377-396
- 【坑】`repo` 对「加载」不是必填（缺失则 None），但对「上架市场」是必填。updater.py:540；plugin-market/2026-06-27.md:139
- 【兼容】`desc` 缺失时会用 `description` 回退。star_manager.py:523-524；updater.py:374-375
- 可选字段：`display_name`、`short_desc`（缺省回退 `desc`）、`repo`、`social_link`、`tags`、`astrbot_version`、`support_platforms`、`views`（`pages` 为别名）。plugin-publish.md:13-29；star_manager.py:530-562
- 【强制】`version` 遵循语义化版本；市场 `version` 必须与 metadata 相同。plugin-publish.md:19；plugin-market/2026-06-27.md:138、269
- 【建议】给每个 Handler 写 docstring，WebUI 会解析展示。simple.md:18

## 4. 声明 AstrBot 版本范围（astrbot_version）

- 【强制语义】格式同 `pyproject.toml` 依赖约束（PEP 440），**不要加 `v` 前缀**。plugin-new.md:127；plugin.md:300
- 【强制】不满足当前 AstrBot 版本时，插件被阻止加载并报版本不兼容；WebUI 安装时可选择「无视警告，继续安装」跳过。plugin-new.md:143-144；star_manager.py:673-705（`SpecifierSet` 校验 + `PluginVersionUnsupportedError`）
- 【坑】写错格式（如 `> =4.17`、`v4.17`）不会静默忽略，而是直接判定不支持 → 加载失败（`InvalidSpecifier` 分支）。star_manager.py:684-690
- 示例：`>=4.17.0`、`>=4.16,<5`、`~=4.17`。plugin-new.md:133-141

## 5. 声明支持平台（support_platforms）

- 【强制取值】`list[str]`，值必须是 `ADAPTER_NAME_2_TYPE` 的 key；文档列出的 key 见 plugin-new.md:103-123（aiocqhttp、qq_official、qq_official_webhook、telegram、wecom、wecom_ai_bot、lark、dingtalk、discord、slack、kook、vocechat、weixin_official_account、weixin_oc、satori、misskey、line、matrix、mattermost）。plugin-new.md:93-124
- 【警告/坑】**文档列 19 项，源码实际还有 `webchat`（共 20 项）**；以源码 `ADAPTER_NAME_2_TYPE` 为准。astrbot/core/star/filter/platform_adapter_type.py:33-54
- 【警告/坑】`support_platforms` 只是元数据：加载时仅过滤非字符串项，WebUI 插件页与插件详情展示；**不参与消息路由/handler 过滤**（想做过滤要对 handler 加平台过滤 / 自行判断 `event.get_platform_name()`）。star_manager.py:542-550、848；dashboard/services/plugin_service.py:659
- 【强制语义】「平台支持」不等于「消息类型全支持」：不同适配器支持的 At/Image/Record/Video/Reply/主动消息不同，需自行降级。plugin.md:182-198
- 市场记录里同名字段 `support_platforms`（string[]）；旧字段 `support_platform`、`platform` 已弃用，新源不得输出。plugin-market/2026-06-27.md:152、221-228

## 6. 插件配置 schema（_conf_schema.json）

- 【强制】`type` 必填。支持：`string`、`text`、`int`、`float`、`bool`、`object`、`list`、`dict`、`template_list`；v4.13.0+ 另有 `file`（文件上传，`file_types` 限定后缀，默认值 `[]`）。plugin-config.md:46、131-144
- 字段：`description`（可选，建议一句话）、`hint`、`obvious_hint`、`default`、`items`（`object` 必填，嵌套子 schema）、`invisible`、`secret`、`options`/`labels`/`subtitles`、`slider`（仅 int/float）、`editor_mode`/`editor_language`/`editor_theme`、`_special`。plugin-config.md:47-61
- 【强制】`object` 必须带 `items`；理论上可无限嵌套但【建议】别深。默认值约定：int 0、float 0.0、bool False、string ""、object {}、list []。plugin-config.md:50-51
- 【建议】API Key/Token/密码等设 `"secret": true`（仅遮罩显示，**不加密存储**，插件不得回显/记录）。plugin-config.md:53、63-84
- `_special`（v4.0.0+）：`select_provider`、`select_provider_tts`、`select_provider_stt`、`select_persona`、`select_knowledgebase`、`sender_id`、`umo`。知识库须 `type: list` 且默认 `[]`。plugin-config.md:96-125
- 【警告/坑】内部专用 `_special` 值（`select_providers`、`provider_pool`、`persona_pool`、`select_plugin_set`、`t2i_template`、`get_embedding_dim`、`select_agent_runner_provider:*`）随时变动，**禁止在插件中使用**。plugin-config.md:124-125
- `dict` 类型用 `items: {}` + 可选 `template_schema` 让 WebUI 可视化编辑。plugin-config.md:146-182
- `template_list`（v4.10.4+）：`templates.<name>.items`，保存值形如 `[{"__template_key": "...", ...}]`；模板支持 `display_item`、`hide_hint_in_list`。plugin-config.md:184-262
- 【强制】schema 文件须 UTF-8（接受 BOM，源码 `utf-8-sig`），JSON 非法会报带行列号的错误。star_manager.py:610-624
- 【强制】读取方式：`__init__(self, context: Context, config: AstrBotConfig)`；`AstrBotConfig` 继承 dict，可 `self.config.save_config()`。plugin-config.md:266-284
- 【行为】更新 Schema 时核心会递归比对：补默认值、移除已不存在的配置项。plugin-config.md:286-288
- 【建议】配置项文案（`description`/`hint`/`labels`/`subtitles`）走 i18n，不要硬编码多语言。plugin-config.md:86-88

## 7. 插件国际化（i18n）

- 【强制】只读 `.astrbot-plugin/i18n/`；文件名 = WebUI locale（如 `zh-CN.json`、`en-US.json`）；内容必须是 JSON object；**必须嵌套结构，不支持点号扁平 key**。plugin-i18n.md:3、17、195-197
- 顶层字段：`metadata`（display_name/short_desc/desc）、`config`（按配置项名嵌套，含 object/template_list 子结构）、`views`（或旧别名 `pages`，key = 视图目录名，含 `title`/`description`/自定义文案）。plugin-i18n.md:25-39、41-83、85-129、131-169
- 【强制】`options` 是保存值不要翻译，展示文本用 `labels`，说明用 `subtitles`（按索引对应）。plugin-i18n.md:83
- 【回退】缺语言/缺字段/无文件时：metadata 回退 `display_name`/`short_desc`/`desc`；配置回退 schema 文案；Page 回退目录名/默认标题。plugin-i18n.md:19-23
- 【警告/坑】源码限制：locale 名长度 ≤32、单个 i18n 文件 ≤1MB，超限被跳过并告警；非 JSON object 被跳过。star_manager.py:568-608
- 【警告/坑】改 locale 文件后需重载插件才对 WebUI 生效。plugin-pages.md:581
- 【与市场的关系】市场 JSON 有独立 `i18n` 字段（locale → {display_name, desc, short_desc}），与插件内 i18n 文件彼此独立。plugin-market/2026-06-27.md:156、187-199

## 8. 插件可视化视图（views / Dashboard 页面）

定位：复杂表单、状态面板、日志查看、文件上传下载、SSE 实时流、图表；**少量配置项优先用 `_conf_schema.json`**。plugin-pages.md:6

### 8.1 能做什么
- 每个 `views/<view_name>/index.html` 是一个独立视图；可带任意静态资源（js/css/assets）。plugin-pages.md:10-24
- `view_name` 规则：简单目录名（`settings`、`bridge-demo`）；禁止空名、`.`、`..`、以 `.` 开头、含 `/` 或 `\`。plugin-pages.md:26；dashboard/services/plugin_page_service.py:434-448
- 后端能力：`context.register_web_api(route, handler, methods, desc)` 注册，handler 用 `astrbot.api.web` 的 `request`/`json_response`/`error_response`/`file_response`/`stream_response`。plugin-pages.md:30-36、125-291；astrbot/core/star/context.py:774-796
- 【强制】路由必须以插件名为前缀：注册 `/<PLUGIN_NAME>/items/<item_id>` ↔ 前端 endpoint `items/123`（Dashboard 转发到 `/api/v1/plugins/extensions/<plugin_name>/items/123`）。plugin-pages.md:140-152；dashboard/api/plugins.py:209-256
- 动态片段支持 `<name>`、`<path:name>`；`path_params` 以关键字参数传给 handler。plugin-pages.md:159-162
- 前端 bridge（`window.AstrBotPluginView`，别名 `AstrBotPluginPage`）：`ready()`、`getContext()`、`getLocale()`、`getI18n()`、`t(key, fallback)`、`onContext()`、`apiGet`、`apiPost`、`upload`、`download`、`subscribeSSE`/`unsubscribeSSE`。plugin-pages.md:311-348、405-539
- 主题：SDK 维护 `<html data-theme="light|dark">`，「跟随系统」也会归一化成 light/dark。plugin-pages.md:599-627

### 8.2 不能做什么（安全边界）
- 【强制】视图运行在受限 iframe（`allow-scripts allow-forms allow-downloads`），**不能直接访问 Dashboard cookies / LocalStorage / 父页面 DOM，也不能绕过 bridge 复用 Dashboard 认证**；所有需要身份的操作必须走 bridge。plugin-pages.md:645-653
- 【强制】bridge endpoint 是插件内相对路径：不能为空、不能含 `\`、URL scheme、query、hash、空路径片段、`.`、`..`；query 用 `apiGet(endpoint, params)` 传，不要拼进 endpoint。plugin-pages.md:367-375
- 【强制】不要手拼内容 URL、不要自行加 `asset_token`、不要用 `..` 逃逸视图根；旧 `/api/plugin/page/content/...?asset_token=` 已移除。资源通过 `content_path = /api/v1/plugins/<插件>/views/<视图>/_t/<token>/` 自带令牌。plugin-pages.md:639-641；plugin_page_service.py:550-567
- 【强制】SPA 用 hash routing；静态资源按真实文件路径解析，history routing 刷新需真实文件存在。plugin-pages.md:643
- 【强制】SSE 401 场景：不要在视图里 `new EventSource("/api/v1/...") `（无法带 Authorization），用 `bridge.subscribeSSE()`，后端返回 `text/event-stream` 且每条以空行结尾。plugin-pages.md:517-539、664-672
- 【强制】后端仍须自行校验输入：不信任视图传来的路径/文件名/格式/数值范围；落盘用安全目录并白名单/重命名文件名。plugin-pages.md:662
- 【建议】用 `astrbot.api.web` 而非把 FastAPI/Starlette/Quart 原始请求对象暴露给业务代码；迁移时不要在同一 handler 混用两个 `request` 代理。plugin-pages.md:42、293-307
- 上传：`bridge.upload()` 字段名固定 `file`，一次只发一个文件；普通字段走 `apiPost` 或 query。plugin-pages.md:443-465
- 权限：插件扩展路由依赖 API Key 的 `plugin` scope（`ScopeDependency("plugin")`）；view 资源走 JWT path token（token 限定 plugin+page，会话同寿命）。dashboard/api/plugins.py:61、64-102、398-440

## 9. 调试与依赖安装

- 【强制流程】AstrBot 是运行时注入插件，**必须启动 AstrBot 本体**调试。plugin-new.md:146-148
- 重载：WebUI `插件` 页卡片上的「重载插件」刷新图标；加载失败的插件在同一页 `加载失败插件` 列表点「重载」。plugin-new.md:150-154
- 【坑】文件监听式热重载依赖环境变量 `ASTRBOT_RELOAD=1` 且需安装 `watchfiles`；否则只能手动重载。star_manager.py:60-65、222-240
- 【坑】`views/` 目录增删改需重载插件；静态资源改动通常刷新视图即可。plugin-pages.md:36
- 日志：【建议】用 `from astrbot.api import logger`（官方提供的 logger 接口），不要各自另建日志器。simple.md:8
- 【强制】依赖机制 = `requirements.txt` + pip；加载/安装时按需安装，且**核心依赖有版本保护**（核心已安装依赖被 pin 成 constraints），与核心冲突会报「dependency conflict」并安装失败。plugin-new.md:156-160；star_manager.py:139-190、353-379；astrbot/core/utils/core_constraints.py:50-88
- 【坑】不要用 `requests`（同步阻塞），用 `aiohttp`/`httpx`。plugin-new.md:171
- 【坑】持久化别写插件目录；用 `data/plugin_data`（源码 helper `get_astrbot_plugin_data_path`）。plugin-new.md:168；astrbot_path.py:61-63

## 10. 发布流程与插件市场规范

### 10.1 发布流程
- GitHub 托管插件：先推代码到插件仓库 → 到 `https://cloud.astrbot.app/publish` 发布（需注册 AstrBot Cloud 账号）→ 系统自动解析 `metadata.yaml`。plugin-publish.md:5-11
- 【强制】上架元数据需含 `name`（英文唯一）、`display_name`、`desc`、`version`、`author`、`repo`；可选 `short_desc`、`astrbot_version`、`support_platforms`、`social_link`、`tags`。plugin-publish.md:13-29
- 【强制】压缩包（zip）**≤ 16MB**，超限 CI/CD 自动拒绝；确需更大需联系维护者 bypass。plugin-publish.md:33-36、44
- 【建议】发布前：压缩图片/音频；清掉 `.git`、`__pycache__`、`node_modules`、开发配置（加 `.gitignore`）；精简大依赖；可用发布分支/`.gitattributes` 控制打包内容。plugin-publish.md:39-42

### 10.2 市场 JSON 规范（schema_version = 1）
- 【强制】市场源是 UTF-8 JSON 对象，根必须有 `$meta`，且 `$meta.schema_version = 1`（整数）；除 `$meta` 外每个根 key 是 `plugin_id`（或兼容的 `name`）并映射到 `PluginRecord`。plugin-market/2026-06-27.md:22-31、66-71
- 【强制】记录必填：`author`、`name`（兼容例外可省）、`version`、`repo`、`desc`；可选 `display_name`、`short_desc`、`download_url`、`logo`、`tags`、`category`、`support_platforms`、`astrbot_version`、`social_link`、`updated_at`、`i18n`、`pinned`、`stars`、`download_count`。plugin-market/2026-06-27.md:130-159
- 【强制】`repo` 仅接受三种 HTTPS GitHub 形式：`https://github.com/{owner}/{repo}`、`.git`、`/tree/{branch}`；owner/repo/branch 必须匹配 `[A-Za-z0-9_-]+`；禁止 HTTP/SSH/非 GitHub/GHE/文件/Release/PR/Issue/子目录 URL。plugin-market/2026-06-27.md:161-185
- 【强制】保留字段禁止出现在 `PluginRecord`：`plugin_id`、`market_plugin_id`、`market_plugin_identifier`、`root_dir_name`、`local_plugin_name`、`install_method`、`registry_url`、`registry_name`、`installed_at`；消费者不得用它们做身份。plugin-market/2026-06-27.md:201-217
- 【强制】未知字段可用于展示，但不得用于身份/安装源/更新源/安全校验。plugin-market/2026-06-27.md:230-240
- 【强制】安装身份校验：安装后读取已装 `metadata.yaml`，其 `author/name` 必须等于 `plugin_id`，否则安装必须失败；`download_url` 优先于 `repo`。plugin-market/2026-06-27.md:271-281
- 更新：必须用安装时记录的 registry source，按 `plugin_id` 解析，版本比较用 metadata 版本 vs 市场 `version`；URL/Git/上传安装**不得**视为市场安装。plugin-market/2026-06-27.md:283-296
- 【警告/坑】官方文档未给量化审核清单；公开可验证的「硬门槛」是：zip ≤16MB、metadata 与市场记录三项一致、`repo` 形式合规、身份唯一、无保留/弃用字段。plugin-publish.md:33-44；plugin-market/2026-06-27.md:241-269
- 【警告/坑】license 不是 metadata 必填字段（必填仅 name/desc/version/author），但开发原则要求：使用/移植他人代码须遵守原许可并保留版权与许可声明；借鉴设计须在 README 说明来源。plugin-new.md:162-174；updater.py:29

## 11. 开发原则（官方明列）

功能需测试；注释良好；数据写 data；良好错误处理不因单错崩溃；提交前用 ruff 格式化；不用 `requests`；扩展现有插件优先提 PR；借鉴须注明来源并遵守原许可。plugin-new.md:162-174（同文 plugin.md:200-214）
- 【强制】所有 Handler 必须写在插件类内注册，前两个参数固定 `self` 和 `event`；长逻辑可抽到外部函数再在 Handler 里调。simple.md:36-42
- `terminate()` 可选实现，插件卸载/停用时调用（清理任务/连接）。simple.md:24-25

## 12. 平台适配器插件的额外约束（与「声明支持平台」相关）

- 适配器也是插件：在插件目录加 `<name>_adapter.py` + `<name>_event.py`，`@register_platform_adapter(name, desc, default_config_tmpl=...)` 自动注册。plugin-platform-adapter.md:11、75-80
- 【强制】必须实现 `send_by_session`、`meta()`（返回 `PlatformMetadata`）、`run()`；转换出的 `AstrBotMessage` 关键字段：`type`、`message_str`、`sender`、`message`、`session_id`；事件须 `commit_event()` 入队。plugin-platform-adapter.md:89-147
- 【强制】`main.py` 里须在 `__init__` 导入该适配器模块（如 `from .fake_platform_adapter import FakePlatformAdapter  # noqa`），否则装饰器不执行、平台不出现。plugin-platform-adapter.md:243-252
- 【坑】新平台名要能被 `support_platforms` 合法声明，就必须是 `ADAPTER_NAME_2_TYPE` 的 key —— 即**自建平台适配器无法用 support_platforms 表达**，该字段只覆盖内置适配器。platform_adapter_type.py:33-54
- 媒体：组件 `file`/`url` 支持本地路径、`file:` URI、HTTP(S)、`base64://`、Data URI；发送时用 `await i.convert_to_file_path()`，不要手写 `startswith("file://")` 判断；自下载的临时文件用 `event.track_temporary_local_file()` 登记。plugin-platform-adapter.md:187-241

## 13. 待 Lead 注意的高价值「边界」结论

1. `support_platforms`、`display_name`、`short_desc`、`i18n` 都是**展示/元数据**，不改变运行时行为；唯一会「阻止加载」的元数据是 `astrbot_version`。
2. `name` 受「Python 标识符」约束，这是文档没写、源码强制的隐性命名红线（含连字符/空格会装不上）。star_manager.py:631-639
3. 插件后端 API 的鉴权是「Dashboard 用户 + `plugin` scope + path token」，插件无法拿到 Dashboard 的 cookie/本地存储，跨插件越权也被路由前缀与 token 限定。
4. 依赖安装受核心依赖 pin 保护，插件不能靠 `requirements.txt` 升降级 AstrBot 核心依赖。
5. 市场文档（schema 1）是**数据规范**而非插件运行时规范；两套 field 名（`support_platforms`、`astrbot_version`、`i18n`）保持一致但不能互相替代。
