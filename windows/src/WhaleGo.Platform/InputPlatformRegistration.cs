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
    /// <summary>Registers the input platform with an empty launch allowlist (go_launch will reject every name).</summary>
    public static IServiceCollection AddInputPlatform(this IServiceCollection services) =>
        AddInputPlatform(services, LaunchAllowlistOptions.Empty);

    /// <summary>
    /// Registers the input platform. T4 passes the config-derived launch allowlist through
    /// <paramref name="options"/> (launch.allowlist); no Contracts change is required.
    /// </summary>
    public static IServiceCollection AddInputPlatform(this IServiceCollection services, LaunchAllowlistOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

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

        // go_launch (allowlist is fixed at DI time).
        services.TryAddSingleton<IAppLauncher>(provider => new AppLauncher(
            options.Entries,
            provider.GetRequiredService<IWindowCatalog>(),
            provider.GetRequiredService<WindowActivator>()));

        // GUI ports (GUI never P/Invokes; D22).
        services.TryAddSingleton<IWindowTheme, DwmWindowTheme>();
        services.TryAddSingleton<IHotkeyRegistrar, HotkeyRegistrar>();

        return services;
    }
}