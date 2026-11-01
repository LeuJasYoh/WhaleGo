# WhaleGo 项目文档地图与 Agent 提示词

> 用途：新开一个 AI 会话/编码 agent 时，把下面的「短版」或「完整版」粘贴给它，它就知道**文档放在哪、先读哪些、哪些规则不能破**。
> 本仓库根目录另有 `AGENTS.md`（一页入口，部分工具会自动读取）。文档变更时请同步本文件与 AGENTS.md。

## 一、短版（日常开场，复制这段即可）

```text
你在 D:\Projects\PCCtrl 工作，项目是 WhaleGo：AstrBot（Docker）容器内模型经 MCP 控制 Windows 桌面（C#/.NET 10 执行器 + Python AstrBot 插件）。

先按顺序读这些文档再动手：
1) README.md（入口/一页架构/已知限制）
2) docs/DESIGN.md（决策 D1-D22、选型、总体架构、安全、部署）
3) docs/PROTOCOL.md + contracts/whalego.tools.v1.json（工具面唯一事实源：7 个 go_*、回执三态、错误码、新鲜度、坐标、隐私、模型使用规则）
4) docs/ARCHITECTURE.md（分层/封装/接口/测试 seam/任务 T1-T7）
5) docs/GUI.md（托盘 + WPF 设置窗口）
6) 需要时看 docs/reviews/design-review-02-response.md（D1-D22 裁决记录）与 docs/NAMING.md
只读参考：research/、ref/AstrBot/、ZCode-main/（不要改）。

硬规则：改契约先改 PROTOCOL 再同步 contracts JSON 与两端实现；模型面 schema 不得出现 session_id/request_id/scope；possibly_sent 禁止重试、动作后 state 失效、恢复止血仅本机、截图默认开启但 defaultInclude=false、自身窗口绝不观察/截图；UI 用 WPF+WinForms 托盘、不做 WebUI；不做开机自启；版本 DESIGN/ARCH/README v0.5、PROTOCOL v1.3、contract_version 1.3、GUI v0.3。

实现按 ARCHITECTURE §7 的 T1-T7 找任务与 write scope；先读文件再改，不要修改 research/ 与 ref/。
```

## 二、完整版（给需要更严格约束的 agent 会话）

