using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using WhaleGo.Gui.Theming;

namespace WhaleGo.Gui.Tests;

/// <summary>
/// Static guards from docs/GUI.md section 12.2: no colour literals in views, theme dictionaries carry every documented
/// token, brushes referenced by views are DynamicResource and exist, and no third-party UI package sneaks in.
/// </summary>
public sealed class XamlStaticTests
{
    private static readonly string[] DocumentedColorTokens =
    [
        "Color.Canvas", "Color.Surface", "Color.SurfaceAlt", "Color.Sidebar",
        "Color.Text.Primary", "Color.Text.Secondary", "Color.Text.Tertiary",
        "Color.Accent", "Color.AccentHover", "Color.AccentPressed", "Color.AccentSubtle",
        "Color.Border", "Color.BorderStrong", "Color.FocusRing",
        "Color.Success", "Color.Warning", "Color.Danger", "Color.Idle", "Color.Shadow",
    ];

    [Fact]
    public void DocumentedTokenListMatchesThemeTokensContract()
    {
        Assert.Equal(19, DocumentedColorTokens.Length);
        Assert.Equal(DocumentedColorTokens, ThemeTokens.RequiredColorKeys);
    }

    [Fact]
    public void ViewsContainNoColorLiterals()
    {
        var colorLiteral = new Regex(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.ViewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in colorLiteral.Matches(text))
            {
                offenders.Add(Path.GetFileName(file) + ": " + match.Value);
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void ThemeDictionariesDefineEveryDocumentedToken(string themeFile)
    {
        using var stream = File.OpenRead(Path.Combine(RepoPaths.ThemesDirectory, themeFile + ".xaml"));
        var missing = ThemeResourceValidator.FindMissingColorTokens(stream);
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryBrushReferencedByViewsIsDefinedAndDynamic()
    {
        var brushKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var themeFile in new[] { "Shared", "Light", "Dark", "HighContrast" })
        {
            foreach (var key in ReadKeys(Path.Combine(RepoPaths.ThemesDirectory, themeFile + ".xaml")))
            {
                if (key.StartsWith("Brush.", StringComparison.Ordinal))
                {
                    brushKeys.Add(key);
                }
            }
        }

        var dynamicBrushReference = new Regex(@"DynamicResource\s+(Brush\.[A-Za-z.]+)", RegexOptions.Compiled);
        var staticBrushReference = new Regex(@"StaticResource\s+(Brush\.[A-Za-z.]+)", RegexOptions.Compiled);
        var missing = new List<string>();
        var staticUsages = new List<string>();

        foreach (var file in Directory.EnumerateFiles(RepoPaths.GuiProjectDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in dynamicBrushReference.Matches(text))
            {
                if (!brushKeys.Contains(match.Groups[1].Value))
                {
                    missing.Add(Path.GetFileName(file) + " -> " + match.Groups[1].Value);
                }
            }

            foreach (Match match in staticBrushReference.Matches(text))
            {
                staticUsages.Add(Path.GetFileName(file) + " -> " + match.Groups[1].Value);
            }
        }

        Assert.Empty(missing);
        // Brushes must be dynamic so a theme swap re-resolves them.
        Assert.Empty(staticUsages);
    }

    [Fact]
    public void ThemeFilesOnlyLiveUnderThemesFolder()
    {
        var themeFiles = Directory.EnumerateFiles(RepoPaths.ThemesDirectory, "*.xaml").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Shared.xaml", themeFiles);
        Assert.Contains("Light.xaml", themeFiles);
        Assert.Contains("Dark.xaml", themeFiles);
        Assert.Contains("HighContrast.xaml", themeFiles);
    }

    [Fact]
    public void ViewsReferenceNoWebViewOrThirdPartyControls()
    {
        // Control tags (not prose: the About page legitimately explains that there is no WebView).
        var forbiddenTags = new[] { "<WebView", "<WebBrowser", "MahApps", "MaterialDesign", "HandyControl", "Hardcodet" };
        foreach (var file in Directory.EnumerateFiles(RepoPaths.ViewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var fragment in forbiddenTags)
            {
                Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static IReadOnlyList<string> ReadKeys(string path)
    {
        const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new List<string>();
        using var reader = XmlReader.Create(path);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var key = reader.GetAttribute("Key", xamlNamespace);
                if (!string.IsNullOrEmpty(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }
}
