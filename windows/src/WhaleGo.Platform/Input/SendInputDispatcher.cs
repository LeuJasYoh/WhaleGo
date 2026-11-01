using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Physical keyboard/mouse dispatch through SendInput (docs/DESIGN.md section 5.5).
/// Every method is a single SendInput call where possible; a short or failed call throws
/// <see cref="Win32Exception"/>, and callers must treat that as "entered native dispatch"
/// (possibly_sent) once they have marked the attempt as sent.
/// </summary>
public sealed class SendInputDispatcher
{
    private static readonly int InputSize = Marshal.SizeOf<NativeInput.Input>();

    /// <summary>Per-character pacing for unicode typing (PROTOCOL section 7: 5s + 20ms/char budget).</summary>
    public int UnicodePacingMs { get; init; } = 20;

    public void MoveTo(int screenX, int screenY) => Send(MakeAbsoluteMove(screenX, screenY));

    public void ClickLeftAt(int screenX, int screenY) =>
        Send(MakeAbsoluteMove(screenX, screenY), MakeMouseButton(down: true), MakeMouseButton(down: false));

    public void ClickLeftHere() => Send(MakeMouseButton(down: true), MakeMouseButton(down: false));

    public void LeftDown() => Send(MakeMouseButton(down: true));

    public void LeftUp() => Send(MakeMouseButton(down: false));

    /// <summary>Wheel scroll at a screen point; positive delta scrolls up (away from the user).</summary>
    public void ScrollAt(int screenX, int screenY, int delta)
    {
        Send(MakeAbsoluteMove(screenX, screenY), MakeMouseWheel(delta));
    }

    /// <summary>Maps a v1 scroll direction/pages pair to a wheel delta.</summary>
    public static int WheelDeltaFor(string? direction, double pages)
    {
        var safePages = double.IsFinite(pages) ? Math.Clamp(pages, 0.25, 20) : 1;
        var magnitude = (int)Math.Round(NativeInput.WheelDelta * safePages, MidpointRounding.AwayFromZero);
        if (magnitude < 1)
        {
            magnitude = 1;
        }

        return string.Equals(direction, "down", StringComparison.Ordinal) ? -magnitude : magnitude;
    }

    public void PressKey(KeyStroke stroke)
    {
        Send(MakeKey(stroke, keyUp: false), MakeKey(stroke, keyUp: true));
    }

    /// <summary>
    /// Sends one key chord in a single SendInput call: modifiers down (Ctrl/Alt/Shift/Win),
    /// base key down/up, modifiers up in reverse order. A single call keeps the chord as atomic
    /// as Windows allows; a short/failed call throws and the caller treats it as possibly_sent.
    /// </summary>
    public void SendChord(HotkeyChord chord)
    {
        var events = new List<NativeInput.Input>((chord.Modifiers.Count * 2) + 2);
        foreach (var modifier in chord.Modifiers)
        {
            events.Add(MakeKey(modifier, keyUp: false));
        }

        events.Add(MakeKey(chord.Key, keyUp: false));
        events.Add(MakeKey(chord.Key, keyUp: true));
        for (var i = chord.Modifiers.Count - 1; i >= 0; i--)
        {
            events.Add(MakeKey(chord.Modifiers[i], keyUp: true));
        }

        Send(events.ToArray());
    }

    public void KeyDown(KeyStroke stroke) => Send(MakeKey(stroke, keyUp: false));

    public void KeyUp(KeyStroke stroke) => Send(MakeKey(stroke, keyUp: true));

    /// <summary>Types text as unicode keyboard input (KEYEVENTF_UNICODE), ~20ms per character.</summary>
    public void TypeUnicode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var pacing = Math.Max(UnicodePacingMs, 0);
        var index = 0;
        foreach (var ch in text)
        {
            Send(MakeUnicode(ch, keyUp: false), MakeUnicode(ch, keyUp: true));
            index++;
            if (pacing > 0 && index < text.Length)
            {
                Thread.Sleep(pacing);
            }
        }
    }

    private static void Send(params NativeInput.Input[] inputs)
    {
        if (inputs.Length == 0)
        {
            return;
        }

        var sent = NativeInput.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != (uint)inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error == 0 ? 0 : error, string.Create(CultureInfo.InvariantCulture, $"SendInput sent {sent}/{inputs.Length} events."));
        }
    }

    private static NativeInput.Input MakeMouse(uint flags, uint mouseData = 0) => new()
    {
        type = NativeInput.InputMouse,
        u = new NativeInput.InputUnion
        {
            mi = new NativeInput.MouseInput
            {
                dx = 0,
                dy = 0,
                mouseData = mouseData,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };

    private static NativeInput.Input MakeMouseButton(bool down) =>
        MakeMouse(down ? NativeInput.MouseLeftDown : NativeInput.MouseLeftUp);

    private static NativeInput.Input MakeMouseWheel(int delta) =>
        MakeMouse(NativeInput.MouseWheel, unchecked((uint)delta));

    private static NativeInput.Input MakeAbsoluteMove(int screenX, int screenY)
    {
        var width = NativeInput.GetVirtualScreenWidth();
        var height = NativeInput.GetVirtualScreenHeight();
        var left = NativeInput.GetVirtualScreenLeft();
        var top = NativeInput.GetVirtualScreenTop();
        var normalizedX = width <= 1 ? 0 : (int)Math.Round((screenX - left) * 65535.0 / (width - 1), MidpointRounding.AwayFromZero);
        var normalizedY = height <= 1 ? 0 : (int)Math.Round((screenY - top) * 65535.0 / (height - 1), MidpointRounding.AwayFromZero);
        return new NativeInput.Input
        {
            type = NativeInput.InputMouse,
            u = new NativeInput.InputUnion
            {
                mi = new NativeInput.MouseInput
                {
                    dx = Math.Clamp(normalizedX, 0, 65535),
                    dy = Math.Clamp(normalizedY, 0, 65535),
                    mouseData = 0,
                    dwFlags = NativeInput.MouseMove | NativeInput.MouseAbsolute | NativeInput.MouseVirtualDesk,
                    time = 0,
                    dwExtraInfo = 0,
                },
            },
        };
    }

    private static NativeInput.Input MakeKey(KeyStroke stroke, bool keyUp)
    {
        var flags = keyUp ? NativeInput.KeyEventKeyUp : 0u;
        if (stroke.Extended)
        {
            flags |= NativeInput.KeyEventExtendedKey;
        }

        return new NativeInput.Input
        {
            type = NativeInput.InputKeyboard,
            u = new NativeInput.InputUnion
            {
                ki = new NativeInput.KeyboardInput
                {
                    wVk = stroke.VirtualKey,
                    wScan = stroke.ScanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = 0,
                },
            },
        };
    }

    private static NativeInput.Input MakeUnicode(char unit, bool keyUp)
    {
        var flags = NativeInput.KeyEventUnicode | (keyUp ? NativeInput.KeyEventKeyUp : 0u);
        return new NativeInput.Input
        {
            type = NativeInput.InputKeyboard,
            u = new NativeInput.InputUnion
            {
                ki = new NativeInput.KeyboardInput
                {
                    wVk = 0,
                    wScan = unit,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = 0,
                },
            },
        };
    }
}