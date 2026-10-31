using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace WhaleGo.Executor.Host;

/// <summary>Composition options for <see cref="ExecutorHost"/>; tests use this to isolate temp data dirs, fake services and an ephemeral port.</summary>
public sealed class ExecutorHostOptions
{
    public bool Headless { get; init; } = true;

    public bool NoHotkey { get; init; } = true;

    /// <summary>Start the Kestrel/MCP listener (false for --self-check / --diagnostics-dump).</summary>
    public bool StartMcp { get; init; } = true;

    /// <summary>Explicit config file path (--config); the data root defaults to its directory unless overridden.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Runtime data root override (WHALEGO_DATA_DIR / test isolation).</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Additional JSON merged above the config file (tests).</summary>
    public string? ConfigJson { get; init; }

    /// <summary>Environment snapshot; tests pass an empty dictionary to avoid ambient WHALEGO_* overrides.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Command-line config overrides (--port etc.).</summary>
    public IReadOnlyDictionary<string, string?>? CommandLineConfig { get; init; }

    public int? ListenPort { get; init; }

    public string? ListenAddress { get; init; }

    /// <summary>Pre-provisioned plaintext token (tests); also written to config hash + secrets.json.</summary>
    public string? AuthToken { get; init; }

    public string? ContractPath { get; init; }

    public bool QuietLogging { get; init; } = true;

    /// <summary>Extra DI registrations applied last (test fakes win over the real platform/framework services).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

}