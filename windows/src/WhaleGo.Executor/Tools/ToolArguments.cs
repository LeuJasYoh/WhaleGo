using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Application;

namespace WhaleGo.Executor.Tools;

/// <summary>Tolerant JSON argument reader: strips hidden internal args, rejects unknown top-level keys, typed getters.</summary>
public sealed class ToolArguments
{
    private static readonly string[] InternalNames = ["session_id", "request_id", "scope"];
    private readonly Dictionary<string, JsonElement> _values;

    private ToolArguments(Dictionary<string, JsonElement> values)
    {
        _values = values;
    }

    public string? InternalSessionId { get; private init; }

    public string? InternalRequestId { get; private init; }

    public string? InternalScope { get; private init; }

    public static ToolArguments Parse(IDictionary<string, JsonElement>? raw)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? sessionId = null;
        string? requestId = null;
        string? scope = null;
        if (raw is not null)
        {
            foreach (var pair in raw)
            {
                if (InternalNames.Contains(pair.Key, StringComparer.Ordinal))
                {
                    switch (pair.Key)
                    {
                        case "session_id":
                            sessionId = ReadInternalString(pair.Key, pair.Value);
                            break;
                        case "request_id":
                            requestId = ReadInternalString(pair.Key, pair.Value);
                            break;
                        case "scope":
                            scope = ReadInternalString(pair.Key, pair.Value);
                            break;
                    }
                    continue;
                }

                values[pair.Key] = pair.Value;
            }
        }

        return new ToolArguments(values)
        {
            InternalSessionId = sessionId,
            InternalRequestId = requestId,
            InternalScope = scope,
        };
    }

    /// <summary>Reject any top-level argument the tool does not declare (contract additionalProperties=false).</summary>
    public void EnsureOnly(params string[] allowed)
    {
        foreach (var key in _values.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "不支持的参数：" + key + "。");
            }
        }
    }

    public JsonElement? Get(string name) =>
        _values.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value
            : null;

    public string? GetString(string name)
    {
        var value = Get(name);
        return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
    }

    public bool? GetBool(string name)
    {
        var value = Get(name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public int? GetInt(string name)
    {
        var value = Get(name);
        if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    public double? GetDouble(string name)
    {
        var value = Get(name);
        if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetDouble(out var number))
        {
            return number;
        }

        return null;
    }

    public JsonElement? GetObject(string name)
    {
        var value = Get(name);
        return value is { ValueKind: JsonValueKind.Object } ? value : null;
    }

    private static string? ReadInternalString(string name, JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "内部参数 " + name + " 必须是字符串。");
        }

        return element.GetString();
    }
}