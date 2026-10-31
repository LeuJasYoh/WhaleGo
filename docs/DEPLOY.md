# WhaleGo 部署与运维指南（v0.1，新增实现文档）

> 定位：**怎么在本机把 WhaleGo 跑起来并接到 AstrBot 容器**（部署/配对/防火墙/排障/运维）。
> 上位文档：`DESIGN.md`（v0.5，决策）、`ARCHITECTURE.md`（v0.5，分层）、`PROTOCOL.md`（v1.2，工具面）、`GUI.md`（v0.3，GUI）。
> 本文是**新增文档**（不改变既有文档版本号）；与 PROTOCOL/DESIGN 冲突时以上位文档为准。
> 本机环境（实测基线）：Windows 11 + .NET SDK 10.0.401 + Docker Desktop + AstrBot 4.29.3 容器（mcp 1.30.x）。
> 标注约定：**[已验证]** = 本机实际执行过；**[按设计]** = 依设计/契约实现后应如此，尚未在本机跑通的分支会点明。

---

## 0. 架构速览（一分钟）

```text
AstrBot 容器（模型 + astrbot_plugin_whalego，显示名 WhaleGo）
   │  MCP Streamable HTTP + Authorization: Bearer wg_...
   ▼  http://host.docker.internal:8765/mcp
Windows 宿主：WhaleGo.Executor.exe（WinExe，交互式用户会话）
   ├─ MCP 服务（7 个 go_* 工具，唯一事实源 contracts/whalego.tools.v1.json）
   ├─ 观察：UIA 树 + PrintWindow 客户区截图（state_id / frame 内部绑定）
   ├─ 动作：单通道 go_act（click/set_value/type/key/scroll；回执三态）
   ├─ 止血：只读 / 暂停 / 停止（恢复仅本机）
   └─ GUI：WPF 设置窗口（浅/深主题）+ WinForms NotifyIcon 托盘；**手动启动，不自启**
本地文件：config.json（token 哈希）、secrets.json（明文 token，仅当前用户 ACL）、control-state.json（全局停止持久化）、logs/（审计/日志）
```

- 网络暴露面只有两个路由：`/mcp`（Bearer 认证）与 `/healthz`（默认免认证最小面）。
- 模型看不到也传不了 `session_id`/`request_id`/`scope`（插件按 umo 注入的内部参数）。
- 截图**默认开启**，但 `defaultInclude=false`：**默认不自动附图**，模型显式 `include_screenshot=true` 才请求。
- 暂停/停止后**模型与容器都不能恢复**，只能在本机托盘/设置窗口恢复（停止需二次确认）。

---

## 1. 前置条件

| 项 | 要求 | 检查命令 |
|---|---|---|
| Windows | Windows 10 2004+ / Windows 11，**交互式用户会话**（不能在 Session 0 / 服务里跑，也不支持锁屏无人值守） | `query session`（当前会话应为 Active） |
| 权限 | 普通用户（中完整性）即可；**不要**用管理员运行执行器（会改变 UIPI/焦点语义，且高完整性目标本就不建议自动化） | — |
| .NET（运行） | .NET 10 Desktop Runtime（WinForms/WPF）；用自包含发布可免运行时 | `dotnet --list-runtimes` |
| .NET（构建） | .NET SDK **10.0.401**（`windows/global.json` 固定） | `dotnet --version` → 10.0.401 |
| Docker | Docker Desktop（容器通过 `host.docker.internal` 访问宿主） | `docker ps` |
| AstrBot | 4.29.3 镜像，容器名本文用 `AstrBot`，bind mount `C:\Users\23500\data → /AstrBot/data` | `docker inspect AstrBot --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'` |
| 插件依赖（容器内） | AstrBot 自带 mcp 1.30.x（插件 requirements：`mcp>=1.30,<2`） | `docker exec AstrBot python -c "import mcp; print(mcp.__file__)"` |
| 测试/脚本工具（可选） | `uv` 0.13+（E2E/契约检查用 `uv run --with mcp==1.30.0`） | `uv --version` |

> 本机实测基线（2026-10-31）[已验证]：SDK 10.0.401；运行时 .NETCore/.WindowsDesktop 8.0.33 / 10.0.10 / 10.0.12；容器 `AstrBot` Up，镜像 `m.daocloud.io/docker.io/soulter/astrbot:latest`。
> 证据与原始命令见 [docs/evidence/container-env.md](evidence/container-env.md)。

