# contracts/ —— 机器可读工具契约（单一事实源）

- `whalego.tools.v1.json`：7 个 MCP 工具的名字/描述/JSON Schema、错误码、回执语义、新鲜度与隐私常量。
- 执行器：工具代码声明 schema；集成测试用 MCP `tools/list` 结果与本文件做归一化比对（漂移即失败）。
- 插件：启动时从包内副本构造 FunctionTool（不手抄 schema）；连接后拉一次 `tools/list` 对比，只告警。
- 修改契约：先改 PROTOCOL.md 与本文件 → 两端实现 → 跑 `scripts/check-contract.ps1`（实现阶段提供）。
- 版本：新增可选字段不升 `protocol_version`；删除/改语义/改工具名才升。
