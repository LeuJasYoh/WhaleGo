using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Input;

/// <summary>Test-local IWindowCatalog over EnumWindows (T2's WindowCatalog is not needed for launch tests).</summary>
internal sealed class EnumWindowCatalog : IWindowCatalog
{
    public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options)
    {
        var windows = ListWindows().ToList();
        return windows
            .GroupBy(window => window.AppId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new AppInfo(
                    group.Key,
                    TryGetProcessName(first.Pid) ?? group.Key,
                    first.Pid,
                    TryGetExecutablePath(first.Pid),
                    AumId: null,
                    IsPackaged: false,
                    group.Any(window => window.IsForeground),
                    options.IncludeWindows ? group.ToList() : Array.Empty<WindowInfo>());
            })
            .ToList();
    }

    public IReadOnlyList<WindowInfo> ListWindows(string? appId = null)
    {
        var windows = new List<WindowInfo>();
        _ = EnumWindows((hwnd, lParam) =>
        {
            _ = lParam;
            if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var executable = TryGetExecutablePath((int)pid);
            var id = executable is null ? AppIdentity.ForUnknownProcess((int)pid) : AppIdentity.ForExePath(executable);
            if (appId is not null && !string.Equals(appId, id, StringComparison.Ordinal))
            {
                return true;
            }

            var bounds = TestSupport.GetWindowScreenRect(hwnd);
            windows.Add(new WindowInfo(
                WindowIdCodec.Format(hwnd),
                id,
                GetWindowTitle(hwnd),
                TestSupport.GetClassNameText(hwnd),
                (int)pid,
                IsOnscreen: true,
                IsMinimized: IsIconic(hwnd),
                IsForeground: hwnd == TestSupport.GetForegroundWindow(),
                IsEnabled: IsWindowEnabled(hwnd),
                Bounds: bounds,
                GeometrySource: "win32",
                Dpi: TestSupport.GetDpi(hwnd),
                CapturedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return true;
        }, IntPtr.Zero); // continue enumerating
        return windows;
    }

    public WindowInfo? TryGetWindow(string windowId) =>
        ListWindows().FirstOrDefault(window => string.Equals(window.WindowId, windowId, StringComparison.Ordinal));

    public bool IsForeground(string windowId) =>
        WindowIdCodec.TryParse(windowId, out var hwnd) && hwnd == TestSupport.GetForegroundWindow();

    private static string? TryGetExecutablePath(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);
}

/// <summary>Catalog with exactly one window (deterministic app_id activation test).</summary>
internal sealed class SingleWindowCatalog : IWindowCatalog
{
    private readonly WindowInfo _window;

    public SingleWindowCatalog(WindowInfo window) => _window = window;

    public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options) =>
        new[]
        {
            new AppInfo(_window.AppId, "fixture", _window.Pid, ExecutablePath: null, AumId: null, IsPackaged: false, IsForeground: true, options.IncludeWindows ? new[] { _window } : Array.Empty<WindowInfo>()),
        };

    public IReadOnlyList<WindowInfo> ListWindows(string? appId = null) =>
        appId is null || string.Equals(appId, _window.AppId, StringComparison.Ordinal) ? new[] { _window } : Array.Empty<WindowInfo>();

    public WindowInfo? TryGetWindow(string windowId) => string.Equals(windowId, _window.WindowId, StringComparison.Ordinal) ? _window : null;

    public bool IsForeground(string windowId) =>
        string.Equals(windowId, _window.WindowId, StringComparison.Ordinal) &&
        WindowIdCodec.TryParse(windowId, out var hwnd) &&
        hwnd == TestSupport.GetForegroundWindow();
}