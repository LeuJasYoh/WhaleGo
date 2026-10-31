using System.Text.RegularExpressions;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>
/// Removes credentials/internal identifiers from anything the GUI logs or shows in tooltips/balloons.
/// Token plaintext and full session ids must never leave the kernel port.
/// </summary>
public static class TokenRedactor
{
    private static readonly Regex TokenPattern = new(@"wg_[A-Za-z0-9_-]{6,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SessionPattern = new(@"wg-[0-9a-fA-F]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = TokenPattern.Replace(text, "wg_***");
        return SessionPattern.Replace(redacted, "wg-***");
    }
}
