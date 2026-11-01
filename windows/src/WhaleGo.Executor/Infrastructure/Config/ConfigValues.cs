using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>Typed reads with defensive fallbacks (configuration is user-editable).</summary>
public static class ConfigValues
{
    public static ExecutorConfig Build(ConfigLoadResult load)
    {
        var values = load.Values;

        return new ExecutorConfig(
            ListenAddress: GetString(values, ConfigKeys.ListenAddress) ?? ConfigDefaults.ListenAddress,
            ListenPort: GetInt(values, ConfigKeys.ListenPort, ConfigDefaults.ListenPort),
            HealthzRequireToken: GetBool(values, ConfigKeys.ListenHealthzRequireToken, ConfigDefaults.ListenHealthzRequireToken),
            AuthTokens: GetTokenEntries(values),
            Readonly: GetBool(values, ConfigKeys.PolicyReadonly, ConfigDefaults.PolicyReadonly),
            ActRatePerMinute: GetInt(values, ConfigKeys.PolicyActRatePerMinute, ConfigDefaults.PolicyActRatePerMinute),
            ControllerWaitMs: GetInt(values, ConfigKeys.PolicyControllerWaitMs, ConfigDefaults.PolicyControllerWaitMs),
            StateTtlSeconds: GetInt(values, ConfigKeys.PolicyStateTtlSeconds, ConfigDefaults.PolicyStateTtlSeconds),
            FrameTtlSeconds: GetInt(values, ConfigKeys.PolicyFrameTtlSeconds, ConfigDefaults.PolicyFrameTtlSeconds),
            MaxObserveElements: GetInt(values, ConfigKeys.LimitsMaxObserveElements, ConfigDefaults.LimitsMaxObserveElements),
            MaxObserveChars: GetInt(values, ConfigKeys.LimitsMaxObserveChars, ConfigDefaults.LimitsMaxObserveChars),
            MaxImageWidth: GetInt(values, ConfigKeys.LimitsMaxImageWidth, ConfigDefaults.LimitsMaxImageWidth),
            MaxImageBytes: GetLong(values, ConfigKeys.LimitsMaxImageBytes, ConfigDefaults.LimitsMaxImageBytes),
            ToolTimeoutSeconds: GetInt(values, ConfigKeys.LimitsToolTimeoutSeconds, ConfigDefaults.LimitsToolTimeoutSeconds),
            ScreenshotEnabled: GetBool(values, ConfigKeys.ScreenshotEnabled, ConfigDefaults.ScreenshotEnabled),
            ScreenshotMode: GetString(values, ConfigKeys.ScreenshotMode) ?? ConfigDefaults.ScreenshotMode,
            ScreenshotDefaultInclude: GetBool(values, ConfigKeys.ScreenshotDefaultInclude, ConfigDefaults.ScreenshotDefaultInclude),
            PrivacyMaskPasswordFields: GetBool(values, ConfigKeys.PrivacyMaskPasswordFields, ConfigDefaults.PrivacyMaskPasswordFields),
            SensitiveWindows: GetStringList(values, ConfigKeys.PrivacySensitiveWindows, ConfigDefaults.SensitiveWindows),
            AuditEnabled: GetBool(values, ConfigKeys.AuditEnabled, ConfigDefaults.AuditEnabled),
            AuditDirRaw: GetString(values, ConfigKeys.AuditDir) ?? ConfigDefaults.AuditDir,
            AuditRequireDurableForActions: GetBool(values, ConfigKeys.AuditRequireDurableForActions, ConfigDefaults.AuditRequireDurableForActions),
            HotkeysEnabled: GetBool(values, ConfigKeys.HotkeysEnabled, ConfigDefaults.HotkeysEnabled),
            HotkeysEmergencyStop: GetString(values, ConfigKeys.HotkeysEmergencyStop) ?? ConfigDefaults.HotkeysEmergencyStop,
            HotkeysOpenSettings: GetString(values, ConfigKeys.HotkeysOpenSettings),
            UiNotificationsLevel: GetString(values, ConfigKeys.UiNotificationsLevel) ?? "important",
            UiTheme: GetString(values, ConfigKeys.UiTheme) ?? ConfigDefaults.UiTheme,
            LoggingLevel: GetString(values, ConfigKeys.LoggingLevel) ?? ConfigDefaults.LoggingLevel,
            LoggingDirRaw: GetString(values, ConfigKeys.LoggingDir) ?? ConfigDefaults.AuditDir);
    }

    public static object? GetRaw(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    public static bool GetBool(IReadOnlyDictionary<string, object?> values, string key, bool fallback) =>
        GetRaw(values, key) switch
        {
            bool boolean => boolean,
            long number => number != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => fallback,
        };

    public static int GetInt(IReadOnlyDictionary<string, object?> values, string key, int fallback) =>
        GetRaw(values, key) switch
        {
            long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
            bool boolean => boolean ? 1 : 0,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };

    public static long GetLong(IReadOnlyDictionary<string, object?> values, string key, long fallback) =>
        GetRaw(values, key) switch
        {
            long number => number,
            bool boolean => boolean ? 1 : 0,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };

    public static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        GetRaw(values, key) switch
        {
            string text => text,
            long number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => null,
        };

    public static IReadOnlyList<string> GetStringList(
        IReadOnlyDictionary<string, object?> values,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (GetRaw(values, key) is not List<object?> list)
        {
            return GetRaw(values, key) is string single && single.Length > 0 ? [single] : fallback;
        }

        return list.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToList();
    }

    private static IReadOnlyList<TokenConfigEntry> GetTokenEntries(IReadOnlyDictionary<string, object?> values)
    {
        if (GetRaw(values, ConfigKeys.AuthTokens) is not List<object?> list)
        {
            return [];
        }

        var tokens = new List<TokenConfigEntry>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> entry)
            {
                continue;
            }

            var name = entry.TryGetValue("name", out var nameValue) ? nameValue?.ToString() ?? string.Empty : string.Empty;
            var sha256 = entry.TryGetValue("sha256", out var hashValue) ? hashValue?.ToString() ?? string.Empty : string.Empty;
            if (name.Length == 0 || sha256.Length != 64)
            {
                continue;
            }

            var createdAt = entry.TryGetValue("createdAt", out var createdValue) ? createdValue?.ToString() : null;
            tokens.Add(new TokenConfigEntry(name, sha256.ToLowerInvariant(), createdAt));
        }
        return tokens;
    }
}
