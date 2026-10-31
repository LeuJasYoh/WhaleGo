using System.Windows;

namespace WhaleGo.Gui.Theming;

/// <summary>Applies the merged theme dictionary. Abstracted so ThemeService can be unit tested with fakes.</summary>
public interface IThemeResourceHost
{
    AppTheme Current { get; }

    /// <summary>Swap the theme dictionary; throws when the dictionary cannot be loaded/validated.</summary>
    void Apply(AppTheme theme);
}

/// <summary>
/// WPF implementation: replaces the single theme slot in Application.Resources.MergedDictionaries
/// (Shared.xaml stays in place), after validating every documented color token is present.
/// </summary>
public sealed class WpfThemeResourceHost : IThemeResourceHost
{
    private const string ThemeFolderMarker = "/Themes/";

    private readonly Application _application;

    public WpfThemeResourceHost(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
        Current = DetectCurrent();
    }

    public AppTheme Current { get; private set; }

    public void Apply(AppTheme theme)
    {
        var dictionary = Load(theme);
        ThemeResourceValidator.EnsureComplete(dictionary);
        var merged = _application.Resources.MergedDictionaries;
        var slot = FindThemeSlot(merged);
        if (slot < 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            merged[slot] = dictionary;
        }

        Current = theme;
    }

    public static ResourceDictionary Load(AppTheme theme) => new()
    {
        Source = new Uri(
            "pack://application:,,,/WhaleGo.Gui;component/Themes/" + theme.FileName() + ".xaml",
            UriKind.Absolute),
    };

    private AppTheme DetectCurrent()
    {
        var merged = _application.Resources.MergedDictionaries;
        var slot = FindThemeSlot(merged);
        if (slot < 0)
        {
            return AppTheme.Light;
        }

        var source = merged[slot].Source?.OriginalString ?? string.Empty;
        if (source.Contains("HighContrast", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.HighContrast;
        }

        return source.Contains("Dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.Light;
    }

    private static int FindThemeSlot(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString;
            if (string.IsNullOrEmpty(source) || !source.Contains(ThemeFolderMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.Contains("Shared", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return i;
        }

        return -1;
    }
}
