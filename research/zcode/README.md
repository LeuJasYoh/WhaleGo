# PCCtrl 前期研究：ZCode 电脑操控（CUA）实现

> 本目录是 PCCtrl 项目的前期技术研究产出。目标：在做"容器内 AstrBot → Windows 本机执行器"之前，
> 先把 ZCode 的 Computer Use 实现吃透，作为架构、工具面、Windows 输入、安全与工程化的参考。
> **研究基线**：`ZCode-main/` 开源构建快照（2026-10 检出，文档时间戳至 2026-10 下旬）；
> AstrBot 侧为 master@2026-10-31（`/d/tmp/AstrBot-src`）。
> **状态**：研究完成（6 个分题并行 + Lead 交叉核验），主报告 2026-10-31 定稿。

## 从哪里开始读

| 你的目的 | 建议阅读 |
| --- | --- |
| 5 分钟了解结论与建议 | `ZCode电脑操控实现研究.md` §0（一页速览） |
| 直接开始设计 PCCtrl 架构 | §2（全局架构）+ §11（落地建议，含工具面/协议/路线图） |
| 写 Windows 执行器 | §6（Windows 专项）+ `notes/04-windows-input-and-frames.md` |
| 设计 MCP 工具面/协议 | §3、§5 + `notes/02`、`notes/03`（含 JSON 契约草案） |
| 做 MCP 服务/客户端与传输选型 | §9 + `notes/05-mcp-and-node-repl.md` |
| 找安全/权限/失败关闭设计 | §7 + `notes/01-cua-architecture.md` |
| 找完整自动化工程范本 | §10 + `notes/06-browser-control.md` |

## 文件清单

| 文件 | 内容 | 规模 |
| --- | --- | --- |
| `ZCode电脑操控实现研究.md` | **主报告**：速览/架构/工具面/观察/动作/Windows/安全/生命周期/MCP/浏览器参考/落地建议/交接/附录 | ~2,500 行 |
| `notes/01-cua-architecture.md` | 架构、权限主体、Helper 生命周期、凭据与 fail-closed、多端会话 | 428 行 |
| `notes/02-cua-model-tool-contract.md` | 模型侧工具契约：SKILL/docs/SDK、14 工具手册、错误语义、AstrBot 工具草案 | 497 行 |
| `notes/03-cua-runtime-contract.md` | SDK v3、回执/act-verify、坐标绑定、AX、app identity、JSON 契约草案 | 574 行 |
| `notes/04-windows-input-and-frames.md` | Windows 输入派发、丢弃矩阵、逐工具设计、焦点/DPI、frame surface、打包、缺陷 | 477 行 |
| `notes/05-mcp-and-node-repl.md` | MCP 宿主/传输/双版本/预算、node_repl 真实实现、AstrBot 映射 | 549 行 |
| `notes/06-browser-control.md` | Browser Use 真实代码：命令协议/执行器/快照/截图/会话/权限治理 | 554 行 |
| `00-研究简报与协作约定.md` | 项目背景、证据标准、任务分工与协作约定（历史文档） | — |

所有引用格式为 `路径:行号`；`docs/...`、`packages/...`、`apps/...` 相对 `ZCode-main/`，
`astrbot/...` 相对 `/d/tmp/AstrBot-src`。证据类型标注【代码】【规格】【契约】【推断】。

## 三条最重要的结论（详见主报告 §0/§11）

1. **ZCode 是"本机桌面 Agent"的同机架构**：CUA 只在 desktop-local Host 上可用，
   执行者是独立签名 Helper（macOS TCC 主体；Windows 安装包内 Helper），远程/容器/SSH/WSL **明确不支持**。
   PCCtrl 的"容器→Windows"跨机器链路在 ZCode 里没有对应实现，网络认证/授权必须自己设计（§7.6）。
2. **模型面值得抄的是"契约"而不是"形态"**：ZCode 用"单 js 工具 + SDK"是因为它拥有 Node Agent 运行时；
   PCCtrl 更适合"少而稳的粗粒度 MCP 工具 + 观察/动作/验证三分离 + action_sent/possibly_sent 回执"（§3、§11.3）。
3. **Windows 输入的关键资产是踩坑结论**：UIA→PostMessage→SendInput 后台优先升级链、
   UIPI 预检、焦点防守/120ms 排水/逐字符节拍、读回裁决、丢弃矩阵、UWP 三重 pid、frame 坐标同源——
   这些能直接省掉数周试错（§6）。

## 给后续开发智能体的交接说明

- 本目录**只读参考**；`ZCode-main/` 是上游快照，不要修改。
- 主报告 §11.2-§11.9 已经给出推荐架构、工具面草案、JSON 契约、阶段路线与验收用例，可直接作为开发起点。
- 未核实清单见主报告 §12.4，尤其是：CUA 原生 producer 与真实 broker server 不在本仓库；
  macOS TCC/签名链路、Windows 真实输入行为本环境无法复测；文档存在版本"时差"（§1.4）。
- 若需要更深细节，优先看对应分题 notes 的"证据索引"（每篇文末有逐文件行号表），再回源码/文档核对。
- AstrBot 侧约束见 `../astrbot/`（插件机制/限制），本目录 §9.4 只补充了 MCP 客户端与内置 computer_tools 的事实。

## 后续建议（研究之外）

1. 先做 **P0+P1**（观察 + 点击）的最小闭环：Windows 常驻 MCP 服务（Streamable HTTP + token）+ AstrBot 注册，
   工具面按 §11.3 的 7 个工具草案，验收按 §11.8 的 first-green 用例。
2. Windows 原生层技术选型未定：C#（UIA COM 成本最低）/ C++/Rust / Node N-API 均可，
   但**执行器必须跑在交互式用户会话**，且要自己补 token+ACL+审计（不要照抄 ZCode Windows 当前无鉴权 peer 门）。
3. 建议后续单独研究/引入 `trycua/cua`（MIT）的 Windows driver 作为原生输入参考（§6.9）。
