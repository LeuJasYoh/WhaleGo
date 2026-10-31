using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WhaleGo.Platform.Windows;

/// <summary>Minimal Win32 interop used by the observation platform (L1 native seam; internal only).</summary>
internal static class Win32
{
    // Window constants.
    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_APPWINDOW = 0x00040000L;
    internal const uint GW_OWNER = 4;
    internal const uint GA_ROOT = 2;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_CLOAKED = 14;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;
    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint DIB_RGB_COLORS = 0;
    internal const int BI_RGB = 0;
    internal const int SM_REMOTESESSION = 0x1000;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenIntegrityLevel = 25;
    internal const int UOI_NAME = 2;
    internal const uint DESKTOP_READOBJECTS = 0x0001;
    internal const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    internal const uint DESKTOP_ENUMERATE = 0x0040;
    internal const long DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    internal const long DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    internal const long DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    internal const long DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    // ---- user32 ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpDwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiAwarenessContextA, IntPtr dpiAwarenessContextB);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    // ---- kernel32 ----
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    internal static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint appModelIdLength, [Out] StringBuilder? appModelId);

    // ---- advapi32 ----
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    // ---- dwmapi ----
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ---- gdi32 ----
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    // ---- helpers ----
    internal static string GetClassNameSafe(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var count = GetClassName(hwnd, buffer, buffer.Capacity);
        return count > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string GetWindowTitleSafe(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(Math.Min(length + 1, 4096));
        var count = GetWindowText(hwnd, buffer, buffer.Capacity);
        return count > 0 ? buffer.ToString() : string.Empty;
    }

    internal static int GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid);
    }

    internal static IntPtr GetRootAncestor(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    internal static bool IsForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        return foreground == hwnd || (foreground != IntPtr.Zero && GetRootAncestor(foreground) == hwnd);
    }

    internal static bool IsToolWindow(IntPtr hwnd)
    {
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return (exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0;
    }

    internal static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            var hr = DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetExtendedFrameBounds(IntPtr hwnd, out RECT bounds)
    {
        bounds = default;
        try
        {
            var hr = DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>());
            if (hr != 0 || rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            bounds = rect;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static uint GetDpiSafe(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96;
        }
    }

    internal static bool TryGetProcessName(int pid, out string processName)
    {
        processName = string.Empty;
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            return processName.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetExePath(int pid, out string exePath)
    {
        exePath = string.Empty;
        if (pid <= 0)
        {
            return false;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, unchecked((uint)pid));
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size <= 0)
            {
                return false;
            }

            exePath = buffer.ToString(0, size);
            return exePath.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static bool TryGetApplicationUserModelId(int pid, out string aumid)
    {
        aumid = string.Empty;
        if (pid <= 0)
        {
            return false;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, unchecked((uint)pid));
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var length = 256u;
            var buffer = new StringBuilder((int)length);
            var result = GetApplicationUserModelId(handle, ref length, buffer);
            if (result != 0 || length == 0)
            {
                return false;
            }

            aumid = buffer.ToString(0, (int)Math.Min(length, (uint)buffer.Capacity));
            return aumid.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static bool IsRemoteSession()
    {
        try
        {
            return GetSystemMetrics(SM_REMOTESESSION) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryOpenInputDesktop(out string desktopName)
    {
        desktopName = string.Empty;
        var handle = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP | DESKTOP_ENUMERATE);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var buffer = new StringBuilder(256);
            if (!GetUserObjectInformation(handle, UOI_NAME, buffer, buffer.Capacity, out _))
            {
                return false;
            }

            desktopName = buffer.ToString();
            return desktopName.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseDesktop(handle);
        }
    }
}
