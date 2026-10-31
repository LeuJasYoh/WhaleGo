namespace WhaleGo.Gui.Theming;

/// <summary>Concrete visual theme actually applied (HighContrast always wins over the preference).</summary>
public enum AppTheme
{
    Light = 0,
    Dark = 1,
    HighContrast = 2,
}

/// <summary>User preference persisted as ui.theme: system / light / dark.</summary>
public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public static class ThemePreferences
{
    public const string SystemConfigValue = "system";
    public const string LightConfigValue = "light";
    public const string DarkConfigValue = "dark";

    public static ThemePreference Parse(string? configValue) => configValue?.Trim().ToLowerInvariant() switch
    {
        LightConfigValue => ThemePreference.Light,
        DarkConfigValue => ThemePreference.Dark,
        _ => ThemePreference.System,
    };

    public static string ToConfigValue(this ThemePreference preference) => preference switch
    {
        ThemePreference.Light => LightConfigValue,
        ThemePreference.Dark => DarkConfigValue,
        _ => SystemConfigValue,
    };

    public static string DisplayName(this ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "浅色",
        ThemePreference.Dark => "深色",
        _ => "跟随系统",
    };

    public static string FileName(this AppTheme theme) => theme switch
    {
        AppTheme.Dark => "Dark",
        AppTheme.HighContrast => "HighContrast",
        _ => "Light",
    };

    /// <summary>Resolves a preference against the OS light/dark setting (high contrast is handled separately).</summary>
    public static AppTheme Resolve(ThemePreference preference, bool systemUsesDarkMode) => preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => systemUsesDarkMode ? AppTheme.Dark : AppTheme.Light,
    };
}
