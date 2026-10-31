using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.Tray;

/// <summary>Five tray states from docs/GUI.md section 5.1 (color + shape + text triple encoding).</summary>
public enum TrayState
{
    RunningConnected = 0,
    RunningIdle = 1,
    Paused = 2,
    Stopped = 3,
    Error = 4,
}

/// <summary>Which .ico variant to use: a dark glyph for light task bars and vice versa.</summary>
public enum TrayIconVariant
{
    LightTaskbar = 0,
    DarkTaskbar = 1,
}

public static class TrayIcons
{
    public const string ResourcePrefix = "WhaleGo.Gui.Assets.Icons.";

    public static readonly IReadOnlyList<string> StateIds =
    [
        "running-connected", "running-idle", "paused", "stopped", "error",
    ];

    public static readonly IReadOnlyList<string> VariantIds = ["light-taskbar", "dark-taskbar"];

    public static string StateId(TrayState state) => state switch
    {
        TrayState.RunningConnected => "running-connected",
        TrayState.RunningIdle => "running-idle",
        TrayState.Paused => "paused",
        TrayState.Stopped => "stopped",
        _ => "error",
    };

    public static string VariantId(TrayIconVariant variant) =>
        variant == TrayIconVariant.LightTaskbar ? "light-taskbar" : "dark-taskbar";

    public static string ResourceName(TrayState state, TrayIconVariant variant) =>
        ResourcePrefix + StateId(state) + "." + VariantId(variant) + ".ico";

    public static IReadOnlyList<string> AllResourceNames() =>
        StateIds.SelectMany(_ => VariantIds, (state, variant) => ResourcePrefix + state + "." + variant + ".ico").ToList();
}

public static class TrayIconSelector
{
    public const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// AppsUseLightTheme == 1 (light task bar) => dark glyph; high contrast => light glyph so it stays visible on
    /// the usual black high-contrast background (docs/GUI.md 5.1).
    /// </summary>
    public static TrayIconVariant Select(bool appsUseLightTheme, bool highContrast)
    {
        if (highContrast)
        {
            return TrayIconVariant.DarkTaskbar;
        }

        return appsUseLightTheme ? TrayIconVariant.LightTaskbar : TrayIconVariant.DarkTaskbar;
    }

    public static bool ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>Everything the tray needs from the rest of the UI that is not in StatusSnapshot.</summary>
public sealed record TrayAuxInfo(
    bool TokenAvailable,
    bool HotkeyRegistered,
    string? HotkeyError,
    bool AuditDangerous,
    string Version)
{
    public static readonly TrayAuxInfo Default = new(false, true, null, false, "0.1.0");
}

/// <summary>Immutable tray presentation; the WinForms layer only applies it (never decides state itself).</summary>
public sealed record TrayPresentation(
    TrayState State,
    string StatusLine,
    string Tooltip,
    string PauseResumeLabel,
    bool PauseResumeEnabled,
    bool PauseResumeNeedsConfirmation,
    bool EmergencyStopEnabled,
    bool CopyConfigEnabled,
    IReadOnlyList<string> WarningLines,
    string ErrorReason);

public static class TrayStateMapper
{
    public const int TooltipMaxLength = 63;
    public const int StatusLineMaxLength = 60;
    public const long RecentRequestWindowMs = 5 * 60 * 1000;

    /// <summary>
    /// Maps a snapshot + recent request log to the five tray states. Deterministic and clock-injected so the
    /// mapping table is unit tested without a tray or a kernel.
    /// </summary>
    public static TrayPresentation Map(
        StatusSnapshot snapshot,
        IReadOnlyList<RecentRequest>? recentRequests,
        long nowMs,
        TrayAuxInfo aux)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(aux);

        var state = ResolveState(snapshot, recentRequests, nowMs);
        var errorReason = state == TrayState.Error
            ? FirstMeaningful(snapshot.Warnings) ?? "执行器异常"
            : string.Empty;

