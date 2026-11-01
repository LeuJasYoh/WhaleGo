using System.ComponentModel;
using System.Diagnostics;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Apps;

/// <summary>
/// go_launch platform implementation (docs/PROTOCOL.md section 3.8).
/// Start path: executable -> direct Process.Start and wait for its main window; shortcut/alias ->
/// ShellExecute and wait for a NEW window (or activation of the existing foreground window).
/// app_id path: activate the existing application main window.
/// No arguments, no retry, never acquires the controller lease.
/// </summary>
/// <remarks>
/// Failure mapping convention for the application layer (LaunchResult has no code field):
/// <list type="bullet">
/// <item><see cref="IndicatesLaunchTimeout"/>: process was started but no main window appeared within wait_ms -> LAUNCH_TIMEOUT.</item>
/// <item><see cref="IndicatesLaunchFailure"/>: nothing was started (bad path/start exception/process died) -> LAUNCH_FAILED.</item>
/// <item><see cref="IndicatesAppActivated"/>: existing app/window activated (Launched=false, Activated=true) -> success.</item>
/// </list>
/// <para>
/// Single-instance apps (Microsoft Edge, Weixin/WeChat, most browsers/IM clients) do not necessarily expose a
/// window owned by the process this code started: the launched stub may exit and hand off to an already-running
/// instance. Therefore the wait resolves a launch window by (a) the started pid, (b) a NEW top-level window that
/// appeared after the start, and finally (c) activation of an already-running instance (foreground changed).
/// </para>
/// </remarks>
public sealed class AppLauncher : IAppLauncher
{
    private const int SettleMs = 120;
    private const int PollMs = 50;

    private readonly IWindowCatalog _windows;
    private readonly WindowActivator _activator;

