using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;

namespace WhaleGo.Gui.Tests;

public sealed class ThemeCoordinatorTests
{
    private static (ThemeCoordinator Coordinator, ThemeService Service, FakeThemeResources Resources, FakeExecutorControl Control) Create(
        Exception? persistFailure = null)
    {
        var resources = new FakeThemeResources();
        var system = new FakeSystemThemeProvider();
        var service = new ThemeService(resources, system, new FakeWindowTheme(), NullAppLogger.Instance);
        Assert.True(service.Initialize(ThemePreference.System));
        var control = new FakeExecutorControl { ThrowOnSetConfig = persistFailure };
        var coordinator = new ThemeCoordinator(
            service,
            new SafeControl(control, TimeSpan.FromSeconds(2), NullAppLogger.Instance),
            NullAppLogger.Instance);
        return (coordinator, service, resources, control);
    }

    [Fact]
    public async Task ThemePreferenceIsAppliedAndPersistedImmediately()
    {
        var (coordinator, service, resources, control) = Create();

        var ok = await coordinator.SetPreferenceAsync(ThemePreference.Dark);

        Assert.True(ok);
        Assert.Equal(ThemePreference.Dark, service.Preference);
        Assert.Equal(AppTheme.Dark, resources.Current);
        Assert.Equal(1, control.SetConfigCalls);
        Assert.NotNull(control.LastPatch);
        Assert.True(control.LastPatch!.Values.ContainsKey(WhaleGo.Contracts.Model.ConfigKeys.UiTheme));
        Assert.Equal("dark", control.LastPatch.Values[WhaleGo.Contracts.Model.ConfigKeys.UiTheme]);
    }

    [Fact]
    public async Task PersistenceFailureRollsThemeBack()
    {
        var (coordinator, service, resources, _) = Create(new InvalidOperationException("synthetic save failure"));

        var ok = await coordinator.SetPreferenceAsync(ThemePreference.Dark);

        Assert.False(ok);
        Assert.Equal(ThemePreference.System, service.Preference);
        Assert.Equal(AppTheme.Light, resources.Current);
    }

    [Fact]
    public async Task HighContrastWinsEvenWhenUserSelectsDark()
    {
        var (coordinator, service, resources, _) = Create();
        var systemField = typeof(ThemeService).GetField("_systemTheme", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var system = (FakeSystemThemeProvider)systemField!.GetValue(service)!;
        system.RaiseChanged(dark: false, highContrast: true);

        var ok = await coordinator.SetPreferenceAsync(ThemePreference.Dark);

        Assert.True(ok);
        Assert.Equal(ThemePreference.Dark, service.Preference);
        Assert.Equal(AppTheme.HighContrast, resources.Current);
    }
}
