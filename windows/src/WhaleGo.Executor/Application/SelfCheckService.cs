using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;
using WhaleGo.Executor.Tools;

namespace WhaleGo.Executor.Application;

/// <summary>--self-check / GUI self-check: deterministic items, never prints token plaintext.</summary>
public sealed class SelfCheckService : ISelfCheck
{
    private readonly ConfigService _config;
    private readonly ExecutorPaths _paths;
    private readonly TokenService _tokens;
    private readonly ExecutorRuntimeState _runtime;
    private readonly ISystemProbe _systemProbe;
    private readonly AuditService _audit;
    private readonly ContractToolCatalog? _catalog;

    public SelfCheckService(
        ConfigService config,
        ExecutorPaths paths,
        TokenService tokens,
        ExecutorRuntimeState runtime,
        ISystemProbe systemProbe,
        AuditService audit,
        ContractToolCatalog? catalog = null)
    {
        _config = config;
        _paths = paths;
        _tokens = tokens;
        _runtime = runtime;
        _systemProbe = systemProbe;
        _audit = audit;
        _catalog = catalog;
    }

    public SelfCheckReport Run()
    {
        var items = new List<SelfCheckItem>
        {
            CheckConfigFile(),
            CheckDataDirectory(),
            CheckAuditDirectory(),
            CheckPort(),
            CheckUia(),
            CheckDpi(),
            CheckInteractiveSession(),
            CheckScreenLock(),
            CheckHotkey(),
            CheckToken(),
            CheckContract(),
            CheckThemeResources(),
        };

        var ok = items.All(item => item.Status != SelfCheckStatus.Fail);
        return new SelfCheckReport(ok, items);
    }

    private SelfCheckItem CheckConfigFile()
    {
        var exists = File.Exists(_paths.ConfigPath);
        return new SelfCheckItem(
            "config",
            "配置文件",
            exists ? SelfCheckStatus.Ok : SelfCheckStatus.Warn,
            exists ? _paths.ConfigPath : "配置文件不存在（将使用默认配置）",
            exists ? null : "启动一次正常实例或在设置窗口保存配置");
    }

