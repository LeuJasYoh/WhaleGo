# WhaleGo —— 让容器里的模型操控你的 Windows 桌面

> 状态：**P0+P1 实现完成，v1.3 移除启动白名单 + 新增组合键（README v0.5 / 协议 v1.3 / contract_version=1.3 / GUI v0.3），本机验收通过**
> （构建 + 401 个 .NET 测试 + 150 个插件测试 + 契约门禁 + 89 条 E2E + 容器→宿主链路；证据见
> [docs/evidence/implementation-e2e.md](docs/evidence/implementation-e2e.md)）。
> 目标：AstrBot（Docker 容器）内的模型通过 MCP 调用 Windows 上的常驻执行器，完成"看屏幕（UIA 树/截图）→ 点鼠标 → 敲键盘"。
> 启动方式：**手动启动**（不做开机自启）；设置窗口 + WinForms 托盘常驻，关闭窗口 = 隐藏到托盘。

## 快速开始

```powershell
# 1) 构建（Windows 宿主机；.NET SDK 10）
dotnet build windows/WhaleGo.sln -c Release

# 2) 运行（托盘 + 设置窗口 + MCP 内核；首次运行生成 config/secrets 并弹配对向导）
windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe

# 3) 无头 / 自检（CI、排障）
windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --headless
windows/src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --self-check --json

# 4) 安装 AstrBot 插件（不重启容器；之后在 WebUI 重载插件并填配对配置）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1

# 5) 验收门禁（本机可复跑）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -CheckHealth
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1
```

部署细节（配对、防火墙、token 轮换、排障）见 [docs/DEPLOY.md](docs/DEPLOY.md)；构建/测试/契约同步见 [docs/DEV.md](docs/DEV.md)。

## 现在看什么

| 想了解 | 文档 |
|---|---|
| 为什么这么选（研究结论/方案对比/选型/总体架构/安全/部署/路线图） | [docs/DESIGN.md](docs/DESIGN.md) |
| 功能怎么分层、模块怎么封装、接口/状态机/测试缝/任务分解 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| MCP 工具面契约：7 个工具、回执三态、错误码、坐标/新鲜度/隐私规则 | [docs/PROTOCOL.md](docs/PROTOCOL.md) |
| Windows 端 WinForms 托盘 + WPF 设置窗口宿主契约（配对/止血/配置/诊断/浅深主题） | [docs/GUI.md](docs/GUI.md) |
| 部署与运维（手动启动/配对/防火墙/插件安装/排障） | [docs/DEPLOY.md](docs/DEPLOY.md) |
| 开发与测试（构建/测试/夹具/契约门禁/E2E） | [docs/DEV.md](docs/DEV.md) |
| 机器可读工具契约（两端共用，单一事实源） | [contracts/whalego.tools.v1.json](contracts/whalego.tools.v1.json) |
| 独立设计评审与逐条处理/复审结论（含审计 02 裁决 D1–D22） | [docs/reviews/](docs/reviews/) |
| 环境证据（工具链、C# MCP ↔ Python mcp 1.30 往返、容器事实） | [docs/evidence/container-env.md](docs/evidence/container-env.md) |
| 实现验收证据（DESIGN §11 oracle 表 + 真实运行记录） | [docs/evidence/implementation-e2e.md](docs/evidence/implementation-e2e.md) |
| 命名设计（WhaleGo / go_ 工具前缀 / 插件/命令/标识） | [docs/NAMING.md](docs/NAMING.md) |
| 前期研究（ZCode CUA / AstrBot 插件） | [research/](research/) |

## 一页架构

```text
AstrBot 容器（模型 + astrbot_plugin_whalego，显示名 WhaleGo）
   │  MCP Streamable HTTP + Bearer Token（host.docker.internal:8765）
   ▼
Windows 执行器 WhaleGo.Executor（C#/.NET 10，单进程：WinForms 托盘 + WPF 设置窗口 + MCP 内核，交互式用户会话）
   ├─ L7 UI（WinForms NotifyIcon 托盘 + WPF 设置窗口/配对；浅/深/高对比主题；--headless 可关）
   ├─ L6 Transport（MCP/认证/限流/healthz 最小面）
   ├─ L5 Tool（7 个 go_* 工具，薄层；tools/list 由 contracts 文件驱动）
   ├─ L4 Application（Observation/Action/Launch/Session/Lease/Audit/Control/Status）
   ├─ L3 Domain（新鲜度/回执状态机/裁剪渲染等纯规则，位于 WhaleGo.Contracts.Domain）
   └─ L2 Platform（Win32/UIA/截图/输入，唯一碰原生 API 的层）
配置文件：config.json（哈希 token）+ secrets.json（明文 token，用户 ACL）+ control-state.json（停机持久化）
日志/审计：默认在可执行程序同目录 `logs/`（隔离测试时在 data root 下）
```