---

## 2. 构建

```powershell
# 在仓库根目录 D:\Projects\PCCtrl
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
# 等价：dotnet build windows/WhaleGo.sln -c Release

# 测试（dotnet + 插件 pytest，若存在 .venv）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1

# 夹具（测试/E2E 用；已在 solution 中，可单独构建）
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
```

产物：
- 执行器：`windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe`
- WinForms 夹具：`windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe`
- 契约文件随执行器输出：`<exe 目录>\contracts\whalego.tools.v1.json`

> [已验证] `scripts/build.ps1` 在本机返回 `BUILD OK`（0 warnings / 0 errors）。FlaUI 的 `NU1701` 在 Platform 项目里已 `NoWarn`（见 [docs/DEV.md](DEV.md)）。

---

## 3. 启动执行器（**手动启动，不做开机自启**）

### 3.1 首次启动（GUI 配对）

直接双击 `WhaleGo.Executor.exe`，或：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1
```

首启行为 [按设计]：
1. 单实例互斥（`Local\WhaleGo.Executor.SingleInstance`；已有实例时退出码 2 并唤起设置窗口）。
2. 生成 `%APPDATA%\WhaleGo\config.json`（含 `auth.tokens[].sha256`）与 `%LOCALAPPDATA%\WhaleGo\secrets.json`（明文 token，仅当前用户 ACL）。
3. 生成 `control-state.json`、审计/日志目录。
4. 打开 **5 步配对向导**（欢迎与风险 → 监听与 token → 复制插件配置片段 → 连接测试 → 完成）。
5. 托盘常驻（5 态图标）；关闭设置窗口 = 隐藏到托盘，真正退出在托盘菜单「退出」。

**启动方式 = 手动启动**：不做开机自启、不写启动项、不装服务（Q5/D22）。执行器必须跑在你日常登录的交互式桌面里，锁屏/无人值守不支持（`SESSION_LOCKED`）。

### 3.2 常用启动参数

| 参数 | 说明 |
|---|---|
| （无） | GUI 模式：托盘 + WPF 设置窗口 + MCP |
| `--headless` | 无托盘/无设置窗口（CI/E2E；日志走 stdout）；MCP 照常 |
| `--no-hotkey` | 不注册全局热键（CI/E2E 推荐；默认 `Ctrl+Alt+Shift+F12` 只触发紧急停止） |
| `--config <path>` | 指定配置文件（CI 用临时目录，避免碰真实 %APPDATA%） |
| `--port <int>` | 覆盖 `listen.port`（0–65535；测试/多实例用） |
| `--self-check --json` | 自检，JSON 到 stdout；退出码 0=全 OK / 非 0=有失败（不启动 MCP 监听） |
| `--diagnostics-dump` | 打印 go_health 等价 JSON + 最近请求后退出（排障） |

数据目录隔离（测试/多实例排查用）：环境变量 `WHALEGO_DATA_DIR=<dir>` 覆盖 `%LOCALAPPDATA%\WhaleGo`（secrets/control-state/logs/ui-state 等本地数据根）。
单实例互斥名随数据根变化：默认数据根用 `Local\WhaleGo.Executor.SingleInstance`；设置了 `WHALEGO_DATA_DIR` 或 `--config` 时追加
`.\<sha256(数据根)[0..8]\>` 后缀，因此"临时数据根实例"可与用户真实实例共存（同一数据根仍只允许一个 headless 实例，第二个退出码 2）。

```powershell
# 无头 + 临时数据目录 + 自检（不碰用户真实数据）
$env:WHALEGO_DATA_DIR = "$env:TEMP\whalego-check"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1 -Headless -NoHotkey
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/self-check.ps1
```

### 3.3 端口与监听

默认 `0.0.0.0:8765`（DESIGN §3.4/§8.2；容器必须能连）。端口冲突/防火墙/权限问题见 §9。改端口：设置窗口「连接与配对」或 config.json 的 `listen.port`（配置来源优先级 appsettings.json < config.json < WHALEGO_* 环境变量 < 命令行）。

---

## 4. 首次配对（5 步向导）

[按设计] 设置窗口 →「连接与配对」，或首次启动自动进入：

1. **欢迎与风险**：能看屏幕/点鼠标/敲键盘；必须本机运行；截图默认可用但**默认不自动附图**；随时可热键停止；勾选「我明白」。
2. **监听与 token**：显示实际监听（默认 0.0.0.0:8765）；生成 `wg_` 前缀 token（掩码显示，首 5 后 4）。
3. **复制插件配置片段**：一键复制（含明文 token 与 url），形如：

   ```json
   {
     "url": "http://host.docker.internal:8765/mcp",
     "token": "wg_xxxxxxxx",
     "timeout": 45,
     "readonly": false,
     "admin_only": true,
     "allow_screenshot": true,
     "allowed_tools": ["go_health","go_apps","go_windows","go_observe","go_act","go_launch","go_stop"]
   }
   ```

4. **连接测试**：本机自测 /healthz（带 token 200 / 不带 401）+「等待容器连接」进度；出现第一条外部 MCP 请求即绿。
5. **完成**：防火墙提示（复制 scripts/firewall.ps1 命令）、日志/审计目录位置、「启动方式：手动启动」、下一步（AstrBot WebUI 保存并重载插件）。

> 跳过向导也可用：托盘会显示「异常：未完成配对」，设置页常驻引导条。

---

## 5. AstrBot 插件安装（容器侧）

插件目录：`astrbot/astrbot_plugin_whalego/`（机器名 astrbot_plugin_whalego，显示名 **WhaleGo**，主命令 `/whalego`，别名 `/wg`）。

### 5.1 方式 A：宿主机 bind mount 复制（推荐；本机即此形态）

```powershell
# 预演（-WhatIf 只打印将做什么）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -WhatIf

