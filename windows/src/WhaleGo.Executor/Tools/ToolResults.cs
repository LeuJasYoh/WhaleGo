using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Tools;

/// <summary>Single result-shaping point: content order, OK:/ERROR[CODE]: first line, image first, wire JSON via WireJson.</summary>
public static class ToolResults
{
    public const string ActSuccessLine = "OK: 动作已发送；state 已失效，下一步请先 go_observe。";

    public static CallToolResult QueryOk(string okLine, object report) => new()
    {
        IsError = false,
        Content = new List<ContentBlock>
        {
            new TextContentBlock { Text = okLine },
            new TextContentBlock { Text = WireJson.Serialize(report) },
        },
    };

    public static CallToolResult ObserveOk(byte[]? pngBytes, string okLine, object report, string treeText)
    {
        var content = new List<ContentBlock>(4);
        if (pngBytes is { Length: > 0 })
        {
            content.Add(ImageContentBlock.FromBytes(pngBytes, "image/png"));
        }

        content.Add(new TextContentBlock { Text = okLine });
        content.Add(new TextContentBlock { Text = WireJson.Serialize(report) });
        content.Add(new TextContentBlock { Text = treeText });
        return new CallToolResult { IsError = false, Content = content };
    }

    public static CallToolResult Error(GoError error)
    {
        var firstLine = "ERROR[" + error.Code + "]: " + error.Message;
        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = firstLine },
                new TextContentBlock { Text = WireJson.Serialize(error) },
            },
        };
    }

    public static CallToolResult InternalError(System.Exception exception)
    {
        var error = GoErrorFactory.FromCode(ErrorCodes.Internal);
        _ = exception;
        return Error(error);
    }

    public static string Serialize(object value) => WireJson.Serialize(value);

    public static JsonElement ToElement(object value) => WireJson.ToElement(value);
}
