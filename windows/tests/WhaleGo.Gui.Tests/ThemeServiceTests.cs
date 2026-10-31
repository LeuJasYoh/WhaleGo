using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;

namespace WhaleGo.Gui.Tests;

public sealed class ThemeServiceTests
{
    private static (ThemeService Service, FakeThemeResources Resources, FakeSystemThemeProvider System, FakeWindowTheme Window, List<AppTheme> Changes) Create(
        ThemePreference initial = ThemePreference.System)
    {
        var resources = new FakeThemeResources();
        var system = new FakeSystemThemeProvider();
        var window = new FakeWindowTheme();
        var service = new ThemeService(resources, system, window, NullAppLogger.Instance);
        var changes = new List<AppTheme>();
        service.ThemeChanged += (_, theme) => changes.Add(theme);
        Assert.True(service.Initialize(initial));
        return (service, resources, system, window, changes);
    }

    [Theory]
    [InlineData(ThemePreference.System, false, AppTheme.Light)]
    [InlineData(ThemePreference.System, true, AppTheme.Dark)]
    [InlineData(ThemePreference.Light, false, AppTheme.Light)]
    [InlineData(ThemePreference.Light, true, AppTheme.Light)]
    [InlineData(ThemePreference.Dark, false, AppTheme.Dark)]
    [InlineData(ThemePreference.Dark, true, AppTheme.Dark)]
    public void DecisionMatrixResolvesPreferenceAgainstSystem(ThemePreference preference, bool systemDark, AppTheme expected)
    {
        var resources = new FakeThemeResources();
        var system = new FakeSystemThemeProvider { IsSystemDarkMode = systemDark };
        var service = new ThemeService(resources, system, new FakeWindowTheme(), NullAppLogger.Instance);

        Assert.True(service.Initialize(preference));
        Assert.Equal(expected, service.EffectiveTheme);
        Assert.Equal(expected, resources.Current);
    }

    [Fact]
    public void SystemPreferenceFollowsOsThemeChanges()
    {
        var (service, resources, system, _, changes) = Create();
        Assert.Equal(AppTheme.Light, resources.Current);

        system.RaiseChanged(dark: true, highContrast: false);

        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.Equal(AppTheme.Dark, resources.Current);
        Assert.Contains(AppTheme.Dark, changes);
    }

    [Fact]
    public void ForcedThemeIgnoresOsThemeChanges()
    {
        var (service, resources, system, _, _) = Create(ThemePreference.Light);

        system.RaiseChanged(dark: true, highContrast: false);

        Assert.Equal(AppTheme.Light, service.EffectiveTheme);
        Assert.Equal(AppTheme.Light, resources.Current);
    }

    [Fact]
    public void HighContrastOverridesAnyPreferenceAndRestoresAfterwards()
    {
        var (service, resources, system, _, _) = Create(ThemePreference.Dark);
        Assert.Equal(AppTheme.Dark, resources.Current);

        system.RaiseChanged(dark: false, highContrast: true);
        Assert.Equal(AppTheme.HighContrast, service.EffectiveTheme);
        Assert.Equal(AppTheme.HighContrast, resources.Current);

        system.RaiseChanged(dark: false, highContrast: false);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.Equal(AppTheme.Dark, resources.Current);
    }

    [Fact]
    public void FailedApplyRollsBackPreferenceAndResources()
    {
        var (service, resources, _, _, _) = Create(ThemePreference.System);
        Assert.Equal(AppTheme.Light, resources.Current);
        resources.ThrowOn = AppTheme.Dark;

        var ok = service.SetPreference(ThemePreference.Dark);

        Assert.False(ok);
        Assert.Equal(ThemePreference.System, service.Preference);
        Assert.Equal(AppTheme.Light, resources.Current);
        Assert.Equal(AppTheme.Light, service.EffectiveTheme);
    }

    [Fact]
    public void SetPreferenceAppliesImmediately()
    {
        var (service, resources, _, _, changes) = Create();

        Assert.True(service.SetPreference(ThemePreference.Dark));

        Assert.Equal(ThemePreference.Dark, service.Preference);
        Assert.Equal(AppTheme.Dark, resources.Current);
        Assert.Contains(AppTheme.Dark, changes);
    }

    [Fact]
    public void DarkTitleBarFollowsEffectiveThemeAndRegisteredHandles()
    {
        var (service, _, system, window, _) = Create(ThemePreference.Dark);
        var hwnd = new IntPtr(0x1234);

        service.RegisterTitleBarHandle(hwnd);

        Assert.Contains((hwnd, true), window.Calls);

        system.RaiseChanged(dark: true, highContrast: true);

        Assert.Equal(AppTheme.HighContrast, service.EffectiveTheme);
        Assert.Equal((hwnd, false), window.Calls[^1]);
    }

    [Fact]
    public void UnregisteredHandleNoLongerReceivesUpdates()
    {
        var (service, _, system, window, _) = Create(ThemePreference.System);
        var hwnd = new IntPtr(0x5678);
        service.RegisterTitleBarHandle(hwnd);
        service.UnregisterTitleBarHandle(hwnd);
        var before = window.Calls.Count;

        system.RaiseChanged(dark: true, highContrast: false);

        Assert.Equal(before, window.Calls.Count);
    }

    [Fact]
    public void ThemePreferencesRoundTripConfigValues()
    {
        Assert.Equal(ThemePreference.System, ThemePreferences.Parse("system"));
        Assert.Equal(ThemePreference.Light, ThemePreferences.Parse("LIGHT"));
        Assert.Equal(ThemePreference.Dark, ThemePreferences.Parse("dark"));
        Assert.Equal(ThemePreference.System, ThemePreferences.Parse(null));
        Assert.Equal("system", ThemePreference.System.ToConfigValue());
        Assert.Equal("light", ThemePreference.Light.ToConfigValue());
        Assert.Equal("dark", ThemePreference.Dark.ToConfigValue());
    }
}
