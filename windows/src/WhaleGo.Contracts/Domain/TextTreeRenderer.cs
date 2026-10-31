using System.Text;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

/// <summary>The single model-visible text-tree renderer (docs/PROTOCOL.md section 4).</summary>
public static class TextTreeRenderer
{
    public const int TitleMaxRunes = 64;
    public const int ValueMaxRunes = 48;
    public const string PrivacyHint = "注意：桌面截图包含用户隐私，未经用户明确要求不得转发。";
    public const string OkPrefix = "OK: ";

    public static string FirstLine(string windowId, string stateId, int elementCount, bool sparse, string screenshotStatus, bool includePrivacyHint)
    {
        var privacy = includePrivacyHint ? PrivacyHint : string.Empty;
        return $"{OkPrefix}{privacy}观察完成 window={windowId} state_id={stateId} elements={elementCount} sparse={(sparse ? "yes" : "no")} screenshot={screenshotStatus}";
    }

    public static string WindowHeader(WindowInfo window) =>
        $"[window {window.WindowId}] \"{TextUtil.EscapeJsonStyle(TextUtil.TruncateRunes(window.Title, TitleMaxRunes))}\" app={window.AppId} dpi={window.Dpi} onscreen={(window.IsOnscreen ? "true" : "false")}";

    public static string ElementLine(ElementNode element)
    {
        var builder = new StringBuilder();
        builder.Append(' ', element.Depth * 2);
        builder.Append('[').Append(element.Index).Append("] ");
        builder.Append(element.Kind);

        if (!string.IsNullOrEmpty(element.Title))
        {
            builder.Append(" \"").Append(TextUtil.EscapeJsonStyle(TextUtil.TruncateRunes(element.Title, TitleMaxRunes))).Append('\"');
        }

        if (element.Value is not null)
        {
            builder.Append(" value=\"").Append(TextUtil.EscapeJsonStyle(TextUtil.TruncateRunes(element.Value, ValueMaxRunes))).Append('\"');
        }

        if (element.Actions.Count > 0)
        {
            builder.Append(" actions=[").Append(string.Join(',', element.Actions)).Append(']');
        }

        if (!element.Enabled)
        {
            builder.Append(" disabled");
        }

        if (element.Selected)
        {
            builder.Append(" selected");
        }

        return builder.ToString();
    }

    public static string OmittedLine(OmissionInfo omitted)
    {
        if (omitted.OmittedCount <= 0)
        {
            return string.Empty;
        }

        var parts = omitted.ByKind
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");
        var reason = string.Equals(omitted.Reason, "budget", StringComparison.Ordinal) ? string.Empty : $" reason={omitted.Reason}";
        return $"… (omitted {omitted.OmittedCount} elements: {string.Join(", ", parts)}{reason})";
    }

    public static string SummaryLine(ObservationSnapshot snapshot)
    {
        var window = snapshot.Window;
        var frame = snapshot.Frame is null ? "none" : $"{snapshot.Frame.ImageWidth}x{snapshot.Frame.ImageHeight}";
        return $"window {window.WindowId} \"{TextUtil.EscapeJsonStyle(TextUtil.TruncateRunes(window.Title, TitleMaxRunes))}\" app={window.AppId} dpi={window.Dpi} onscreen={(window.IsOnscreen ? "true" : "false")} frame={frame}";
    }

    /// <summary>Render the full tree text (first line + header + elements + omitted + summary).</summary>
    public static string Render(
        ObservationSnapshot snapshot,
        IReadOnlyList<ElementNode> elements,
        OmissionInfo omitted,
        string screenshotStatus,
        bool includePrivacyHint)
    {
        var lines = new List<string>(elements.Count + 4)
        {
            FirstLine(snapshot.Window.WindowId, snapshot.StateId, elements.Count, omitted.SparseIndices, screenshotStatus, includePrivacyHint),
            WindowHeader(snapshot.Window),
        };
        foreach (var element in elements)
        {
            lines.Add(ElementLine(element));
        }

        var omittedLine = OmittedLine(omitted);
        if (omittedLine.Length > 0)
        {
            lines.Add(omittedLine);
        }

        lines.Add(SummaryLine(snapshot));
        return string.Join('\n', lines);
    }

    /// <summary>Approximate rendered length of one element line (used by the budget shaper).</summary>
    public static int EstimateElementLineLength(ElementNode element)
    {
        var length = (element.Depth * 2) + 4 + element.Kind.Length;
        if (!string.IsNullOrEmpty(element.Title))
        {
            length += 4 + Math.Min(TextUtil.RuneCount(element.Title), TitleMaxRunes);
        }
        if (element.Value is not null)
        {
            length += 9 + Math.Min(TextUtil.RuneCount(element.Value), ValueMaxRunes);
        }
        if (element.Actions.Count > 0)
        {
            length += 11 + element.Actions.Sum(a => a.Length + 1);
        }
        return length;
    }
}
