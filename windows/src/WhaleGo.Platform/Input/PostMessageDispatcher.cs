using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WhaleGo.Platform.Input;

/// <summary>Result of one background message sequence; <see cref="AnyQueued"/> distinguishes a clean not-sent from a partial send.</summary>
public readonly record struct PostResult(bool Delivered, bool AnyQueued, string? Error)
{
    public static PostResult Ok() => new(true, true, null);

    public static PostResult NotSent(string? error) => new(false, false, error);

    public static PostResult Partial(string? error) => new(false, true, error);
}

/// <summary>
/// Background dispatch to a specific HWND (WM_MOUSE*/WM_CHAR/WM_KEY*), docs/ARCHITECTURE.md section 3.6.
/// PostMessage only reports whether the message entered the target queue; it never claims the UI acted.
/// </summary>
public sealed class PostMessageDispatcher
{
    /// <summary>
    /// Resolves the deepest HWND at a screen point that belongs to the target top-level window and
    /// converts the screen point into that HWND's client coordinates.
    /// </summary>
    public static bool TryResolveChildHwnd(nint rootHwnd, int screenX, int screenY, out nint childHwnd, out int clientX, out int clientY)
    {
        childHwnd = 0;
        clientX = 0;
        clientY = 0;
        if (rootHwnd == 0 || !NativeInput.IsWindow(rootHwnd))
        {
            return false;
        }

        var hit = NativeInput.FindChildHwndAt(rootHwnd, screenX, screenY);
        if (hit == 0)
        {
            return false;
        }

        var client = NativeInput.ScreenToClientPoint(hit, screenX, screenY);
        if (client.x is < 0 or > 32767 || client.y is < 0 or > 32767)
        {
            return false;
        }

        childHwnd = hit;
        clientX = client.x;
        clientY = client.y;
        return true;
    }

    /// <summary>Clicks the client point of a specific HWND with WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP.</summary>
    public PostResult ClickClient(nint hwnd, int clientX, int clientY)
    {
        if (hwnd == 0 || clientX < 0 || clientY < 0 || clientX > 32767 || clientY > 32767)
        {
            return PostResult.NotSent("invalid_hwnd_or_point");
        }

        var lParam = NativeInput.MakeLParam(clientX, clientY);
        var anyQueued = false;
        if (!Post(hwnd, NativeInput.WmMouseMove, 0, lParam, ref anyQueued, out var error))
        {
            return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
        }

        if (!Post(hwnd, NativeInput.WmLeftButtonDown, NativeInput.MouseKeyLeft, lParam, ref anyQueued, out error))
        {
            return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
        }

        if (!Post(hwnd, NativeInput.WmLeftButtonUp, 0, lParam, ref anyQueued, out error))
        {
            return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
        }

        return PostResult.Ok();
    }

    /// <summary>Clicks a screen point that must belong to the given top-level window.</summary>
    public PostResult ClickAtScreenPoint(nint rootHwnd, int screenX, int screenY)
    {
        if (!TryResolveChildHwnd(rootHwnd, screenX, screenY, out var child, out var clientX, out var clientY))
        {
            return PostResult.NotSent("point_not_in_window");
        }

        return ClickClient(child, clientX, clientY);
    }

    /// <summary>Types text into a specific HWND as a WM_CHAR sequence (one message per UTF-16 unit).</summary>
    public PostResult TypeUnicode(nint hwnd, string? text)
    {
        if (hwnd == 0)
        {
            return PostResult.NotSent("invalid_hwnd");
        }

        if (string.IsNullOrEmpty(text))
        {
            return PostResult.Ok();
        }

        var anyQueued = false;
        foreach (var ch in text)
        {
            if (!Post(hwnd, NativeInput.WmChar, ch, 1, ref anyQueued, out var error))
            {
                return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
            }
        }

        return PostResult.Ok();
    }

    /// <summary>Posts one key down/up pair (+ WM_CHAR for printable space) to a specific HWND.</summary>
    public PostResult PressKey(nint hwnd, KeyStroke stroke)
    {
        if (hwnd == 0)
        {
            return PostResult.NotSent("invalid_hwnd");
        }

        var down = (nint)MakeKeyLParam(stroke, keyUp: false);
        var up = (nint)MakeKeyLParam(stroke, keyUp: true);
        var anyQueued = false;
        if (!Post(hwnd, NativeInput.WmKeyDown, stroke.VirtualKey, down, ref anyQueued, out var error))
        {
            return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
        }

        if (stroke.VirtualKey == 0x20)
        {
            if (!Post(hwnd, NativeInput.WmChar, (nint)' ', down, ref anyQueued, out error))
            {
                return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
            }
        }

        if (!Post(hwnd, NativeInput.WmKeyUp, stroke.VirtualKey, up, ref anyQueued, out error))
        {
            return anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
        }

        return PostResult.Ok();
    }

    /// <summary>Posts WM_MOUSEWHEEL to a HWND (lParam carries the physical screen point).</summary>
    public PostResult WheelAtScreenPoint(nint hwnd, int screenX, int screenY, int delta)
    {
        if (hwnd == 0)
        {
            return PostResult.NotSent("invalid_hwnd");
        }

        if (screenX < -32768 || screenX > 32767 || screenY < -32768 || screenY > 32767)
        {
            return PostResult.NotSent("screen_point_out_of_range");
        }

        var wParam = unchecked((nint)(uint)((ushort)(short)delta << 16));
        var lParam = NativeInput.MakeLParam(screenX, screenY);
        var anyQueued = false;
        return Post(hwnd, NativeInput.WmMouseWheel, wParam, lParam, ref anyQueued, out var error)
            ? PostResult.Ok()
            : anyQueued ? PostResult.Partial(error) : PostResult.NotSent(error);
    }

    private static int MakeKeyLParam(KeyStroke stroke, bool keyUp)
    {
        var scan = (uint)stroke.ScanCode & 0xFF;
        var lParam = 1u | (scan << 16);
        if (stroke.Extended)
        {
            lParam |= 1u << 24;
        }

        if (keyUp)
        {
            lParam |= 1u << 30;
            lParam |= 1u << 31;
        }

        return unchecked((int)lParam);
    }

    private static bool Post(nint hwnd, uint message, nint wParam, nint lParam, ref bool anyQueued, out string? error)
    {
        if (NativeInput.PostMessageW(hwnd, message, wParam, lParam))
        {
            anyQueued = true;
            error = null;
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        error = new Win32Exception(code).Message;
        return false;
    }
}