- 工具面：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`（7 个必需；`go_` 是 WhaleGo 品牌前缀，**不是 Go 语言**）；
  模型看不到也传不了 `session_id`/`request_id`/`scope`（插件按 umo 注入的内部参数）；`go_launch` 无白名单（程序名/桌面/开始菜单/完整路径）；`go_act` 支持 `hotkey` 组合键（如 ctrl+s、alt+f4）。
- 契约：观察（UIA 树 + 可选单窗截图 + state_id）与动作（单通道 go_act + receipt 三态）强分离；
  `possibly_sent` 禁止自动重放；动作后状态立即失效，必须重新观察。
- 安全：0.0.0.0 监听 + Bearer Token（`wg_` 前缀，明文只在 secrets.json）、只读/暂停/停止三态止血、controller lease、审计 fail-closed；
  截图默认开启但默认不自动附图（`defaultInclude=false`），敏感窗口抑制/自身窗口排除；暂停/停止恢复仅本机（托盘/设置窗口二次确认），
  热键 Ctrl+Alt+Shift+F12 只触发紧急停止。
- UI：WPF 设置窗口（6 页；浅/深主题跟随系统 + 手动热切换）；WinForms 托盘常驻；**不嵌 WebView、不做 WebUI/网页设置页**。

## 本机验收结果（2026-11-01 v1.3，详见证据文件）

| 验收块 | 结果 |
|---|---|
| 解决方案构建（Debug/Release，含测试/夹具） | ✅ 0 warnings / 0 errors |
| .NET 测试（Contracts 89 + Platform 129 + Executor 65 + Gui 118） | ✅ 401 passed / 0 failed / 0 skipped |
| AstrBot 插件（pytest + ruff + 容器内真实 AstrBot/mcp 1.30 导入与链路冒烟） | ✅ 150 passed；ruff clean；容器→宿主 12/12 链路 PASS |
| 契约门禁 `tools/list` vs `contracts/whalego.tools.v1.json` | ✅ PASS（7 工具、描述、x-internal-stripped schema、无内部参数） |
| E2E（真实执行器 + 真实 mcp 1.30 客户端；夹具动作/截图/回执/STALE/只读/租约/停止/组合键） | ✅ 89 passed / 0 failed / 0 skipped |
| `--self-check --json` | ✅ exit 0，12 项（无头下 hotkey/theme 为 warn，符合预期） |

## v1 已知限制

右键/双击/拖拽/粘贴；菜单弹层的可靠编排；保存对话框全自动（仅元素点击可及则尽力，否则明确失败）；
长文档全量读取（受 `max_value_chars` 与预算约束）；Chromium/GPU 未验证宿主坐标点击；多屏负坐标坐标点击；
锁屏/安全桌面操作；文件系统读写；远程恢复止血；多语言/自动更新/安装器/代码签名；不做 WebUI/WebView；
启动方式 = 手动启动（不自启）；多执行器/TLS。`go_launch` 无白名单（产品决定）：只要名称/路径能解析即可启动；
`go_act op=hotkey` 已放开全部组合键（含 win/alt+f4），不再有黑白名单。UWP 真实应用身份解析已实现但缺 UWP 夹具验证（P1 补齐）。

## 目录

```text
docs/       设计/协议评审/部署/开发/证据
contracts/  机器可读工具契约（单一事实源）
windows/    C# 执行器：src/{WhaleGo.Contracts,WhaleGo.Platform,WhaleGo.Executor,WhaleGo.Gui} + tests/（含单元/集成测试与 WinForms 夹具）
astrbot/    astrbot_plugin_whalego（MCP 客户端 + 7 工具 + 命令 + 测试）
scripts/    构建/测试/运行/自检/插件安装/防火墙/契约门禁/E2E/GUI 截图脚本
research/   前期研究（只读）
ref/        AstrBot 4.29.3 参考源码（只读）
```

## 实现路线

- **P0+P1（已完成，本机验收通过）**：观察闭环（health/apps/windows/observe/launch/stop + 认证 + 无白名单启动 + kill switch）+
  动作闭环（click/set_value/type/key/hotkey/scroll + 坐标 hit-test 链 + 回执/审计/租约 + WPF 设置窗口 v1（浅/深主题）/热键/配对）。
- **P2+（未开始）**：宿主矩阵（Chromium/UWP/WPF）、WGC 截图、drag/paste/右键/双击、确认流程、崩溃重启、TLS/mTLS、多执行器。
- 契约唯一事实源 = [docs/PROTOCOL.md](docs/PROTOCOL.md) + [contracts/whalego.tools.v1.json](contracts/whalego.tools.v1.json)；
  改动顺序：PROTOCOL → contracts → 执行器 Tool 层 → 插件 schema → 跑 `scripts/check-contract.ps1`。

## TODO

- [ ] **重写前端**：重新设计并实现 WhaleGo 的设置/托盘界面（当前为 WPF 6 页设置窗口 + WinForms 托盘）。
- [ ] **提供命令行功能（像 DSH 一样）**：为 WhaleGo 增加 CLI 入口，支持终端下的启动、状态/健康检查、配对/诊断、工具调用等脚本化用法，与 GUI/托盘并存。
