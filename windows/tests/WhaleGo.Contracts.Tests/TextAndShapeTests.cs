using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Tests;

public sealed class TextAndShapeTests
{
    private static ElementNode Element(int index, string kind, string? title, string? value, int depth = 1, params string[] actions) =>
        new(index, kind, title, value, value is not null && value.Length > 48, "auto-" + index, actions, true, false,
            new RectI(0, 0, 10, 10), depth);

    private static ObservationSnapshot Snapshot(
        IReadOnlyList<ElementNode>? elements = null,
        FrameInfo? frame = null,
        OmissionInfo? omitted = null) =>
        new(ProtocolInfo.ObserveSchemaVersion, "wg-1", "st-8f21cd04",
            new AppInfo("win-exe:ab12cd34ef56", "Notepad", 1234, "C:/Windows/System32/notepad.exe", null, false, true, []),
            new WindowInfo("w-00000000006AF2", "win-exe:ab12cd34ef56", "无标题 - 记事本", "Notepad", 1234, true, false, true, true,
                new RectI(100, 100, 900, 700), "dwm", 120, 1793435400000),
            WindowResolution.Exact,
            elements ?? [],
            omitted ?? OmissionInfo.None,
            frame,
            [],
            1793435400000);

    [Fact]
    public void EscapeHandlesQuotesBackslashAndControlChars()
    {
        Assert.Equal("a\\\"b", TextUtil.EscapeJsonStyle("a\"b"));
        Assert.Equal("line1\\nline2", TextUtil.EscapeJsonStyle("line1\nline2"));
        Assert.Equal("tab\\there", TextUtil.EscapeJsonStyle("tab\there"));
        Assert.Equal("x\\u0001", TextUtil.EscapeJsonStyle("x\u0001"));
    }

    [Fact]
    public void TruncateNeverSplitsSurrogatePairs()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 100));
        var truncated = TextUtil.TruncateRunes(emoji, 64);
        Assert.Equal(64, TextUtil.RuneCount(truncated));
        Assert.True(IsWellFormedUtf16(truncated));
        Assert.EndsWith(TextUtil.Ellipsis, truncated);
    }

    private static bool IsWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void FirstLineHasOkPrefixAndPrivacyHintInFront()
    {
        var line = TextTreeRenderer.FirstLine("w-1", "st-8f21cd04", 3, true, "no(disabled)", includePrivacyHint: true);
        Assert.StartsWith("OK: ", line);
        Assert.Contains(TextTreeRenderer.PrivacyHint, line);
        Assert.Contains("window=w-1", line);
        Assert.Contains("state_id=st-8f21cd04", line);
        Assert.Contains("elements=3", line);
        Assert.Contains("sparse=yes", line);
        Assert.Contains("screenshot=no(disabled)", line);
    }

    [Fact]
    public void ElementLineContainsIndexKindValueAndFlags()
    {
        var line = TextTreeRenderer.ElementLine(Element(6, "button", "7", null, 2, "invoke"));
        Assert.Equal("    [6] button \"7\" actions=[invoke]", line);

        var edit = TextTreeRenderer.ElementLine(new ElementNode(3, "edit", "文本编辑器", "hello", false, null,
            ["set_value"], true, true, new RectI(0, 0, 1, 1), 1));
        Assert.Contains("value=\"hello\"", edit);
        Assert.Contains("actions=[set_value]", edit);
        Assert.Contains("selected", edit);

        var disabled = TextTreeRenderer.ElementLine(new ElementNode(9, "button", "x", null, false, null, [], false, false,
            new RectI(0, 0, 1, 1), 1));
        Assert.Contains("disabled", disabled);
    }

    [Fact]
    public void OmittedLineListsKinds()
    {
        var omitted = new OmissionInfo(42, new Dictionary<string, int> { ["container"] = 30, ["text"] = 12 }, "budget", true);
        var line = TextTreeRenderer.OmittedLine(omitted);
        Assert.StartsWith("… (omitted 42 elements:", line);
        Assert.Contains("container=30", line);
        Assert.Contains("text=12", line);
    }

    [Fact]
    public void RenderIncludesHeaderElementsOmittedSummary()
    {
        var elements = new[] { Element(6, "button", "7", null, 0, "invoke"), Element(7, "edit", "in", "v", 1) };
        var snapshot = Snapshot(elements, omitted: new OmissionInfo(2, new Dictionary<string, int> { ["text"] = 2 }, "budget", true));
        var text = TextTreeRenderer.Render(snapshot, elements, snapshot.Omitted, "no", includePrivacyHint: false);
        var lines = text.Split('\n');
        Assert.StartsWith("OK: 观察完成", lines[0]);
        Assert.StartsWith("[window w-00000000006AF2]", lines[1]);
        Assert.Contains("button", lines[2]);
        Assert.Contains("omitted 2 elements", text);
        Assert.StartsWith("window w-00000000006AF2", lines[^1]);
        Assert.Contains("frame=none", lines[^1]);
    }

    [Fact]
    public void ShaperCapsElementCountAndMarksSparse()
    {
        var elements = Enumerable.Range(0, 10).Select(i => Element(i, "button", "b" + i, null)).ToList();
        var shaped = ObservationShaper.Shape(elements, maxElements: 4, maxChars: 20000);
        Assert.Equal(4, shaped.Elements.Count);
        Assert.Equal(6, shaped.Omitted.OmittedCount);
        Assert.True(shaped.Omitted.SparseIndices);
        Assert.True(shaped.TreeBudgetExceeded);
        Assert.Equal(6, shaped.Omitted.ByKind["button"]);
    }

    [Fact]
    public void ShaperReportsNoOmissionWhenFits()
    {
        var elements = Enumerable.Range(0, 3).Select(i => Element(i, "button", "b", null)).ToList();
        var shaped = ObservationShaper.Shape(elements, maxElements: 300, maxChars: 20000);
        Assert.Equal(3, shaped.Elements.Count);
        Assert.Equal(0, shaped.Omitted.OmittedCount);
        Assert.False(shaped.Omitted.SparseIndices);
        Assert.False(shaped.TreeBudgetExceeded);
        Assert.Equal("none", shaped.Omitted.Reason);
    }

    [Fact]
    public void ShaperRespectsCharacterBudget()
    {
        var elements = Enumerable.Range(0, 200)
            .Select(i => Element(i, "text", new string('x', 60), new string('y', 48)))
            .ToList();
        var shaped = ObservationShaper.Shape(elements, maxElements: 300, maxChars: 1000);
        var rendered = string.Join('\n', shaped.Elements.Select(TextTreeRenderer.ElementLine));
        Assert.True(rendered.Length <= 1000);
        Assert.True(shaped.Omitted.OmittedCount > 0);
        Assert.True(shaped.TreeBudgetExceeded);
    }
}