# 安装/更新（复制到 C:\Users\23500\data\plugins\astrbot_plugin_whalego）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1

# 目标目录不同：显式指定 bind-mount 的宿主根
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -DataRoot "C:\Users\23500\data"
```

脚本行为：只复制插件文件（排除 .venv/__pycache__/测试等），**绝不重启/停止/启动容器**；若目标不存在会明确报错。

### 5.2 方式 B：docker cp（无宿主数据目录时）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -Mode Docker -Container AstrBot
# 等价手工命令：
# docker exec AstrBot mkdir -p /AstrBot/data/plugins/astrbot_plugin_whalego
# docker cp "astrbot/astrbot_plugin_whalego/." "AstrBot:/AstrBot/data/plugins/astrbot_plugin_whalego"
```

### 5.3 方式 C：WebUI 上传 zip

AstrBot WebUI →「插件管理」→ 上传 astrbot_plugin_whalego 的 zip（压缩包内顶层目录必须是 `astrbot_plugin_whalego/`）。

### 5.4 安装后（**需要用户手动重载**）

1. AstrBot WebUI →「插件管理」→ 找到 **WhaleGo** →「重载」（reload）。生效不了再**由用户决定**是否重启 AstrBot 容器（部署脚本从不代办重启）。
2. 「配置」里粘贴 §4 第 3 步的片段（`url`、`token` 必备；`admin_only=true`；`allow_screenshot=true`）。
3. 验证：
   - 对话里发 `/whalego health`（或 `/wg health`）应返回执行器健康/协议版本；
   - 让模型调用 `go_apps` / `go_observe`；
   - 容器内直连冒烟见 §7.1。

> 插件配置键（`_conf_schema.json`，snake_case）：url、token(secret)、timeout(默认 45s)、readonly、admin_only(默认 true)、allow_screenshot(默认 true)、inject_usage_prompt(默认 true)、allowed_tools(默认 7 个全开)。

---

## 6. 执行器配置（config.json，本地 camelCase）

位置：`%APPDATA%\WhaleGo\config.json`（测试用 `--config <path>`）。默认值/范围以 [docs/GUI.md §6.2](GUI.md) 与 DESIGN §5.8 为准，最小可用示例：

