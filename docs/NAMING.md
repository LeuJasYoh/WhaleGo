# WhaleGo 命名设计（v1.0，已定名）

> 状态：**已拍板**（2026-10-31）。项目品牌 **WhaleGo**，模型可见工具前缀 **go_**。
> 本文记录完整命名包、可用性/撞名核查、AstrBot 侧命名设计、迁移清单与保留意见。
> 数据采集：2026-10-31；注册表/域名/DNS 只做初筛，不等于商标可用性。

---

## 0. 结论

| 层 | 定名 |
|---|---|
| 项目品牌 | **WhaleGo**（规范写法；不要写成 WhaleGO/whalego 作为展示名） |
| 中文名（建议） | **鲸跃**（备选：鲸渡、鲸行）——待用户最终选 |
| Slogan | *Give your Windows a hand.* /「让 AI 伸手到你的桌面」 |
| Windows 执行器 | **WhaleGo Executor**（`WhaleGo.Executor.exe`，单进程：MCP 内核 + WinForms `NotifyIcon` 托盘 + WPF 设置窗口；托盘名 WhaleGo） |
| GUI 项目 | `WhaleGo.Gui`（WPF 设置窗口 + WinForms `NotifyIcon` 托盘，net10.0-windows，`UseWPF`+`UseWindowsForms`，只引用 Contracts）；设置窗口标题「WhaleGo 设置」；浅/深主题（`ui.theme`，跟随系统+手动热切换） |
| 单实例/窗口事件 | 互斥体 `Local\WhaleGo.Executor.SingleInstance`（`--headless` 共用）；事件 `Local\WhaleGo.Executor.ShowSettings`；**手动启动**（不自启） |
| 本地文件 | `%APPDATA%\WhaleGo\config.json`（哈希 token）、`%LOCALAPPDATA%\WhaleGo\secrets.json`（明文 token）、`control-state.json`（停机持久化） |
| token 前缀 | `wg_`（32 字节 CSPRNG base64url；`config.json` 只存 SHA-256） |
| 无头参数 | `--headless` / `--self-check --json` / `--no-hotkey` / `--config <path>` / `--diagnostics-dump` |
| AstrBot 插件 | `astrbot_plugin_whalego`（display_name「WhaleGo」） |
| 模型可见工具前缀 | **`go_`**（7 个工具，见第 3 节） |
| MCP server 名 | `whalego` |
| 协议版本标识 | `whalego-*-v1`；应用协议 `protocol_version=1` |
| 会话 id 前缀 | `wg-<sha16>` |

**一句话**：WhaleGo 是品牌，`go_` 是模型工具命名空间；二者分离，协议不受品牌改名影响。

---

## 1. 为什么保留 WhaleGo（以及需要知道的撞名）

### 1.1 优点

- 语义：鲸（跨越大洋的巨物，暗合"容器 → 宿主"的跨边界）+ Go（行动/出发），好记、有形象；
- 视觉：鲸 + 光标/手形 Logo 空间大；中文「鲸跃」自然；
- 工程：`whalego` 全小写可用于仓库/包名；`WhaleGo.Contracts` 等 C# 命名空间合法；插件 `astrbot_plugin_whalego` 合法。

### 1.2 撞名核查（2026-10-31）

| 项 | 结果 |
|---|---|
| PyPI / npm / NuGet | `whalego`、`whale-go` 均未占用（404）✅ |
| GitHub 句柄 | @whalego 已占用；@whale-go 可用 |
| GitHub 仓库名 | 13 个小仓库（最高 2★），无强势同名项目 |
| 域名 | `whalego.com/.dev/.io/.app/.cn` 当前均无 DNS 解析（不代表可注册，需注册商核实） |
| 商标/品牌 | 中文已有「鲸菱 WHALEGO」「远鲸 WHALEGO」（家电类商标）与加密社群 "WhaleGo"；**个人开源项目可用，但不要按"全新商标"预期**；商用/公开发布前需做商标检索 |
| 关键词噪声 | "WhaleGo" 搜索结果会混入家电商标与加密社群；建议仓库 README 首行加 "WhaleGo (Windows desktop control via MCP)" 便于检索 |

> 结论：作为个人/开源项目没问题；如果未来要商业化，建议换一个更"空白"的品牌或做商标注册。

