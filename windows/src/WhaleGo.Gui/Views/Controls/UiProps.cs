using System.Windows;
using System.Windows.Media;

namespace WhaleGo.Gui.Views.Controls;

/// <summary>
/// Small attached properties shared by the hand-written control templates in Themes/Shared.xaml.
/// They exist so that every control style can stay a single template (no per-style template copies).
/// </summary>
public static class UiProps
{
    /// <summary>Marks an input as invalid; the themed TextBox draws a danger border.</summary>
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(UiProps), new FrameworkPropertyMetadata(false));

    /// <summary>Brush used by the animated hover overlay of a button.</summary>
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBrush", typeof(Brush), typeof(UiProps), new FrameworkPropertyMetadata((Brush?)null));

    /// <summary>Brush used by the pressed overlay of a button.</summary>
    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.RegisterAttached(
        "PressedBrush", typeof(Brush), typeof(UiProps), new FrameworkPropertyMetadata((Brush?)null));

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject element, bool value) => element.SetValue(HasErrorProperty, value);

    public static Brush? GetHoverBrush(DependencyObject element) => (Brush?)element.GetValue(HoverBrushProperty);

    public static void SetHoverBrush(DependencyObject element, Brush? value) => element.SetValue(HoverBrushProperty, value);

    public static Brush? GetPressedBrush(DependencyObject element) => (Brush?)element.GetValue(PressedBrushProperty);

    public static void SetPressedBrush(DependencyObject element, Brush? value) => element.SetValue(PressedBrushProperty, value);
}
