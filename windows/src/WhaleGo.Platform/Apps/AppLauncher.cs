using System.ComponentModel;
using System.Diagnostics;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Apps;

/// <summary>
/// go_launch platform implementation (docs/PROTOCOL.md section 3.8).
/// Start path: allowlist name -> absolute existing exe -> UseShellExecute=false -> bounded wait for a
/// main window of the new process. app_id path: activate the existing application main window.
/// No PATH names, no relative paths, no arguments, no retry, never acquires the controller lease.
/// </summary>
/// <remarks>
/// Failure mapping convention for the application layer (LaunchResult has no code field):
/// <list type="bullet">
/// <item><see cref="IndicatesLaunchTimeout"/>: process was started but no main window appeared within wait_ms -> LAUNCH_TIMEOUT.</item>
/// <item><see cref="IndicatesLaunchFailure"/>: nothing was started (bad allowlist entry/path/start exception/process died) -> LAUNCH_FAILED.</item>
/// <item><see cref="IndicatesAppActivated"/>: app_id path, existing window activated (Launched=false, Activated=true).</item>
/// </list>
/// APP_NOT_FOUND/AMBIGUOUS_APP are resolved by the application layer before calling (allowlist/app_id lookup);
/// this implementation returns <see cref="IndicatesLaunchFailure"/> for an unknown name/app_id.
/// </remarks>
public sealed class AppLauncher : IAppLauncher
{
    private const int SettleMs = 120;
    private const int PollMs = 50;

    private readonly IReadOnlyList<LaunchAllowlistEntry> _allowlist;
    private readonly IWindowCatalog _windows;
    private readonly WindowActivator _activator;

    public AppLauncher(IReadOnlyList<LaunchAllowlistEntry> allowlist, IWindowCatalog windows, WindowActivator activator)
    {
        _allowlist = allowlist ?? Array.Empty<LaunchAllowlistEntry>();
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
    }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(request.AppName))
        {
            return await LaunchByNameAsync(request, ct).ConfigureAwait(false);
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

    private async Task<LaunchResult> LaunchByNameAsync(LaunchRequest request, CancellationToken ct)
    {
        var name = request.AppName!.Trim();
        var entry = _allowlist.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !IsSafeExecutablePath(entry.Path))
        {
            return Failure();
        }

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = entry.Path,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(entry.Path) ?? string.Empty,
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
            var waitMs = Math.Clamp(request.WaitMs, 0, 10000);
            return await WaitForMainWindowAsync(process, waitMs, ct).ConfigureAwait(false);
        }
    }

    private async Task<LaunchResult> WaitForMainWindowAsync(Process process, int waitMs, CancellationToken ct)
    {
        // Once the process is started the launch is a write action: honour the bounded wait even if the
        // caller cancels (the process cannot be "unsent"), but never exceed wait_ms.
        var stopwatch = Stopwatch.StartNew();
        WindowInfo? candidate = null;
        string? stableId = null;
        RectI stableBounds = default;
        long stableSinceMs = -1;
        var pid = process.Id;

        do
        {
            if (process.HasExited)
            {
                return Failure();
            }

            var window = TryFindMainWindow(pid);
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
                    return new LaunchResult(true, pid, window.AppId, window, Activated: false, Settled: true);
                }
            }

            if (stopwatch.ElapsedMilliseconds >= waitMs)
            {
                break;
            }

            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(false);
        }
        while (stopwatch.ElapsedMilliseconds < waitMs);

        if (candidate is not null)
        {
            // The window exists but did not stabilise inside the budget: report launched + settled=false,
            // never "timeout" (that would tell the model the process may not exist while it does).
            return new LaunchResult(true, pid, candidate.AppId, candidate, Activated: false, Settled: false);
        }

        return new LaunchResult(false, pid, null, null, Activated: false, Settled: false);
    }

    private WindowInfo? TryFindMainWindow(int pid)
    {
        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = _windows.ListWindows(null);
        }
        catch (Exception)
        {
            return null;
        }

        var owned = windows.Where(window => window.Pid == pid).ToList();
        if (owned.Count == 0)
        {
            return null;
        }

        return owned.FirstOrDefault(window => window.IsOnscreen && !window.IsMinimized && window.IsEnabled && window.Kind == "top-level")
            ?? owned.FirstOrDefault(window => window.IsOnscreen && window.Kind == "top-level")
            ?? owned.FirstOrDefault(window => window.IsOnscreen)
            ?? owned[0];
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

    private static bool IsSafeExecutablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        File.Exists(path);

    private static LaunchResult Failure() => new(false, null, null, null, Activated: false, Settled: false);
}
