using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// FlaUI UIA3 wrapper: creates (lazily, once, thread-safe) the shared automation instance and exposes only
/// exception-safe entry points. Every UIA property read elsewhere should go through <see cref="SafeRead{T}"/>:
/// UIA providers can throw COMException/ElementNotAvailableException/PropertyNotSupported on any property.
/// </summary>
public sealed class UiaEngine : IDisposable
{
    private readonly object _sync = new();
    private UIA3Automation? _automation;
    private bool _disposed;

    /// <summary>Shared automation instance (do not dispose; owned by this engine).</summary>
    public AutomationBase Automation
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _automation ??= new UIA3Automation();
            }
        }
    }

    /// <summary>Best-effort availability probe (desktop root element readable).</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return Automation.GetDesktop() is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public AutomationElement? TryGetRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Automation.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    public AutomationElement? TryGetDesktop()
    {
        try
        {
            return Automation.GetDesktop();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>UIA ElementFromPoint in physical screen pixels; null when unavailable.</summary>
    public AutomationElement? TryFromPoint(int screenX, int screenY)
    {
        try
        {
            return Automation.FromPoint(new Point(screenX, screenY));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read any UIA-backed value, swallowing provider exceptions (returns default(T)).</summary>
    public T? SafeRead<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default;
        }
    }

    public bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _automation?.Dispose();
            }
            catch
            {
                // ignore UIA teardown failures
            }

            _automation = null;
        }
    }
}
