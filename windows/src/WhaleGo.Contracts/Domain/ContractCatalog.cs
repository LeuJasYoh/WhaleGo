using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhaleGo.Contracts.Domain;

/// <summary>Machine-readable tool contract (contracts/whalego.tools.v1.json) loaded without drift.</summary>
public sealed class ContractDocument
{
    public ContractDocument(JsonObject root)
    {
        Root = root;
        Contract = root["contract"]?.GetValue<string>() ?? string.Empty;
        ContractVersion = root["contract_version"]?.GetValue<string>() ?? string.Empty;
        ProtocolVersion = root["protocol_version"]?.GetValue<int>() ?? 0;
        Transport = root["transport"]?.GetValue<string>() ?? string.Empty;
        Tools = root["tools"] as JsonArray
            ?? throw new InvalidDataException("contract tools[] is missing");
        if (Tools.Count == 0)
        {
            throw new InvalidDataException("contract tools[] is empty");
        }
    }

    public JsonObject Root { get; }
    public string Contract { get; }
    public string ContractVersion { get; }
    public int ProtocolVersion { get; }
    public string Transport { get; }
    public JsonArray Tools { get; }

    public IReadOnlyList<string> ToolNames() => Tools
        .Select(node => node?["name"]?.GetValue<string>())
        .Where(name => !string.IsNullOrEmpty(name))
        .Select(name => name!)
        .ToList();

    public JsonObject? TryGetTool(string name) => Tools
        .OfType<JsonObject>()
        .FirstOrDefault(tool => string.Equals(tool["name"]?.GetValue<string>(), name, StringComparison.Ordinal));

    /// <summary>Tools with x-internal parameters stripped; this is exactly what tools/list must expose to models.</summary>
    public JsonArray ModelTools() => ModelSchemaSanitizer.SanitizeTools(Tools);

    /// <summary>Any forbidden parameter left in the model-facing schemas (must always be empty).</summary>
    public IReadOnlyList<string> FindForbiddenModelParams()
    {
        var forbidden = new[] { "session_id", "request_id", "scope" };
        var found = new List<string>();
        foreach (var tool in ModelTools().OfType<JsonObject>())
        {
            var schema = tool["inputSchema"];
            found.AddRange(ModelSchemaSanitizer.FindForbiddenParams(schema, forbidden));
        }
        return found;
    }
}

public static class ContractCatalog
{
    public static ContractDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"contract file not found: {Path.GetFullPath(path)}", path);
        }
        return Parse(File.ReadAllText(path));
    }

    public static ContractDocument Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"contract file is not valid JSON: {ex.Message}", ex);
        }

        return new ContractDocument(node as JsonObject ?? throw new InvalidDataException("contract root must be an object"));
    }
}
