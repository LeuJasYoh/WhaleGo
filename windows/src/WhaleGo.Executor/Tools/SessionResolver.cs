using System;
using System.Text.RegularExpressions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Tools;

/// <summary>
/// Business session resolution (PROTOCOL §3.3): hidden injected session_id → X-WhaleGo-Session-Id header →
/// MCP connection session id → "default". The model never sees or passes these values.
/// </summary>
public static partial class SessionResolver
{
    public const string DefaultSessionId = "default";
    public const string SessionHeader = "X-WhaleGo-Session-Id";

    [GeneratedRegex("^wg-[0-9a-f]{16}$")]
    private static partial Regex BusinessSessionPattern();

    public static bool IsValidBusinessSessionId(string? value) =>
        string.Equals(value, DefaultSessionId, StringComparison.Ordinal) ||
        (value is not null && BusinessSessionPattern().IsMatch(value));

    public static string Resolve(string? internalSessionId, string? headerSessionId, string? mcpSessionId)
    {
        if (IsValidBusinessSessionId(internalSessionId))
        {
            return internalSessionId!;
        }

        if (IsValidBusinessSessionId(headerSessionId))
        {
            return headerSessionId!;
        }

        if (!string.IsNullOrWhiteSpace(mcpSessionId))
        {
            return mcpSessionId.Length <= 64 ? mcpSessionId : "mcp-" + TextUtilShortSha(mcpSessionId);
        }

        return DefaultSessionId;
    }

    public static void ValidateInternalOrThrow(string? internalSessionId)
    {
        if (internalSessionId is null || IsValidBusinessSessionId(internalSessionId))
        {
            return;
        }

        throw Application.WhaleGoToolException.FromCode(
            ErrorCodes.InvalidArgument,
            message: "内部参数 session_id 非法（应由插件注入，模型不可传）。");
    }

    public static string Mask(string sessionId) =>
        sessionId.Length <= 12 ? sessionId : sessionId[..10] + "…";

    private static string TextUtilShortSha(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..8];
}
