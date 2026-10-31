namespace WhaleGo.Platform.Windows;

/// <summary>
/// Window activation/foreground helper (ARCHITECTURE §3.6). Best effort but never fakes success: the return value is
/// only true when the target is verified as the foreground window (root-ancestor comparison) within
/// <c>confirmWaitMs</c>. Sync on purpose; callers that need async wrap this in Task.Run.
/// Strategy: restore/show if needed, AttachThreadInput to both the current foreground thread and the target thread
/// (best effort), then retry BringWindowToTop + SetForegroundWindow while polling the foreground window until the
/// deadline; attachments are released in finally.
/// </summary>
public sealed class WindowActivator
{
    public const int DefaultConfirmWaitMs = 250;

    /// <summary>Activate by window_id ("w-" + HWND X12).</summary>
    public bool TryActivate(string windowId, int confirmWaitMs = DefaultConfirmWaitMs) =>
        WindowIdCodec.TryParse(windowId, out var hwnd) && TryActivate(hwnd, confirmWaitMs);

    /// <summary>Activate from WindowInfo facts (window_id is the HWND locator).</summary>
    public bool TryActivate(WhaleGo.Contracts.Model.WindowInfo window, int confirmWaitMs = DefaultConfirmWaitMs)
    {
        ArgumentNullException.ThrowIfNull(window);
        return WindowIdCodec.TryParse(window.WindowId, out var hwnd) && TryActivate(hwnd, confirmWaitMs);
    }

    /// <summary>Activate a window handle; returns true only when hwnd (or its root popup) really owns the foreground.</summary>
    public bool TryActivate(IntPtr hwnd, int confirmWaitMs = DefaultConfirmWaitMs)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
        {
            return false;
        }

        if (IsForeground(hwnd))
        {
            return true;
        }

        var currentThread = Win32.GetCurrentThreadId();
        var foregroundHandle = Win32.GetForegroundWindow();
        var foregroundThread = foregroundHandle == IntPtr.Zero ? 0u : Win32.GetWindowThreadProcessId(foregroundHandle, IntPtr.Zero);
        var targetThread = Win32.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = Win32.AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = Win32.AttachThreadInput(currentThread, targetThread, true);
            }

            if (!Win32.IsWindowVisible(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_SHOWNORMAL);
            }

            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            }

            var wait = Math.Max(0, confirmWaitMs);
            var deadline = Environment.TickCount64 + wait;
            do
            {
                try
                {
                    Win32.BringWindowToTop(hwnd);
                    Win32.SetForegroundWindow(hwnd);
                }
                catch
                {
                    // Keep polling: providers/Windows can transiently refuse; failure is reported honestly below.
                }

                // Poll briefly after each attempt so a fast activation returns immediately.
                var pollDeadline = Math.Min(deadline, Environment.TickCount64 + 50);
                while (true)
                {
                    if (IsForeground(hwnd))
                    {
                        return true;
                    }

                    if (Environment.TickCount64 >= pollDeadline)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                }
            }
            while (Environment.TickCount64 < deadline);

            return IsForeground(hwnd);
        }
        finally
        {
            if (attachedTarget)
            {
                Win32.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                Win32.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>True when hwnd (or a child popup whose root ancestor is hwnd) owns the foreground.</summary>
    public bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && Win32.IsForegroundWindow(hwnd);
}
