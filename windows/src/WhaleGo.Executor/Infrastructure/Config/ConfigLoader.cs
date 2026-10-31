using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>Effective values plus per-key source badges (default / file / environment / commandLine).</summary>
public sealed record ConfigLoadResult(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyDictionary<string, string> Sources,
    JsonObject? FileRoot,
    IReadOnlyList<string> Warnings)
{
    public static ConfigLoadResult Empty() => new(
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        null,
        []);
}

/// <summary>
/// Precedence: appsettings.json &lt; %APPDATA% config.json &lt; extra JSON override &lt; WHALEGO_* environment &lt; command line.
/// The config file is the only persisted source; everything else is a live overlay.
/// </summary>
public static class ConfigLoader
{
    public static ConfigLoadResult Load(
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string?>? commandLine = null,
        string? extraJson = null)
    {
        // Structural lookup is case-insensitive so camelCase config keys (policy.actRatePerMinute) and their
        // env equivalents (WHALEGO_POLICY__ACTRATEPERMINUTE) resolve to the same setting.
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var pair in ConfigDefaultsFactory.Create())
        {
            values[pair.Key] = pair.Value;
            sources[pair.Key] = ConfigSourceKinds.Default;
        }

        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(appSettingsPath))
        {
            TryApplyJsonFile(appSettingsPath, values, sources, warnings);
        }

        JsonObject? fileRoot = null;
        if (File.Exists(configPath))
        {
            fileRoot = TryReadRoot(configPath, warnings);
            if (fileRoot is not null)
            {
                ApplyNested(fileRoot, values, sources, ConfigSourceKinds.File);
            }
        }

        if (!string.IsNullOrWhiteSpace(extraJson))
        {
            try
            {
                if (JsonNode.Parse(extraJson) is JsonObject extraRoot)
                {
                    ApplyNested(extraRoot, values, sources, ConfigSourceKinds.File);
                }
                else
                {
                    warnings.Add("额外配置 JSON 不是对象，已忽略。");
                }
            }
            catch (JsonException ex)
            {
                warnings.Add("额外配置 JSON 解析失败：" + ex.Message);
            }
        }

        ApplyEnvironment(environment, values, sources);
        if (commandLine is not null)
        {
            foreach (var pair in commandLine)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    continue;
                }

                values[NormalizeKey(pair.Key)] = pair.Value;
                sources[NormalizeKey(pair.Key)] = ConfigSourceKinds.CommandLine;
            }
        }

        return new ConfigLoadResult(values, sources, fileRoot, warnings);
    }

    private static string NormalizeKey(string key) => key.Trim();

    private static void ApplyEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        IDictionary<string, object?> values,
        IDictionary<string, string> sources)
    {
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key is null || pair.Value is null)
            {
                continue;
            }

            if (!pair.Key.StartsWith("WHALEGO_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = pair.Key["WHALEGO_".Length..];
            if (suffix.ToUpperInvariant() is "DATA_DIR" or "HEADLESS" or "CONFIG" or "CONTRACT_PATH" or "SELF_CHECK")
            {
                continue;
            }

            var key = NormalizeKey(suffix.Replace("__", "."));
            values[key] = ParseEnvironmentValue(pair.Value);
            sources[key] = ConfigSourceKinds.Environment;
        }
    }

    private static object? ParseEnvironmentValue(string raw)
    {
        var text = raw.Trim();
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        if ((text.StartsWith('[') && text.EndsWith(']')) || (text.StartsWith('{') && text.EndsWith('}')))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return Normalize(document.RootElement);
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        return raw;
    }

    private static JsonObject? TryReadRoot(string path, List<string> warnings)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (node is JsonObject obj)
            {
                return obj;
            }
            warnings.Add("配置文件根不是 JSON 对象：" + path);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("配置文件读取失败：" + ex.Message);
            return null;
        }
    }

    private static void TryApplyJsonFile(
        string path,
        IDictionary<string, object?> values,
        IDictionary<string, string> sources,
        List<string> warnings)
    {
        var root = TryReadRoot(path, warnings);
        if (root is not null)
        {
            ApplyNested(root, values, sources, ConfigSourceKinds.File);
        }
    }

    private static void ApplyNested(
        JsonObject root,
        IDictionary<string, object?> values,
        IDictionary<string, string> sources,
        string source)
    {
        Walk(root, string.Empty, (key, value) =>
        {
            var normalized = NormalizeKey(key);
            values[normalized] = value;
            sources[normalized] = source;
        });
    }

    private static void Walk(JsonNode? node, string prefix, Action<string, object?> visit)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var pair in obj)
        {
            var key = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;
            switch (pair.Value)
            {
                case JsonObject child:
                    Walk(child, key, visit);
                    break;
                case null:
                    visit(key, null);
                    break;
                default:
                    visit(key, Normalize(pair.Value));
                    break;
            }
        }
    }

    private static object? Normalize(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return obj.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value), StringComparer.Ordinal);
        }

        if (node is JsonArray array)
        {
            return array.Select(Normalize).ToList();
        }

        var value = node.AsValue();
        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    private static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(pair => pair.Name, pair => Normalize(pair.Value), StringComparer.Ordinal),
        _ => null,
    };
}