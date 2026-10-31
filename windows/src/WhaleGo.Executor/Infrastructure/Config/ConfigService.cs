using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>
/// Effective configuration owner: merges sources, persists patches atomically (camelCase file keys, unknown keys preserved),
/// exposes source badges for the GUI and hot-applies runtime changes.
/// </summary>
public sealed class ConfigService : IConfigStore
{
    private static readonly string[] VisibleKeys =
    [
        ConfigKeys.ListenAddress, ConfigKeys.ListenPort, ConfigKeys.ListenHealthzRequireToken,
        ConfigKeys.PolicyReadonly, ConfigKeys.PolicyActRatePerMinute, ConfigKeys.PolicyControllerWaitMs,
        ConfigKeys.PolicyStateTtlSeconds, ConfigKeys.PolicyFrameTtlSeconds,
        ConfigKeys.LimitsMaxObserveElements, ConfigKeys.LimitsMaxObserveChars, ConfigKeys.LimitsMaxImageWidth,
        ConfigKeys.LimitsMaxImageBytes, ConfigKeys.LimitsToolTimeoutSeconds,
        ConfigKeys.ScreenshotEnabled, ConfigKeys.ScreenshotMode, ConfigKeys.ScreenshotDefaultInclude,
        ConfigKeys.PrivacyMaskPasswordFields, ConfigKeys.PrivacySensitiveWindows,
        ConfigKeys.AuditEnabled, ConfigKeys.AuditDir, ConfigKeys.AuditRequireDurableForActions,
        ConfigKeys.HotkeysEnabled, ConfigKeys.HotkeysEmergencyStop, ConfigKeys.HotkeysOpenSettings,
        ConfigKeys.UiNotificationsLevel, ConfigKeys.UiTheme,
        ConfigKeys.LoggingLevel, ConfigKeys.LoggingDir, ConfigKeys.LaunchAllowlist,
        ConfigKeys.LaunchEnforceAllowlist,
    ];

    private readonly object _lock = new();
    private readonly ExecutorPaths _paths;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly IReadOnlyDictionary<string, string?>? _commandLine;
    private readonly string? _extraJson;
    private ConfigLoadResult _load;
    private ExecutorConfig _config;
    private JsonObject? _fileRoot;

    public ConfigService(
        ExecutorPaths paths,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string?>? commandLine = null,
        string? extraJson = null,
        ConfigLoadResult? initial = null)
    {
        _paths = paths;
        _environment = environment;
        _commandLine = commandLine;
        _extraJson = extraJson;
        _load = initial ?? ConfigLoader.Load(paths.ConfigPath, environment, commandLine, extraJson);
        _fileRoot = _load.FileRoot;
        _config = ConfigValues.Build(_load);
    }

    public event Action? Changed;

    public ExecutorPaths Paths => _paths;

    public ExecutorConfig Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    public ConfigPatch Current
    {
        get
        {
            lock (_lock)
            {
                return new ConfigPatch(VisibleValuesLocked());
            }
        }
    }

    public IReadOnlyDictionary<string, string> Sources
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_load.Sources, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public string? SourceOf(string key)
    {
        lock (_lock)
        {
            return _load.Sources.TryGetValue(key, out var source) ? source : null;
        }
    }

    /// <summary>Effective audit directory (config may point outside the data root; %VARS% expanded).</summary>
    public string EffectiveAuditDir
    {
        get
        {
            lock (_lock)
            {
                return ResolveConfiguredDirectory(_config.AuditDirRaw, _paths.DefaultAuditDir);
            }
        }
    }

    public string EffectiveLoggingDir
    {
        get
        {
            lock (_lock)
            {
                return ResolveConfiguredDirectory(_config.LoggingDirRaw, _paths.LogsRoot);
            }
        }
    }

    /// <summary>Create the config file with documented defaults when missing (first run). No-op when it exists.</summary>
    public void WriteDefaultsFileIfMissing()
    {
        lock (_lock)
        {
            if (File.Exists(_paths.ConfigPath))
            {
                return;
            }

            Directory.CreateDirectory(_paths.ConfigRoot);
            _fileRoot = BuildDefaultRoot();
            WriteFileLocked();
            ReloadLocked();
        }

        RaiseChanged();
    }

    /// <summary>Replace the full hashed token list (bootstrap/rotate). Writes config.json before returning.</summary>
    public void SetTokens(IReadOnlyList<TokenConfigEntry> tokens)
    {
        lock (_lock)
        {
            EnsureFileRootLocked();
            var root = _fileRoot!;
            if (root["auth"] is not JsonObject auth)
            {
                auth = new JsonObject();
                root["auth"] = auth;
            }

            var array = new JsonArray();
            foreach (var token in tokens)
            {
                array.Add(new JsonObject
                {
                    ["name"] = token.Name,
                    ["sha256"] = token.Sha256,
                    ["createdAt"] = token.CreatedAt,
                });
            }

            auth["tokens"] = array;
            WriteFileLocked();
            ReloadLocked();
        }

        RaiseChanged();
    }

    public void Save(ConfigPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_lock)
        {
            EnsureFileRootLocked();
            foreach (var pair in patch.Values)
            {
                var key = pair.Key.Trim();
                if (key.Length == 0 || key == ConfigKeys.AuthTokens)
                {
                    continue;
                }

                SetPath(_fileRoot!, key, ToNode(pair.Value));
            }

            WriteFileLocked();
            ReloadLocked();
        }

