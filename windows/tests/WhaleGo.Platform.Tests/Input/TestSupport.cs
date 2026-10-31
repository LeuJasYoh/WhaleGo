using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Tests.Input;

/// <summary>Shared helpers for the T3 cross-process input tests (Win32 facts + fixture paths).</summary>
internal static class TestSupport
{
    private const uint GaRoot = 2;

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureExe { get; } = Path.Combine(
        RepoRoot, "windows", "tests", "WhaleGo.Fixtures.WinForms", "bin", "Release", "net10.0-windows", "WhaleGo.Fixtures.WinForms.exe");

    public static bool FixtureExists => File.Exists(FixtureExe);

    public static bool IsInteractiveDesktop => Environment.UserInteractive;

    /// <summary>
    /// Per-monitor-V2 awareness is required for physical-pixel UIA rectangles and SendInput absolute coordinates.
    /// The test project may already set it in a module initializer; this defensive call is a no-op then.
    /// </summary>
    public static void EnsureProcessDpiAware()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (Exception)
        {
            // Older Windows / already set: tests still run, coordinates may be virtualized at high DPI.
        }
    }

    public static bool WaitUntil(Func<bool> condition, int timeoutMs, int pollMs = 40)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // transient UIA/Win32 read; keep polling
            }

            Thread.Sleep(pollMs);
        }

        try
        {
            return condition();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(RepoRoot, ".tmp", "tg-platform-input", "test-" + prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IntPtr FindTopLevelWindow(int pid)
    {
        var found = IntPtr.Zero;
        _ = EnumWindows((hwnd, lParam) =>
        {
            _ = lParam;
            _ = GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == (uint)pid && IsWindowVisible(hwnd))
            {
                var className = GetClassNameText(hwnd);
                if (className.StartsWith("WindowsForms10.Window", StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static RectI GetWindowScreenRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return default;
        }

        return RectI.FromSize(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static RectI GetClientScreenRect(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return default;
        }

        return RectI.FromSize(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    public static uint GetDpi(IntPtr hwnd) => GetDpiForWindow(hwnd);

    public static string GetClassNameText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    public static IntPtr WindowFromScreenPoint(int x, int y) => WindowFromPoint(new NativePoint { X = x, Y = y });

    public static IntPtr RootWindow(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GaRoot);

    public static bool PostMessageForClick(IntPtr hwnd, int clientX, int clientY)
    {
        var lParam = unchecked((IntPtr)((uint)clientX | ((uint)clientY << 16)));
        var move = PostMessageW(hwnd, 0x0200, IntPtr.Zero, lParam);
        var down = PostMessageW(hwnd, 0x0201, new IntPtr(1), lParam);
        var up = PostMessageW(hwnd, 0x0202, IntPtr.Zero, lParam);
        return move && down && up;
    }

    public static (int Panel, int Target) ParseClickCounts(string text)
    {
        var panel = 0;
        var target = 0;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2 || !int.TryParse(pieces[1], out var value))
            {
                continue;
            }

            if (pieces[0] == "panel")
            {
                panel = value;
            }
            else if (pieces[0] == "target")
            {
                target = value;
            }
        }

        return (panel, target);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "windows", "src", "WhaleGo.Platform")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}