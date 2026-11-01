using System.Collections.Generic;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Tests;

internal static class TestData
{
    public static readonly GuiOptions Options = new(
        Headless: true,
        NoHotkey: false,
        Version: "0.1.0",
        ProtocolVersion: "1",
        ExecutablePath: @"C:\WhaleGo\WhaleGo.Executor.exe",
        ConfigPath: @"C:\Users\test\AppData\Roaming\WhaleGo\config.json",
        LogsDirectory: @"C:\Users\test\AppData\Local\WhaleGo\logs");

    public static StatusSnapshot Status(
        ControlState state = ControlState.Running,
        bool paused = false,
        bool stopped = false,
        bool sessionStopped = false,
        int sessions = 0,
        bool accepting = true,
        bool readonlyMode = false,
        bool screenshot = true,
        bool audit = true,
        bool durable = true,
        string listen = "0.0.0.0:8765",
        IReadOnlyList<string>? warnings = null) =>
        new(
            State: state,
            AcceptingActions: accepting,
            Locked: false,
            UptimeMs: 120_000,
            Version: "0.1.0",
            ProtocolVersion: "1",
            McpProtocolVersion: "2025-06-18",
            Paused: paused,
            PauseReason: paused ? "user_button" : null,
            PausedSinceMs: paused ? 1_760_000_000_000 : null,
            Stopped: stopped,
            SessionStopped: sessionStopped,
            Readonly: readonlyMode,
            ScreenshotEnabled: screenshot,
            ScreenshotDefaultInclude: false,
            AuditEnabled: audit,
            AuditDurable: durable,
            ListenRequested: listen,
            ListenActual: listen,
            Sessions: sessions,
            Controller: sessions > 0 ? new ControllerStatus(true, false, 43_000) : null,
            Warnings: warnings ?? []);

    public static ConfigPatch FullConfig() => new(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        [ConfigKeys.ListenAddress] = "0.0.0.0",
        [ConfigKeys.ListenPort] = 8765,
        [ConfigKeys.ListenHealthzRequireToken] = false,
        [ConfigKeys.PolicyReadonly] = false,
        [ConfigKeys.PolicyActRatePerMinute] = 120,
        [ConfigKeys.PolicyControllerWaitMs] = 2000,
        [ConfigKeys.PolicyStateTtlSeconds] = 120,
        [ConfigKeys.PolicyFrameTtlSeconds] = 60,
        [ConfigKeys.LimitsMaxObserveElements] = 300,
        [ConfigKeys.LimitsMaxObserveChars] = 20000,
        [ConfigKeys.LimitsMaxImageWidth] = 1024,
        [ConfigKeys.LimitsMaxImageBytes] = 200000,
        [ConfigKeys.LimitsToolTimeoutSeconds] = 40,
        [ConfigKeys.ScreenshotEnabled] = true,
        [ConfigKeys.ScreenshotMode] = "printwindow",
        [ConfigKeys.ScreenshotDefaultInclude] = false,
        [ConfigKeys.PrivacyMaskPasswordFields] = true,
        [ConfigKeys.PrivacySensitiveWindows] = new[] { "*password*", "*bank*" },
        [ConfigKeys.AuditEnabled] = true,
        [ConfigKeys.AuditDir] = @"%EXEDIR%\logs",
        [ConfigKeys.AuditRequireDurableForActions] = true,
        [ConfigKeys.HotkeysEnabled] = true,
        [ConfigKeys.HotkeysEmergencyStop] = "Ctrl+Alt+Shift+F12",
        [ConfigKeys.UiNotificationsLevel] = "important",
        [ConfigKeys.UiTheme] = "system",
        [ConfigKeys.LoggingLevel] = "information",
    });
}

internal sealed class FakeSystemThemeProvider : ISystemThemeProvider
{
    public bool IsSystemDarkMode { get; set; }

    public bool IsHighContrast { get; set; }

    public event EventHandler? Changed;

    public void RaiseChanged(bool dark, bool highContrast)
    {
        IsSystemDarkMode = dark;
        IsHighContrast = highContrast;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeThemeResources : IThemeResourceHost
{
    public AppTheme Current { get; private set; } = AppTheme.Light;

    public AppTheme? ThrowOn { get; set; }

    public List<AppTheme> Applied { get; } = [];

    public void Apply(AppTheme theme)
    {
        if (ThrowOn == theme)
        {
            throw new InvalidOperationException("synthetic theme failure");
        }

        Applied.Add(theme);
        Current = theme;
    }
}

internal sealed class FakeWindowTheme : IWindowTheme
{
    public List<(IntPtr Handle, bool Dark)> Calls { get; } = [];

    public bool Result { get; set; } = true;

    public bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark)
    {
        Calls.Add((hwnd, dark));
        return Result;
    }
}

internal sealed class FakeExecutorControl : IExecutorControl
{
    public StatusSnapshot Status { get; set; } = TestData.Status();

    public ConfigPatch Config { get; set; } = TestData.FullConfig();

    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);

    public List<TokenDescriptor> Tokens { get; set; } = [new("astrbot", "wg_ab12••••9f3c", "2026-10-31", true)];

