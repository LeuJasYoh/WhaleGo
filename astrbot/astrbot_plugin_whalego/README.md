# WhaleGo AstrBot 插件（astrbot_plugin_whalego）

> AstrBot 4.29.3 插件：容器内的模型通过 MCP（Streamable HTTP + Bearer Token）连接 Windows 上的
> **WhaleGo Executor**，观察并操作桌面（UIA 树 / 可选截图 / 点击 / 输入）。
> `go_` 是 WhaleGo 的品牌前缀，**不是 Go 语言**。契约版本 `contract_version=1.2`（PROTOCOL v1.2）。

## 功能

- 注册 7 个模型可见工具（工具面唯一事实源 = `whalego/contracts/whalego.tools.v1.json`）：
  `go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`。
- 模型看不到也传不了 `session_id` / `request_id` / `scope`：插件按 umo 派生
  `wg-<sha256(umo)[:16]>` 注入；`go_stop` 的 `scope` 固定为 `session`（模型只能停本会话）。
- 统一的模型可见文本：成功 `OK:`、失败 `ERROR[<code>]:`（AstrBot 不读 `isError`，故用文本前缀兜底）。
- 截图三开关 AND：执行器 `screenshot.enabled` 且（模型 `include_screenshot=true` 或执行器
  `screenshot.defaultInclude=true`）且插件 `allow_screenshot=true`；**默认不自动附图**
  （`defaultInclude=false`），模型按需请求；有图时 `OK:` 摘要行最前面固定插入隐私提示。
- 三态止血：只读（插件配置 `readonly` / `/whalego readonly`）、暂停/停止（Windows 本机托盘/热键）；
  **恢复只允许在本机完成**（二次确认），`/whalego resume` 只返回提示，不远程恢复。
- 后台指数退避重连（1s → 30s），失败只告警、不阻塞 AstrBot 启动；动作类超时/断线按
  `DISPATCH_UNCERTAIN`（possibly_sent，禁止重试，先重新观察）呈现。

## 安装

1. 手动启动 Windows 端 `WhaleGo.Executor.exe`（**设计上不做开机自启**），首启完成配对，
   在「设置 → 连接与配对」复制插件配置片段（含 `url` 与 `token`）。
2. 把本目录 `astrbot_plugin_whalego/` 复制到 AstrBot 数据目录的 `data/plugins/` 下：
   `data/plugins/astrbot_plugin_whalego/{main.py,metadata.yaml,...}`（WebUI 上传 zip 安装亦可）。
   Docker 容器内即 `/AstrBot/data/plugins/astrbot_plugin_whalego/`。
3. AstrBot WebUI → 插件管理 → WhaleGo（`astrbot_plugin_whalego`）→ 配置，粘贴配对片段，
   保存并「重载插件」。
4. 验证：对机器人发 `/whalego health`（或 `/wg health`）；再让模型调用 `go_apps` / `go_observe`。

> 不需要重启 AstrBot 容器本身；重载插件即可（本插件不含自启动逻辑）。

## 配置（`_conf_schema.json`）

配对片段示例（Windows 端「复制插件配置」会生成等价 JSON，token 每次复制为明文，请勿外传）：

```json
{
  "url": "http://host.docker.internal:8765/mcp",
  "token": "wg_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "timeout": 45,
  "readonly": false,
  "admin_only": true,
  "allow_screenshot": true,
  "inject_usage_prompt": true,
  "allowed_tools": ["go_health","go_apps","go_windows","go_observe","go_act","go_launch","go_stop"]
}
```

| 键 | 默认 | 说明 |
|---|---|---|
| `url` | `http://host.docker.internal:8765/mcp` | 执行器 MCP 端点；Docker Desktop 默认可用，Linux Docker 需 `--add-host=host.docker.internal:host-gateway` |
| `token` | `""` | `wg_` 前缀 Bearer Token；**只遮罩显示、不写日志**；错误/轮换后返回 `ERROR[AUTH_FAILED]` |
| `timeout` | `45` | 单次调用超时秒；读取 AstrBot `agent_runner.config.misc.tool_call_timeout`（4.29.3 实际键，兼容文档里的 `provider_settings.tool_call_timeout`）并 clamp 到其 -5s；若 ≤ 执行器整体预算（≤40s）会告警 |
| `readonly` | `false` | 只读：插件侧拦截 `go_act`/`go_launch`（`ERROR[READONLY_MODE]`） |
| `admin_only` | `true` | 工具级管理员校验（`event.is_admin()` 兜底；非管理员返回 `ERROR[AUTH_FAILED]: 仅管理员可以使用工具 …`；**务必同时设置 WebUI 工具权限**，见下） |
| `allow_screenshot` | `true` | 允许 `go_observe` 返回截图（三开关 AND 的插件侧） |
| `inject_usage_prompt` | `true` | 经 `on_llm_request` 注入 ≤15 行使用规则（`prompts/usage.md`） |
| `allowed_tools` | 全 7 个 | 白名单在调用时过滤（7 个工具仍注册）；被禁工具返回 `ERROR[INVALID_ARGUMENT]` |

