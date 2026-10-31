namespace WhaleGo.Gui.Theming;

/// <summary>The documented theme token contract (docs/GUI.md section 10.2). Static tests and the runtime
/// theme resource validator both use this list; adding a token requires updating the spec first.</summary>
public static class ThemeTokens
{
    public static readonly IReadOnlyList<string> RequiredColorKeys =
    [
        "Color.Canvas",
        "Color.Surface",
        "Color.SurfaceAlt",
        "Color.Sidebar",
        "Color.Text.Primary",
        "Color.Text.Secondary",
        "Color.Text.Tertiary",
        "Color.Accent",
        "Color.AccentHover",
        "Color.AccentPressed",
        "Color.AccentSubtle",
        "Color.Border",
        "Color.BorderStrong",
        "Color.FocusRing",
        "Color.Success",
        "Color.Warning",
        "Color.Danger",
        "Color.Idle",
        "Color.Shadow",
    ];

    /// <summary>Brushes derived from the color tokens in Themes/Shared.xaml (views only reference these).</summary>
    public static readonly IReadOnlyList<string> RequiredBrushKeys =
    [
        "Brush.Canvas",
        "Brush.Surface",
        "Brush.SurfaceAlt",
        "Brush.Sidebar",
        "Brush.Text.Primary",
        "Brush.Text.Secondary",
        "Brush.Text.Tertiary",
        "Brush.Accent",
        "Brush.AccentHover",
        "Brush.AccentPressed",
        "Brush.AccentSubtle",
        "Brush.Border",
        "Brush.BorderStrong",
        "Brush.FocusRing",
        "Brush.Success",
        "Brush.Warning",
        "Brush.Danger",
        "Brush.Idle",
        "Brush.Shadow",
    ];

    public static IReadOnlyList<string> AllWithDistinctCount => RequiredColorKeys;
}
