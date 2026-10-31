using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Executor.Application;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Logging;
using WhaleGo.Executor.Infrastructure.Secrets;
using WhaleGo.Executor.Tools;

namespace WhaleGo.Executor.Host;

/// <summary>
/// Composition root used by both Program.cs and integration tests. Startup order:
/// paths/config/secrets → kernel bind/routes (fast ready) → restore persisted global stop → hotkey/GUI (Program).
/// </summary>
public static class ExecutorHost
{
    public static async Task<ExecutorHostInstance> StartAsync(ExecutorHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var paths = ExecutorPaths.Resolve(options.ConfigPath, options.DataDirectory);
        paths.EnsureDirectories();
        var environment = options.Environment ?? CaptureEnvironment();
        var commandLine = MergeCommandLine(options);
        var initialLoad = ConfigLoader.Load(paths.ConfigPath, environment, commandLine, options.ConfigJson);
        var configService = new ConfigService(paths, environment, commandLine, options.ConfigJson, initialLoad);
        configService.WriteDefaultsFileIfMissing();
        var tokenService = new TokenService(configService, paths);
        tokenService.Bootstrap(options.AuthToken);

        var config = configService.Config;
        var validationErrors = ConfigValidation.Validate(config);
        if (validationErrors.Count > 0)
        {
            throw new ConfigValidationException(validationErrors);
        }

        var catalog = ContractToolCatalog.Load(options.ContractPath);
        var controlState = new ControlStateStore(paths.ControlStatePath);
        var runtime = new ExecutorRuntimeState
        {
            Headless = options.Headless,
            NoHotkey = options.NoHotkey,
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        foreach (var warning in initialLoad.Warnings)
        {
            runtime.AddWarning(warning);
        }

        var status = new StatusService();
        var listenAddress = options.ListenAddress ?? config.ListenAddress;
        var listenPort = options.ListenPort ?? config.ListenPort;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        ConfigureLogging(builder, options, configService);
        if (options.StartMcp)
        {
            builder.WebHost.UseUrls("http://" + listenAddress + ":" + listenPort.ToString(CultureInfo.InvariantCulture));
        }

        RegisterCoreServices(builder.Services, options, paths, configService, tokenService, catalog, controlState, runtime, status);
        PlatformComposition.RegisterPlatform(builder.Services, config);
        PlatformComposition.RegisterFallbacks(builder.Services);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(mcp =>
        {
            mcp.ServerInfo = new Implementation { Name = "whalego", Version = ProtocolInfo.ExecutorVersion };
            mcp.Handlers.ListToolsHandler = static (context, _) =>
                new ValueTask<ListToolsResult>(context.Services!.GetRequiredService<McpToolHandlers>().ListTools());
            mcp.Handlers.CallToolHandler = static (context, token) =>
                context.Services!.GetRequiredService<McpToolHandlers>().CallToolAsync(context, token);
        }).WithHttpTransport(transport => transport.Stateless = false);

        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var instance = new ExecutorHostInstance(app, paths, options, tokenService.TryGetPlaintextForCopy());
        try
        {
            if (options.StartMcp)
            {
                app.UseMiddleware<TransportAuthMiddleware>();
                app.MapGet("/healthz", HealthzEndpoint.HandleAsync);
                app.MapMcp("/mcp");
                runtime.ListenRequested = listenAddress + ":" + listenPort.ToString(CultureInfo.InvariantCulture);
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                var address = ResolveListeningAddress(app, listenAddress, listenPort);
                var actualPort = ParsePort(address, listenPort);
                runtime.McpListening = true;
                runtime.ListenActual = address;
                runtime.ActualPort = actualPort;
                instance.SetListening(address, actualPort);
            }

            var sessions = app.Services.GetRequiredService<SessionManager>();
            var stored = controlState.Load();
            if (stored.Stopped)
            {
                sessions.RestoreGlobalStop(stored.Reason, stored.SinceMs);
            }

            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void RegisterCoreServices(
        IServiceCollection services,
        ExecutorHostOptions options,
        ExecutorPaths paths,
        ConfigService config,
        TokenService tokens,
        ContractToolCatalog catalog,
        ControlStateStore controlState,
        ExecutorRuntimeState runtime,
        StatusService status)
    {
        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton(config);
        services.AddSingleton<IConfigStore>(config);
        services.AddSingleton(tokens);
        services.AddSingleton<ITokenService>(tokens);
        services.AddSingleton(catalog);
        services.AddSingleton(controlState);
        services.AddSingleton(runtime);
        services.AddSingleton(status);
        services.AddSingleton<IAuditSink>(_ => new JsonlAuditSink(config));
        services.AddSingleton<AuditService>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<ObservationService>();
        services.AddSingleton<ActionService>();
        services.AddSingleton<AppLaunchService>();
        services.AddSingleton<HealthService>();
        services.AddSingleton<SelfCheckService>();
        services.AddSingleton<ISelfCheck>(provider => provider.GetRequiredService<SelfCheckService>());
        services.AddSingleton<ControlService>();
        services.AddSingleton<IExecutorControl>(provider => provider.GetRequiredService<ControlService>());
        services.AddSingleton<McpToolHandlers>();
        services.AddSingleton<TransportRateLimiter>();
    }

    private static void ConfigureLogging(WebApplicationBuilder builder, ExecutorHostOptions options, ConfigService config)
    {
        builder.Logging.ClearProviders();
        if (options.QuietLogging)
        {
            return;
        }

        var level = ParseLogLevel(config.Config.LoggingLevel);
        builder.Logging.SetMinimumLevel(level);
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        try
        {
            builder.Logging.AddProvider(new FileLoggerProvider(config.EffectiveLoggingDir, level));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // file logging is best effort
        }
    }

    private static LogLevel ParseLogLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    private static string ResolveListeningAddress(WebApplication app, string listenAddress, int listenPort)
    {
        try
        {
            var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var first = feature?.Addresses.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // fall through to the requested address
        }

        return "http://" + listenAddress + ":" + listenPort.ToString(CultureInfo.InvariantCulture);
    }

    private static int ParsePort(string address, int fallback) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : fallback;

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                snapshot[key] = entry.Value?.ToString();
            }
        }
        return snapshot;
    }

    private static Dictionary<string, string?> MergeCommandLine(ExecutorHostOptions options)
    {
        var commandLine = options.CommandLineConfig is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(options.CommandLineConfig, StringComparer.Ordinal);
        if (options.ListenPort is { } port && port > 0)
        {
            // Port 0 means "ephemeral test port": used for binding only, never fed into config validation.
            commandLine["listen.port"] = port.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(options.ListenAddress))
        {
            commandLine["listen.address"] = options.ListenAddress;
        }

        return commandLine;
    }
}