### 1.3 大小写规范（重要）

- 展示名一律 **WhaleGo**；不写 `WhaleGO`/`WHALEGO`（会被当成缩略词/拼写错误）。
- 代码标识符一律小写：仓库 `whalego`、包/命名空间 `WhaleGo.*`（PascalCase）、环境变量 `WHALEGO_*`、路径 `WhaleGo`。
- 句子里提品牌写 "WhaleGo"，不要写 "the WhaleGo" 之类。

---

## 2. 关于 `go_` 工具前缀的评估（可以，但有注意点）

**结论：可用。** 7 个具体工具名冲突概率低，且 AstrBot 内置工具中**没有** `go_` 前缀（实测 0 匹配）。
但需要知道三个风险，并用工程手段兜住：

| 风险 | 说明 | 缓解 |
|---|---|---|
| 与 Go 语言联想 | `go_` 会让人误以为项目是 Go 写的（实际执行器 C#、插件 Python） | README/插件描述首段写明"`go_` 是 WhaleGo 品牌前缀，不是 Go 语言" |
| 前缀太通用 | "go" 是常见英语动词，其他 AstrBot 插件/MCP server 可能也有 `go_*` 工具；AstrBot 同名工具会替换并告警，不保证隔离 | ① 在 README 声明保留前缀；② 插件启动时拉 `tools/list` 与本地对比 + 检查 AstrBot 现有工具重名；③ 7 个名字足够具体（`go_health` 等） |
| 个别读感 | `go_stop` 读起来像"去停下"（矛盾） | 工具描述里写清"kill switch"；保持同前缀一致性优先于读感 |

> 如果未来觉得 `go_` 太通用，唯一低成本改法是在**下一个协议大版本**统一换前缀（如 `wg_`）；v1 内不改名。

---

## 3. 模型可见工具面（7 个，`go_*`）

| 工具 | 语义 | 备注 |
|---|---|---|
| `go_health` | 健康/能力/版本 | 只读 |
| `go_apps` | 列出应用 | 只读；与 `go_windows` 对称 |
| `go_windows` | 列出窗口 | 只读 |
| `go_observe` | 观察（UIA 树 + 可选截图，返回 `state_id`） | **唯一观察入口**；动作前必须观察 |
| `go_act` | 执行单次动作（`op` 枚举） | **唯一动作通道**；成功只回 accepted 回执 |
| `go_launch` | 启动/激活应用 | 有界等待窗口 |
| `go_stop` | kill switch | 只"停"；恢复走管理员命令/托盘/热键 |

命名规则（写进契约，v1 冻结）：

1. 前缀 `go_` = WhaleGo 品牌 + "去做"；全小写 snake_case；长度 ≤18；不带版本号/数字/平台名。
2. 动作扩展优先加 `go_act.op`（`click/set_value/type/key/scroll`，P2 增 `drag/paste`），而不是加新工具。
3. 新工具只用于"新的对象类型"（如未来剪贴板/文件）；工具名发布后不改名，弃用走"保留+标注 deprecated+新名并存"一个版本。
4. 参数/枚举 snake_case、小写：`window_id/app_id/state_id/target/args/op`（模型面；`session_id` 等内部参数见第 7 条）。
5. 工具描述中文优先、首词动词、≤120 字、必须写"动作后需重新 observe"等关键约束；不写内部派发术语。
6. 错误码不带品牌前缀（`STALE_STATE` 等），跨客户端稳定；advisories 闭集小写。
7. 模型面 schema **不得出现** `session_id`/`request_id`/`scope`（内部参数，contracts 标 `"internal": true`，由插件注入）；会话 id 一律 `wg-<sha16>`。

---

## 4. AstrBot 侧命名设计

### 4.1 插件标识（三个名字，别混）

| 名字 | 取值 | 说明 |
|---|---|---|
| 机器名 `metadata.name` | `astrbot_plugin_whalego` | AstrBot 硬约束：`name.isidentifier()` 且非关键字；**安装目录名 = name**；市场身份 `<author>/<name>`；全小写、无连字符 |
| 显示名 `display_name` | `WhaleGo` | WebUI / 插件市场 / 日志里给人看的名字（可配 i18n） |
| 目录名 | `astrbot/astrbot_plugin_whalego/`（安装后 `data/plugins/astrbot_plugin_whalego/`） | 与 `metadata.name` 一致 |

