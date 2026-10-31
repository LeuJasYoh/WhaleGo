using System.Windows;
using WhaleGo.Gui.Views.Controls;

namespace WhaleGo.Gui.Views.Dialogs;

public sealed record ConfirmDialogModel(
    string Title,
    string Message,
    string ConfirmText,
    bool Danger = false,
    string? Detail = null,
    string? RequiredText = null);

/// <summary>
/// Destructive confirmation dialog. Safe defaults: Esc/Cancel are always available, Enter never confirms implicitly,
/// the cancel button gets focus for dangerous actions (docs/GUI.md 10.5 / 4.3).
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
        : this(new ConfirmDialogModel("确认", string.Empty, "确定"))
    {
    }

    public ConfirmDialog(ConfirmDialogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        Title = model.Title;
        HeadingText.Text = model.Title;
        MessageText.Text = model.Message;
        if (!string.IsNullOrWhiteSpace(model.Detail))
        {
            DetailText.Text = model.Detail;
            DetailBox.Visibility = Visibility.Visible;
        }

        ConfirmButton.Content = model.ConfirmText;
        if (model.Danger)
        {
            ConfirmButton.Style = (Style)FindResource("Button.Danger");
        }

        if (!string.IsNullOrWhiteSpace(model.RequiredText))
        {
            TypedPanel.Visibility = Visibility.Visible;
            RequiredLabel.Text = model.RequiredText;
            ConfirmButton.IsEnabled = false;
            TypedInput.TextChanged += (_, _) =>
            {
                ConfirmButton.IsEnabled = string.Equals(
                    TypedInput.Text.Trim(),
                    model.RequiredText,
                    StringComparison.Ordinal);
            };
        }

        ConfirmButton.Click += (_, _) => DialogResult = true;
        CancelButton.Click += (_, _) => DialogResult = false;
        Loaded += (_, _) =>
        {
            if (model.Danger || !string.IsNullOrWhiteSpace(model.RequiredText))
            {
                CancelButton.Focus();
            }
            else
            {
                ConfirmButton.Focus();
            }
        };
    }

    public static bool Show(Window? owner, ConfirmDialogModel model)
    {
        var dialog = new ConfirmDialog(model);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
