# WhaleGo —— 让容器里的模型操控你的 Windows 桌面

> 状态：**设计阶段完成（v0.5 / 协议 v1.2 / contract_version=1.2），等待用户评审后进入 P0+P1 实现**。
> 目标：AstrBot（Docker 容器）内的模型通过 MCP 调用 Windows 上的常驻执行器，完成"看屏幕（UIA 树/截图）→ 点鼠标 → 敲键盘"。

## 现在看什么

| 想了解 | 文档 |
|---|---|
| 为什么这么选（研究结论/方案对比/选型/总体架构/安全/部署/路线图） | [docs/DESIGN.md](docs/DESIGN.md) |
| 功能怎么分层、模块怎么封装、接口/状态机/测试缝/任务分解 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| MCP 工具面契约：7 个工具、回执三态、错误码、坐标/新鲜度/隐私规则 | [docs/PROTOCOL.md](docs/PROTOCOL.md) |
| Windows 端 WinForms 托盘 + WPF 设置窗口宿主契约（配对/止血/配置/诊断/浅深主题） | [docs/GUI.md](docs/GUI.md) |
| 机器可读工具契约（两端共用，单一事实源） | [contracts/whalego.tools.v1.json](contracts/whalego.tools.v1.json) |
| 独立设计评审与逐条处理/复审结论（含审计 02 `design-review-02` / 裁决 `design-review-02-response` / 场景走查 `workflow-trace-01`） | [docs/reviews/](docs/reviews/) |
| 环境证据（容器 mcp 1.30.0、Docker/宿主版本） | [docs/evidence/container-env.md](docs/evidence/container-env.md) |
| 命名设计（WhaleGo / go_ 工具前缀 / 插件/命令/标识） | [docs/NAMING.md](docs/NAMING.md) |
| 前期研究（ZCode CUA / AstrBot 插件） | [research/](research/) |

## 一页架构

```text
AstrBot 容器（模型 + astrbot_plugin_whalego，显示名 WhaleGo）
   │  MCP Streamable HTTP + Bearer Token（host.docker.internal:8765）
   ▼
Windows 执行器 WhaleGo.Executor（C#/.NET 10，单进程：WinForms 托盘 + WPF 设置窗口 + MCP 内核，交互式用户会话）
   ├─ L7 UI（WinForms NotifyIcon 托盘 + WPF 设置窗口/配对；浅/深主题；--headless 可关）
   ├─ L6 Transport（MCP/认证/限流/healthz 最小面）
   ├─ L5 Tool（7 个 go_* 工具，薄层）
   ├─ L4 Application（Observation/Action/Launch/Session/Lease/Audit/Control/Status）
   ├─ L3 Domain（新鲜度/回执状态机/裁剪渲染等纯规则）
   └─ L2 Platform（Win32/UIA/截图/输入，唯一碰原生 API 的层）
配置文件：config.json（哈希 token）+ secrets.json（明文 token，用户 ACL）+ control-state.json（停机持久化）
```

- 工具面：`go_health / go_apps / go_windows / go_observe / go_act / go_launch / go_stop`（7 个必需；`go_` 是 WhaleGo 品牌前缀，**不是 Go 语言**）；
  模型看不到也传不了 `session_id`/`request_id`/`scope`（插件注入的内部参数），`go_launch` 只走 allowlist。
- 契约：观察（UIA 树 + 可选单窗截图 + state_id）与动作（单通道 go_act + receipt 三态）强分离；
  `possibly_sent` 禁止自动重放；动作后状态立即失效，必须重新观察。
- 安全：0.0.0.0 监听 + Bearer Token（`wg_` 前缀，明文只在 secrets.json）、只读/暂停/停止三态止血、controller lease、审计 fail-closed；
  截图默认开启但可一键关闭（敏感窗口抑制/自身窗口排除不变，`defaultInclude=false` 不自动附图）；
  暂停/停止恢复仅本机（托盘/设置窗口二次确认），热键 Ctrl+Alt+Shift+F12 只触发紧急停止。
- UI：WPF 设置窗口（浅/深主题跟随系统 + 手动热切换，视觉参考 deepseek.com 的清爽排版/蓝白主色/圆角卡片）；WinForms 托盘常驻；
  **不嵌 WebView、不做 WebUI/网页设置页**；启动方式 = 手动启动（不自启）。

## v1 已知限制

组合键/右键/双击/拖拽/粘贴；菜单弹层的可靠编排；保存对话框全自动（仅元素点击可及则尽力，否则明确失败）；
长文档全量读取（受 `max_value_chars` 与预算约束）；Chromium/GPU 未验证宿主坐标点击；多屏负坐标坐标点击；
锁屏/安全桌面操作；文件系统读写；远程恢复止血；多语言/自动更新/安装器/代码签名；不做 WebUI/WebView；启动方式 = 手动启动（不自启）；多执行器/TLS。

## 目录约定

```text
docs/       设计/协议/评审/证据
contracts/  机器可读工具契约
research/   前期研究（只读）
windows/    实现阶段：C# 执行器 + WinForms 托盘/WPF 设置窗口（待创建）
astrbot/    实现阶段：AstrBot 插件（待创建）
scripts/    实现阶段：构建/安装/E2E/防火墙脚本（待创建）
```

## 实现路线（评审通过后）

P0 观察闭环（go_health/apps/windows/observe/launch/stop + 认证 + launch allowlist + kill switch）→ P1 动作闭环
（click/set_value/type/key/scroll + 坐标 hit-test 链 + 回执/审计/租约 + WPF 设置窗口 v1（浅/深主题）/热键/配对）→ P2 宿主矩阵与增强。
验收标准见 DESIGN §11、任务分解见 ARCHITECTURE §7。
