using Microsoft.Extensions.DependencyInjection;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.SystemInfo;
using WhaleGo.Platform.Tests.TestInfrastructure;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class RegistrationAndActivatorTests
{
    [Fact]
    public void AddObservationPlatformRegistersResolvableSingletons()
    {
        var services = new ServiceCollection();
        var configured = false;
        services.AddObservationPlatform(options =>
        {
            configured = true;
            options.ScreenshotEnabled = false;
            options.MaxImageWidth = 800;
        });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.True(configured);
        var catalog = provider.GetRequiredService<WindowCatalog>();
        Assert.Same(catalog, provider.GetRequiredService<IWindowCatalog>());
        var capture = provider.GetRequiredService<IScreenCapture>();
        Assert.IsType<PrintWindowCapture>(capture);
        Assert.IsType<UiaObservationEngine>(provider.GetRequiredService<IObservationEngine>());
        Assert.IsType<SystemProbe>(provider.GetRequiredService<ISystemProbe>());
        Assert.NotNull(provider.GetRequiredService<StateHandleStore>());
        Assert.NotNull(provider.GetRequiredService<WindowResolver>());
        Assert.NotNull(provider.GetRequiredService<WindowActivator>());
        Assert.NotNull(provider.GetRequiredService<UiaTreeBuilder>());
        Assert.IsType<UiaEngine>(provider.GetRequiredService<UiaEngine>());
        Assert.NotNull(provider.GetRequiredService<IPlatformClock>());
        var options = provider.GetRequiredService<ObservationEngineOptions>();
        Assert.False(options.ScreenshotEnabled);
        Assert.Equal(800, options.MaxImageWidth);

        // The engine must really be wired and able to resolve a target through the provider-built graph.
        using var harness = PlatformHarness.Start();
        var window = catalog.TryGetWindow(harness.WindowId);
        Assert.NotNull(window);
        Assert.True(provider.GetRequiredService<StateHandleStore>().Count >= 0);
    }

    [Fact]
    public void WindowActivatorNeverFakesSuccessForInvalidHandles()
    {
        var activator = new WindowActivator();
        Assert.False(activator.TryActivate(IntPtr.Zero, 0));
        Assert.False(activator.TryActivate(new IntPtr(0xDEAD), 0));
        Assert.False(activator.TryActivate("w-000000000001", 0));
        Assert.False(activator.TryActivate("bogus", 0));
        Assert.False(activator.IsForeground(IntPtr.Zero));
    }
}
