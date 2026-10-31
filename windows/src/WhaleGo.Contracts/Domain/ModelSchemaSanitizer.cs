using System.Text.Json.Nodes;

namespace WhaleGo.Contracts.Domain;

/// <summary>
/// Strips contract parameters marked "x-internal": true before exposing a tool schema to models.
/// Model-facing schemas must never contain session_id/request_id/scope (hard project rule).
/// </summary>
public static class ModelSchemaSanitizer
{
    public static JsonObject SanitizeTool(JsonObject tool)
    {
        var clone = (JsonObject)tool.DeepClone();
        if (clone["inputSchema"] is JsonObject inputSchema)
        {
            SanitizeSchema(inputSchema);
        }
        return clone;
    }

    public static JsonArray SanitizeTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var node in tools)
        {
            if (node is JsonObject tool)
            {
                result.Add(SanitizeTool(tool));
            }
        }
        return result;
    }

    /// <summary>Recursively remove x-internal properties and their names from required arrays.</summary>
    public static void SanitizeSchema(JsonObject schema)
    {
        if (schema["properties"] is JsonObject properties)
        {
            var internalNames = new List<string>();
            foreach (var key in properties.Select(kv => kv.Key).ToList())
            {
                if (properties[key] is JsonObject propertySchema &&
                    propertySchema["x-internal"] is JsonValue flag &&
                    flag.TryGetValue<bool>(out var isInternal) && isInternal)
                {
                    internalNames.Add(key);
                    properties.Remove(key);
                }
            }

            if (internalNames.Count > 0 && schema["required"] is JsonArray required)
            {
                foreach (var name in internalNames)
                {
                    for (var i = required.Count - 1; i >= 0; i--)
                    {
                        if (required[i] is JsonValue value && value.TryGetValue<string>(out var requiredName) &&
                            string.Equals(requiredName, name, StringComparison.Ordinal))
                        {
                            required.RemoveAt(i);
                        }
                    }
                }
            }

            foreach (var child in properties.Select(kv => kv.Value))
            {
                if (child is JsonObject childSchema)
                {
                    SanitizeSchema(childSchema);
                }
            }
        }

        foreach (var childName in new[] { "items", "additionalProperties", "if", "then", "else", "not", "contains" })
        {
            if (schema[childName] is JsonObject child)
            {
                SanitizeSchema(child);
            }
        }

        foreach (var arrayName in new[] { "oneOf", "anyOf", "allOf", "prefixItems" })
        {
            if (schema[arrayName] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject itemSchema)
                    {
                        SanitizeSchema(itemSchema);
                    }
                }
            }
        }
    }

    /// <summary>All forbidden parameter names present in a schema tree (used by contract tests; must be empty).</summary>
    public static IReadOnlyList<string> FindForbiddenParams(JsonNode? schema, IReadOnlyList<string> forbidden)
    {
        var found = new List<string>();
        Walk(schema, forbidden, found);
        return found;
    }

    private static void Walk(JsonNode? node, IReadOnlyList<string> forbidden, List<string> found)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties)
                {
                    foreach (var property in properties)
                    {
                        if (forbidden.Contains(property.Key, StringComparer.Ordinal))
                        {
                            found.Add(property.Key);
                        }
                        Walk(property.Value, forbidden, found);
                    }
                }
                foreach (var child in obj.Where(kv => kv.Key is not "properties"))
                {
                    Walk(child.Value, forbidden, found);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, forbidden, found);
                }
                break;
        }
    }
}
