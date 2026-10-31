using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Abstractions;

/// <summary>Window/app enumeration (the only source of window identity).</summary>
public interface IWindowCatalog
{
    IReadOnlyList<AppInfo> ListApps(ListAppsOptions options);
    IReadOnlyList<WindowInfo> ListWindows(string? appId = null);
    WindowInfo? TryGetWindow(string windowId);
    bool IsForeground(string windowId);
}

/// <summary>Observation engine: collects the raw snapshot and registers platform handles for (stateId, index).</summary>
public interface IObservationEngine
{
    ObservationSnapshot Observe(ObserveOptions options);
}

/// <summary>Single-window client-area screenshot (PrintWindow today; WGC in P2).</summary>
public interface IScreenCapture
{
    CaptureResult Capture(string windowId, CaptureOptions options);
}

/// <summary>Coordinate hit-test: classifies the point so the dispatch path can be chosen (never rewrites the action).</summary>
public interface IUiHitTester
{
    HitTestResult HitTest(WindowInfo window, int screenX, int screenY);
}

public interface IAppLauncher
{
    Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct);
}

/// <summary>Element dispatch bound to a freshly observed state. Expired/unknown state -> FailureCode STALE_STATE/ELEMENT_UNAVAILABLE.</summary>
public interface IElementActionDispatcher
{
    Task<PlatformActionOutcome> ExecuteAsync(string stateId, int elementIndex, ActionPlan plan, CancellationToken ct);
}

/// <summary>Coordinate dispatch bound to an internal frame. Never auto-observes.</summary>
public interface ICoordinateActionDispatcher
{
    Task<PlatformActionOutcome> ExecuteAsync(FrameBinding frame, int imageX, int imageY, ActionPlan plan, CancellationToken ct);
}

/// <summary>Process-wide physical input lane (SendInput/activation/clipboard must be serialized).</summary>
public interface IInputLane
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

/// <summary>Environment facts (session/lock/integrity/DPI/...).</summary>
public interface ISystemProbe
{
    SystemFacts GetFacts();
}

public interface IPlatformClock
{
    long NowMs { get; }
}
