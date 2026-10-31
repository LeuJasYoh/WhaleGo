namespace WhaleGo.Contracts.Model;

public enum WindowResolution
{
    Exact = 0,
    Fallback = 1,
    Unavailable = 2,
}

/// <summary>One element in the current observation. Index is only valid for the state_id it was issued in.</summary>
public sealed record ElementNode(
    int Index,
    string Kind,
    string? Title,
    string? Value,
    bool ValueTruncated,
    string? AutomationId,
    IReadOnlyList<string> Actions,
    bool Enabled,
    bool Selected,
    RectI BoundsScreenDiag,
    int Depth);

/// <summary>What was dropped from the observation and why (sparse indices are normal).</summary>
public sealed record OmissionInfo(
    int OmittedCount,
    IReadOnlyDictionary<string, int> ByKind,
    string Reason,
    bool SparseIndices)
{
    public static readonly OmissionInfo None = new(0, new Dictionary<string, int>(), "none", false);
}

/// <summary>Internal frame binding (the frame_id never leaves the executor; wire only carries the frame facts).</summary>
public sealed record FrameInfo(
    string FrameId,
    string WindowId,
    int ImageWidth,
    int ImageHeight,
    int ClientWidth,
    int ClientHeight,
    double ScaleX,
    double ScaleY,
    string CaptureRegion,
    bool Actionable,
    string CaptureMethod,
    string Signature,
    long CapturedAtMs,
    RectI WindowBounds,
    RectI ClientOrigin,
    uint Dpi,
    byte[]? PngBytes);

/// <summary>Snapshot of the platform frame identity as seen by the application layer (for freshness rules).</summary>
public sealed record FrameBinding(
    string FrameId,
    string WindowId,
    int OwnerPid,
    string ClassName,
    RectI WindowBounds,
    RectI ClientOrigin,
    int ClientWidth,
    int ClientHeight,
    int ImageWidth,
    int ImageHeight,
    uint Dpi,
    long CapturedAtMs,
    bool Actionable,
    string CaptureMethod,
    string Signature);

/// <summary>The single UI truth for one session: window identity + element table + optional frame.</summary>
public sealed record ObservationSnapshot(
    string SchemaVersion,
    string SessionId,
    string StateId,
    AppInfo App,
    WindowInfo Window,
    WindowResolution Resolution,
    IReadOnlyList<ElementNode> Elements,
    OmissionInfo Omitted,
    FrameInfo? Frame,
    IReadOnlyList<string> Advisories,
    long CapturedAtMs);

public sealed record ObserveOptions(
    string? WindowId,
    string? AppId,
    bool IncludeScreenshot,
    int SettleMs,
    int MaxElements,
    int MaxValueChars);

public sealed record ListAppsOptions(bool IncludeWindows);

public sealed record CaptureOptions(
    int MaxImageWidth,
    long MaxImageBytes,
    string Mode,
    bool SuppressSensitiveWindows);

/// <summary>Platform capture result: frame metadata plus optional PNG bytes; a failure is reported via advisory, never an exception.</summary>
public sealed record CaptureResult(
    FrameInfo? Frame,
    byte[]? PngBytes,
    string? FailureAdvisory,
    string? FailureDetail);