```json
{
  "listen": { "address": "0.0.0.0", "port": 8765, "healthzRequireToken": false },
  "auth": { "tokens": [ { "name": "astrbot", "sha256": "<64位hex>", "createdAt": "2026-10-31T12:00:00+08:00" } ] },
  "policy": { "readonly": false, "actRatePerMinute": 120, "controllerWaitMs": 2000,
              "stateTtlSeconds": 120, "frameTtlSeconds": 60 },
  "limits": { "maxObserveElements": 300, "maxObserveChars": 20000, "maxImageWidth": 1024,
              "maxImageBytes": 200000, "toolTimeoutSeconds": 40 },
  "screenshot": { "enabled": true, "mode": "printwindow", "defaultInclude": false },
  "privacy": { "maskPasswordFields": true, "sensitiveWindows": ["*password*", "*bank*"] },
  "audit": { "enabled": true, "dir": "%LOCALAPPDATA%\\WhaleGo\\logs", "requireDurableForActions": true },
  "hotkeys": { "enabled": true, "emergencyStop": "Ctrl+Alt+Shift+F12", "openSettings": null },
  "ui": { "notificationsLevel": "important", "theme": "system" },
  "logging": { "level": "information", "dir": "%LOCALAPPDATA%\\WhaleGo\\logs" },
  "launch": { "allowlist": [ { "name": "notepad", "path": "C:\\Windows\\System32\\notepad.exe" } ] }
}
```

要点：
- token 明文**不进** config.json（只存 sha256）；明文在 `%LOCALAPPDATA%\WhaleGo\secrets.json`（仅当前用户 ACL）。
- `launch.allowlist` 是模型 go_launch 的**唯一启动方式**（只接受 name；默认 notepad/calc/mspaint/explorer）。
  Edge/微信等常用程序可按 name+绝对路径格式追加（本机已加 `edge`、`weixin`）。
  **注意：当前实现启动时加载 allowlist，修改 config 后需重启 WhaleGo.Executor 才生效**（GUI 规格里的“热生效”待后续版本）。
- `screenshot.enabled=true` + `screenshot.defaultInclude=false` = **截图可用但不自动附图**（Q4 拍板）。
- 热键默认 Ctrl+Alt+Shift+F12 **只触发紧急停止**，不恢复、不弹确认。
- 不做开机自启（无 startup.* 配置组）。

配置来源优先级：appsettings.json < %APPDATA%\WhaleGo\config.json < 环境变量 `WHALEGO_*` < 命令行；GUI 显示来源徽标，被环境变量/命令行覆盖的项只读。环境变量命名用双下划线分段，如 `WHALEGO_UI__THEME=dark`、`WHALEGO_LISTEN__PORT=8766`。

---

## 7. 网络与防火墙

### 7.1 容器 → 宿主

- Docker Desktop：容器内 `host.docker.internal` 解析到宿主（默认可用）。
- Linux Docker（非 Desktop）需自行 `--add-host=host.docker.internal:host-gateway`（本机为 Docker Desktop，不适用）。
- 容器内冒烟（把 token 换成你的）：

  ```bash
  curl -fsS http://host.docker.internal:8765/healthz
  curl -fsS -H "Authorization: Bearer wg_..." http://host.docker.internal:8765/healthz
  ```

### 7.2 防火墙规则（只放行 Docker/WSL 网段）

执行器监听 `0.0.0.0:8765` 时，**建议**用入站规则把来源限制在 Docker/WSL 虚拟网段，避免 LAN 暴露。脚本：`scripts/firewall.ps1`（`-Action add|remove|show`，add/remove 需管理员）。

```powershell
# 查看当前规则 + 本机自动探测到的 Docker/WSL 网段（非管理员可运行）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action show

# 以管理员身份运行 PowerShell 后：
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action add
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action remove
```

脚本等价命令（会打印出来；可手工执行）：

```powershell
New-NetFirewallRule -DisplayName 'WhaleGo-MCP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8765 -RemoteAddress @('<自动探测的 Docker/WSL 网段 CIDR>') -Profile Any
```

- [已验证] 本机 `firewall.ps1 -Action show` 正常：自动探测到 Docker 网段 `192.168.240.0/20`，规则 `WhaleGo-MCP` 当前 **NOT FOUND**（尚未创建；add 需管理员）。
- 不要用「允许任何远程地址」的规则；如必须手动写，用 `Get-NetIPAddress | ? InterfaceAlias -match 'vEthernet|WSL|Docker'` 确认网段。
- 规则只开 TCP 8765（改端口时同步改 `-Port`）。

### 7.3 不要做的事

