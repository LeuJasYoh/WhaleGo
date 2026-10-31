# 环境证据（原始命令输出）

> 采集时间：2026-10-31（Asia/Shanghai）。用于支撑 DESIGN/ARCHITECTURE/PROTOCOL 中关于容器版本与工具链的事实断言。

## AstrBot 容器

命令：`docker exec AstrBot python -c "import astrbot; print(astrbot.__version__)"`
```text
4.29.3
```

命令：`docker exec AstrBot sh -c "pip list 2>/dev/null | grep -i mcp"`
```text
mcp                                1.30.0
```

命令：`docker exec AstrBot sh -c "pip show mcp 2>/dev/null | head -8"`
```text
Name: mcp
Version: 1.30.0
Summary: Model Context Protocol SDK
Home-page: https://modelcontextprotocol.io
Author: Anthropic, PBC.
Author-email: 
License: MIT
Location: /usr/local/lib/python3.12/site-packages
```

## Docker / 网络

命令：`docker version --format 'client={{.Client.Version}} server={{.Server.Version}}'`
```text
client=29.9.2 server=29.9.2
```

命令：`docker exec AstrBot sh -c "getent hosts host.docker.internal"`
```text
192.168.65.254  host.docker.internal
```

## Windows 工具链

命令：`dotnet --list-sdks`
```text
10.0.302 [C:\Program Files\dotnet\sdk]
10.0.401 [C:\Program Files\dotnet\sdk]
```

命令：`dotnet --list-runtimes`
```text
Microsoft.AspNetCore.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
Microsoft.AspNetCore.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
Microsoft.NETCore.App 8.0.33 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
Microsoft.NETCore.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
Microsoft.NETCore.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
Microsoft.WindowsDesktop.App 8.0.33 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
Microsoft.WindowsDesktop.App 10.0.10 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
Microsoft.WindowsDesktop.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
```

---

## 实现阶段复验（2026-10-31 晚，实现 T1–T7 期间，T6 采集）

> 采集者：T6（docs/scripts）；用于支撑实现阶段的技术栈/兼容性结论。命令均为本机（D:\Projects\PCCtrl，Windows 11）实际执行输出。
> 环境基线见上文；本节只记录**实现阶段新增**的复验项。

### 宿主工具链与包（复验）

命令：`dotnet --version`
```text
10.0.401
```

命令：`dotnet restore windows/WhaleGo.sln`
```text
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
```

命令：`dotnet list windows/src/WhaleGo.Executor/WhaleGo.Executor.csproj package`（节选）
```text
项目“WhaleGo.Executor”具有以下包引用
   [net10.0-windows]:
   顶级包                                 已请求   已解决
   > ModelContextProtocol.AspNetCore      2.3.0   2.3.0
```

命令：`dotnet list windows/src/WhaleGo.Platform/WhaleGo.Platform.csproj package`（节选）
```text
项目“WhaleGo.Platform”具有以下包引用
   [net10.0-windows]:
   顶级包                                                       已请求   已解决
   > FlaUI.UIA3                                               5.0.0   5.0.0
   > Microsoft.Extensions.DependencyInjection.Abstractions    10.0.0  10.0.0
   > Microsoft.Extensions.Logging.Abstractions                10.0.0  10.0.0
```

> FlaUI 的 NuGet 目标框架告警 `NU1701` 在 Platform/Executor csproj 以精确 `NoWarn` 处理（见 docs/DEV.md §9），构建 0 warnings。

Python / uv（E2E 与契约门禁使用与容器一致的 mcp 1.30.0 客户端）：

命令：`uv --version`
```text
uv 0.13.6 (329bf9442 2026-10-26 x86_64-pc-windows-msvc)
```

命令：`uv run --no-project --with mcp==1.30.0 python -c "import sys, importlib.metadata as m; print('python', sys.version.split()[0]); print('mcp', m.version('mcp'))"`
```text
python 3.14.3
mcp 1.30.0
```

说明：
- 宿主另有系统 Python `3.12.10`；uv 使用其管理的 `3.14.3` 解释器 + uv 缓存里的 mcp 1.30.0。**协议兼容性由 mcp 1.30.0 包保证**，解释器版本差异不影响与容器（Python 3.12 + mcp 1.30.0）的 wire 兼容。
- 仓库内历史临时 venv `.tmp/venv-mcp`（gitignored）同样为 python 3.14.3 + mcp 1.30.0，仅作本机离线缓存，不是交付物。
- 容器内 mcp 版本见上文 `docker exec … pip show mcp`：**1.30.0**。

### C# MCP 服务端 ↔ Python mcp 1.30 客户端 往返（text + image + custom header）[已验证]

