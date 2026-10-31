# WhaleGo 设计独立审计 02（第二轮，对抗性）

> 元信息：审计时间 2026-10-31；审计员 design-reviewer；性质：独立红队复审（不采信 response 自述）。
> 审阅现行版：README.md(53) / docs/DESIGN.md(788，页眉仍标 v0.2) / docs/ARCHITECTURE.md(896，页眉 v0.2) /
> docs/PROTOCOL.md(560，v1.1) / contracts/whalego.tools.v1.json(587) / docs/NAMING.md(196) / docs/evidence/container-env.md(59)。
> 方法：全文通读 + 跨文档 token/字段/枚举/数字比对（工具名、错误码、回执、session、TTL、TFM、命令、版本）+ ref/AstrBot 源码抽查。
> 范围：不评审 GUI 设计；仅标出现行"宿主形态/配置/自启/止血"表述与并行 GUI 的可能冲突。历史名 PCCtrl/pc_* 仅出现在 reviews/NAMING 迁移表，符合约定。
>
> **结论：需修订后实现**（0 blocker，9 major，10 minor）。major 均为"实现前必须消歧/定稿"的协议与安全缺口，不是推倒架构；预计 0.5–1 天文档修订可关闭。

## 缺陷表（按严重度）

| ID | 级别 | 位置 | 证据 | 影响 | 建议修法 |
|---|---|---|---|---|---|
| WG-M01 | major | PROTOCOL.md:134,143,214,287,409,465-467；DESIGN.md:358-363,480；NAMING.md:21,156 | `session_id` 是模型可见参数（observe/act），插件本应自行注入；`go_launch`/`go_stop` schema（PROTOCOL.md:374-386,404-411）无此参数；`go_stop.scope=session`（:409）却无法定位会话；go_health 把 controller 的 session_id 暴露给模型（:134）；默认 scope=all（:409） | 模型可读 controller id 后跨会话传参，抢/扰乱其它会话的 lease 与 state；launch/stop 的会话归属与 act/observe 不一致；scope=session 不可实现；全局 stop 可被提示注入用作跨群 DoS | 从模型面 schema 删除 `session_id`/`request_id`，由插件按 umo 注入并在 executor 侧与 MCP 连接身份绑定（或插件签名的映射）；launch/stop 需要会话时仅接受插件注入参数；model 调 stop 默认 scope=session，scope=all 需管理员；health 不向模型回显 controller 的完整 id |
| WG-M02 | major | DESIGN.md:348,713；ARCHITECTURE.md:592-603；PROTOCOL.md:336,358 | DESIGN §5.5"元素动作成功后不读回、verified 由调用方再观察决定"；ARCH §3.12 定义 set_value/type 读回并产出 verified/mismatched；DESIGN 验收 #4 要求"set_value/type 读回一致"；PROTOCOL 只有 receipt 示例，无 verification 状态枚举/模型处置规则 | 执行器与插件对"是否读回、mismatched 怎么处置"会做出相反实现；模型可能把 mismatched 当失败重试 | 在 PROTOCOL §3.7 增 verification 契约（status 闭集、每 op 读回方式、mismatched→只可 reobserve 不可重放），删除 DESIGN 的"全部不读回"表述，ARCH 引用协议 |
| WG-M03 | major | ARCHITECTURE.md:748；NAMING.md:140-145；DESIGN.md:512 | ARCH"命令统一 `permission_type(ADMIN)`"；NAMING 规定 status/health/stop/help=成员、resume/readonly=管理员；DESIGN 只写 resume/readonly 管理员 | 若全 ADMIN，普通成员失去止血命令（只有托盘/热键可用）；若按 NAMING，ARCH 实现会拒绝成员调用 | 以 NAMING 表为唯一事实源：status/health/stop/help=member，resume/readonly=admin；ARCH 命令段引用 NAMING 并删"统一 ADMIN"；补一条"kill switch 对成员可用"的安全理由 |
| WG-M04 | major | PROTOCOL.md:46-47,111,374-386,402；DESIGN.md:371-372,690；contracts:402-440 | go_launch 被归为"只读/查询类"（:46-47）但会启动进程；`exe_path` 接受"PATH 内名称"（:381）；LAUNCH_TIMEOUT 明说"进程可能已启动"（:402）却无 possibly-launched 语义；P0 里程碑已含 launch（DESIGN:690）但协议标 P1（:111） | PATH/当前目录解析可被劫持执行非预期程序；超时后模型重试可能重复启动；launch 是否算"写动作"（审计 fail-closed）未定 | 只接受绝对路径或管理员 allowlist（禁 PATH 名）；launch 明确为写动作、审计 fail-closed；新增 LAUNCH_UNKNOWN/进程已启动语义并禁止自动重试，成功尽量回 pid；PROTOCOL/DESIGN 统一 P0/P1 归属 |
| WG-M05 | major | DESIGN.md:405-410,610,639；ARCHITECTURE.md:842；NAMING.md:124-134 | 执行器被定义为控制台/托盘 WinExe + JSON 配置文件 + 首次启动生成 token + 启动脚本；`/mcp` 与 `/healthz` 之外"无其他路由"（DESIGN:610）；tray/hotkey 在 P1（:408） | 并行的 GUI 设置界面若需读写配置/展示 token/停止恢复，将与"无其他路由"和"托盘是唯一 UI"冲突；无 single-instance/配置热加载/控制 IPC 契约，两个进程可能抢配置/托盘 | 增加"GUI 与 executor 的接口契约"：单实例互斥、配置 schema 版本与热加载（或"改配置需重启"）、token 一次性展示/轮换、停止/恢复的控制通道（进程内 UI vs 本地 IPC），并明确 GUI 不是第二套事实源；同步 DEPLOY/README |
| WG-M06 | major | DESIGN.md:691,709-719；ARCHITECTURE.md:837-844 | P1 验收要求"WPF+Edge+高完整性窗口+用户中途切走"，但 T1-T6 任务表无对应夹具/提权安装说明；#3 依赖 provider image modality；#6 审计 fail-closed 无负例注入；#7 双会话 lease 冲突无脚本；#8"30 分钟部署"无环境前置 | 验收不可判定，容易"假绿"；高完整性/切走需要专门 harness，当前无人负责 | 每条验收补"前置条件+夹具+oracle+预期输出"：如 elevated WinForms 助手、焦点 watcher、模拟 provider 无 image、磁盘只读/队列满注入、双 MCP 连接脚本、部署计时基线（机器/网络前提）；夹具列入 T6 交付物 |
| WG-M07 | major | contracts:402-440,578-587；PROTOCOL.md:374-386,447-460,552-560；DESIGN.md:398,536-537；ARCHITECTURE.md:791-793 | contract 的 go_launch 有 `oneOf`（:427）而 PROTOCOL schema 无；contract 的 privacy 仅 2 字段、freshness 缺几何/DPI 与 stop 失效规则；DESIGN §7/§5.9 首条与 ARCH §5 仍写全局"成功=isError=false && dispatch_status=accepted"，与 PROTOCOL:16-17,45-47 的动作/查询二分不一致 | 两个"单一事实源"漂移：模型看到 schema 与执行器校验不一致；只读工具被误套动作判据；隐私/新鲜度实现缺项 | 合同与 PROTOCOL 做 CI diff（DESIGN §6.1 已有 check-contract 脚本职责）；补齐 contract privacy/freshness；DESIGN/ARCH 摘要改为"动作类/查询类"或直接删成功判据句只留链接 |
| WG-M08 | major | PROTOCOL.md:210,319,447-460；DESIGN.md:327 | §3.7 说"用最近一次带截图观察的 frame"（:319），§5.2 说新 go_observe（同一窗口）"frame 顶替"（:451-452），§3.6 又说坐标前必须做带截图观察（:210）；未定义"截图后又一次无截图 observe"是否清掉 frame | 模型按建议先截图再补一次普通观察时，坐标动作会随机 STALE/FRAME_NOT_ACTIONABLE；行为取决于实现 | 定死：无截图 observe 保留旧 frame（需几何/身份/actionable 校验）或强制清空并在 frame 标 `available=false`（advisory），坐标前置说明改为"本次会话最近 frame 必须 actionable" |
| WG-M09 | major | PROTOCOL.md:301-307,317-320；DESIGN.md:353 | type/key 的 target 非必填（allOf 只 required args）；DESIGN 写"否则前台 Unicode 输入"；未定义无 target 时以哪个窗口/焦点为准 | 模型观察 A 窗口后无 target 发键，实际可能落到用户当前焦点 B 窗口（含密码框/聊天框），造成误输入且无 FEEDBACK | type/key 要求 target=element（或显式 window_id+前台前置）；无 target 时服务端校验目标窗口=会话当前窗口且为前台，否则 FOREGROUND_REQUIRED 零发送 |
| WG-m01 | minor | DESIGN.md:1,3,246,297-300,442,459,480；ARCHITECTURE.md:1,244,494,511,533,655,695,707,724,827；PROTOCOL.md:143,444；README.md:3；NAMING.md:21,156 | 页眉版本 DESIGN/ARCH=v0.2 而 README=v0.3；代码标识仍 PcError/PcSession/PcTools/PcToolException/PcObserveTool；session id 文档用 `astrbot-…` 而 NAMING 定 `wg-…` | 文档版本自相矛盾；实现者按旧品牌命名；两处 session 前缀有歧义 | 统一页眉 v0.3；Pc*→WhaleGo*/Go*；session 前缀在 PROTOCOL/DESIGN 改为 `wg-` 或更新 NAMING |
| WG-m02 | minor | PROTOCOL.md:32；DESIGN.md:373,610；ARCHITECTURE.md:479 | healthz：PROTOCOL 免认证且仅 ok/version/uptime；DESIGN 说返回版本/会话/能力且"也可要求 token"；ARCH 说"可要求 token" | 未认证信息暴露面与实现行为不一致；排障文档预期不同 | PROTOCOL 写明 healthz 可配置 require_token，payload 定字段（建议不含 sessions 细节） |
| WG-m03 | minor | DESIGN.md:324,386-389,422,504；PROTOCOL.md:555-557 | 200KiB vs maxImageBytes=200000；本地 config camelCase（privacy.sensitiveWindows）而 PROTOCOL §11 引用 snake_case `privacy.sensitive_windows`；截图三开关（executor enabled/defaultInclude、插件 allow_screenshot、模型 include_screenshot）未定义优先级；_conf_schema 注释含 limits 但 §6.6 无 limits 键 | 配置/协议字段对不上，截图预算与开关行为不确定 | 统一单位与键名（wire snake_case、本地 camelCase 并给出映射表）；定义"三开关 AND"并写进 PROTOCOL；删或补 limits |
| WG-m04 | minor | contracts:578-587；PROTOCOL.md:447-460,552-560 | contract 的 privacy 仅 mask_password_fields/screenshot_default；freshness 仅 ttl+any_act+title，缺几何/DPI/stop 规则 | 机器可读契约不完整，check-contract 无法发现语义漂移 | 补齐 privacy（sensitive_windows、screenshot_enabled）、freshness（element vs frame、geometry/DPI、stop/session switch） |
| WG-m05 | minor | PROTOCOL.md:173；DESIGN.md:318 | app_id 冲突用 `#2/#3`（按枚举顺序） | 顺序变化/重启后 #N 不稳定，模型持久化 app_id 会指错应用 | 用 canonical exe 绝对路径 hash 或稳定 pid/exe 组合；至少声明 #N 仅本次列举有效 |
| WG-m06 | minor | DESIGN.md:55,87,93,97,138,524；NAMING.md:58 | "handler 无框架级超时"表述不区分 LLM 工具（有 asyncio.wait_for）与事件 handler；"工具名 sanitize"只适用于内置 MCP 客户端；行号引用 `tool_loop_agent_runner.py:1294-1142` 反向；引用 `.spike` venv 但工作区已删除该目录，docs/evidence 只有容器侧 mcp 1.30.0 | 事实精度不足，后续维护者难以复核；宿主 mcp 2.x 证据链断 | 修正表述与行号；把宿主 mcp 2.3.0 的 pip show/源码片段补进 docs/evidence，或删掉对该路径的断言 |
| WG-m07 | minor | DESIGN.md:507-513；ARCHITECTURE.md:748；NAMING.md:140-145 | NAMING 定义 /wg 别名与 /whalego help，DESIGN/ARCH 命令清单无别名/help | 实现漏做别名/帮助；与"两处文档一致"目标不符 | 命令清单以 NAMING 为准并在 DESIGN/ARCH 引用（AstrBot CommandFilter 支持 alias，ref/AstrBot/star/filter/command.py:37） |
| WG-m08 | minor | PROTOCOL.md:444-445,452；DESIGN.md:300-311 | §5.1 写"每会话同一时刻只有一个当前 state"，§5.2 又写新 observe（其它窗口）"该窗口旧 state 失效"；PcSession 旧名 | 多窗口语义读起来像 per-window 状态，与单窗口模型矛盾 | 统一措辞："仅保留最近一个窗口的 state；切换窗口即丢弃旧窗口 state"；PcSession→GoSession/WhaleGoSession |
| WG-m09 | minor | PROTOCOL.md:234-236,242 | `signature:"nonblank_ok"`、`resolution:"exact"` 无取值词表；advisories 有闭集而它们没有 | 模型/实现自定义值，跨版本不兼容 | 给 signature/resolution 定闭集并写"新增值需升协议" |
| WG-m10 | minor | PROTOCOL.md:111；DESIGN.md:690；README.md:51 | go_launch 协议标 P1，DESIGN/README 的 P0 已含 launch | 里程碑验收范围不一致 | 统一：若 P0 交付 launch，则 PROTOCOL 去掉 P1 标注；否则 P0 删除 launch |

