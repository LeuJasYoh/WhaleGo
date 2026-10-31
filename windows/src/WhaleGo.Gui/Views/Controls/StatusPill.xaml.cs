using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Views.Controls;

/// <summary>Small status badge: filled dot + text on a 12% tinted capsule (docs/GUI.md 10.5).</summary>
public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(StatusLevel), typeof(StatusPill),
        new PropertyMetadata(StatusLevel.Idle, OnVisualChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatusPill),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public StatusPill()
    {
        InitializeComponent();
        ApplyVisuals();
    }

    public StatusLevel Level
    {
        get => (StatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusPill)d).ApplyVisuals();

    private void ApplyVisuals()
    {
        if (Tint is null)
        {
            return;
        }

        var brushKey = Level switch
        {
            StatusLevel.Running => "Brush.Success",
            StatusLevel.Paused => "Brush.Warning",
            StatusLevel.Stopped => "Brush.Danger",
            StatusLevel.Faulted => "Brush.Danger",
            _ => "Brush.Idle",
        };
        Tint.SetResourceReference(BackgroundProperty, brushKey);
        Dot.SetResourceReference(Shape.FillProperty, brushKey);
        Caption.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        Caption.Text = string.IsNullOrEmpty(Label) ? DefaultLabel(Level) : Label;
    }

    private static string DefaultLabel(StatusLevel level) => level switch
    {
        StatusLevel.Running => "运行中",
        StatusLevel.Paused => "已暂停",
        StatusLevel.Stopped => "已停止",
        StatusLevel.Faulted => "异常",
        _ => "空闲",
    };
}
