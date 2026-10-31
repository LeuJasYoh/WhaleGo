using System.Text;

namespace WhaleGo.Contracts.Domain;

/// <summary>Unicode-codepoint-safe text helpers for model-visible text (never split surrogate pairs/emoji).</summary>
public static class TextUtil
{
    public const string Ellipsis = "…";

    public static int RuneCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.EnumerateRunes().Count();

    /// <summary>Truncate to at most <paramref name="maxRunes"/> Unicode code points, appending an ellipsis when cut.</summary>
    public static string TruncateRunes(string? text, int maxRunes, bool addEllipsis = true)
    {
        if (string.IsNullOrEmpty(text) || maxRunes <= 0)
        {
            return string.Empty;
        }

        if (RuneCount(text) <= maxRunes)
        {
            return text;
        }

        var builder = new StringBuilder();
        var taken = 0;
        var limit = addEllipsis ? Math.Max(0, maxRunes - 1) : maxRunes;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken >= limit)
            {
                break;
            }
            builder.Append(rune.ToString());
            taken++;
        }

        if (addEllipsis)
        {
            builder.Append(Ellipsis);
        }
        return builder.ToString();
    }

    /// <summary>JSON-style escaping for tree lines: backslash, quote, newline, tab and other control chars as \uXXXX.</summary>
    public static string EscapeJsonStyle(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    public static string ShortSha256(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..8];
    }
}
