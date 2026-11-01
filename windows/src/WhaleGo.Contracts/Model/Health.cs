namespace WhaleGo.Contracts.Model;

public sealed record HealthOsInfo(
    string WindowsVersion,
    int WindowsSessionId,
    bool Interactive,
    bool Locked,
    string Integrity,
    string DpiAwareness);

public sealed record HealthCapabilities(
    IReadOnlyList<string> Observation,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Launch);

public sealed record HealthControllerInfo(bool Held, bool Mine, long ExpiresInMs);

public sealed record HealthLimitsInfo(
    int MaxObserveElements,
    int MaxObserveChars,
    long MaxImageBytes,
    int ActRatePerMinute,
    int StateTtlSeconds,
    int FrameTtlSeconds,
    int ToolTimeoutSeconds);

/// <summary>go_health response (whalego-health-v1).</summary>
public sealed record HealthReport(
    string SchemaVersion,
    bool Ok,
    string Version,
    int ProtocolVersion,
    string McpProtocolVersion,
    HealthOsInfo Os,
    HealthCapabilities Capabilities,
    bool Paused,
    string? PauseReason,
    long? PausedSinceMs,
    bool Stopped,
    bool SessionStopped,
    bool AcceptingActions,
    long UptimeS,
    int Sessions,
    HealthControllerInfo? Controller,
    HealthLimitsInfo Limits,
    long AuditDropped);
