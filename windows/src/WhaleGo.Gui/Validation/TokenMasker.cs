using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.Validation;

/// <summary>
/// Token display rules (docs/GUI.md section 7.2): always masked, first 5 + last 4, never a "show plaintext"
/// affordance. The kernel normally supplies the already masked value; this helper is the defensive fallback and
/// keeps formatting in one place for tests.
/// </summary>
public static class TokenMasker
{
    public const string UnavailableText = "token 不可读（只能轮换）";

    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "wg_••••••••";
        }

        var text = token.Trim();
        if (LooksMasked(text))
        {
            return text;
        }

        if (text.Length <= 9)
        {
            return text.Length <= 5 ? text + "••••" : text[..5] + "••••";
        }

        return text[..5] + "••••" + text[^4..];
    }

    public static bool LooksMasked(string token) => token.Contains('•', StringComparison.Ordinal);

    public static string Display(TokenDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return UnavailableText;
        }

        if (!descriptor.Available && string.IsNullOrWhiteSpace(descriptor.Masked))
        {
            return UnavailableText;
        }

        return string.IsNullOrWhiteSpace(descriptor.Masked)
            ? "wg_••••••••"
            : Mask(descriptor.Masked);
    }
}