        var statusLine = state switch
        {
            TrayState.RunningConnected => "● 运行中 · 已连接",
            TrayState.RunningIdle => "○ 运行中 · 暂无客户端",
            TrayState.Paused => "⏸ 已暂停" + FormatSince(snapshot.PausedSinceMs),
            TrayState.Stopped => "■ 已停止 · 需本机恢复",
            _ => "! 异常：" + Shorten(errorReason, 40),
        };

        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(aux.HotkeyError))
        {
            warnings.Add("⚠ 热键未注册：" + Shorten(aux.HotkeyError, 46));
        }

        if (aux.AuditDangerous)
        {
            warnings.Add("⚠ 审计：非 fail-closed（事故无法复盘）");
        }

        if (state != TrayState.Error && snapshot.Warnings.Count > 0 && snapshot.State != ControlState.Faulted)
        {
            warnings.Add("⚠ " + Shorten(FirstMeaningful(snapshot.Warnings) ?? string.Empty, 50));
        }

        var listen = string.IsNullOrWhiteSpace(snapshot.ListenActual) ? snapshot.ListenRequested : snapshot.ListenActual;
        var sessions = snapshot.Sessions;
        var tooltip = BuildTooltip(state, listen, sessions, snapshot, aux);

        var stopped = snapshot.Stopped;
        var paused = snapshot.Paused && !stopped;
        var label = stopped ? "恢复（需确认）" : paused ? "恢复" : "暂停";
        const bool pauseEnabled = true;

        return new TrayPresentation(
            state,
            Shorten(statusLine, StatusLineMaxLength),
            tooltip,
            label,
            pauseEnabled,
            stopped,
            !stopped,
            aux.TokenAvailable,
            warnings,
            errorReason);
    }

    public static TrayState ResolveState(StatusSnapshot snapshot, IReadOnlyList<RecentRequest>? recentRequests, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State == ControlState.Faulted)
        {
            return TrayState.Error;
        }

        if (snapshot.Stopped)
        {
            return TrayState.Stopped;
        }

        if (snapshot.Paused)
        {
            return TrayState.Paused;
        }

        if (snapshot.Sessions > 0)
        {
            return TrayState.RunningConnected;
        }

        if (recentRequests is not null)
        {
            foreach (var request in recentRequests)
            {
                if (nowMs - request.TsMs <= RecentRequestWindowMs)
                {
                    return TrayState.RunningConnected;
                }
            }
        }

        return TrayState.RunningIdle;
    }

    /// <summary>Single-line tooltip, &lt;=63 chars, no token / full session id / window title (docs/GUI.md 5.1).</summary>
    public static string BuildTooltip(
        TrayState state,
        string listen,
        int sessions,
        StatusSnapshot snapshot,
        TrayAuxInfo aux)
    {
        var stateText = state switch
        {
            TrayState.RunningConnected => "运行中 · 已连接",
            TrayState.RunningIdle => "运行中 · 暂无客户端",
            TrayState.Paused => "已暂停",
            TrayState.Stopped => "已停止",
            _ => "异常",
        };

        var text = "WhaleGo · " + stateText + " / " + (string.IsNullOrWhiteSpace(listen) ? "未监听" : listen) + " / " +
                   sessions.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 会话";
        if (aux.AuditDangerous)
        {
            text += " / 审计非 fail-closed";
        }

        return Shorten(text, TooltipMaxLength);
    }

    private static string FormatSince(long? pausedSinceMs)
    {
        if (pausedSinceMs is null)
        {
            return string.Empty;
        }

        try
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(pausedSinceMs.Value).ToLocalTime();
            return "（" + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 起）";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? FirstMeaningful(IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                return warning.Trim();
            }
        }

        return null;
    }

    public static string Shorten(string? text, int maxLength)
    {
        var value = text ?? string.Empty;
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";
    }
}
