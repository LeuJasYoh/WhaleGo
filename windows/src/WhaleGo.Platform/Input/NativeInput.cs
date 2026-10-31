using System.Runtime.InteropServices;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Win32 raw-input / window primitives used only by the platform input layer.
/// Kept internal: no Win32 type ever leaves WhaleGo.Platform (docs/ARCHITECTURE.md section 1.3).
/// </summary>
internal static class NativeInput
{
    // ── SendInput ────────────────────────────────────────────────────────
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseMove = 0x0001;
    internal const uint MouseLeftDown = 0x0002;
    internal const uint MouseLeftUp = 0x0004;
    internal const uint MouseWheel = 0x0800;
    internal const uint MouseHWheel = 0x1000;
    internal const uint MouseVirtualDesk = 0x4000;
    internal const uint MouseAbsolute = 0x8000;

    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint KeyEventUnicode = 0x0004;
    internal const uint KeyEventExtendedKey = 0x0001;

    internal const int WheelDelta = 120;

    // ── Window messages ─────────────────────────────────────────────────
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmKeyFirst = 0x0100;
    internal const uint WmKeyLast = 0x0109;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLeftButtonDown = 0x0201;
    internal const uint WmLeftButtonUp = 0x0202;
    internal const uint WmMouseWheel = 0x020A;

    internal const int MouseKeyLeft = 0x0001;

    // ── Misc window constants ───────────────────────────────────────────
    internal const uint PmRemove = 0x0001;
    internal const uint GaRoot = 2;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput mi;

        [FieldOffset(0)]
        public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int x;
        public int y;

        public Point(int xValue, int yValue)
        {
            x = xValue;
            y = yValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public readonly int Width => right - left;

        public readonly int Height => bottom - top;

        public readonly bool Contains(int x, int y) => x >= left && x < right && y >= top && y < bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(nint dpiAwarenessContext);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint cInputs, [In] Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int MapWindowPoints(nint hWndFrom, nint hWndTo, ref Point lpPoints, uint cPoints);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(nint hWndParent, nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    internal static int GetVirtualScreenLeft() => GetSystemMetrics(SmXVirtualScreen);

    internal static int GetVirtualScreenTop() => GetSystemMetrics(SmYVirtualScreen);

    internal static int GetVirtualScreenWidth() => GetSystemMetrics(SmCxVirtualScreen);

    internal static int GetVirtualScreenHeight() => GetSystemMetrics(SmCyVirtualScreen);

    internal static nint MakeLParam(int low, int high) =>
        unchecked((nint)((uint)(ushort)low | ((uint)(ushort)high << 16)));

    internal static int GetWindowDpi(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 96;
        }

        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : (int)dpi;
    }

    internal static string GetWindowClassName(nint hwnd)
    {
        if (hwnd == 0)
        {
            return string.Empty;
        }

        var buffer = new char[256];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    internal static Rect GetWindowScreenRect(nint hwnd) =>
        GetWindowRect(hwnd, out var rect) ? rect : default;

    internal static Point ClientToScreenPoint(nint hwnd, int clientX, int clientY)
    {
        var point = new Point(clientX, clientY);
        return ClientToScreen(hwnd, ref point) ? point : point;
    }

    internal static Rect GetClientScreenRect(nint hwnd)
    {
        if (hwnd == 0 || !GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = new Point(0, 0);
        if (!ClientToScreen(hwnd, ref origin))
        {
            return default;
        }

        return new Rect
        {
            left = origin.x,
            top = origin.y,
            right = origin.x + client.Width,
            bottom = origin.y + client.Height,
        };
    }

    internal static Point ScreenToClientPoint(nint hwnd, int screenX, int screenY)
    {
        var point = new Point(screenX, screenY);
        MapWindowPoints(0, hwnd, ref point, 1);
        return point;
    }

    /// <summary>Finds the deepest HWND at a screen point and clamps it into the given root window tree.</summary>
    internal static nint FindChildHwndAt(nint rootHwnd, int screenX, int screenY)
    {
        if (rootHwnd == 0)
        {
            return 0;
        }

        var hit = WindowFromPoint(new Point(screenX, screenY));
        if (hit == 0)
        {
            return 0;
        }

        if (hit == rootHwnd || IsChild(rootHwnd, hit))
        {
            return hit;
        }

        // The point may resolve to a popup/owned window; walking up to the root tells us whether it
        // belongs to the target window tree.
        var root = GetAncestor(hit, GaRoot);
        return root == rootHwnd ? hit : 0;
    }

    internal static void DrainKeyboardMessages(nint hwnd, int maxMessages = 256)
    {
        if (hwnd == 0)
        {
            return;
        }

        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;
        if (targetThread != 0 && targetThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, targetThread, fAttach: true);
        }

        try
        {
            for (var i = 0; i < maxMessages; i++)
            {
                if (!PeekMessageW(out _, hwnd, WmKeyFirst, WmKeyLast, PmRemove))
                {
                    break;
                }
            }
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, targetThread, fAttach: false);
            }
        }
    }
}
