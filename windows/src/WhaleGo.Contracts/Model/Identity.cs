namespace WhaleGo.Contracts.Model;

/// <summary>Application identity. <see cref="AppId"/> is stable: win-exe:&lt;sha1(normalized exe path lowercase)[:12]&gt; or win-aumid:&lt;aumid&gt;.</summary>
public sealed record AppInfo(
    string AppId,
    string Name,
    int? Pid,
    string? ExecutablePath,
    string? AumId,
    bool IsPackaged,
    bool IsForeground,
    IReadOnlyList<WindowInfo> Windows);

/// <summary>Top-level window facts. Title is for humans only and never used for identity.</summary>
public sealed record WindowInfo(
    string WindowId,
    string AppId,
    string Title,
    string ClassName,
    int Pid,
    bool IsOnscreen,
    bool IsMinimized,
    bool IsForeground,
    bool IsEnabled,
    RectI Bounds,
    string GeometrySource,
    uint Dpi,
    long CapturedAtMs,
    string Kind = "top-level",
    string? OwnerWindowId = null,
    string? ParentWindowId = null,
    bool IsModal = false);

/// <summary>Window identity used for fail-closed freshness checks (never trust HWND alone).</summary>
public sealed record WindowIdentity(
    string WindowId,
    int OwnerPid,
    string ClassName,
    RectI Bounds,
    uint Dpi)
{
    public static WindowIdentity From(WindowInfo window) =>
        new(window.WindowId, window.Pid, window.ClassName, window.Bounds, window.Dpi);
}

/// <summary>Host environment facts used by go_health, SESSION_LOCKED, self-check and the GUI.</summary>
public sealed record SystemFacts(
    string WindowsVersion,
    int WindowsSessionId,
    bool IsInteractive,
    bool IsLocked,
    string Integrity,
    string DpiAwareness,
    int ProcessId,
    bool IsRemoteSession,
    long UptimeMs);
