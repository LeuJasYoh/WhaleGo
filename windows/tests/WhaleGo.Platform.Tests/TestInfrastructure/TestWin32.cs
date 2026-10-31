using System.Runtime.InteropServices;
using System.Text;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Tests.TestInfrastructure;

/// <summary>Independent Win32 reads used to cross-check platform results (never to drive the platform itself).</summary>
internal static class TestWin32
{
    public static RectI GetWindowRectValue(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return default;
        }

        return new RectI(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static RectI GetClientRectValue(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect))
        {
            return default;
        }

        return new RectI(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static RectI GetClientScreenRect(IntPtr hwnd)
    {
        var client = GetClientRectValue(hwnd);
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return default;
        }

        return new RectI(origin.X, origin.Y, origin.X + client.Width, origin.Y + client.Height);
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var count = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return count > 0 ? buffer.ToString() : string.Empty;
    }

    public static uint GetDpi(IntPtr hwnd) => GetDpiForWindow(hwnd);

    public static bool IsWindowVisible(IntPtr hwnd) => IsWindowVisibleNative(hwnd);

    public static IntPtr FindTopLevelWindow(string exactTitle)
    {
        var found = IntPtr.Zero;
        _ = EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisibleNative(hwnd))
            {
                return true;
            }

            var buffer = new StringBuilder(512);
            var length = GetWindowTextLengthW(hwnd);
            if (length <= 0 || length >= buffer.Capacity)
            {
                return true;
            }

            _ = GetWindowTextW(hwnd, buffer, buffer.Capacity);
            if (string.Equals(buffer.ToString(), exactTitle, StringComparison.Ordinal))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
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
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleNative(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
