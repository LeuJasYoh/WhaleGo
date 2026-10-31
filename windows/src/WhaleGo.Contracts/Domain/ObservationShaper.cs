using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

public sealed record ObservationShape(
    IReadOnlyList<ElementNode> Elements,
    OmissionInfo Omitted,
    bool TreeBudgetExceeded);

/// <summary>Pure cropping/omission rules; never touches the platform and never invents elements.</summary>
public static class ObservationShaper
{
    public const int MinimalCharsBudget = 200;

    public static ObservationShape Shape(IReadOnlyList<ElementNode> elements, int maxElements, int maxChars)
    {
        var elementCap = Math.Clamp(maxElements, 1, 2000);
        var charCap = Math.Max(MinimalCharsBudget, maxChars);

        var kept = new List<ElementNode>(Math.Min(elements.Count, elementCap));
        foreach (var element in elements)
        {
            if (kept.Count >= elementCap)
            {
                break;
            }
            kept.Add(element);
        }

        var omitted = new List<ElementNode>();
        for (var i = kept.Count; i < elements.Count; i++)
        {
            omitted.Add(elements[i]);
        }

        // Character budget: drop from the end (deepest/last elements) until the rendered text fits.
        var estimated = EstimateBaseLength(kept) + OmittedEstimate(omitted);
        while (kept.Count > 0 && estimated > charCap)
        {
            var last = kept[^1];
            kept.RemoveAt(kept.Count - 1);
            omitted.Add(last);
            estimated = EstimateBaseLength(kept) + OmittedEstimate(omitted);
        }

        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in omitted)
        {
            byKind[element.Kind] = byKind.TryGetValue(element.Kind, out var count) ? count + 1 : 1;
        }

        var reason = omitted.Count > 0 ? "budget" : "none";
        var omission = new OmissionInfo(omitted.Count, byKind, reason, omitted.Count > 0);
        return new ObservationShape(kept, omission, omitted.Count > 0);
    }

    private static int EstimateBaseLength(IReadOnlyList<ElementNode> elements) =>
        200 + elements.Sum(TextTreeRenderer.EstimateElementLineLength);

    private static int OmittedEstimate(IReadOnlyList<ElementNode> omitted) => omitted.Count > 0 ? 48 : 0;
}
