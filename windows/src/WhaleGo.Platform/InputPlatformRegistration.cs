using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Platform.Apps;
using WhaleGo.Platform.Input;
using WhaleGo.Platform.System;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform;

/// <summary>
/// DI registration for the input/launch/theme/hotkey platform (implemented by T3).
/// Depends on the observation registration for <see cref="IWindowCatalog"/> (T2 AddObservationPlatform)
/// and for <see cref="StateHandleStore"/>; the TryAdd registrations below keep this method usable on its
/// own (they are skipped whenever the observation registration already added the same service).
/// </summary>
public static class InputPlatformRegistration
{
    /// <summary>Registers the input platform. go_launch resolves apps through the installed-app catalog.</summary>
    public static IServiceCollection AddInputPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Physical input primitives.
        services.TryAddSingleton<IInputLane, InputLane>();
        services.TryAddSingleton<UipiGuard>();
        services.TryAddSingleton<SendInputDispatcher>();
        services.TryAddSingleton<PostMessageDispatcher>();

        // Observation services consumed by input (skipped when AddObservationPlatform already registered them).
        services.TryAddSingleton<StateHandleStore>();
        services.TryAddSingleton<WindowActivator>();
        services.TryAddSingleton<UiaEngine>();

        // Dispatch chain.
        services.TryAddSingleton<IElementHandleResolver, StateHandleStoreResolver>();
        services.TryAddSingleton<ForegroundGuard>();
        services.TryAddSingleton<UiaHitTester>();
        services.TryAddSingleton<IUiHitTester>(provider => provider.GetRequiredService<UiaHitTester>());
        services.TryAddSingleton<IWindowPointResolver>(provider => provider.GetRequiredService<UiaHitTester>());
        services.TryAddSingleton<IElementActionDispatcher, UiaActionExecutor>();
        services.TryAddSingleton<ICoordinateActionDispatcher, CoordinateDispatcher>();

        // go_launch: no allowlist; desktop/start-menu shortcuts, execution aliases and App Paths are resolved on demand.
        services.TryAddSingleton<IInstalledAppCatalog, InstalledAppCatalog>();
        services.TryAddSingleton<IAppLauncher>(provider => new AppLauncher(
            provider.GetRequiredService<IWindowCatalog>(),
            provider.GetRequiredService<WindowActivator>()));

        // GUI ports (GUI never P/Invokes; D22).
        services.TryAddSingleton<IWindowTheme, DwmWindowTheme>();
        services.TryAddSingleton<IHotkeyRegistrar, HotkeyRegistrar>();

        return services;
    }
}
