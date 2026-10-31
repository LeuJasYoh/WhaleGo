# WhaleGo.Fixtures.Elevated —— 高完整性（UIPI）手工夹具（P1 手工用例，不进 CI）

> 用途：验证执行器对**更高完整性级别窗口**的拒绝路径（PROTOCOL §8 `UIPI_BLOCKED`，`retry=never`、`dispatch_status=not_sent`）。
> 清单 `requestedExecutionLevel=requireAdministrator`：**每次启动都会触发 UAC**，所以本夹具**不进入自动化 CI**，
> 只在需要人工复验 UIPI 行为时使用。窗口/控件与主夹具同名同语义的子集：`btnIncrement`/`lblCounter`/`lblStatus`。

## 构建（不运行）

```powershell
dotnet build windows/tests/WhaleGo.Fixtures.Elevated -c Release
# 产物：windows/tests/WhaleGo.Fixtures.Elevated/bin/Release/net10.0-windows/WhaleGo.Fixtures.Elevated.exe
```

## 手工 UIPI_BLOCKED 复验步骤（P1）

前置：WhaleGo 执行器以**普通用户**（中完整性）运行；本夹具以管理员（高完整性）运行。

1. 构建并启动夹具（UAC 确认）：
   ```powershell
   $fixture = "windows/tests/WhaleGo.Fixtures.Elevated/bin/Release/net10.0-windows/WhaleGo.Fixtures.Elevated.exe"
   Start-Process $fixture -ArgumentList '--title','uipi','--ready-file',"$env:TEMP\whalego-uipi.ready",'--auto-close-ms','300000'
   # 等 ready 文件出现（内容含 elevated=true）
   ```
2. 用 MCP 客户端 / 插件：
   - `go_windows` 找到标题 `WhaleGo Fixture Elevated - uipi` 的窗口 → 取 `window_id`；
   - `go_observe(window_id=...)` → 得 `state_id` 与元素 `index`；
   - `go_act(op="click", target={kind:"element", index=<btnIncrement 的 index>, state_id:...}, args={button:"left",count:1})`。
3. 期望（oracle）：
   - `isError=true`，文本首行 `ERROR[UIPI_BLOCKED]: …`；
   - 错误 JSON：`code="UIPI_BLOCKED"`、`retry="never"`，内嵌 `action_receipt.dispatch_status="not_sent"`、`retry_action=false`；
   - 因为 `not_sent`，该会话的 state/frame **保持有效**（PROTOCOL §5.2），可直接重试同一 index（例如改用前台激活后再试，仍应被拒绝）；
   - 执行器审计记录本次拒绝；窗口侧不会收到点击（夹具 `lblCounter` 不变）。
4. 清理：关闭夹具窗口（或等待 `--auto-close-ms`）。

## 说明

- CLI 与主夹具一致：`--title` / `--ready-file` / `--auto-close-ms` / `--click-log` / `--help`。
- 窗口标题前缀 `WhaleGo Fixture Elevated`，automation id：`frmElevated` / `btnIncrement` / `lblCounter` / `lblStatus`。
- 若 UAC 被拒绝，进程不会启动（Start-Process 可能抛错或没有窗口），这是预期行为。
- `--help` 本身也会触发 UAC（清单要求管理员），手工查看帮助时请接受提权。
