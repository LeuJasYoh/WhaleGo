using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhaleGo.Contracts.Domain;

/// <summary>Single serialization point for wire JSON (snake_case, Chinese kept readable, enums as snake_case strings).</summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options;

    static WireJson()
    {
        Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement(object? value) => JsonSerializer.SerializeToElement(value, Options);

    public static string SerializeIndented(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = true });
}
