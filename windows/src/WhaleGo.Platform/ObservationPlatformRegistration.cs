using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.SystemInfo;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform;

/// <summary>
/// DI registration for the observation platform (consumed by the Executor composition root, T4):
/// <code>services.AddObservationPlatform(); services.AddInputPlatform();</code>
/// Registers: IPlatformClock, Redactor, UiaEngine, WindowCatalog (+IWindowCatalog), WindowResolver, WindowActivator,
/// UiaTreeBuilder, StateHandleStore, PrintWindowCapture (+IScreenCapture), ObservationEngineOptions,
/// UiaObservationEngine (+IObservationEngine), SystemProbe (+ISystemProbe).
/// All registrations use TryAddSingleton so the host can pre-register customized singletons (e.g. a Redactor with
/// configured privacy.sensitiveWindows or an ObservationEngineOptions built from config).
/// </summary>
public static class ObservationPlatformRegistration
{
    public static IServiceCollection AddObservationPlatform(this IServiceCollection services) =>
        AddObservationPlatform(services, null);

    public static IServiceCollection AddObservationPlatform(
        this IServiceCollection services,
        Action<ObservationEngineOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPlatformClock>(static _ => PlatformClock.Instance);
        services.TryAddSingleton<Redactor>(static _ => new Redactor());
        services.TryAddSingleton<UiaEngine>();
        services.TryAddSingleton<WindowCatalog>();
        services.TryAddSingleton<IWindowCatalog>(static sp => sp.GetRequiredService<WindowCatalog>());
        services.TryAddSingleton<WindowResolver>();
        services.TryAddSingleton<WindowActivator>();
        services.TryAddSingleton<UiaTreeBuilder>();
        services.TryAddSingleton<StateHandleStore>();
        services.TryAddSingleton<PrintWindowCapture>();
        services.TryAddSingleton<IScreenCapture>(static sp => sp.GetRequiredService<PrintWindowCapture>());

        var options = new ObservationEngineOptions();
        configureOptions?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<UiaObservationEngine>();
        services.TryAddSingleton<IObservationEngine>(static sp => sp.GetRequiredService<UiaObservationEngine>());
        services.TryAddSingleton<ISystemProbe>(static _ => new SystemProbe());

        return services;
    }
}
