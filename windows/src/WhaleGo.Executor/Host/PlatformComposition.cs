using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Platform;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Executor.Host;

/// <summary>Composition root's only platform touch point (ARCHITECTURE §3.1): real registrations first, benign fallbacks after.</summary>
public static class PlatformComposition
{
    public static void RegisterPlatform(IServiceCollection services, ExecutorConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Privacy policy holds privacy.sensitiveWindows + privacy.maskPasswordFields (pre-registered; TryAdd keeps it).
        services.TryAddSingleton(_ => new Redactor(config.SensitiveWindows, config.PrivacyMaskPasswordFields));

        // Pre-register the configured engine options: AddObservationPlatform's TryAddSingleton keeps this instance.
        services.TryAddSingleton(new ObservationEngineOptions
        {
            DefaultMaxElements = config.MaxObserveElements,
            MaxObserveChars = config.MaxObserveChars,
            ScreenshotEnabled = config.ScreenshotEnabled,
            ScreenshotMode = config.ScreenshotMode,
            MaxImageWidth = config.MaxImageWidth,
            MaxImageBytes = config.MaxImageBytes,
            SuppressSensitiveWindows = true,
        });
        services.AddObservationPlatform();

        services.AddInputPlatform();
    }

    /// <summary>TryAdd-only fallbacks: they never override a real T2/T3 registration (order-insensitive).</summary>
    public static void RegisterFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<IPlatformClock>(_ => PlatformClock.Instance);
        services.TryAddSingleton<IWindowCatalog, FallbackWindowCatalog>();
        services.TryAddSingleton<IObservationEngine, FallbackObservationEngine>();
        services.TryAddSingleton<IScreenCapture, FallbackScreenCapture>();
        services.TryAddSingleton<IUiHitTester, FallbackUiHitTester>();
        services.TryAddSingleton<IAppLauncher, FallbackAppLauncher>();
        services.TryAddSingleton<IInstalledAppCatalog, FallbackInstalledAppCatalog>();
        services.TryAddSingleton<IElementActionDispatcher, FallbackElementActionDispatcher>();
        services.TryAddSingleton<ICoordinateActionDispatcher, FallbackCoordinateActionDispatcher>();
        services.TryAddSingleton<IInputLane, FallbackInputLane>();
        services.TryAddSingleton<ISystemProbe, FallbackSystemProbe>();
        services.TryAddSingleton<IWindowTheme, FallbackWindowTheme>();
        services.TryAddSingleton<IHotkeyRegistrar, FallbackHotkeyRegistrar>();
    }
}
