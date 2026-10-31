using System.IO;
using System.Windows;
using System.Xml;

namespace WhaleGo.Gui.Theming;

/// <summary>
/// Checks that a theme dictionary defines every documented color token. Used both at runtime (theme switch must
/// fail closed and roll back) and by static tests (parse the XAML files via XmlReader; no WPF needed).
/// </summary>
public static class ThemeResourceValidator
{
    public static IReadOnlyList<string> FindMissingColorTokens(ResourceDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var missing = new List<string>();
        foreach (var key in ThemeTokens.RequiredColorKeys)
        {
            if (!dictionary.Contains(key))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    public static void EnsureComplete(ResourceDictionary dictionary)
    {
        var missing = FindMissingColorTokens(dictionary);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Theme dictionary is missing color tokens: " + string.Join(", ", missing));
        }
    }

    /// <summary>Parses a XAML file and returns the color token keys it defines (for static tests / diagnostics).</summary>
    public static IReadOnlyList<string> ReadColorTokenKeys(Stream xaml)
    {
        ArgumentNullException.ThrowIfNull(xaml);
        const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new List<string>();
        using var reader = XmlReader.Create(xaml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.LocalName, "Color", StringComparison.Ordinal))
            {
                continue;
            }

            var key = reader.GetAttribute("Key", xamlNamespace);
            if (!string.IsNullOrEmpty(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    public static IReadOnlyList<string> FindMissingColorTokens(Stream xaml)
    {
        var defined = new HashSet<string>(ReadColorTokenKeys(xaml), StringComparer.Ordinal);
        return ThemeTokens.RequiredColorKeys.Where(token => !defined.Contains(token)).ToList();
    }
}
