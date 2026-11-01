namespace WhaleGo.Contracts.Domain;

/// <summary>
/// One validated key chord for the go_act hotkey op (e.g. "ctrl+shift+s").
/// Modifiers are normalized to Ctrl/Alt/Shift/Win order; exactly one base key is allowed.
/// </summary>
public readonly record struct HotkeySpec(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    private static readonly HashSet<string> BaseKeys = CreateBaseKeys();

    /// <summary>Canonical lower-case form used in the action plan/audit, e.g. "ctrl+alt+shift+win+f12".</summary>
    public string Canonical
    {
        get
        {
            var parts = new List<string>(5);
            if (Ctrl) parts.Add("ctrl");
            if (Alt) parts.Add("alt");
            if (Shift) parts.Add("shift");
            if (Win) parts.Add("win");
            parts.Add(Key);
            return string.Join('+', parts);
        }
    }

    /// <summary>
    /// Parses "mod+...+key". A chord with no modifier (single key such as "f5") is valid; duplicate or
    /// unknown tokens, empty parts and more than one base key are rejected.
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Length is < 1 or > 5)
        {
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        string? key = null;
        foreach (var raw in parts)
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0)
            {
                return false;
            }

            switch (token)
            {
                case "ctrl" or "control":
                    if (ctrl) return false;
                    ctrl = true;
                    break;
                case "alt":
                    if (alt) return false;
                    alt = true;
                    break;
                case "shift":
                    if (shift) return false;
                    shift = true;
                    break;
                case "win" or "meta" or "windows":
                    if (win) return false;
                    win = true;
                    break;
                default:
                    if (key is not null || !BaseKeys.Contains(token))
                    {
                        return false;
                    }

                    key = NormalizeKey(token);
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        spec = new HotkeySpec(ctrl, alt, shift, win, key);
        return true;
    }

    private static string NormalizeKey(string token) => token switch
    {
        "return" => "enter",
        "esc" => "escape",
        "del" => "delete",
        "ins" => "insert",
        "pgup" => "pageup",
        "pgdn" => "pagedown",
        "prtsc" => "printscreen",
        _ => token,
    };

    private static HashSet<string> CreateBaseKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var ch = 'a'; ch <= 'z'; ch++)
        {
            keys.Add(ch.ToString());
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            keys.Add(digit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        for (var fn = 1; fn <= 24; fn++)
        {
            keys.Add("f" + fn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var named in new[]
                 {
                     "enter", "return", "tab", "escape", "esc", "space", "backspace", "delete", "del",
                     "insert", "ins", "home", "end", "pageup", "pgup", "pagedown", "pgdn",
                     "up", "down", "left", "right", "printscreen", "prtsc", "pause",
                 })
        {
            keys.Add(named);
        }

        return keys;
    }
}