- **用户看到的"插件名"是 `display_name = WhaleGo`**，不是 `metadata.name`；所以"插件叫 WhaleGo"已经满足。
- **不建议**把 `metadata.name` 写成 `WhaleGo`：技术上 `"WhaleGo".isidentifier()` 为真（源码 `star_manager.py:636` 只校验 isidentifier/keyword），
  但 ① 违反生态惯例 `astrbot_plugin_*`；② 安装目录会变成 `data/plugins/WhaleGo/`，Linux 容器大小写敏感，
  仓库目录/安装目录大小写不一致容易踩坑；③ 市场 id 会归一化大小写，`author/WhaleGo` 与 `author/whalego`
  在升级/覆盖比对时有歧义风险；④ 命令、日志、排障保持全小写更一致。
- 命令名 `/whalego`、工具前缀 `go_`、MCP server 名 `whalego` 已全部对齐品牌。
- 入口 `main.py`；`_conf_schema.json`；`requirements.txt`（`mcp>=1.30,<2`）；`logo.png` 用 WhaleGo 品牌图。

```yaml
name: astrbot_plugin_whalego
display_name: WhaleGo
desc: 通过 MCP 连接 Windows 上的 WhaleGo Executor，让模型观察并操作桌面（UIA 观察/截图/点击/输入）。
short_desc: 给 AI 一双手，让它操作你的 Windows 桌面
version: 0.1.0
author: <你的作者名>
repo: https://github.com/<you>/whalego
astrbot_version: ">=4.29,<5"
tags: [mcp, computer-use, windows, automation]
```

### 4.2 配置键（`_conf_schema.json`，snake_case）

| 键 | 类型 | 说明 |
|---|---|---|
| `url` | string | 默认 `http://host.docker.internal:8765/mcp` |
| `token` | string (secret) | `wg_` 前缀 Bearer Token；只遮罩不加密、不写日志（明文在 Windows 端 `secrets.json`，D13） |
| `timeout` | int | 默认 45s；clamp 到 AstrBot `tool_call_timeout-5s`，且须 > 执行器整体预算（≤40s）5s 以上（D12） |
| `readonly` | bool | 只读：禁用 `go_act`/`go_launch` |
| `admin_only` | bool | 默认 true（AstrBot 非内置工具默认权限是 member） |
| `allow_screenshot` | bool | 默认 true（Q4）；允许 `go_observe` 返回截图（执行器 `screenshot.enabled=true`、`defaultInclude=false` 不自动附图） |
| `inject_usage_prompt` | bool | 默认 true；经 `on_llm_request` 注入 ≤15 行使用说明（P1，内容 `prompts/usage.md`） |
| `allowed_tools` | list[string] | 白名单，默认 7 个全开 |

> 执行器侧本地配置键（`config.json` camelCase，**不是插件 schema**）：`listen.*`、`policy.*`、`limits.*`（执行器预算，插件不重复）、
> `screenshot.*`（默认 `enabled=true`、`defaultInclude=false`）、`privacy.*`、`audit.*`、`hotkeys.{enabled,emergencyStop,openSettings}`、
> `ui.{notificationsLevel,theme}`（`theme` = `system|light|dark`，默认 `system`，热切换）、`logging.{level,dir}`、`launch.allowlist`
> （见 DESIGN §5.8，D4/D14/Q4/Q6）；启动方式 = 手动启动（不自启，Q5）。

### 4.3 命令

| 命令 | 别名 | 权限 | 作用 |
|---|---|---|---|
| `/whalego status` | `/wg status` | 成员 | 连接状态/版本/会话/最近观察 |
| `/whalego health` | `/wg health` | 成员 | 调 `go_health` |
| `/whalego stop` | `/wg stop` | 成员 | kill switch |
| `/whalego resume` | `/wg resume` | 成员（只读提示） | **不是远程恢复**：仅提示到 Windows 托盘/设置窗口恢复（D18） |
| `/whalego readonly on/off` | `/wg ro on/off` | 管理员 | 只读开关 |
| `/whalego help` | `/wg` | 成员 | 帮助 |