```text
# 角色与项目
你在 D:\Projects\PCCtrl 仓库内工作。项目 WhaleGo：AstrBot 4.29.3（Docker 容器）里的模型通过 MCP Streamable HTTP + Bearer Token 调用 Windows 端常驻执行器（C#/.NET 10，图形界面为托盘 + WPF 设置窗口），完成"观察桌面（UIA 控件树/UIA 截图）→ 点击鼠标 → 输入键盘"。工具前缀 go_*。

# 文档地图（全部在仓库内，相对路径）
- README.md：项目入口、一页架构、v1 已知限制。
- docs/DESIGN.md：为什么——研究结论、方案对比、选型、总体架构、安全、部署、路线图、决策记录 D1-D22。
- docs/ARCHITECTURE.md：怎么做——分层/封装边界/Contracts 接口/状态机/并发/测试 seam/任务分解 T1-T7/风险。
- docs/PROTOCOL.md：工具面契约唯一事实源——7 个工具、回执三态、错误码、新鲜度、坐标与命中测试、超时链、隐私、模型使用规则。
- contracts/whalego.tools.v1.json：机器可读契约（与 PROTOCOL 同步，contract_version=1.3）。
- docs/GUI.md：Windows 托盘 + WPF 设置窗口规格 v0.3（配对/止血/配置/诊断/浅深主题）。
- docs/NAMING.md：命名规范（WhaleGo、go_*、wg- 会话前缀、目录/标识）。
- docs/reviews/design-review-02-response.md：最终裁决记录 D1-D22（任何设计争议先看这里）。
- docs/reviews/design-review-02.md：第二轮独立审计（9 major/10 minor 的原始记录）。
- docs/reviews/workflow-trace-01.md：5 个端到端场景走查与规格缺口。
- docs/evidence/container-env.md：容器环境证据。
- research/：前期研究（只读）。ref/AstrBot/：AstrBot 4.29.3 源码参考（只读）。ZCode-main/：上游快照（只读）。
- 待创建目录：windows/（C# solution）、astrbot/（插件）、scripts/（构建/E2E/防火墙/契约检查）。

# 开工顺序
1) README.md → 2) docs/DESIGN.md §0/§1/§4/§5/§8/§11/§12 → 3) docs/PROTOCOL.md 全篇 + contracts JSON → 4) docs/ARCHITECTURE.md §2/§3/§7 → 5) 按你接的任务读对应章节（UI 读 docs/GUI.md；命名读 docs/NAMING.md）→ 6) 有疑问查 docs/reviews/design-review-02-response.md。

# 不可违反的规则
- PROTOCOL.md 是工具面唯一事实源；改契约必须同步 contracts/whalego.tools.v1.json、执行器 Tool 层、插件 schema，并跑 scripts/check-contract.ps1（实现阶段建立）。
- 模型面工具 schema 不得出现 session_id / request_id / scope（插件按 umo 注入的内部参数，contract 标 internal）。
- 动作类成功 = isError=false 且 dispatch_status=accepted；查询类成功 = isError=false 且首行 OK:；失败一律 ERROR[<code>]:（AstrBot 不读 isError）。
- possibly_sent 禁止重试（只能 reobserve）；动作进入派发后 state/frame 立即失效；不得自动补观察。
- 恢复止血只允许本机（设置窗口/托盘二次确认）；模型/容器不能恢复；全局停止持久化到 control-state.json。
- 截图默认开启但 defaultInclude=false（默认不自动附图，模型按需 include_screenshot=true）；敏感窗口不截图；自身进程窗口（WhaleGo.*）永不枚举/观察/截图。
- GUI：WPF 设置窗口 + WinForms NotifyIcon 托盘，同进程 UseWPF+UseWindowsForms，零第三方 UI 库；不嵌 WebView、不做 WebUI；浅/深主题跟随系统+手动热切换。
- 不做开机自启（手动启动）。
- 版本约定：DESIGN/ARCHITECTURE/README v0.5、PROTOCOL v1.3、contract_version 1.3、GUI v0.3；改文档同步页眉版本与 README 状态行。
- 只读目录：research/、ref/、ZCode-main/；不要修改。
- 代码规范：C# net10.0(-windows)、Nullable=enable、TreatWarningsAsErrors；Python ruff + 类型注解 + async-only。

# 工作方式
- 先读后改；不确定时先查文档/裁决记录，不要发明新设计。
- 实现按 ARCHITECTURE §7 的 T1-T7：T1 Contracts+Domain、T2/T3 Platform、T4 Executor、T5 AstrBot 插件、T6 文档/脚本/E2E、T7 GUI；只写自己任务的 write scope，完成后跑对应单测/自检。
- 文档有错/过时：先报告，再按裁决记录修订并同步版本号；不要悄悄改语义。

# 常用命令（实现阶段建立后可用）
dotnet build windows/WhaleGo.sln -c Release
src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --headless
src/WhaleGo.Executor/bin/Release/net10.0-windows/WhaleGo.Executor.exe --self-check --json
cd astrbot/astrbot_plugin_whalego && uv run pytest
powershell -File scripts/check-contract.ps1
```

## 三、文档地图速查

| 想了解 | 看 |
|---|---|
| 项目是什么、怎么快速跑起来 | README.md |
| 为什么选 C#/插件做 MCP 客户端/0.0.0.0+token、全自动+审计 | docs/DESIGN.md（§3、§8、§12） |
| 7 个工具的参数/返回值/回执/错误码/坐标/隐私 | docs/PROTOCOL.md + contracts/whalego.tools.v1.json |
| 分层、接口、状态机、并发、测试缝、任务分工 T1–T7 | docs/ARCHITECTURE.md |
| 托盘/设置窗口长什么样、有什么设置项、怎么配对/止血 | docs/GUI.md |
| 命名（WhaleGo/go_*/wg-/插件三名字） | docs/NAMING.md |
| 每条设计争议的最终裁决（D1–D22） | docs/reviews/design-review-02-response.md |
| 审计发现、场景走查证据 | docs/reviews/design-review-02.md、workflow-trace-01.md |
| 容器环境事实（mcp 1.30.0 等） | docs/evidence/container-env.md |
| 前期研究/源码参考（只读） | research/、ref/AstrBot/ |

## 四、维护约定

- 新增/改名文档：同步更新本文件 §三 与仓库根 `AGENTS.md`。
- 改契约或设计：同步更新 PROTOCOL/contracts/DESIGN/ARCH/GUI 的版本号，并在 `docs/reviews/` 追加裁决记录。
- 本项目文档语言：简体中文；代码/协议字段：英文。