### ⚠ WebUI 工具权限（必须设置）

AstrBot 对**非内置工具**的默认权限是 `member`（源码已核实：`provider/func_tool_manager.py`
的 `_default_permission` 返回 `"member"`）。因此除了 `admin_only=true`，请管理员在
**AstrBot WebUI → 扩展 → 组件** 里把 7 个 `go_*` 工具的权限设置为 `admin`；
两项同时生效才可靠。

## 截图、隐私与图片能力

- 截图默认**开启**，但**默认不自动附图**（`defaultInclude=false`）：模型需要看图/坐标操作时必须传
  `include_screenshot=true`。敏感窗口由执行器抑制截图，自身进程窗口永不观察/截图。
- 有图时插件把固定隐私提示放在 `OK:` 摘要行最前面：
  `注意：桌面截图包含用户隐私，未经用户明确要求不得转发。` ——抵消 AstrBot「可把图片发给用户」的提示。
- **图片可见性**：AstrBot 仅在当前 provider 的 `modalities` 支持 `image` 时才把工具返回的图片
  注入下一轮模型上下文（`tool_loop_agent_runner.py` 的缓存/注入逻辑）。插件会探测
  `provider.provider_config.modalities`；不支持时丢弃图片并追加一句文本说明，避免模型误以为看到了图。
- 输入文本、token、截图字节不写日志/审计；token 明文只存在 Windows 端
  `%LOCALAPPDATA%\WhaleGo\secrets.json`。

## 命令（NAMING §4.3 权限模型）

| 命令 | 权限 | 说明 |
|---|---|---|
| `/whalego status`（`/wg status`） | 成员 | 连接状态/协议/策略/会话/最近观察 |
| `/whalego health` | 成员 | 调用执行器 `go_health` 并摘要 |
| `/whalego stop` | 成员 | **只停本会话**动作（`scope=session`）；观察仍可用 |
| `/whalego resume` | 成员（只读提示） | **不是远程恢复**：仅提示到 Windows 托盘/设置窗口恢复（二次确认） |
| `/whalego readonly on\|off` | 管理员 | 插件只读开关（尽力写回插件配置） |
| `/whalego help` | 成员 | 帮助；不带子命令等同 help |

## 模型使用规则（`prompts/usage.md`，与 PROTOCOL §12 同步）

`inject_usage_prompt=true` 时经 `on_llm_request` 注入 ≤15 行固定规则：先找目标、
动作前必须 `go_observe`、动作后 state 失效需重观察、`OK:`/`ERROR[...]` 语义、
`possibly_sent` 禁止重试、`CONTROLLER_BUSY` 退避、v1 不支持组合键/右键/双击/拖拽/粘贴、
截图隐私与不得转发、暂停/停止只能本机恢复、读长文用 `max_value_chars`。单测锁定与 PROTOCOL §12 同步。

## v1 已知限制

组合键/右键/双击/拖拽/粘贴；菜单弹层可靠编排；保存对话框全自动（仅元素点击可及则尽力，否则明确失败）；
长文档全量读取（受 `max_value_chars` 与预算约束）；Chromium/GPU 未验证宿主坐标点击；多屏负坐标坐标点击；
锁屏/安全桌面操作；文件系统读写；远程恢复止血；多语言/自动更新/安装器/代码签名；多执行器/TLS。
**不做 WebUI/WebView，不做开机自启（手动启动）**。

## 实现说明与 v1 偏差（Lead 裁决，2026-10-31）

本插件按 PROTOCOL v1.2 实现；下列“文档措辞/早期假设 vs 实现”的差异已由 Lead 裁决接受，v1 内不作为缺陷处理：

1. **超时键解析**：AstrBot 4.29.3 的实际键是 `agent_runner.config.misc.tool_call_timeout`（默认 120s）；
   插件优先读它，读不到再回退文档中的 `provider_settings.tool_call_timeout`。`timeout` 默认 45s，
   启动时 clamp 到该值 -5s；结果 ≤ 执行器整体预算（40s）时告警（警告“动作可能先在插件侧超时”）。
2. **权限拒绝复用 `AUTH_FAILED`**：`admin_only=true` 时非管理员调用返回
   `ERROR[AUTH_FAILED]: 仅管理员可以使用工具 …`。v1 不新增错误码（复用 PROTOCOL §8.1 插件侧闭集）；
   这是已知限制：与 token 失效共用错误码，靠 message 与 details 区分（不要只看错误码排查）。
3. **`allowed_tools` 调用时过滤**：7 个工具始终注册（工具面固定 7 个、不随配置增减），
   白名单在调用时校验；被禁工具返回 `ERROR[INVALID_ARGUMENT]`，而不是从模型工具列表中消失。