- 不要把 8765 暴露到公网/路由器端口映射；没有 TLS（v1）。
- 不要在 `listen.address=127.0.0.1` 时期望容器能连：`host.docker.internal` 走宿主虚拟网卡，不是回环（GUI 会强警告）。
- 不要用管理员运行执行器。

---

## 8. 认证与 token 运维

- 认证：`Authorization: Bearer wg_...`（每次请求）；缺失/错误 → HTTP 401 + `WWW-Authenticate: Bearer`；`/mcp` 必须带 token；`/healthz` 默认免认证（`listen.healthzRequireToken=true` 可要求）。
- 生成：32 字节 CSPRNG → base64url，前缀 `wg_`；服务端只存 SHA-256（常数时间比较）。
- 存储：config.json 只存 `{name,sha256,createdAt}`；明文在 %LOCALAPPDATA%\WhaleGo\secrets.json（仅当前用户 ACL；DPAPI 可选 P2）。token 不进工具参数/返回值/日志/审计/托盘 tooltip。
- 掩码展示：`wg_ab12••••••••9f3c`（首 5 后 4），不提供「显示明文」；要明文只能轮换并复制新 token。
- 轮换：设置窗口 →「连接与配对」→「轮换 token」→ 确认（旧 token 立即失效，AstrBot 插件需同步修改）→ 复制新 token + 新插件片段 → 在 AstrBot WebUI 更新插件配置并重载。
- 轮换后旧 token **立即 401**；插件侧表现为 `AUTH_FAILED`（不重试）。
- secrets 丢失/损坏：UI 显示 token 不可读 → 只能轮换；MCP 服务不受影响（认证用哈希）。
- 复制到剪贴板后 60s 条件清空（仅当剪贴板内容仍等于所复制值）。

### 8.1 认证自测

```powershell
# 无 token -> 401（Windows PowerShell 5.1 用 try/catch 读状态码）
try { Invoke-WebRequest -UseBasicParsing -Uri http://127.0.0.1:8765/mcp -Method Post -Body '{}' -ContentType 'application/json' -Headers @{Accept='application/json, text/event-stream'} } catch { $_.Exception.Response.StatusCode }
# healthz
Invoke-RestMethod -Uri http://127.0.0.1:8765/healthz | ConvertTo-Json -Depth 5
```

---

## 9. 排障

