namespace WhaleGo.Contracts.Model;

public enum RetryHint
{
    Never = 0,
    Retry = 1,
    Reobserve = 2,
}

/// <summary>Model-facing error object (schema whalego-error-v1). isError is the MCP layer flag, not a field here.</summary>
public sealed record GoError(
    string SchemaVersion,
    string Code,
    string Message,
    RetryHint Retry,
    bool ActionSent,
    DispatchStatus DispatchStatus,
    IReadOnlyDictionary<string, object?>? Details = null,
    IReadOnlyDictionary<string, object?>? Diagnostic = null,
    ActionReceiptWire? ActionReceipt = null,
    long? RetryAfterMs = null);

/// <summary>Stable error-code constants (single source: docs/PROTOCOL.md section 8).</summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";
    public const string UnsupportedAction = "UNSUPPORTED_ACTION";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string SessionStopped = "SESSION_STOPPED";
    public const string Paused = "PAUSED";
    public const string SessionLocked = "SESSION_LOCKED";
    public const string ControllerBusy = "CONTROLLER_BUSY";
    public const string ReadonlyMode = "READONLY_MODE";
    public const string RateLimited = "RATE_LIMITED";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string StaleState = "STALE_STATE";
    public const string FrameNotActionable = "FRAME_NOT_ACTIONABLE";
    public const string ElementUnavailable = "ELEMENT_UNAVAILABLE";
    public const string ElementActionUnavailable = "ELEMENT_ACTION_UNAVAILABLE";
    public const string WindowNotFound = "WINDOW_NOT_FOUND";
    public const string WindowChanged = "WINDOW_CHANGED";
    public const string AppNotFound = "APP_NOT_FOUND";
    public const string AmbiguousApp = "AMBIGUOUS_APP";
    public const string LaunchFailed = "LAUNCH_FAILED";
    public const string LaunchTimeout = "LAUNCH_TIMEOUT";
    public const string ForegroundRequired = "FOREGROUND_REQUIRED";
    public const string UipiBlocked = "UIPI_BLOCKED";
    public const string ActionTimeout = "ACTION_TIMEOUT";
    public const string DispatchUncertain = "DISPATCH_UNCERTAIN";
    public const string Internal = "INTERNAL";

    // Plugin-side (not returned by the executor; kept here so both ends share the vocabulary in tests/docs).
    public const string ExecutorUnreachable = "EXECUTOR_UNREACHABLE";
    public const string ExecutorTimeout = "EXECUTOR_TIMEOUT";
    public const string AuthFailed = "AUTH_FAILED";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string ConnectionLost = "CONNECTION_LOST";

    public static readonly IReadOnlyList<string> ExecutorCodes =
    [
        InvalidArgument, UnsupportedProtocol, UnsupportedAction, SessionNotFound, SessionExpired, SessionStopped,
        Paused, SessionLocked, ControllerBusy, ReadonlyMode, RateLimited, AuditUnavailable, StaleState,
        FrameNotActionable, ElementUnavailable, ElementActionUnavailable, WindowNotFound, WindowChanged,
        AppNotFound, AmbiguousApp, LaunchFailed, LaunchTimeout, ForegroundRequired, UipiBlocked, ActionTimeout,
        DispatchUncertain, Internal,
    ];

    public static readonly IReadOnlyList<string> PluginCodes =
    [
        ExecutorUnreachable, ExecutorTimeout, AuthFailed, ProtocolMismatch, ConnectionLost,
    ];
}