## 旧评审闭合核对表（B1-B3 / M1-M13 / m1-m9）

| 项 | 结论 | 当前落点（现行文本） |
|---|---|---|
| B1 插件注册 | PASS | DESIGN.md:437-466；ARCHITECTURE.md:655-661,698-725（子类+handler=None+禁止绑定方法+容器冒烟 ARCH:781-782） |
| B2 TFM | PASS | DESIGN.md:221-225,632；ARCHITECTURE.md:112-115,839（含测试项目 net10.0-windows） |
| B3 单失败通道 | PASS（核心） | PROTOCOL.md:15-20,43-47,342-363；残留摘要见 WG-M07 |
| M1 新鲜度 | PASS | PROTOCOL.md:21-22,447-460；frame_id 内部化 :256-264；ARCHITECTURE.md:25,797 |
| M2 坐标 hit-test/actionable | PASS | PROTOCOL.md:258-260,476-478；DESIGN.md:342；ARCHITECTURE.md:430-435,459 |
| M3 工具面闭环 | 部分 | go_launch/settle/advisories 已加（PROTOCOL.md:374-402），但 app_id #N 与 schema 漂移见 WG-m05/WG-M07 |
| M4 权限/止血 | PASS | DESIGN.md:500-512,618-623；PROTOCOL.md:404-417；新增命令权限冲突见 WG-M03 |
| M5 隐私 | PASS | PROTOCOL.md:552-560；DESIGN.md:601,620-623；ARCHITECTURE.md:438,766 |
| M6 审计 fail-closed | PASS | ARCHITECTURE.md:35-36,522-524；DESIGN.md:371-372；PROTOCOL.md:512 |
| M7 取消/超时 | PASS | PROTOCOL.md:484-496；ARCHITECTURE.md:33-34 |
| M8 多会话/租约 | PASS（主体） | PROTOCOL.md:462-467；DESIGN.md:356-364；作用域缺口见 WG-M01 |
| M9 mcp 版本 | PASS | DESIGN.md:30-31,137-140,777（附录 #19）；docs/evidence/container-env.md:12-15；requirements 三处一致 |
| M10 v1 收窄 | PASS | PROTOCOL.md:266-318；DESIGN.md:691 |
| M11 schema 自治 | PASS | PROTOCOL.md:268-314（oneOf/allOf；无 strategy/perform_action） |
| M12 7 工具 | PASS | PROTOCOL.md:12,102-113；README.md:33；contracts 7 tools；NAMING.md:71-81 |
| M13 isError/modality | PASS | DESIGN.md:93-94,487-491；ARCHITECTURE.md:763-766 |
| m1 命名/配置 | 部分 | 多数已统一；残留见 WG-m01/WG-m03 |
| m2 版本标识 | PASS | protocol_version=1、请求不带 schema_version（PROTOCOL.md:36,530-532） |
| m3 diff/mode | PASS | observe 无 mode；diff 移出 v1 |
| m4 预算/图片顺序/DPI | PASS | 图片最前（PROTOCOL.md:222,PROTOCOL.md:54；DESIGN.md:559；ARCHITECTURE.md:489/794）；预算标"自定"（DESIGN.md:324） |
| m5 分层规则 | PASS | ARCHITECTURE.md:624,649（L3 允许 import 三个 astrbot 模块）；DESIGN.md:432 不再写观察缓存 |
| m6 错误码单一源 | 部分 | INTERNAL 已补（PROTOCOL.md:524,contracts:573）；contracts 与 PROTOCOL 字段漂移见 WG-M07/WG-m04 |
| m7 receipt 泄漏术语 | PASS | 模型面 receipt 无 method、仅 escalated（PROTOCOL.md:323-340） |
| m8 插件预算/超时 | PASS | DESIGN.md:487,520（事件 handler 与工具调用超时已区分） |
| m9 SSE | PASS | DESIGN.md:145（改"未验证 P3+"）；但 ARCHITECTURE.md:612 扩展点仍列 SSE，可接受（标注未验证即可） |

## 开放问题（不阻塞本次结论，但需 Lead/用户拍板）

1. **GUI 归属**：设置界面是与 executor 同进程（WPF/WinUI 页签）还是独立 app？若独立，其与 executor 的 IPC/单实例/配置热加载/停止恢复契约必须先定（见 WG-M05）；当前"仅 /mcp 与 /healthz"需要相应放宽并做本地鉴权。
2. **会话信任模型**：若坚持 `session_id` 模型可见，需在威胁模型明确"模型可跨会话传参"的风险与缓解；建议直接改为插件注入（WG-M01）。
3. **go_launch 策略**：是否允许 PATH 名/带 arguments/任意 exe？是否 P0 就放开？建议 P0 仅绝对路径 + 仅启动白名单，P2 再扩。
4. **go_stop 默认作用域**：模型默认 stop=session、all 仅管理员，是否接受（当前默认 all）？
5. 中文名（鲸跃/鲸渡/鲸行）与域名/仓库注册仍待用户选择（NAMING.md §6），不影响协议。

> 审计限制：未运行执行器/AstrBot；AstrBot 行为以 ref/AstrBot 4.29.3 源码抽查为准；GUI 未评审。