| 症状 | 先查 | 处理 |
|---|---|---|
| 容器连不上（插件 EXECUTOR_UNREACHABLE） | `docker exec AstrBot curl -fsS http://host.docker.internal:8765/healthz`；执行器是否在跑（托盘/进程）；防火墙 | 手动启动执行器；确认监听 0.0.0.0:8765；`scripts/firewall.ps1 -Action show/add`；Linux Docker 检查 --add-host |
| /mcp 401 | 插件 token 与执行器 secrets 是否一致；是否刚轮换 | 重新「复制插件配置」→ AstrBot 更新并重载；轮换后旧 token 立即失效 |
| /healthz 也连不上 | 端口是否被占（单实例/其它程序）、执行器是否异常退出、是否 Session 0 | 看 logs/ 与托盘状态；`--diagnostics-dump`；`netstat -ano | findstr 8765` |
| 工具调用超时（EXECUTOR_TIMEOUT） | 执行器日志/审计；观察是否过大窗口；插件 timeout(45s) 与执行器预算(≤40s) 链 | 缩小 max_elements/max_value_chars；确认 AstrBot tool_call_timeout 足够；网络抖动 |
| 动作返回 PAUSED | `go_health.paused=true` | 用户在本机托盘/设置窗口点「恢复」（模型/容器不能恢复） |
| 动作返回 SESSION_STOPPED | `go_health.stopped/session_stopped` | 会话被模型 go_stop 停（换会话重新观察即可）；全局停止需**本机**托盘「恢复」二次确认；全局停止重启后仍保持 |
| 动作返回 SESSION_LOCKED | 桌面是否锁屏/安全桌面 | 解锁后重新 go_observe（锁屏时树可能仍可读，动作一律拒绝） |
| 动作返回 STALE_STATE | 是否动作后没重新 observe / 超过 state TTL(120s) / 窗口切换 | 重新 go_observe 再动作（这是正常语义，不是 bug） |
| 动作返回 FRAME_NOT_ACTIONABLE | frame 是否过期(60s)/黑帧/最小化/敏感窗口/宿主未验证 | 重新带截图 observe；敏感窗口改用元素路径；Chromium/GPU 坐标按设计降级 |
| 动作返回 UIPI_BLOCKED | 目标窗口是否以管理员/更高完整性运行 | 设计如此（retry=never、not_sent、零发送）；高完整性目标请人工操作；手动复验用 WhaleGo.Fixtures.Elevated（见 DEV.md） |
| 动作返回 CONTROLLER_BUSY | 是否有另一会话持有输入租约（多会话/多连接） | 按 retry_after_ms 等待后重试（retry=retry，state 不失效）；不要并发争抢 |
| AUDIT_UNAVAILABLE | 审计目录是否可写/磁盘满 | 恢复可写；这是 fail-closed 设计（返回前落盘） |
| 点击无效果 | receipt：dispatch_status/escalated/verification | OK: 只表示「已发送」不等于生效；重新 observe 验证；坐标点击要求带截图 observe 的图片像素坐标 |
| 截图看不到 | 三开关 AND：执行器 screenshot.enabled、(模型 include_screenshot=true 或 defaultInclude=true)、插件 allow_screenshot | 检查插件配置 allow_screenshot=true；go_health.capabilities.observation 是否含 screenshot_printwindow；AstrBot/provider 是否支持 image |
| Chromium/GPU 坐标点击不准 | 未验证宿主默认 capture_degraded | 用元素路径（UIA）；不要依赖坐标点击；WGC 采集 P2 |
| 执行器第二次启动立即退出（退出码 2） | 单实例互斥 Local\WhaleGo.Executor.SingleInstance | 已有实例在跑：从托盘打开设置；要换实例先退出旧的 |
| 端口冲突/绑定失败 | `netstat -ano | findstr 8765` | 改 listen.port；托盘「异常」气泡会给出原因 |
| 审计/日志不含输入明文 | 设计如此（只记长度 + SHA-256 前 8 位） | 需要内容请用观察树，不要指望审计回放输入 |

---

## 10. 截图与隐私默认（重要）

