using System.Windows;
using WhaleGo.Gui.Infrastructure;

namespace WhaleGo.Gui.Views.Dialogs;

/// <summary>Shows a rotated token exactly once, with a copy button; the token is never persisted by the GUI.</summary>
public partial class TokenOnceDialog : Window
{
    private readonly string _token;
    private readonly IClipboardService _clipboard;

    public TokenOnceDialog()
        : this("新的 token", string.Empty, string.Empty, new NullClipboardService())
    {
    }

    public TokenOnceDialog(string title, string message, string token, IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        _token = token;
        _clipboard = clipboard;
        TokenBox.Text = token;
        TokenBox.Focus();
        TokenBox.SelectAll();
    }

    public static void Show(Window? owner, string title, string message, string token, IClipboardService clipboard)
    {
        var dialog = new TokenOnceDialog(title, message, token, clipboard);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        _clipboard.SetText(_token, TimeSpan.FromSeconds(60));
        StatusText.Text = "已复制（剪贴板 60 秒后条件清空）";
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed class NullClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }

        public void SetText(string text, TimeSpan autoClearAfter)
        {
        }
    }
}
