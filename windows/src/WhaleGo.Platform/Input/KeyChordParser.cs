using System.Globalization;
using WhaleGo.Contracts.Domain;

namespace WhaleGo.Platform.Input;

/// <summary>One physical key stroke: virtual-key code + hardware scan code (+ extended flag).</summary>
public readonly record struct KeyStroke(ushort VirtualKey, ushort ScanCode, bool Extended);

/// <summary>A parsed hotkey chord: base key plus modifier keys in canonical Ctrl/Alt/Shift/Win order.</summary>
public readonly record struct HotkeyChord(KeyStroke Key, IReadOnlyList<KeyStroke> Modifiers);

/// <summary>
/// v1 single-key parser plus the v1.3 hotkey chord parser (docs/PROTOCOL.md section 3.7).
/// Chords are validated by <see cref="HotkeySpec"/> (closed key set) and mapped to Win32 virtual keys here.
/// </summary>
public static class KeyChordParser
{
    public const string Enter = "enter";
    public const string Tab = "tab";
    public const string Escape = "escape";
    public const string Backspace = "backspace";
    public const string Delete = "delete";
    public const string Insert = "insert";
    public const string Up = "up";
    public const string Down = "down";
    public const string Left = "left";
    public const string Right = "right";
    public const string PageUp = "pageup";
    public const string PageDown = "pagedown";
    public const string Home = "home";
    public const string End = "end";
    public const string Space = "space";
    public const string PrintScreen = "printscreen";
    public const string Pause = "pause";

    /// <summary>Left-side modifier strokes used by SendInput (right-side modifiers are not needed by v1).</summary>
    public static readonly KeyStroke LeftControl = new(0xA2, 0x1D, false);
    public static readonly KeyStroke LeftAlt = new(0xA4, 0x38, false);
    public static readonly KeyStroke LeftShift = new(0xA0, 0x2A, false);
    public static readonly KeyStroke LeftWin = new(0x5B, 0x5B, false);

    private static readonly Dictionary<string, KeyStroke> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        [Enter] = new KeyStroke(0x0D, 0x1C, false),
        [Tab] = new KeyStroke(0x09, 0x0F, false),
        [Escape] = new KeyStroke(0x1B, 0x01, false),
        [Backspace] = new KeyStroke(0x08, 0x0E, false),
        [Delete] = new KeyStroke(0x2E, 0x53, true),
        [Insert] = new KeyStroke(0x2D, 0x52, true),
        [Up] = new KeyStroke(0x26, 0x48, true),
        [Down] = new KeyStroke(0x28, 0x50, true),
        [Left] = new KeyStroke(0x25, 0x4B, true),
        [Right] = new KeyStroke(0x27, 0x4D, true),
        [PageUp] = new KeyStroke(0x21, 0x49, true),
        [PageDown] = new KeyStroke(0x22, 0x51, true),
        [Home] = new KeyStroke(0x24, 0x47, true),
        [End] = new KeyStroke(0x23, 0x4F, true),
        [Space] = new KeyStroke(0x20, 0x39, false),
        [PrintScreen] = new KeyStroke(0x2C, 0x37, true),
        [Pause] = new KeyStroke(0x13, 0x45, false),
    };

    public static IReadOnlyCollection<string> SupportedKeys => Keys.Keys;

    /// <summary>Parses one v1 single key name; returns false for chords ("ctrl+c"), whitespace and unknown names.</summary>
    public static bool TryParse(string? text, out KeyStroke stroke)
    {
        stroke = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains('+', StringComparison.Ordinal) || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return Keys.TryGetValue(trimmed, out stroke);
    }

    public static bool IsSupported(string? text) => TryParse(text, out _);

    /// <summary>Parses a hotkey chord such as "ctrl+shift+s" / "alt+f4" / "f5" into SendInput-ready strokes.</summary>
    public static bool TryParseHotkey(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (!HotkeySpec.TryParse(text, out var spec))
        {
            return false;
        }

        if (!TryMapKey(spec.Key, out var key))
        {
            return false;
        }

        var modifiers = new List<KeyStroke>(4);
        if (spec.Ctrl)
        {
            modifiers.Add(LeftControl);
        }

        if (spec.Alt)
        {
            modifiers.Add(LeftAlt);
        }

        if (spec.Shift)
        {
            modifiers.Add(LeftShift);
        }

        if (spec.Win)
        {
            modifiers.Add(LeftWin);
        }

        chord = new HotkeyChord(key, modifiers);
        return true;
    }

    private static bool TryMapKey(string token, out KeyStroke stroke)
    {
        stroke = default;
        if (Keys.TryGetValue(token, out var named))
        {
            stroke = named;
            return true;
        }

        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                stroke = new KeyStroke(ch, 0, false);
                return true;
            }

            return false;
        }

        if (token.Length is >= 2 and <= 3 && token[0] is 'f' or 'F' &&
            int.TryParse(token.AsSpan(1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var fn) && fn is >= 1 and <= 24)
        {
            stroke = new KeyStroke((ushort)(0x70 + fn - 1), 0, false);
            return true;
        }

        return false;
    }
}