    private SelfCheckItem CheckDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataRoot);
            var probe = Path.Combine(_paths.DataRoot, ".selfcheck-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new SelfCheckItem("data_dir", "数据目录", SelfCheckStatus.Ok, _paths.DataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SelfCheckItem("data_dir", "数据目录", SelfCheckStatus.Fail, ex.Message, "换一个可写的数据目录（--config/WHALEGO_DATA_DIR）");
        }
    }

    private SelfCheckItem CheckAuditDirectory()
    {
        var config = _config.Config;
        if (!config.AuditEnabled)
        {
            return new SelfCheckItem("audit", "审计目录", SelfCheckStatus.Warn, "审计已关闭（动作不再 fail-closed）", "在设置窗口的隐私与审计页开启");
        }

        var directory = _audit.AuditDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".selfcheck-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new SelfCheckItem("audit", "审计目录", SelfCheckStatus.Ok, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SelfCheckItem("audit", "审计目录", SelfCheckStatus.Fail, ex.Message, "把 audit.dir 改到可写目录；审计不可写时动作会被拒绝");
        }
    }

    private SelfCheckItem CheckPort()
    {
        var config = _config.Config;
        if (_runtime.McpListening)
        {
            return new SelfCheckItem("port", "监听端口", SelfCheckStatus.Ok, _runtime.ListenActual ?? config.ListenEndpoint);
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(ResolveBindAddress(config.ListenAddress), config.ListenPort);
            listener.Start();
            return new SelfCheckItem("port", "监听端口", SelfCheckStatus.Ok, config.ListenEndpoint + "（可绑定）");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            return new SelfCheckItem("port", "监听端口", SelfCheckStatus.Fail, config.ListenEndpoint + "：" + ex.Message, "换一个端口或结束占用该端口的进程");
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch (SocketException)
            {
                // ignore
            }
        }
    }

    private SelfCheckItem CheckUia()
    {
        try
        {
            var facts = _systemProbe.GetFacts();
            var fallback = _systemProbe is FallbackSystemProbe;
            return fallback
                ? new SelfCheckItem("uia", "UI 自动化", SelfCheckStatus.Warn, "平台层未注册（使用 fallback 探针）", "检查 WhaleGo.Platform 依赖是否完整")
                : new SelfCheckItem("uia", "UI 自动化", SelfCheckStatus.Ok, "系统探针可用（" + facts.WindowsVersion + "）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SelfCheckItem("uia", "UI 自动化", SelfCheckStatus.Fail, ex.Message, "检查 UIA 服务/安全软件拦截；必要时重启 Windows");
        }
    }

    private SelfCheckItem CheckDpi()
    {
        try
        {
            var awareness = _systemProbe.GetFacts().DpiAwareness;
            var ok = awareness.Contains("per_monitor", StringComparison.OrdinalIgnoreCase) ||
                     awareness.Contains("permonitor", StringComparison.OrdinalIgnoreCase);
            return new SelfCheckItem("dpi", "DPI 感知", ok ? SelfCheckStatus.Ok : SelfCheckStatus.Warn, awareness, "确认 app.manifest 的 PerMonitorV2 已生效");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SelfCheckItem("dpi", "DPI 感知", SelfCheckStatus.Warn, "无法读取：" + ex.Message);
        }
    }

    private SelfCheckItem CheckInteractiveSession()
    {
        try
        {
            var facts = _systemProbe.GetFacts();
            return facts.IsInteractive
                ? new SelfCheckItem("interactive_session", "交互会话", SelfCheckStatus.Ok, "session=" + facts.WindowsSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : new SelfCheckItem("interactive_session", "交互会话", SelfCheckStatus.Fail, "当前不是交互式会话（Session 0/服务/无桌面）", "请用登录用户的桌面会话直接运行 WhaleGo.Executor.exe");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SelfCheckItem("interactive_session", "交互会话", SelfCheckStatus.Fail, ex.Message, "请用登录用户的桌面会话直接运行");
        }
    }

    private SelfCheckItem CheckScreenLock()
    {
        try
        {
            return _systemProbe.GetFacts().IsLocked
                ? new SelfCheckItem("screen_lock", "锁屏状态", SelfCheckStatus.Warn, "桌面已锁屏：动作会被拒绝（SESSION_LOCKED）", "解锁后重试")
                : new SelfCheckItem("screen_lock", "锁屏状态", SelfCheckStatus.Ok, "未锁屏");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SelfCheckItem("screen_lock", "锁屏状态", SelfCheckStatus.Warn, "无法读取：" + ex.Message);
        }
    }

    private SelfCheckItem CheckHotkey()
    {
        var config = _config.Config;
        if (!config.HotkeysEnabled)
        {
            return new SelfCheckItem("hotkey", "紧急停止热键", SelfCheckStatus.Warn, "热键已禁用", "在设置窗口开启热键（恢复仅本机）");
        }

        if (_runtime.Headless || _runtime.NoHotkey)
        {
            return new SelfCheckItem("hotkey", "紧急停止热键", SelfCheckStatus.Warn, "headless/--no-hotkey：未注册全局热键");
        }

        return _runtime.HotkeyRegistered
            ? new SelfCheckItem("hotkey", "紧急停止热键", SelfCheckStatus.Ok, _runtime.HotkeyGesture ?? config.HotkeysEmergencyStop ?? string.Empty)
            : new SelfCheckItem("hotkey", "紧急停止热键", SelfCheckStatus.Fail, _runtime.HotkeyError ?? "热键注册失败", "修改 hotkeys.emergencyStop 或关闭占用该组合键的程序");
    }

    private SelfCheckItem CheckToken()
    {
        var tokens = _tokens.GetTokens();
        var available = tokens.Count(token => token.Available);
        return available > 0
            ? new SelfCheckItem("token", "认证 token", SelfCheckStatus.Ok, "可用 token：" + string.Join("、", tokens.Where(token => token.Available).Select(token => token.Name)))
            : new SelfCheckItem("token", "认证 token", SelfCheckStatus.Fail, "secrets.json 不可读或与配置不匹配", "在设置窗口轮换 token 并同步 AstrBot 插件配置");
    }

    private SelfCheckItem CheckContract()
    {
        if (_catalog is null)
        {
            return new SelfCheckItem("contract", "工具契约", SelfCheckStatus.Warn, "契约文件未加载");
        }

        var forbidden = _catalog.Document.FindForbiddenModelParams();
        if (forbidden.Count > 0)
        {
            return new SelfCheckItem("contract", "工具契约", SelfCheckStatus.Fail, "模型面 schema 含内部参数：" + string.Join(",", forbidden), "修正 contracts/whalego.tools.v1.json");
        }

        return new SelfCheckItem("contract", "工具契约", SelfCheckStatus.Ok, _catalog.Document.Tools.Count + " 个工具，contract_version=" + _catalog.Document.ContractVersion);
    }

    private SelfCheckItem CheckThemeResources()
    {
        // Theme resources live in the GUI project (T7); headless runs the same logic but cannot load WPF dictionaries.
        return _runtime.Headless
            ? new SelfCheckItem("theme", "主题资源", SelfCheckStatus.Warn, "headless：不加载 WPF 主题资源")
            : new SelfCheckItem("theme", "主题资源", SelfCheckStatus.Ok, "由设置窗口加载（ui.theme=" + _config.Config.UiTheme + "）");
    }

    private static IPAddress ResolveBindAddress(string address)
    {
        if (IPAddress.TryParse(address, out var parsed))
        {
            return parsed;
        }

        return address is "localhost" ? IPAddress.Loopback : IPAddress.Any;
    }
}