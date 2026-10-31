using System.Windows;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Views.Dialogs;

namespace WhaleGo.Gui.Views;

/// <summary>WPF implementation of <see cref="IUserDialogs"/>; owner window resolved lazily by the shell.</summary>
public sealed class WpfUserDialogs : IUserDialogs
{
    private readonly Func<Window?> _ownerProvider;
    private readonly IClipboardService _clipboard;

    public WpfUserDialogs(Func<Window?> ownerProvider, IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(ownerProvider);
        ArgumentNullException.ThrowIfNull(clipboard);
        _ownerProvider = ownerProvider;
        _clipboard = clipboard;
    }

    public bool Confirm(string title, string message, string confirmText, bool danger = false, string? detail = null) =>
        ConfirmDialog.Show(_ownerProvider(), new ConfirmDialogModel(title, message, confirmText, danger, detail));

    public bool ConfirmWithTypedText(string title, string message, string requiredText, string? detail = null) =>
        ConfirmDialog.Show(_ownerProvider(), new ConfirmDialogModel(title, message, "确认", true, detail, requiredText));

    public void ShowTokenOnce(string title, string message, string token) =>
        TokenOnceDialog.Show(_ownerProvider(), title, message, token, _clipboard);

    public void ShowMessage(string title, string message)
    {
        var owner = _ownerProvider();
        if (owner is { IsVisible: true })
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        var owner = _ownerProvider();
        var accepted = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return accepted == true ? dialog.FileName : null;
    }
}
