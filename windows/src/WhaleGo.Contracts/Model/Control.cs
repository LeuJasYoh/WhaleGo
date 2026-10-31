namespace WhaleGo.Contracts.Model;

/// <summary>Three-state stop/pause model (readonly is a config dimension, not a state).</summary>
public enum ControlState
{
    Running = 0,
    Paused = 1,
    Stopped = 2,
    Faulted = 3,
}

public enum SelfCheckStatus
{
    Ok = 0,
    Warn = 1,
    Fail = 2,
}

public sealed record SelfCheckItem(string Id, string Name, SelfCheckStatus Status, string Detail, string? Advice = null);

public sealed record SelfCheckReport(bool Ok, IReadOnlyList<SelfCheckItem> Items);

public sealed record ControllerStatus(bool Held, bool Mine, long? ExpiresInMs);

/// <summary>Snapshot consumed by the tray/settings window (GUI reads this through IExecutorControl).</summary>
public sealed record StatusSnapshot(
    ControlState State,
    bool AcceptingActions,
    bool Locked,
    long UptimeMs,
    string Version,
    string ProtocolVersion,
    string McpProtocolVersion,
    bool Paused,
    string? PauseReason,
    long? PausedSinceMs,
    bool Stopped,
    bool SessionStopped,
    bool Readonly,
    bool ScreenshotEnabled,
    bool ScreenshotDefaultInclude,
    bool AuditEnabled,
    bool AuditDurable,
    string ListenRequested,
    string ListenActual,
    int Sessions,
    ControllerStatus? Controller,
    IReadOnlyList<string> Warnings);

/// <summary>Ring-buffer entry for the diagnostics page. No parameters/input text/window titles/token, ever.</summary>
public sealed record RecentRequest(
    long TsMs,
    string Tool,
    string? SessionTag,
    string? Op,
    string Result,
    int ElapsedMs,
    string? ErrorCode = null,
    int? Elements = null,
    bool HasScreenshot = false,
    long? LeaseWaitMs = null);

/// <summary>Masked token descriptor for the settings window (never the plaintext).</summary>
public sealed record TokenDescriptor(string Name, string Masked, string CreatedAt, bool Available);

/// <summary>A camelCase-keyed configuration patch (GUI writes local config keys only; wire is snake_case).</summary>
public sealed record ConfigPatch(IReadOnlyDictionary<string, object?> Values);
