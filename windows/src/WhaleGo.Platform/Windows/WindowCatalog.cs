using System.Diagnostics;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Windows;

/// <summary>
/// Enumeration of top-level windows and application identity (docs/PROTOCOL.md §3.4/§3.5, DESIGN §5.4).
/// <para>
/// window_id = "w-" + HWND X12 (see <see cref="WindowIdCodec"/>). app_id is
/// win-exe:&lt;sha1(normalized absolute exe path, lowercase)[:12]&gt; for classic apps or
/// win-aumid:&lt;aumid&gt; for packaged apps. Title is never used for identity.
/// ApplicationFrameHost windows resolve the real owning application through the UIA root/children
/// ProcessId (cached briefly, 60 s) before exe/AUMID lookup.
/// </para>
/// <para>WhaleGo's own processes (Contracts.Domain.PrivacyRules.IsSelfProcess) are never enumerated.</para>
/// </summary>
public sealed class WindowCatalog : IWindowCatalog
{
    public const int MaxWindows = 500;
    private const int TitleMaxChars = 512;
    private const long FrameHostPidCacheMs = 60_000;

    // Shell/infrastructure classes that are visible top-level windows but never user targets.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow", "DV2ControlHost", "SysShadow", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.Internal.Shell.TabProxyWindow", "Base_PowerMessageWindow",
    };

    private readonly IPlatformClock _clock;
    private readonly Redactor _redactor;
    private readonly UiaEngine? _uia;
    private readonly object _frameHostCacheLock = new();
    private readonly Dictionary<IntPtr, (int Pid, long AtMs)> _frameHostPidCache = new();

    public WindowCatalog(IPlatformClock clock, Redactor? redactor = null, UiaEngine? uia = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _redactor = redactor ?? new Redactor();
        _uia = uia;
    }

    /// <summary>Explicit self-process predicate used by the enumeration filter (per Contracts PrivacyRules).</summary>
    public static bool IsSelfProcessName(string? processName) => PrivacyRules.IsSelfProcess(processName);

    public IReadOnlyList<WindowInfo> ListWindows(string? appId = null)
    {
        var entries = EnumerateWindows(_clock.NowMs);
        IEnumerable<EnumeratedWindow> query = entries;
        if (!string.IsNullOrEmpty(appId))
        {
            query = query.Where(e => string.Equals(e.Window.AppId, appId, StringComparison.Ordinal));
        }

        return query
            .Select(e => e.Window)
            .OrderByDescending(w => w.IsForeground)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WindowId, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entries = EnumerateWindows(_clock.NowMs);
        return BuildApps(entries, options.IncludeWindows)
            .OrderByDescending(a => a.IsForeground)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fresh window facts for a window_id (re-validates HWND liveness and enumeration filters).</summary>
    public WindowInfo? TryGetWindow(string windowId)
    {
        if (!WindowIdCodec.TryParse(windowId, out var hwnd) || !Win32.IsWindow(hwnd))
        {
            return null;
        }

        var entries = EnumerateWindows(_clock.NowMs);
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Window.WindowId, windowId, StringComparison.Ordinal))
            {
                return entry.Window;
            }
        }

        return null;
    }

    /// <summary>Parses window_id and verifies the HWND is still alive (identity check stays the caller's job).</summary>
    public bool TryGetHwnd(string windowId, out IntPtr hwnd)
    {
        if (WindowIdCodec.TryParse(windowId, out hwnd) && Win32.IsWindow(hwnd))
        {
            return true;
        }

        hwnd = IntPtr.Zero;
        return false;
    }

    public bool IsForeground(string windowId) =>
        WindowIdCodec.TryParse(windowId, out var hwnd) && Win32.IsForegroundWindow(hwnd);

    /// <summary>AppInfo for one app_id from the current window set (null when no live window has that app identity).</summary>
    public AppInfo? TryGetApp(string appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return null;
        }

        var entries = EnumerateWindows(_clock.NowMs);
        return BuildApps(entries, includeWindows: false)
            .FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.Ordinal));
    }

    private static List<AppInfo> BuildApps(List<EnumeratedWindow> entries, bool includeWindows)
    {
        var apps = new List<AppInfo>();
        foreach (var group in entries.GroupBy(e => e.Window.AppId, StringComparer.Ordinal))
        {
            var first = group.First();
            var windows = group.Select(e => e.Window).ToList();
            apps.Add(new AppInfo(
                first.Window.AppId,
                first.AppName,
                first.AppPid,
                first.ExePath,
                first.AumId,
                first.IsPackaged,
                windows.Any(w => w.IsForeground),
                includeWindows ? windows : Array.Empty<WindowInfo>()));
        }

        return apps;
    }

    private List<EnumeratedWindow> EnumerateWindows(long nowMs)
    {
        var results = new List<EnumeratedWindow>(64);
        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                if (results.Count >= MaxWindows)
                {
                    return false;
                }

                try
                {
                    var entry = TryDescribeWindow(hwnd, nowMs);
                    if (entry is not null)
                    {
                        results.Add(entry);
                    }
                }
                catch
                {
                    // A window may vanish mid-enumeration; skip it and keep going.
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // EnumWindows failed: return whatever we collected so callers fail closed at a higher layer.
        }

        return results;
    }

    private EnumeratedWindow? TryDescribeWindow(IntPtr hwnd, long nowMs)
    {
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd) || Win32.IsToolWindow(hwnd))
        {
            return null;
        }

        if (!Win32.GetWindowRect(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            return null;
        }

        var className = Win32.GetClassNameSafe(hwnd);
        if (ShellClasses.Contains(className))
        {
            return null;
        }

        var windowPid = Win32.GetWindowProcessId(hwnd);
        Win32.TryGetProcessName(windowPid, out var windowProcessName);
        if (_redactor.IsSelfProcess(windowProcessName))
        {
            return null;
        }

        var title = TrimTitle(Win32.GetWindowTitleSafe(hwnd));
        var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER);
        if (title.Length == 0 && owner == IntPtr.Zero)
        {
            return null;
        }

        var isFrameHost = string.Equals(className, "ApplicationFrameWindow", StringComparison.Ordinal) ||
                          string.Equals(className, "ApplicationFrameHost", StringComparison.Ordinal) ||
                          string.Equals(windowProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

        var appPid = windowPid;
        if (isFrameHost)
        {
            var realPid = TryResolveFrameHostAppPid(hwnd);
            if (realPid > 0)
            {
                appPid = realPid;
            }
        }

        string? exePath = null;
        if (Win32.TryGetExePath(appPid, out var exe))
        {
            exePath = exe;
        }

        var isPackaged = Win32.TryGetApplicationUserModelId(appPid, out var aumid);
        var appId = isPackaged
            ? AppIdentity.ForAumid(aumid)
            : exePath is not null
                ? AppIdentity.ForExePath(exePath)
                : AppIdentity.ForUnknownProcess(appPid);

        RectI bounds;
        string geometrySource;
        if (Win32.TryGetExtendedFrameBounds(hwnd, out var dwmRect))
        {
            bounds = ToRectI(dwmRect);
            geometrySource = "dwm";
        }
        else
        {
            bounds = ToRectI(windowRect);
            geometrySource = "win32";
        }

        var isMinimized = Win32.IsIconic(hwnd);
        var isForeground = Win32.IsForegroundWindow(hwnd);
        var isEnabled = Win32.IsWindowEnabled(hwnd);
        var isModal = owner != IntPtr.Zero && !Win32.IsWindowEnabled(owner);
        var kind = string.Equals(className, "#32770", StringComparison.Ordinal) ? "dialog" : owner != IntPtr.Zero ? "owned" : "top-level";
        var windowId = WindowIdCodec.Format(hwnd);
        var ownerWindowId = owner != IntPtr.Zero ? WindowIdCodec.Format(owner) : null;

        var window = new WindowInfo(
            windowId,
            appId,
            title,
            className,
            windowPid,
            IsOnscreen: !isMinimized,
            IsMinimized: isMinimized,
            IsForeground: isForeground,
            IsEnabled: isEnabled,
            bounds,
            geometrySource,
            Win32.GetDpiSafe(hwnd),
            nowMs,
            Kind: kind,
            OwnerWindowId: ownerWindowId,
            ParentWindowId: null,
            IsModal: isModal);

        var appName = ResolveAppName(appPid, exePath, isPackaged, hwnd);
        return new EnumeratedWindow(window, windowProcessName, exePath, isPackaged ? aumid : null, isPackaged, appPid, appName);
    }

    private int TryResolveFrameHostAppPid(IntPtr hwnd)
    {
        var now = _clock.NowMs;
        lock (_frameHostCacheLock)
        {
            if (_frameHostPidCache.TryGetValue(hwnd, out var cached) && now - cached.AtMs < FrameHostPidCacheMs)
            {
                return cached.Pid;
            }
        }

        var resolved = 0;
        try
        {
            var root = _uia?.TryGetRoot(hwnd);
            if (root is not null && _uia is not null)
            {
                var windowPid = Win32.GetWindowProcessId(hwnd);
                var rootPid = _uia.SafeRead(() => root.Properties.ProcessId.ValueOrDefault);
                if (rootPid > 0 && rootPid != windowPid)
                {
                    resolved = rootPid;
                }
                else
                {
                    var children = _uia.SafeRead(() => root.FindAllChildren());
                    if (children is not null)
                    {
                        foreach (var child in children)
                        {
                            var childPid = _uia.SafeRead(() => child.Properties.ProcessId.ValueOrDefault);
                            if (childPid > 0 && childPid != windowPid)
                            {
                                resolved = childPid;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            resolved = 0;
        }

        lock (_frameHostCacheLock)
        {
            _frameHostPidCache[hwnd] = (resolved, now);
        }

        return resolved;
    }

    private string ResolveAppName(int appPid, string? exePath, bool isPackaged, IntPtr hwnd)
    {
        if (isPackaged)
        {
            try
            {
                var root = _uia?.TryGetRoot(hwnd);
                var uiaName = root is null || _uia is null ? null : _uia.SafeRead(() => root.Properties.Name.ValueOrDefault);
                if (!string.IsNullOrWhiteSpace(uiaName))
                {
                    return uiaName!;
                }
            }
            catch
            {
                // fall through to process name
            }

            if (Win32.TryGetProcessName(appPid, out var packagedName) && packagedName.Length > 0)
            {
                return packagedName;
            }

            return "Packaged application";
        }

        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription!;
                }

                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName!;
                }
            }
            catch
            {
                // fall through to file name
            }

            var fileName = Path.GetFileNameWithoutExtension(exePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }
        }

        if (Win32.TryGetProcessName(appPid, out var processName) && processName.Length > 0)
        {
            return processName;
        }

        return "Unknown application";
    }

    private static string TrimTitle(string title) =>
        title.Length <= TitleMaxChars ? title : title[..TitleMaxChars];

    private static RectI ToRectI(Win32.RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private sealed record EnumeratedWindow(
        WindowInfo Window,
        string ProcessName,
        string? ExePath,
        string? AumId,
        bool IsPackaged,
        int AppPid,
        string AppName);
}