    public SelfCheckReport SelfCheck { get; set; } = new(true, [new SelfCheckItem("port", "端口", SelfCheckStatus.Ok, "8765 可用")]);

    public List<RecentRequest> Recent { get; set; } = [];

    public Dictionary<string, long> Counters { get; set; } = new(StringComparer.Ordinal) { ["go_observe"] = 42 };

    public List<string> Audit { get; set; } = [];

    public ConfigPatch? LastPatch { get; private set; }

    public Exception? ThrowOnSetConfig { get; set; }

    public int SetConfigCalls { get; private set; }

    public string RevealedToken { get; set; } = "wg_newtokenvalue1234";

    public string RotatedToken { get; set; } = "wg_rotatedtoken5678";

    public StatusSnapshot GetStatus() => Status;

    public IReadOnlyList<RecentRequest> GetRecentRequests(int limit) => Recent.Take(limit).ToList();

    public IReadOnlyDictionary<string, long> GetToolCounters() => Counters;

    public void Pause(string reason) => Status = Status with { Paused = true, AcceptingActions = false };

    public void Resume() => Status = Status with { Paused = false, AcceptingActions = true };

    public void Stop(string reason) => Status = Status with { Stopped = true, AcceptingActions = false };

    public void ResetStop() => Status = Status with { Stopped = false, AcceptingActions = true };

    public ConfigPatch GetConfig() => Config;

    public IReadOnlyDictionary<string, string> GetConfigSources() => Sources;

    public void SetConfig(ConfigPatch patch)
    {
        SetConfigCalls++;
        LastPatch = patch;
        if (ThrowOnSetConfig is not null)
        {
            throw ThrowOnSetConfig;
        }

        var merged = new Dictionary<string, object?>(Config.Values, StringComparer.Ordinal);
        foreach (var pair in patch.Values)
        {
            merged[pair.Key] = pair.Value;
        }

        Config = new ConfigPatch(merged);
    }

    public IReadOnlyList<TokenDescriptor> GetTokens() => Tokens;

    public string RotateToken(string name) => RotatedToken;

    public string RevealTokenForCopy() => RevealedToken;

    public SelfCheckReport RunSelfCheck() => SelfCheck;

    public IReadOnlyList<string> ReadAuditTail(int lines) => Audit.TakeLast(lines).ToList();

    public void OpenDataFolder()
    {
    }

    public void RequestRestart()
    {
        RestartRequested = true;
    }

    public bool RestartRequested { get; private set; }
}

internal sealed class FakeDialogs : IUserDialogs
{
    public bool ConfirmResult { get; set; } = true;

    public bool TypedConfirmResult { get; set; } = true;

    public List<string> Calls { get; } = [];

    public string? LastRequiredText { get; private set; }

    public bool Confirm(string title, string message, string confirmText, bool danger = false, string? detail = null)
    {
        Calls.Add("confirm:" + title);
        return ConfirmResult;
    }

    public bool ConfirmWithTypedText(string title, string message, string requiredText, string? detail = null)
    {
        Calls.Add("typed:" + title);
        LastRequiredText = requiredText;
        return TypedConfirmResult;
    }

    public void ShowTokenOnce(string title, string message, string token) => Calls.Add("token:" + token);

    public void ShowMessage(string title, string message) => Calls.Add("message:" + title);

    public string? PickOpenFile(string title, string filter) => null;
}

internal sealed class FakeClipboardService : IClipboardService
{
    public List<string> Texts { get; } = [];

    public void SetText(string text) => Texts.Add(text);

    public void SetText(string text, TimeSpan autoClearAfter) => Texts.Add(text);
}

internal sealed class FakeThemeController : IThemeController
{
    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public AppTheme EffectiveTheme => AppTheme.Light;

    public bool Succeed { get; set; } = true;

    public event EventHandler<AppTheme>? ThemeChanged
    {
        add { }
        remove { }
    }

    public Task<bool> SetPreferenceAsync(ThemePreference preference)
    {
        if (Succeed)
        {
            Preference = preference;
        }

        return Task.FromResult(Succeed);
    }
}

internal sealed class InMemoryUiStateStore : IUiStateStore
{
    private UiState _state = UiState.Default;

    public UiState Load() => _state;

    public void Save(UiState state) => _state = state;
}

internal static class TestContexts
{
    public static GuiContext Create(
        FakeExecutorControl? control = null,
        FakeDialogs? dialogs = null,
        FakeClipboardService? clipboard = null,
        FakeThemeController? theme = null,
        TimeSpan? timeout = null,
        Func<long>? nowMs = null)
    {
        return new GuiContext(
            new SafeControl(control ?? new FakeExecutorControl(), timeout ?? TimeSpan.FromSeconds(2), NullAppLogger.Instance),
            dialogs ?? new FakeDialogs(),
            clipboard ?? new FakeClipboardService(),
            theme ?? new FakeThemeController(),
            NullAppLogger.Instance,
            TestData.Options,
            nowMs ?? (() => 1_760_000_000_000));
    }
}
