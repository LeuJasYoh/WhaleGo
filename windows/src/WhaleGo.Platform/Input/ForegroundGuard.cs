using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Foreground safety around physical input (docs/ARCHITECTURE.md section 3.6):
/// foreground check, bounded activation, keyboard-queue drain and best-effort focus restore.
/// </summary>
public sealed class ForegroundGuard
{
    private readonly WindowActivator _activator;

    public ForegroundGuard(WindowActivator activator) =>
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));

    public bool IsForeground(nint hwnd) => hwnd != 0 && _activator.IsForeground(hwnd);

    /// <summary>Bounded activation; returns false instead of pretending success.</summary>
    public bool TryActivate(nint hwnd, int timeoutMs = 250) =>
        hwnd != 0 && _activator.TryActivate(hwnd, Math.Clamp(timeoutMs, 0, 2000));

    /// <summary>Drops stale WM_KEYDOWN/WM_KEYUP/WM_CHAR messages queued for the window.</summary>
    public void DrainKeyboardQueue(nint hwnd) => NativeInput.DrainKeyboardMessages(hwnd);

    /// <summary>
    /// Enters the target window's foreground: returns a lease that restores the previous foreground
    /// window on dispose, or null when activation was refused (caller maps FOREGROUND_REQUIRED).
    /// </summary>
    public ForegroundLease? TryEnter(nint hwnd, int timeoutMs = 250)
    {
        if (hwnd == 0 || !NativeInput.IsWindow(hwnd))
        {
            return null;
        }

        var previous = NativeInput.GetForegroundWindow();
        var alreadyForeground = _activator.IsForeground(hwnd);
        if (!alreadyForeground && !_activator.TryActivate(hwnd, Math.Clamp(timeoutMs, 0, 2000)))
        {
            return null;
        }

        DrainKeyboardQueue(hwnd);
        return new ForegroundLease(this, previous, activated: !alreadyForeground);
    }

    private void Restore(nint previous, nint current)
    {
        if (previous == 0 || previous == current || !NativeInput.IsWindow(previous) || !NativeInput.IsWindowVisible(previous))
        {
            return;
        }

        _ = _activator.TryActivate(previous, 100);
    }

    /// <summary>Best-effort focus restore token for one physical action.</summary>
    public sealed class ForegroundLease : IDisposable
    {
        private readonly ForegroundGuard _owner;
        private readonly nint _previous;
        private readonly bool _activated;
        private bool _disposed;

        internal ForegroundLease(ForegroundGuard owner, nint previous, bool activated)
        {
            _owner = owner;
            _previous = previous;
            _activated = activated;
        }

        public bool Activated => _activated && !_disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_activated)
            {
                _owner.Restore(_previous, NativeInput.GetForegroundWindow());
            }
        }
    }
}