4. **`go_launch` 超时/断线不可重试**：写动作按 possibly_sent 呈现 `ERROR[DISPATCH_UNCERTAIN]`
   （“动作可能已发送，禁止重试，请先重新观察”），请用 `go_apps` 确认是否已启动；
   只有只读/查询类（health/apps/windows/observe/stop）允许“重连后重试一次”。
5. **无远程恢复 / 无远程全局停止**：`/whalego resume` 只提示到 Windows 托盘/设置窗口恢复；
   `/whalego stop` 固定 `scope=session`（成员/模型只能停本会话）；
   `scope=all` 全局停止与恢复仅限本机托盘/热键/管理员本机操作，容器与模型都没有恢复能力。
6. **内部参数注入**：7 个工具调用都由插件注入内部 `session_id`（模型不可见、不可传）；
   `scope` 仅 `go_stop` 注入且固定 `session`。执行器需忽略多余内部参数（T4 E2E 验证项）。
7. **隐私提示由插件负责**：有截图时由插件把固定隐私提示插入 `OK:` 摘要行最前面；
   执行器不应重复添加。截图默认开启但默认不自动附图（`defaultInclude=false`）。

## 排障

| 症状 | 排查 |
|---|---|
| 工具返回 `ERROR[EXECUTOR_UNREACHABLE]` | Windows 端是否**手动启动**了 WhaleGo；是否同机 Docker；`docker exec AstrBot curl -fsS http://host.docker.internal:8765/healthz`；防火墙是否放行 8765 |
| `ERROR[AUTH_FAILED]` | ① token 错误/已轮换：回 Windows 端「连接与配对」重新复制配置并重载插件；② 非管理员调用受保护工具：message 会写“仅管理员可以使用工具 …”，把 WebUI 工具权限与 `admin_only` 一起检查 |
| `ERROR[EXECUTOR_TIMEOUT]` | 只读类已重连重试一次仍超时：检查执行器日志/审计、`tool_call_timeout`、是否 observe 了过大窗口 |
| `ERROR[DISPATCH_UNCERTAIN]` | 动作可能已发出：**不要重试**，先 `go_observe` 重新观察确认 |
| `ERROR[READONLY_MODE]` | 插件 `readonly=true`：管理员 `/whalego readonly off` 或改配置 |
| `ERROR[PAUSED]` / `ERROR[SESSION_STOPPED]` | 执行器被本机暂停/停止：到 Windows 托盘/设置窗口恢复（远程不能恢复） |
| 模型看不到截图 | 三开关 AND：执行器 `screenshot.enabled`、模型 `include_screenshot=true`（或执行器 `defaultInclude=true`）、插件 `allow_screenshot=true`；再确认 provider 支持 image |
| 点击坐标偏移/无效 | 元素路径优先；坐标动作需先做一次带截图观察；Chromium/GPU 宿主 v1 不保证坐标点击 |
| 插件加载异常 | 查看 AstrBot 插件日志；确认 `mcp>=1.30,<2`（容器自带 1.30.0）；确认目录名 = `astrbot_plugin_whalego` |

## 开发与测试

```bash
# 仓库根目录（需要 uv + Python 3.12）
cd astrbot
uv venv --python 3.12 .venv
uv pip install --python .venv/Scripts/python.exe pytest pytest-asyncio "mcp==1.30.0" ruff
.venv/Scripts/python.exe -m pytest astrbot_plugin_whalego/tests -q
.venv/Scripts/python.exe -m ruff check astrbot_plugin_whalego
.venv/Scripts/python.exe -m ruff format --check astrbot_plugin_whalego
```

- L0-L2（`config/errors/compat/mcp_client/client/session/policy/convert`）**不 import astrbot**，
  可在 venv 里直接测试；L3（`tools.py`）在宿主用最小 AstrBot stub 测试，真正的注册/加载冒烟在
  AstrBot 4.29.3 容器内执行（`docker exec`，不重启容器）。
- 工具注册必须使用官方子类方式（`@dataclass class GoXxxTool(FunctionTool[AstrAgentContext])` +
  `async def call(...)`，`handler=None`）。**禁止** `FunctionTool(handler=插件绑定方法)`：
  AstrBot 加载期会重绑定 handler（`star_manager.py` 的 llm_tools 循环），绑定方法会被错位成
  `(star_cls, event)` → TypeError，且 `handler_module_path` 解析错导致插件禁用后工具仍 active（评审 B1）。
- 契约修改流程：先改 `docs/PROTOCOL.md` → 同步 `contracts/whalego.tools.v1.json` → 复制本包内副本
  （`whalego/contracts/whalego.tools.v1.json`）→ 两端实现；单测会断言包内副本与仓库契约一致。

## 版本

- 插件版本 `0.1.0`；目标 AstrBot `>=4.29,<5`（容器实测 4.29.3，mcp 1.30.0）。
- 契约 `contract_version = 1.2`，`protocol_version = 1`（PROTOCOL v1.2）。
