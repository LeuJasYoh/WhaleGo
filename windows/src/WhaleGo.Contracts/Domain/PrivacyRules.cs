using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

/// <summary>Privacy day-1 rules: password masking, sensitive-window matching, self-window exclusion.</summary>
public static class PrivacyRules
{
    public const string MaskedValue = "***";

    public static string? MaskValue(string? value, bool isPassword, bool maskEnabled) =>
        isPassword && maskEnabled ? MaskedValue : value;

    public static bool GlobMatch(string pattern, string? text)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        var candidate = text ?? string.Empty;
        var p = 0;
        var t = 0;
        var star = -1;
        var match = 0;
        while (t < candidate.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(candidate[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                match = t;
            }
            else if (star != -1)
            {
                p = star + 1;
                t = ++match;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    public static bool IsSensitiveWindow(string? title, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatch(pattern, title))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// WhaleGo's own processes are never enumerated/observed/captured. Test fixtures (WhaleGo.Fixtures.*)
    /// are deliberate automation targets and are NOT self windows.
    /// </summary>
    public static bool IsSelfProcess(string? processName) =>
        !string.IsNullOrEmpty(processName) &&
        (string.Equals(processName, "WhaleGo", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(processName, "WhaleGo.Executor", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(processName, "WhaleGo.Gui", StringComparison.OrdinalIgnoreCase) ||
         processName.StartsWith("WhaleGo.Executor.", StringComparison.OrdinalIgnoreCase) ||
         processName.StartsWith("WhaleGo.Gui.", StringComparison.OrdinalIgnoreCase));
}
