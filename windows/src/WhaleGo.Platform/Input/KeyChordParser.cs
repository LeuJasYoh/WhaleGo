namespace WhaleGo.Platform.Input;

/// <summary>One physical key stroke: virtual-key code + hardware scan code (+ extended flag).</summary>
public readonly record struct KeyStroke(ushort VirtualKey, ushort ScanCode, bool Extended);

/// <summary>
/// v1 single-key parser (docs/PROTOCOL.md section 3.7). Chords / modifiers are rejected on purpose:
/// the domain validator (Contracts.Domain.ActionValidator) only allows the 14 single keys.
/// </summary>
public static class KeyChordParser
{
    public const string Enter = "enter";
    public const string Tab = "tab";
    public const string Escape = "escape";
    public const string Backspace = "backspace";
    public const string Delete = "delete";
    public const string Up = "up";
    public const string Down = "down";
    public const string Left = "left";
    public const string Right = "right";
    public const string PageUp = "pageup";
    public const string PageDown = "pagedown";
    public const string Home = "home";
    public const string End = "end";
    public const string Space = "space";

    private static readonly Dictionary<string, KeyStroke> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        [Enter] = new KeyStroke(0x0D, 0x1C, false),
        [Tab] = new KeyStroke(0x09, 0x0F, false),
        [Escape] = new KeyStroke(0x1B, 0x01, false),
        [Backspace] = new KeyStroke(0x08, 0x0E, false),
        [Delete] = new KeyStroke(0x2E, 0x53, true),
        [Up] = new KeyStroke(0x26, 0x48, true),
        [Down] = new KeyStroke(0x28, 0x50, true),
        [Left] = new KeyStroke(0x25, 0x4B, true),
        [Right] = new KeyStroke(0x27, 0x4D, true),
        [PageUp] = new KeyStroke(0x21, 0x49, true),
        [PageDown] = new KeyStroke(0x22, 0x51, true),
        [Home] = new KeyStroke(0x24, 0x47, true),
        [End] = new KeyStroke(0x23, 0x4F, true),
        [Space] = new KeyStroke(0x20, 0x39, false),
    };

    public static IReadOnlyCollection<string> SupportedKeys => Keys.Keys;

    /// <summary>Parses one v1 key name; returns false for chords ("ctrl+c"), whitespace and unknown names.</summary>
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
}