> `/go` 虽然短，但与"go"通用词冲突概率高（其他插件可能占用），不设为别名；主命令 `/whalego` + 别名 `/wg`。

### 4.4 其他标识

| 项 | 命名 |
|---|---|
| MCP server 名（`mcpServers` key） | `whalego` |
| MCP 端点 | `http://host.docker.internal:8765/mcp`（`/mcp` 为 MCP 惯例，不改） |
| 协议载荷 | `whalego-observe-v1` / `whalego-act-v1` / `whalego-error-v1` / `whalego-health-v1` … |
| 会话 id | `wg-<sha16>`（如 `wg-8f21cd04`） |
| C# 解决方案/项目 | `WhaleGo.sln` / `WhaleGo.Contracts` / `WhaleGo.Platform` / `WhaleGo.Executor` |
| 测试项目/夹具 | `WhaleGo.Contracts.Tests` / `WhaleGo.Platform.Tests` / `WhaleGo.Executor.Tests` / `WhaleGo.Fixtures.WinForms` |
| 环境变量 | `WHALEGO_*`（`WHALEGO_CONFIG`、`WHALEGO_TOKEN`…） |
| 配置/日志目录 | `%APPDATA%\WhaleGo`、`%LOCALAPPDATA%\WhaleGo\logs` |
| 契约文件 | `contracts/whalego.tools.v1.json` |
| GUI 项目/测试 | `WhaleGo.Gui` / `WhaleGo.Gui.Tests`（WPF 设置窗口 + WinForms 托盘，只引用 Contracts；窗口标题「WhaleGo 设置」；浅/深主题） |
| 配置/密钥/状态文件 | `%APPDATA%\WhaleGo\config.json`、`%LOCALAPPDATA%\WhaleGo\secrets.json`、`%LOCALAPPDATA%\WhaleGo\control-state.json` |
| 互斥/窗口事件 | `Local\WhaleGo.Executor.SingleInstance` / `Local\WhaleGo.Executor.ShowSettings`（手动启动，不自启） |
| 无头参数与注入 | `--headless`、`--self-check --json`、`--no-hotkey`、`--config <path>`、`--diagnostics-dump`；`prompts/usage.md`；`contract_version="1.2"` |
| 默认端口 | 8765（不变） |

---

## 5. 迁移清单（2026-10-31 执行）

- [x] 文档/契约/README：`PCCtrl→WhaleGo`、`PcCtrl→WhaleGo`、`pcctrl→whalego`、`pc_*→go_*`、`PCTRL→WHALEGO`；
- [x] 契约文件改名 `contracts/pcctrl.tools.v1.json → contracts/whalego.tools.v1.json`，工具名改 `go_*`；
- [x] 设计文档标题/正文品牌替换；评审文档保留历史名（PCCtrl/pc_*），不篡改历史记录；
- [ ] 中文名「鲸跃/鲸渡/鲸行」最终选择（展示用，可选）；
- [ ] GitHub 仓库（个人账号 `whalego`）与域名（`whalego.dev` 等）注册（用户操作）；
- [ ] 实现阶段直接按新名创建 C#/Python 工程，无历史包袱。

---

## 6. 待用户确认（不阻塞开发）

1. 中文名：**鲸跃**（推荐，动感）/ 鲸渡（跨容器意象）/ 鲸行 / 暂不起中文名。
2. Logo：鲸尾 + 鼠标光标 / 鲸 + 手 / 纯文字标（实现阶段再定）。
3. 是否要 GitHub 组织（@whalego 已被占，可用 `whalego-ai` / `whale-go`）。

---

## 附录：命名对照速查

| 旧 | 新 |
|---|---|
| PCCtrl | WhaleGo |
| PcCtrl.Contracts / Platform / Executor | WhaleGo.Contracts / Platform / Executor |
| astrbot_plugin_pcctrl | astrbot_plugin_whalego |
| /pcctrl, /dh | /whalego, /wg |
| pc_health/apps/windows/observe/act/launch/stop | go_health/apps/windows/observe/act/launch/stop |
| pcctrl-observe-v1 等 | whalego-observe-v1 等 |
| %APPDATA%\PcCtrl | %APPDATA%\WhaleGo |
| PCTRL_* | WHALEGO_* |
