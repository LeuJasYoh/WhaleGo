namespace WhaleGo.Contracts.Model;

// ── go_apps ──────────────────────────────────────────────────────────────
public sealed record AppEntry(
    string AppId,
    string Name,
    int? Pid,
    string? Exe,
    string? Aumid,
    bool IsPackaged,
    bool Foreground,
    int WindowsCount,
    string? RecommendedWindowId);

public sealed record AppsReport(string SchemaVersion, IReadOnlyList<AppEntry> Apps, int OmittedApps);

// ── go_windows ───────────────────────────────────────────────────────────
public sealed record WindowEntry(
    string WindowId,
    string AppId,
    string Title,
    string ClassName,
    int Pid,
    bool Onscreen,
    bool Minimized,
    bool Foreground,
    bool Enabled,
    int[] Bounds,
    string GeometrySource,
    uint Dpi,
    long CapturedAtMs,
    string Kind,
    string? OwnerWindowId,
    string? ParentWindowId,
    bool IsModal);

public sealed record WindowsReport(string SchemaVersion, IReadOnlyList<WindowEntry> Windows);

// ── go_observe metadata ──────────────────────────────────────────────────
public sealed record ObserveFrameReport(
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
    long CapturedAtMs);

public sealed record ObserveAppReport(string AppId, int? Pid, string Name);

public sealed record ObserveWindowReport(
    string WindowId,
    string Title,
    bool Onscreen,
    bool Foreground,
    int[] Bounds,
    uint Dpi,
    string GeometrySource);

public sealed record ObserveTargetReport(ObserveAppReport App, ObserveWindowReport Window, string Resolution);

public sealed record ObserveElementReport(
    int Index,
    string Kind,
    string? Title,
    string? Value,
    bool ValueTruncated,
    string[] Actions,
    bool Enabled,
    bool Selected,
    string? AutomationId,
    int[] BoundsScreenDiag,
    int Depth);

public sealed record ObserveOmittedReport(
    int Count,
    IReadOnlyDictionary<string, int> ByKind,
    string Reason,
    bool SparseIndices);

public sealed record ObserveReport(
    string SchemaVersion,
    string SessionId,
    string StateId,
    ObserveFrameReport? Frame,
    ObserveTargetReport Target,
    IReadOnlyList<ObserveElementReport> Elements,
    ObserveOmittedReport Omitted,
    IReadOnlyList<string> Advisories,
    long CapturedAtMs);

// ── go_act ───────────────────────────────────────────────────────────────
public sealed record ActReport(
    string SchemaVersion,
    string SessionId,
    string RequestId,
    string Op,
    ActionReceiptWire ActionReceipt);

// ── go_launch ────────────────────────────────────────────────────────────
public sealed record LaunchAppReport(string AppId, int? Pid);

public sealed record LaunchWindowReport(string WindowId, string Title, bool Onscreen);

public sealed record LaunchReport(
    string SchemaVersion,
    bool Launched,
    bool Activated,
    LaunchAppReport? App,
    LaunchWindowReport? Window,
    bool Settled);

// ── go_stop ──────────────────────────────────────────────────────────────
public sealed record StopReport(string SchemaVersion, bool Ok, bool Stopped, string Scope, int SessionsAffected);
