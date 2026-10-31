using System.Runtime.InteropServices;
using WhaleGo.Contracts.Abstractions;

namespace WhaleGo.Platform.System;

/// <summary>
/// DWM dark title bar helper (Contracts port; the GUI never P/Invokes, docs/GUI.md section 9 / D22).
/// Win11 uses DWMWA_USE_IMMERSIVE_DARK_MODE = 20; older Windows 10 1809+ builds only know attribute 19.
/// Returns false when the OS does not support it (Win10 before 1809 / non-DWM windows) and never throws.
/// </summary>
public sealed class DwmWindowTheme : IWindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = dark ? 1 : 0;
            var size = sizeof(int);
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, size) == 0)
            {
                return true;
            }

            value = dark ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, size) == 0;
        }
        catch (Exception)
        {
            // Missing dwmapi / unsupported OS: the GUI falls back to the native light title bar.
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
