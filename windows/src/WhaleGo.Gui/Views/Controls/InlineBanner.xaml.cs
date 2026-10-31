using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Views.Controls;

/// <summary>Inline info / warning / danger banner (docs/GUI.md 10.5) with an optional action link.</summary>
public partial class InlineBanner : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(UiSeverity), typeof(InlineBanner),
        new PropertyMetadata(UiSeverity.Info, OnVisualChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InlineBanner),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InlineBanner),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(InlineBanner),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(InlineBanner),
        new PropertyMetadata(null, OnVisualChanged));

    public InlineBanner()
    {
        InitializeComponent();
        ApplyVisuals();
    }

    public UiSeverity Severity
    {
        get => (UiSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((InlineBanner)d).ApplyVisuals();

    private void ApplyVisuals()
    {
        if (Frame is null)
        {
            return;
        }

        var brushKey = Severity switch
        {
            UiSeverity.Danger => "Brush.Danger",
            UiSeverity.Warning => "Brush.Warning",
            _ => "Brush.Accent",
        };
        Tint.SetResourceReference(BackgroundProperty, brushKey);
        Frame.SetResourceReference(Border.BorderBrushProperty, brushKey);
        Icon.SetResourceReference(BackgroundProperty, brushKey);
        IconText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        IconText.Text = Severity == UiSeverity.Info ? "i" : "!";
        TitleText.Text = Title ?? string.Empty;
        TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = Message ?? string.Empty;
        var hasAction = ActionCommand is not null && !string.IsNullOrEmpty(ActionText);
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        Visibility = string.IsNullOrEmpty(Message) && string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
    }
}
