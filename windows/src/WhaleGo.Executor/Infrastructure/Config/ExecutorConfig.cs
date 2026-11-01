using System;
using System.Collections.Generic;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>One hashed bearer token recorded in config.json (plaintext lives only in secrets.json).</summary>
public sealed record TokenConfigEntry(string Name, string Sha256, string? CreatedAt);

/// <summary>Immutable typed snapshot of the effective configuration (all sources merged).</summary>
public sealed record ExecutorConfig(
    string ListenAddress,
    int ListenPort,
    bool HealthzRequireToken,
    IReadOnlyList<TokenConfigEntry> AuthTokens,
    bool Readonly,
    int ActRatePerMinute,
    int ControllerWaitMs,
    int StateTtlSeconds,
    int FrameTtlSeconds,
    int MaxObserveElements,
    int MaxObserveChars,
    int MaxImageWidth,
    long MaxImageBytes,
    int ToolTimeoutSeconds,
    bool ScreenshotEnabled,
    string ScreenshotMode,
    bool ScreenshotDefaultInclude,
    bool PrivacyMaskPasswordFields,
    IReadOnlyList<string> SensitiveWindows,
    bool AuditEnabled,
    string AuditDirRaw,
    bool AuditRequireDurableForActions,
    bool HotkeysEnabled,
    string? HotkeysEmergencyStop,
    string? HotkeysOpenSettings,
    string UiNotificationsLevel,
    string UiTheme,
    string LoggingLevel,
    string LoggingDirRaw)
{
    public string ListenEndpoint => ListenAddress + ":" + ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<string> errors)
        : base("配置无效：" + string.Join("；", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Path helpers for local config files (all local config JSON is camelCase).</summary>
public static class ConfigJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}