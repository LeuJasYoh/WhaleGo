using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.ViewModels;

/// <summary>Semantic status used by the status pill / tray dot (color + text, never color alone).</summary>
public enum StatusLevel
{
    Running = 0,
    Paused = 1,
    Stopped = 2,
    Faulted = 3,
    Idle = 4,
}

/// <summary>
/// Pure formatting of StatusSnapshot for the settings window (docs/GUI.md sections 8.1 / 9.1).
/// Kept free of WPF so the mapping table can be unit tested.
/// </summary>
public static class StatusText
{
    public static StatusLevel LevelOf(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State == ControlState.Faulted || snapshot.Warnings.Count > 0 && !snapshot.Paused && !snapshot.Stopped && snapshot.State != ControlState.Running)
        {
            return StatusLevel.Faulted;
        }

        if (snapshot.Stopped)
        {
            return StatusLevel.Stopped;
        }

        if (snapshot.Paused)
        {
            return StatusLevel.Paused;
        }

        return snapshot.AcceptingActions ? StatusLevel.Running : StatusLevel.Idle;
    }

    public static string Label(StatusSnapshot snapshot) => LevelOf(snapshot) switch
    {
        StatusLevel.Faulted => "异常",
        StatusLevel.Stopped => snapshot.SessionStopped && !snapshot.Stopped ? "已停止（本会话）" : "已停止",
        StatusLevel.Paused => "已暂停",
        StatusLevel.Idle => "运行中",
        _ => "运行中",
    };

    public static string Detail(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (LevelOf(snapshot) == StatusLevel.Faulted)
        {
            return snapshot.Warnings.Count > 0 ? snapshot.Warnings[0] : "执行器异常，请查看诊断页";
        }

        if (snapshot.Stopped)
        {
            return "紧急停止生效：模型无法再操作桌面，恢复需在本机二次确认";
        }

        if (snapshot.Paused)
        {
            var reason = string.IsNullOrWhiteSpace(snapshot.PauseReason) ? "本机暂停" : snapshot.PauseReason;
            var since = snapshot.PausedSinceMs is null
                ? string.Empty
                : "，自 " + DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PausedSinceMs.Value).ToLocalTime()
                    .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 起";
            return "模型动作会被拒绝（PAUSED）：" + reason + since + "；观察仍可用";
        }

        if (snapshot.SessionStopped)
        {
            return "本会话已被 go_stop 停止；其它会话不受影响";
        }

        if (!snapshot.AcceptingActions)
        {
            return snapshot.Sessions == 0 ? "暂无客户端连接" : "当前暂不接受动作";
        }

        return snapshot.Sessions == 0 ? "等待 AstrBot 容器连接" : "模型可以操作桌面（受只读/速率/审计约束）";
    }

    /// <summary>Configured vs actually bound address (docs/GUI.md 8.1: actual is what Kestrel reports).</summary>
    public static string ListenLine(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requested = snapshot.ListenRequested;
        var actual = snapshot.ListenActual;
        if (string.IsNullOrWhiteSpace(actual))
        {
            return "未监听（配置 " + (string.IsNullOrWhiteSpace(requested) ? "未知" : requested) + "）";
        }

        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase))
        {
            return actual + "（配置 " + requested + "，实际绑定以运行时为准）";
        }

        return actual;
    }

    public static string VersionLine(StatusSnapshot snapshot, WhaleGo.Gui.GuiOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var version = string.IsNullOrWhiteSpace(snapshot.Version) ? options.Version : snapshot.Version;
        var protocol = string.IsNullOrWhiteSpace(snapshot.ProtocolVersion) ? options.ProtocolVersion : snapshot.ProtocolVersion;
        var mcp = string.IsNullOrWhiteSpace(snapshot.McpProtocolVersion) ? "未知" : snapshot.McpProtocolVersion;
        return "WhaleGo " + version + " · protocol v" + protocol + " · MCP " + mcp;
    }

    public static string ControllerLine(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var controller = snapshot.Controller;
        if (controller is null || !controller.Held)
        {
            return "无租约";
        }

        var remaining = controller.ExpiresInMs is null
            ? string.Empty
            : "（剩 " + Math.Max(0, controller.ExpiresInMs.Value / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s）";
        return controller.Mine ? "本会话持有" + remaining : "其它会话持有" + remaining;
    }

    public static string SessionsLine(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sessions = snapshot.Sessions.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个会话";
        return sessions + " · 租约：" + ControllerLine(snapshot);
    }

    /// <summary>One-line summary of the three privacy dimensions (readonly / screenshot / audit).</summary>
    public static string PolicyLine(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var readonlyText = snapshot.Readonly ? "只读：开" : "只读：关";
        var screenshot = snapshot.ScreenshotEnabled
            ? "截图：开" + (snapshot.ScreenshotDefaultInclude ? "（自动附图）" : "（不自动附图）")
            : "截图：关";
        var audit = snapshot.AuditEnabled
            ? "审计：开" + (snapshot.AuditDurable ? "（fail-closed）" : "（非 fail-closed）")
            : "审计：关";
        var locked = snapshot.Locked ? " · 锁屏：是" : string.Empty;
        return readonlyText + " · " + screenshot + " · " + audit + locked;
    }

    public static string PauseResumeLabel(StatusSnapshot snapshot) => snapshot.Stopped ? "恢复（需确认）" : snapshot.Paused ? "恢复" : "暂停";

    public static bool CanPause(StatusSnapshot snapshot) => !snapshot.Stopped && !snapshot.Paused;

    public static bool CanResume(StatusSnapshot snapshot) => snapshot.Paused || snapshot.Stopped;

    public static bool CanEmergencyStop(StatusSnapshot snapshot) => !snapshot.Stopped;
}