目的：在实现阶段复验执行器技术栈的关键链路——`ModelContextProtocol.AspNetCore 2.3.0` 的 **handlers-based tools/list（从 contracts 构造）**、Bearer 认证中间件、**自定义 HTTP header 会话注入**、**PNG ImageContent 返回**，与容器/宿主同版本的 `mcp 1.30.0` Python 客户端互通。

临时验证工程（只在本机 `.tmp/tg-docs-scripts/roundtrip/`，**不是仓库交付物**；正式实现见 windows/src/WhaleGo.Executor）：
- 服务端：`net10.0` + `ModelContextProtocol.AspNetCore 2.3.0`；`AddMcpServer(o => { o.ServerInfo=…; o.Handlers.ListToolsHandler=…; o.Handlers.CallToolHandler=…; }).WithHttpTransport(Stateless=false)`；
  tools 直接读 `contracts/whalego.tools.v1.json`（7 个）；CallToolHandler 通过 `IHttpContextAccessor` 读 `X-WhaleGo-Session-Id` 请求头，对 `go_observe` 返回 PNG `ImageContentBlock`，所有调用返回 TextContentBlock；
  `/healthz` 免认证；`/mcp` 前 Bearer 中间件校验（错误 → 401 + `WWW-Authenticate: Bearer`）。
- 客户端：`uv run --no-project --with mcp==1.30.0 python roundtrip_client.py`：`streamablehttp_client(url, headers={"Authorization": "Bearer wg_test", "X-WhaleGo-Session-Id": "wg-8f21cd04"})` → `ClientSession.initialize()` → `list_tools()` → `call_tool("go_health", …)` / `call_tool("go_observe", …)`。

命令：
```powershell
dotnet build .tmp/tg-docs-scripts/roundtrip/s.csproj -c Release
# 启动（后台）
.tmp/tg-docs-scripts/roundtrip/bin/Release/net10.0/s.exe --urls http://127.0.0.1:19765
# 免认证 healthz / 无 token -> 401
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:19765/healthz            # 200 {"ok":true}
# POST /mcp without Bearer -> 401
# 真实 mcp 1.30.0 客户端：
uv run --no-project --with mcp==1.30.0 python .tmp/tg-docs-scripts/roundtrip/roundtrip_client.py http://127.0.0.1:19765/mcp
```

输出（实际）：
```text
spike server pid=29744 url=http://127.0.0.1:19765 healthz={"ok":true}
MCP without token: HTTP 401
tools/list: ['go_health', 'go_apps', 'go_windows', 'go_observe', 'go_act', 'go_launch', 'go_stop']
go_health isError: False
go_health text: OK: go_health sid=wg-8f21cd04 mcpSession=2uzvyRdOKijhNmV_SUTFLg args={"session_id":"wg-8f21cd04"}
go_observe isError: False
go_observe image count: 1 mime: image/png
go_observe text: OK: go_observe sid=wg-8f21cd04 mcpSession=2uzvyRdOKijhNmV_SUTFLg args={"window_id":"w-1","include_screenshot":true,"session_id":"wg-8f21cd04"}
round-trip assertions: ALL PASS
client-exit=0
ROUNDTRIP OK
```

结论：
1. Python mcp 1.30（Streamable HTTP）可完成 initialize / list_tools / call_tool；**7 个契约工具名原样到达客户端**。
2. handlers-based `tools/list` 可直接从 `contracts/whalego.tools.v1.json` 构造，`inputSchema` 原样返回 → 契约单一事实源方案可行（T4 的 tools/list 来源）。
3. 自定义请求头（`X-WhaleGo-Session-Id`）可在 CallToolHandler 内经 `IHttpContextAccessor` 读取 → 会话注入有 HTTP 层兜底路径（主路径仍为插件隐藏参数 `session_id`，二者都属“模型不可见”）。
4. `ImageContentBlock` 以 base64 data + `mimeType="image/png"` 到达 Python 客户端，可解码为合法 PNG（`\x89PNG` 魔数校验）→ go_observe 截图通道可行。
5. 服务端 MCP session id（`ctx.Server.SessionId`）可用 → 支撑“内部参数 → MCP 连接身份 → default”的会话解析顺序（PROTOCOL §3.3）。
6. 无 token 的 `/mcp` 请求返回 401，Bearer 中间件位置正确（执行器正式实现仍需完整认证/错误语义）。

> 该往返为**技术风险验证**，不等于实现验收；实现验收（tools/list 与契约 diff、E2E 全套 oracle）见 [implementation-e2e.md](implementation-e2e.md)。

