# WhaleGo — Agent 入口

本文件给任何在本仓库工作的 AI/agent 的启动提示。完整可复制版见 [docs/AGENT-PROMPT.md](docs/AGENT-PROMPT.md)。

- 项目根：`D:\Projects\PCCtrl`
- 项目：**WhaleGo** —— AstrBot（Docker）容器内的模型经 MCP 控制 Windows 桌面：C#/.NET 10 执行器 + Python AstrBot 插件。

## 先读什么（按顺序）

1. `README.md` —— 入口、一页架构、v1 已知限制。
2. `docs/DESIGN.md` —— 为什么：背景/选型/总体架构/安全/部署/路线 + 决策记录 D1–D22。
3. `docs/PROTOCOL.md` + `contracts/whalego.tools.v1.json` —— **工具面唯一事实源**（7 个 `go_*`、回执三态、错误码、新鲜度、坐标、隐私、模型使用规则）。
4. `docs/ARCHITECTURE.md` —— 分层/封装/接口/状态机/测试 seam/任务分解 T1–T7。
5. `docs/GUI.md` —— 托盘 + WPF 设置窗口规格（v0.3，用户拍板）。
6. `docs/NAMING.md` —— 命名（WhaleGo / `go_*` / `wg-` / 目录）。
7. `docs/reviews/design-review-02-response.md` —— D1–D22 裁决记录（**有争议先看这里**）。
8. 只读参考：`research/`（前期研究）、`ref/AstrBot/`（AstrBot 4.29.3 源码）、`ZCode-main/`（上游快照）；**不要修改**。

## 硬规则

- 改工具契约 = 先改 `PROTOCOL.md`，再同步 `contracts/whalego.tools.v1.json` 与两端实现，并跑 `scripts/check-contract.ps1`（实现阶段建立）。
- 模型面 schema **不得**出现 `session_id`/`request_id`/`scope`（插件按 umo 注入的内部参数）。
- 不得改语义（除非先改 PROTOCOL 并升版本）：`possibly_sent` 禁止重试；动作后 state/frame 失效；恢复止血仅限本机；截图默认开启但 `defaultInclude=false`（默认不自动附图）；自身进程窗口绝不观察/截图。
- **不做 WebUI/WebView**；UI = WPF 设置窗口 + WinForms `NotifyIcon` 托盘（同进程，`UseWPF`+`UseWindowsForms`）。
- **不做开机自启**（手动启动）。
- 版本：DESIGN/ARCH/README **v0.5**、PROTOCOL **v1.2**、`contract_version` **1.2**、GUI **v0.3**；改文档同步页眉与 README 状态行。
- 代码规范：C# `net10.0`/`net10.0-windows`、`Nullable=enable`、`TreatWarningsAsErrors`；Python `ruff` + 类型注解 + async-only；先读后改；不碰 `research/` 与 `ref/`。
- 实现按 `ARCHITECTURE.md §7` 的 T1–T7 分工，写 scope 不重叠；Lead 维护契约与最终合并。

## 常用入口（实现阶段建立后可用）

```powershell
dotnet build windows/WhaleGo.sln -c Release
src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --headless   # 无头
src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --self-check --json
cd astrbot/astrbot_plugin_whalego; uv run pytest
powershell -File scripts/check-contract.ps1
```
