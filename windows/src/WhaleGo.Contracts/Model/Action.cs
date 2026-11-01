namespace WhaleGo.Contracts.Model;

public enum DispatchStatus
{
    NotSent = 0,
    Accepted = 1,
    PossiblySent = 2,
}

public enum TargetKind
{
    Element = 0,
    Coordinate = 1,
}

/// <summary>Model-facing action target. Element targets must carry state_id + index; coordinate targets use image pixels.</summary>
public sealed record ActionTarget(
    TargetKind Kind,
    int? ElementIndex = null,
    int? X = null,
    int? Y = null,
    string? StateId = null);

/// <summary>Raw action request as received from the tool layer (args kept as JSON for domain validation).</summary>
public sealed record ActionRequest(
    string SchemaVersion,
    string SessionId,
    string Op,
    ActionTarget Target,
    IReadOnlyDictionary<string, object?> Args,
    string? RequestId = null);

/// <summary>Fully validated, typed action plan (the only shape the platform may execute).</summary>
public sealed record ActionPlan(
    string Op,
    TargetKind Kind,
    int? ElementIndex = null,
    string? StateId = null,
    int? X = null,
    int? Y = null,
    string ClickButton = "left",
    int ClickCount = 1,
    string Modifiers = "",
    string? SetValue = null,
    string? TypeText = null,
    string? Key = null,
    string? ScrollDirection = null,
    double ScrollPages = 1,
    string? Hotkey = null);

public sealed record VerificationInfo(string Status, string? Reason = null)
{
    public static readonly VerificationInfo Unverified = new("unverified");
}

/// <summary>Internal receipt used by application services (includes session/op; the wire receipt is <see cref="ActionReceiptWire"/>).</summary>
public sealed record ActionReceipt(
    string SessionId,
    string Op,
    bool ActionSent,
    DispatchStatus DispatchStatus,
    bool RetryAction,
    TargetKind TargetKind,
    bool Escalated,
    VerificationInfo Verification,
    bool StateAvailable,
    DispatchInfo? Dispatch = null);

/// <summary>Receipt object embedded in the model-facing act report / error object (exact wire shape).</summary>
public sealed record ActionReceiptWire(
    bool ActionSent,
    DispatchStatus DispatchStatus,
    bool RetryAction,
    string TargetKind,
    bool Escalated,
    VerificationInfo Verification,
    bool StateAvailable);

/// <summary>Internal dispatch metadata (audit only; the model never sees method names).</summary>
public sealed record DispatchInfo(string Method, bool Escalated, string? WindowId = null, int? ElementIndex = null);

/// <summary>Platform result of one native dispatch attempt. <c>Sent=false</c> means nothing entered native input.</summary>
public sealed record PlatformActionOutcome(
    bool Sent,
    bool Uncertain,
    string Method,
    bool Escalated,
    string? FailureCode,
    string? FailureDetail,
    string VerificationStatus = "unverified",
    string? VerificationReason = null)
{
    public static PlatformActionOutcome NotSent(string failureCode, string? detail = null, bool escalated = false) =>
        new(false, false, "none", escalated, failureCode, detail);

    public static PlatformActionOutcome Accepted(string method, bool escalated, VerificationInfo verification) =>
        new(true, false, method, escalated, null, null, verification.Status, verification.Reason);

    public static PlatformActionOutcome PossiblySent(string method, bool escalated, string reason) =>
        new(true, true, method, escalated, null, null, "unverified", reason);
}

public enum HostClass
{
    Unknown = 0,
    Deliverable = 1,
    Droppable = 2,
}

/// <summary>Coordinate hit-test result used to choose a dispatch path (never to rewrite a coordinate action into an element action).</summary>
public sealed record HitTestResult(
    bool WithinWindow,
    bool WithinClient,
    HostClass HostClass,
    string? ControlType,
    string? AutomationId,
    string? ClassName);

/// <summary>
/// go_launch request. <paramref name="Target"/> is the app resolved by the platform catalog
/// (desktop/start-menu shortcut, execution alias, or absolute executable path); when null the
/// request activates an existing app via <paramref name="AppId"/>.
/// </summary>
public sealed record LaunchRequest(LaunchTarget? Target, string? AppId, int WaitMs);

public sealed record LaunchResult(
    bool Launched,
    int? Pid,
    string? AppId,
    WindowInfo? Window,
    bool Activated,
    bool Settled);