        RaiseChanged();
    }

    public void Reload()
    {
        lock (_lock)
        {
            ReloadLocked();
        }

        RaiseChanged();
    }

    /// <summary>GUI patch values for all visible local keys (auth tokens are exposed via ITokenService only).</summary>
    private Dictionary<string, object?> VisibleValuesLocked()
    {
        var visible = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in VisibleKeys)
        {
            if (!_load.Values.TryGetValue(key, out var value) || value is null)
            {
                visible[key] = null;
                continue;
            }

            visible[key] = key switch
            {
                ConfigKeys.LaunchAllowlist => NormalizeLaunchEntries(value),
                ConfigKeys.PrivacySensitiveWindows => NormalizeStringList(value),
                _ => NormalizeScalar(value),
            };
        }
        return visible;
    }

    private static object? NormalizeScalar(object? value) => value switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            _ => element.ToString(),
        },
        List<object?> list => list.Select(NormalizeScalar).ToList(),
        IReadOnlyList<string> strings => strings.ToList(),
        _ => value,
    };

    private static List<string> NormalizeStringList(object value) => value switch
    {
        List<object?> list => list.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToList(),
        IReadOnlyList<string> strings => strings.ToList(),
        string single => [single],
        _ => [],
    };

    private static List<Dictionary<string, object?>> NormalizeLaunchEntries(object value)
    {
        var result = new List<Dictionary<string, object?>>();
        switch (value)
        {
            case List<object?> list:
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object?> entry)
                    {
                        result.Add(entry);
                    }
                }
                break;
            case IReadOnlyList<LaunchAllowlistEntry> entries:
                foreach (var entry in entries)
                {
                    result.Add(new Dictionary<string, object?> { ["name"] = entry.Name, ["path"] = entry.Path });
                }
                break;
        }
        return result;
    }

    /// <summary>Expand a configured directory; the documented %LOCALAPPDATA% macro follows the effective data root.</summary>
    private string ResolveConfiguredDirectory(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, ConfigDefaults.AuditDir, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return ExecutorPaths.ResolveDirectory(trimmed, fallback, _paths.DataRoot);
    }

    private void ReloadLocked()
    {
        var load = ConfigLoader.Load(_paths.ConfigPath, _environment, _commandLine, _extraJson);
        _load = load;
        _fileRoot = load.FileRoot ?? _fileRoot;
        _config = ConfigValues.Build(load);
    }

    private void EnsureFileRootLocked()
    {
        _fileRoot ??= BuildDefaultRoot();
    }

    private JsonObject BuildDefaultRoot()
    {
        var root = new JsonObject();
        foreach (var pair in ConfigDefaultsFactory.Create())
        {
            SetPath(root, pair.Key, ToNode(pair.Value));
        }

        if (root["auth"] is not JsonObject auth)
        {
            auth = new JsonObject();
            root["auth"] = auth;
        }

        auth["tokens"] = new JsonArray();
        return root;
    }

    private void WriteFileLocked()
    {
        var json = _fileRoot!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var text = json + Environment.NewLine;
        var temp = _paths.ConfigPath + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, _paths.ConfigPath, overwrite: true);
    }

    private static void SetPath(JsonObject root, string dottedKey, JsonNode? value)
    {
        var parts = dottedKey.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[parts[i]] = child;
            }

            current = child;
        }

        current[parts[^1]] = value;
    }

    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText());
            case bool boolean:
                return JsonValue.Create(boolean);
            case int integer:
                return JsonValue.Create(integer);
            case long integer:
                return JsonValue.Create(integer);
            case double number:
                return JsonValue.Create(number);
            case string text:
                return JsonValue.Create(text);
            case List<LaunchAllowlistEntry> entries:
            {
                var array = new JsonArray();
                foreach (var entry in entries)
                {
                    array.Add(new JsonObject { ["name"] = entry.Name, ["path"] = entry.Path });
                }
                return array;
            }
            case IReadOnlyList<LaunchAllowlistEntry> entries:
                return ToNode(entries.ToList());
            case List<TokenConfigEntry> tokens:
            {
                var array = new JsonArray();
                foreach (var token in tokens)
                {
                    array.Add(new JsonObject { ["name"] = token.Name, ["sha256"] = token.Sha256, ["createdAt"] = token.CreatedAt });
                }
                return array;
            }
            case List<string> strings:
            {
                var array = new JsonArray();
                foreach (var item in strings)
                {
                    array.Add(JsonValue.Create(item));
                }
                return array;
            }
            case IReadOnlyList<string> strings:
                return ToNode(strings.ToList());
            case List<object?> list:
            {
                var array = new JsonArray();
                foreach (var item in list)
                {
                    array.Add(ToNode(item));
                }
                return array;
            }
            case Dictionary<string, object?> dictionary:
            {
                var obj = new JsonObject();
                foreach (var pair in dictionary)
                {
                    obj[pair.Key] = ToNode(pair.Value);
                }
                return obj;
            }
            default:
                return JsonSerializer.SerializeToNode(value, ConfigJson.Options);
        }
    }

    private void RaiseChanged() => Changed?.Invoke();
}