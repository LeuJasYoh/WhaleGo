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