- **截图默认开启**（`screenshot.enabled=true`，插件 allow_screenshot=true），但 **defaultInclude=false：默认不自动附图**；模型要图必须显式 `include_screenshot=true`（三开关 AND）。
- 敏感窗口（`privacy.sensitiveWindows`，默认 *password*/*bank*）命中：只返回 UIA 树，**不返回任何截图**，`advisories=["screenshot_suppressed"]`；坐标动作 → FRAME_NOT_ACTIONABLE。
- **自身进程窗口（WhaleGo.*，含设置/诊断窗口）永不枚举、不观察、不截图**。
- UIA IsPassword 控件值恒为 `***`；输入文本不写日志/审计（只记长度 + 哈希）。
- 图片存在时插件在 OK 摘要行最前固定隐私提示：「注意：桌面截图包含用户隐私，未经用户明确要求不得转发。」
- 一键关闭截图：设置窗口「执行策略 → 截图」把 `screenshot.enabled` 关掉（热生效）；插件侧 allow_screenshot=false。

---

## 11. 暂停 / 停止与恢复（止血）

| 态 | 触发 | 动作工具 | 观察 | 恢复 | 持久化 |
|---|---|---|---|---|---|
| 只读 readonly | 执行器或插件配置 readonly（任一） | READONLY_MODE | 允许 | 改配置 | 跟配置 |
| 暂停 paused | 本机托盘/设置窗口「暂停」 | PAUSED | 允许 | 本机一键「恢复」 | 不持久化（重启回运行） |
| 停止（全局）stopped | 本机热键 Ctrl+Alt+Shift+F12 / 托盘「紧急停止」/ 管理员命令 | SESSION_STOPPED | 允许 | **仅本机**托盘/设置窗口「恢复」，需二次确认 | 持久化到 %LOCALAPPDATA%\WhaleGo\control-state.json（重启后仍停止） |
| 停止（会话）session_stopped | 模型调用 go_stop（scope=session） | 仅该会话 SESSION_STOPPED | 允许 | 换/新会话重新观察（该会话停着） | 不持久化 |

- **模型和容器永远不能解除止血**（无 resume 工具；`/whalego resume` 只是提示到本机恢复）。
- 暂停/停止都立即释放 controller lease、取消排队动作；在途派发不撤销（可能 possibly_sent，禁止重试、只能重新观察）。
- 热键只停不恢复，避免误解除。

---

## 12. 无头运行与自检（CI/排障）

```powershell
# 自检（退出码 0=全 OK；JSON 到 stdout）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/self-check.ps1
# 等价：WhaleGo.Executor.exe --self-check --json

# 无头 + 隔离数据目录（不碰真实 %APPDATA%/%LOCALAPPDATA%）
$env:WHALEGO_DATA_DIR = "$env:TEMP\whalego-headless"
WhaleGo.Executor.exe --headless --no-hotkey --config "$env:TEMP\whalego-headless\config.json"
```

自检项（GUI 与 --self-check --json 共用同一逻辑）[按设计]：端口绑定/占用、防火墙规则存在性（只查询）、UIA 可用性、DPI 感知（PerMonitorV2）、交互会话可用、审计目录可写、热键注册、认证（200/401）、主题资源完整性。

---

## 13. 契约一致性（部署/升级前必跑）

工具面唯一事实源 = `docs/PROTOCOL.md` v1.2 + `contracts/whalego.tools.v1.json`（`contract_version="1.2"`）。执行器与插件都必须与契约对齐。

```powershell
# 起执行器（临时数据目录/端口）→ 用真实 mcp 1.30 客户端拉 tools/list → 与契约 diff
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
# 依赖：uv（uv run --no-project --with mcp==1.30.0 ...）
```

通过判据：7 个工具名精确一致、描述一致、inputSchema 结构一致（x-internal 参数剥离后，忽略键序）、模型面 schema 无 session_id/request_id/scope、go_health.protocol_version=1。

升级插件/执行器前建议顺序：`scripts/check-contract.ps1` → `scripts/e2e.ps1`（见 [DEV.md](DEV.md)）。

---

## 14. v1 已知限制（如实）

- 动作：仅左键单击、单键（无组合键）、上下滚动；无右键/双击/拖拽/粘贴/菜单弹层可靠编排；保存对话框仅在可点控件路径下尽力，否则明确失败。
- 观察：长文受 max_value_chars（≤4000）与总预算约束；菜单/弹层不是独立窗口，v1 单窗观察。
- 宿主：Chromium/GPU 未验证宿主坐标点击按设计降级（元素路径可用）；多屏负坐标坐标点击未验证；锁屏/安全桌面不可操作；WGC 采集 P2。
- 安全：无 TLS/mTLS（仅 Bearer）；多执行器未支持；无文件系统读写工具。
- GUI：不做 WebUI/WebView；不做自动更新/安装器/代码签名；**不做开机自启**。
- 审计只记元数据（输入只记长度+哈希），不能回放输入内容。

---

## 15. 安全注意事项（务必）

1. **默认监听 0.0.0.0:8765**：必须用防火墙把来源限制在 Docker/WSL 网段（§7.2）；绝不端口映射到公网；无 TLS，局域网嗅探风险自担。
2. **token 是桌面的钥匙**：不进聊天/截图/日志；只放 AstrBot 插件配置与执行器 secrets.json；怀疑泄漏立即轮换（旧 token 立即 401）。
3. **必须跑在交互式用户会话**：Session 0/服务/无人值守不受支持（无桌面可操作，且 SESSION_LOCKED）；锁屏时不要指望自动化。
4. **执行器以普通用户运行**：不要管理员运行（UIPI/焦点语义变化）；高完整性目标人工操作。
5. **审计 fail-closed**：动作返回前必须落盘；目录不可写时动作被拒绝（宁可不可用，不可无审计）。
6. **止血恢复只在本机**：远程/模型无法解除；这是安全边界，不是缺陷。
7. 不上传截图/观察结果到非用户指定的第三方；模型侧隐私提示固定存在（§10）。

---

## 附：一页命令速查

```powershell
# 构建 / 测试
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1

# 运行（手动启动；GUI / headless）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1 -Headless -NoHotkey -WaitReadySec 30

# 自检 / 契约 / E2E
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/self-check.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1

# 插件安装（不重启容器）/ 防火墙
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -WhatIf
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action show
```