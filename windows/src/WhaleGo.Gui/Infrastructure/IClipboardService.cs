namespace WhaleGo.Gui.Infrastructure;

/// <summary>
/// Clipboard access is a UI concern; view models receive this port so their tests never touch the real clipboard.
/// Copied tokens are cleared after 60s only if the clipboard still contains the copied value (docs/GUI.md 7.2).
/// </summary>
public interface IClipboardService
{
    void SetText(string text);

    void SetText(string text, TimeSpan autoClearAfter);
}

public static class ClipboardClearPolicy
{
    /// <summary>True only when the clipboard still holds exactly what we copied (never clobber user data).</summary>
    public static bool ShouldClear(string? currentClipboardText, string? copiedText) =>
        !string.IsNullOrEmpty(copiedText) && string.Equals(currentClipboardText, copiedText, StringComparison.Ordinal);
}
