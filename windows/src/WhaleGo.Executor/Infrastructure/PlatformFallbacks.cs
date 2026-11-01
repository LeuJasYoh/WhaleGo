using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure;

// Benign fallbacks used only while the platform registration is still a stub (or in unit-test composition).
// T2/T3 registrations win because these are TryAdd-only after the platform registration calls.

public sealed class FallbackWindowCatalog : IWindowCatalog
{
    public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options) => [];

    public IReadOnlyList<WindowInfo> ListWindows(string? appId = null) => [];

    public WindowInfo? TryGetWindow(string windowId) => null;

    public bool IsForeground(string windowId) => false;
}

public sealed class ObservationPlatformUnavailableException : Exception
{
    public ObservationPlatformUnavailableException() : base("observation platform is not registered")
    {
    }

    public string Code => ErrorCodes.Internal;
}

public sealed class FallbackObservationEngine : IObservationEngine
{
    public ObservationSnapshot Observe(ObserveOptions options) => throw new ObservationPlatformUnavailableException();
}

public sealed class FallbackScreenCapture : IScreenCapture
{
    public CaptureResult Capture(string windowId, CaptureOptions options) =>
        new(null, null, Advisories.ScreenshotFailed, "capture platform is not registered");
}

public sealed class FallbackUiHitTester : IUiHitTester
{
    public HitTestResult HitTest(WindowInfo window, int screenX, int screenY) =>
        new(false, false, HostClass.Unknown, null, null, null);
}

public sealed class FallbackAppLauncher : IAppLauncher
{
    public Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct) =>
        Task.FromResult(new LaunchResult(false, null, null, null, false, false));
}

public sealed class FallbackInstalledAppCatalog : IInstalledAppCatalog
{
    public InstalledAppResolveResult Resolve(string name) => InstalledAppResolveResult.Missing();
}

public sealed class FallbackElementActionDispatcher : IElementActionDispatcher
{
    public Task<PlatformActionOutcome> ExecuteAsync(string stateId, int elementIndex, ActionPlan plan, CancellationToken ct) =>
        Task.FromResult(PlatformActionOutcome.NotSent(ErrorCodes.Internal, "input platform is not registered"));
}

public sealed class FallbackCoordinateActionDispatcher : ICoordinateActionDispatcher
{
    public Task<PlatformActionOutcome> ExecuteAsync(FrameBinding frame, int imageX, int imageY, ActionPlan plan, CancellationToken ct) =>
        Task.FromResult(PlatformActionOutcome.NotSent(ErrorCodes.Internal, "input platform is not registered"));
}

public sealed class FallbackInputLane : IInputLane
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
}

public sealed class FallbackSystemProbe : ISystemProbe
{
    public SystemFacts GetFacts() => new(
        WindowsVersion: Environment.OSVersion.Version.ToString(),
        WindowsSessionId: 0,
        IsInteractive: true,
        IsLocked: false,
        Integrity: "unknown",
        DpiAwareness: "per_monitor_v2",
        ProcessId: Environment.ProcessId,
        IsRemoteSession: false,
        UptimeMs: Environment.TickCount64);
}

public sealed class FallbackWindowTheme : IWindowTheme
{
    public bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark) => false;
}

public sealed class FallbackHotkeyRegistrar : IHotkeyRegistrar
{
    public bool TryRegister(string id, string gesture, Action onPressed) => false;

    public void Unregister(string id)
    {
    }
}
