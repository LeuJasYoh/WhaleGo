using System.Text.Json;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.Validation;

/// <summary>
/// Defensive reader for the loosely typed ConfigPatch.Values dictionary. The kernel (T4) may hand us plain CLR
/// values or JsonElement values; the GUI accepts all of them so the two sides stay
/// decoupled (Contracts is frozen and ConfigPatch is deliberately object?-typed).
/// </summary>
public static class ConfigValue
{
    public static bool? AsBool(object? value) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e when bool.TryParse(e.GetString(), out var parsed) => parsed,
        string s when bool.TryParse(s, out var parsedString) => parsedString,
        long l => l != 0,
        int i => i != 0,
        _ => null,
    };

    public static int? AsInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        short s => s,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var parsed) => parsed,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    public static long? AsLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.String } e when long.TryParse(e.GetString(), out var parsed) => parsed,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    public static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement e => e.ToString(),
        _ => value.ToString(),
    };

    public static IReadOnlyList<string> AsStringList(object? value)
    {
        switch (value)
        {
            case null:
                return [];
            case string single:
                return [single];
            case JsonElement { ValueKind: JsonValueKind.Array } element:
            {
                var result = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    var text = AsString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text);
                    }
                }

                return result;
            }

            case JsonElement { ValueKind: JsonValueKind.String } elementString:
                return [elementString.GetString() ?? string.Empty];
            case IEnumerable<string> strings:
                return strings.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
            case System.Collections.IEnumerable enumerable:
            {
                var result = new List<string>();
                foreach (var item in enumerable)
                {
                    var text = AsString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text);
                    }
                }

                return result;
            }

            default:
                return [];
        }
    }
}