    public AppLauncher(IWindowCatalog windows, WindowActivator activator)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
    }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (request.Target is not null)
        {
            return request.Target.Kind == LaunchTargetKind.Executable
                ? await LaunchExecutableAsync(request.Target, request.WaitMs, ct).ConfigureAwait(false)
                : await LaunchViaShellAsync(request.Target, request.WaitMs, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.AppId))
        {
            return await ActivateExistingAsync(request.AppId.Trim(), ct).ConfigureAwait(false);
        }

        return Failure();
    }

    public static bool IndicatesLaunchTimeout(LaunchResult result) =>
        result is { Launched: false, Activated: false, Pid: not null, Window: null };

    public static bool IndicatesLaunchFailure(LaunchResult result) =>
        result is { Launched: false, Activated: false, Pid: null, Window: null };

    public static bool IndicatesAppActivated(LaunchResult result) =>
        result is { Launched: false, Activated: true };

    private async Task<LaunchResult> LaunchExecutableAsync(LaunchTarget target, int waitMs, CancellationToken ct)
    {
        var path = target.LaunchPath;
        if (!IsAbsoluteExistingExe(path))
        {
            return Failure();
        }

        string expectedAppId;
        try
        {
            expectedAppId = AppIdentity.ForExePath(path);
        }
        catch (Exception)
        {
            return Failure();
        }

        var preExistingWindowIds = SnapshotWindowIds();
        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
                Arguments = string.Empty,
            };
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failure();
        }

        if (process is null)
        {
            return Failure();
        }

        using (process)
        {
            return await WaitForLaunchWindowAsync(
                process, expectedAppId, preExistingWindowIds, Math.Clamp(waitMs, 0, 10000), ct).ConfigureAwait(false);
        }
    }

    private async Task<LaunchResult> LaunchViaShellAsync(LaunchTarget target, int waitMs, CancellationToken ct)
    {
        var preExistingWindowIds = SnapshotWindowIds();
        var previousForeground = ListWindowsSafe().FirstOrDefault(window => window.IsForeground)?.WindowId;

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target.LaunchPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target.LaunchPath) ?? string.Empty,
            };
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failure();
        }

        if (process is null)
        {
            return await WaitForNewWindowAsync(preExistingWindowIds, Math.Clamp(waitMs, 0, 10000), ct).ConfigureAwait(false);
        }

        using (process)
        {
            return await WaitForShellLaunchAsync(
                process, preExistingWindowIds, previousForeground, Math.Clamp(waitMs, 0, 10000), ct).ConfigureAwait(false);
        }
    }

    private async Task<LaunchResult> WaitForShellLaunchAsync(
        Process process,
        HashSet<string> preExistingWindowIds,
        string? previousForeground,
        int waitMs,
        CancellationToken ct)
    {
        var result = await WaitForNewWindowCoreAsync(
            preExistingWindowIds, process.Id, waitMs, ct).ConfigureAwait(false);
        if (result.Window is not null)
        {
            return result;
        }

        var activated = TryActivatedFallback(previousForeground);
        if (activated is not null)
        {
            return activated;
        }

        return process.HasExited ? Failure() : result;
    }

    private async Task<LaunchResult> WaitForNewWindowAsync(
        HashSet<string> preExistingWindowIds,
        int waitMs,
        CancellationToken ct) =>
        await WaitForNewWindowCoreAsync(preExistingWindowIds, pid: null, waitMs, ct).ConfigureAwait(false);

    private async Task<LaunchResult> WaitForNewWindowCoreAsync(
        HashSet<string> preExistingWindowIds,
        int? pid,
        int waitMs,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        WindowInfo? candidate = null;
        string? stableId = null;
        RectI stableBounds = default;
        long stableSinceMs = -1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var window = FindNewWindow(ListWindowsSafe(), pid, preExistingWindowIds);
            if (window is not null)
            {
                candidate = window;
                if (!string.Equals(stableId, window.WindowId, StringComparison.Ordinal) || stableBounds != window.Bounds)
                {
                    stableId = window.WindowId;
                    stableBounds = window.Bounds;
                    stableSinceMs = stopwatch.ElapsedMilliseconds;
                }
                else if (stopwatch.ElapsedMilliseconds - stableSinceMs >= SettleMs)
                {
                    return new LaunchResult(true, window.Pid, window.AppId, window, Activated: false, Settled: true);
                }
            }

            if (stopwatch.ElapsedMilliseconds >= waitMs)
            {
                break;
            }

            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(false);
        }

        if (candidate is not null)
        {
            return new LaunchResult(true, candidate.Pid, candidate.AppId, candidate, Activated: false, Settled: false);
        }

        return new LaunchResult(false, pid, null, null, Activated: false, Settled: false);
    }

    private LaunchResult? TryActivatedFallback(string? previousForeground)
    {
        var foreground = ListWindowsSafe().FirstOrDefault(window => window.IsForeground);
        if (foreground is null ||
            !foreground.IsOnscreen ||
            !string.Equals(foreground.Kind, "top-level", StringComparison.Ordinal) ||
            string.Equals(foreground.WindowId, previousForeground, StringComparison.Ordinal))
        {
            return null;
        }

        return new LaunchResult(false, foreground.Pid, foreground.AppId, foreground, Activated: true, Settled: true);
    }

    private HashSet<string> SnapshotWindowIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in ListWindowsSafe())
        {
            ids.Add(window.WindowId);
        }

        return ids;
    }

    private async Task<LaunchResult> WaitForLaunchWindowAsync(
        Process process,
        string expectedAppId,
        HashSet<string> preExistingWindowIds,
        int waitMs,
        CancellationToken ct)
    {
        // Once the process is started the launch is a write action: honour the bounded wait even if the
        // caller cancels (the process cannot be "unsent"), but never exceed wait_ms.
        var stopwatch = Stopwatch.StartNew();
        WindowInfo? candidate = null;
        string? stableId = null;
        RectI stableBounds = default;
        long stableSinceMs = -1;
        var pid = process.Id;
        var processExited = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var windows = ListWindowsSafe();
            var window = FindLaunchWindow(windows, pid, expectedAppId, preExistingWindowIds);
            if (window is not null)
            {
                candidate = window;
                if (!string.Equals(stableId, window.WindowId, StringComparison.Ordinal) || stableBounds != window.Bounds)
                {
                    stableId = window.WindowId;
                    stableBounds = window.Bounds;
                    stableSinceMs = stopwatch.ElapsedMilliseconds;
                }
                else if (stopwatch.ElapsedMilliseconds - stableSinceMs >= SettleMs)
                {
                    return new LaunchResult(true, window.Pid, window.AppId, window, Activated: false, Settled: true);
                }
            }

            if (!processExited)
            {
                try
                {
                    processExited = process.HasExited;
                }
                catch (Exception)
                {
                    processExited = true;
                }
            }

            if (stopwatch.ElapsedMilliseconds >= waitMs)
            {
                break;
            }

            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(false);
        }

        if (candidate is not null)
        {
            // The window exists but did not stabilise inside the budget: report launched + settled=false,
            // never "timeout" (that would tell the model the process may not exist while it does).
            return new LaunchResult(true, candidate.Pid, candidate.AppId, candidate, Activated: false, Settled: false);
        }

        if (processExited)
        {
            // Single-instance handoff: the started stub may exit after asking an existing instance to show a
            // window. Activate the running instance instead of reporting a misleading LAUNCH_FAILED.
            var activated = await TryActivateExistingAsync(expectedAppId, ct).ConfigureAwait(false);
            if (activated is not null)
            {
                return activated;
            }

            return Failure();
        }

        return new LaunchResult(false, pid, null, null, Activated: false, Settled: false);
    }

    private IReadOnlyList<WindowInfo> ListWindowsSafe()
    {
        try
        {
            return _windows.ListWindows(null);
        }
        catch (Exception)
        {
            return Array.Empty<WindowInfo>();
        }
    }

    private static WindowInfo? FindLaunchWindow(
        IReadOnlyList<WindowInfo> windows,
        int pid,
        string expectedAppId,
        HashSet<string> preExistingWindowIds)
    {
        var owned = windows.Where(window => window.Pid == pid).ToList();
        if (owned.Count > 0)
        {
            return ChooseMain(owned);
        }

        var newByApp = windows
            .Where(window => !preExistingWindowIds.Contains(window.WindowId) &&
                             string.Equals(window.AppId, expectedAppId, StringComparison.Ordinal))
            .ToList();
        return newByApp.Count > 0 ? ChooseMain(newByApp) : null;
    }

    private static WindowInfo? FindNewWindow(
        IReadOnlyList<WindowInfo> windows,
        int? pid,
        HashSet<string> preExistingWindowIds)
    {
        if (pid is not null)
        {
            var owned = windows.Where(window => window.Pid == pid.Value).ToList();
            if (owned.Count > 0)
            {
                return ChooseMain(owned);
            }
        }

        var newWindows = windows
            .Where(window => !preExistingWindowIds.Contains(window.WindowId) &&
                             window.IsOnscreen &&
                             string.Equals(window.Kind, "top-level", StringComparison.Ordinal))
            .ToList();
        return newWindows.Count > 0 ? ChooseMain(newWindows) : null;
    }

    private static WindowInfo ChooseMain(IReadOnlyList<WindowInfo> windows) =>
        windows.FirstOrDefault(window => window.IsOnscreen && !window.IsMinimized && window.IsEnabled && window.Kind == "top-level")
        ?? windows.FirstOrDefault(window => window.IsOnscreen && window.Kind == "top-level")
        ?? windows.FirstOrDefault(window => window.IsOnscreen)
        ?? windows[0];

    private async Task<LaunchResult?> TryActivateExistingAsync(string expectedAppId, CancellationToken ct)
    {
        var existing = ListWindowsSafe()
            .Where(window => string.Equals(window.AppId, expectedAppId, StringComparison.Ordinal))
            .ToList();
        if (existing.Count == 0)
        {
            var app = TryFindApp(expectedAppId);
            if (app is null)
            {
                return null;
            }

            existing = app.Windows.ToList();
        }

        var window = existing.Count == 0 ? null : ChooseMain(existing);
        if (window is null)
        {
            return null;
        }

        var activated = false;
        try
        {
            activated = _activator.TryActivate(window, confirmWaitMs: 250);
        }
        catch (Exception)
        {
            activated = false;
        }

        if (!activated)
        {
            return null;
        }

        await WaitUntilForegroundAsync(window.WindowId, ct).ConfigureAwait(false);
        return new LaunchResult(false, window.Pid, window.AppId, window, Activated: true, Settled: true);
    }

    private async Task<LaunchResult> ActivateExistingAsync(string appId, CancellationToken ct)
    {
        WindowInfo? window;
        try
        {
            var windows = _windows.ListWindows(appId);
            window = windows.FirstOrDefault(candidate => candidate.IsForeground)
                ?? windows.FirstOrDefault(candidate => candidate.IsOnscreen && !candidate.IsMinimized)
                ?? windows.FirstOrDefault(candidate => candidate.IsOnscreen)
                ?? windows.FirstOrDefault();
        }
        catch (Exception)
        {
            window = null;
        }

        if (window is null)
        {
            var app = TryFindApp(appId);
            if (app is null)
            {
                return Failure();
            }

            window = app.Windows.FirstOrDefault(candidate => candidate.IsForeground)
                ?? app.Windows.FirstOrDefault(candidate => candidate.IsOnscreen && !candidate.IsMinimized)
                ?? app.Windows.FirstOrDefault(candidate => candidate.IsOnscreen)
                ?? app.Windows.FirstOrDefault();
            if (window is null)
            {
                return new LaunchResult(false, app.Pid, appId, null, Activated: false, Settled: false);
            }
        }

        var activated = false;
        try
        {
            activated = _activator.TryActivate(window, confirmWaitMs: 250);
        }
        catch (Exception)
        {
            activated = false;
        }

        if (activated)
        {
            await WaitUntilForegroundAsync(window.WindowId, ct).ConfigureAwait(false);
        }

        return new LaunchResult(false, window.Pid, window.AppId, window, activated, Settled: activated);
    }

    private async Task WaitUntilForegroundAsync(string windowId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 300)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_windows.IsForeground(windowId))
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private AppInfo? TryFindApp(string appId)
    {
        try
        {
            return _windows.ListApps(new ListAppsOptions(IncludeWindows: true))
                .FirstOrDefault(app => string.Equals(app.AppId, appId, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsAbsoluteExistingExe(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    private static LaunchResult Failure() => new(false, null, null, null, Activated: false, Settled: false);
}
