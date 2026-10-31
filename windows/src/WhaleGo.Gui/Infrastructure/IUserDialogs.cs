namespace WhaleGo.Gui.Infrastructure;

/// <summary>
/// Confirmation / one-time-token dialogs. Abstracted so presenters can be unit tested without WPF windows.
/// All implementations must ensure the safe default (cancel) has focus for dangerous actions.
/// </summary>
public interface IUserDialogs
{
    /// <summary>Destructive confirmation; returns true only when the user explicitly confirms.</summary>
    bool Confirm(string title, string message, string confirmText, bool danger = false, string? detail = null);

    /// <summary>Type-to-confirm gate used for dangerous advanced switches ("我了解风险").</summary>
    bool ConfirmWithTypedText(string title, string message, string requiredText, string? detail = null);

    /// <summary>Shows a freshly rotated token exactly once, with a copy button (never re-readable afterwards).</summary>
    void ShowTokenOnce(string title, string message, string token);

    void ShowMessage(string title, string message);

    /// <summary>Native open-file dialog; returns null when cancelled.</summary>
    string? PickOpenFile(string title, string filter);
}
