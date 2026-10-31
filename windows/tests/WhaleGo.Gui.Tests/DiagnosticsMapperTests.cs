using WhaleGo.Contracts.Model;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Tests;

public sealed class DiagnosticsMapperTests
{
    [Theory]
    [InlineData("wg-8f21cd04", "…21cd04")]
    [InlineData("abc", "abc")]
    [InlineData("", "—")]
    [InlineData(null, "—")]
    public void SessionTagsAreTruncatedToSixCharacters(string? input, string expected) =>
        Assert.Equal(expected, DiagnosticsMapper.MaskSessionTag(input));

    [Fact]
    public void SessionTagNeverLeaksTheFullId()
    {
        var masked = DiagnosticsMapper.MaskSessionTag("wg-8f21cd04");
        Assert.DoesNotContain("wg-", masked);
        Assert.True(masked.Length <= 7);
    }

    [Fact]
    public void RequestNoteUsesMetadataOnly()
    {
        var request = new RecentRequest(
            1_760_000_000_000,
            "go_observe",
            "wg-8f21cd04",
            "observe",
            "OK",
            42,
            Elements: 12,
            HasScreenshot: true,
            LeaseWaitMs: 130);
        var note = DiagnosticsMapper.BuildRequestNote(request);
        Assert.Contains("elements=12", note);
        Assert.Contains("含截图", note);
        Assert.Contains("等租约 130ms", note);
        Assert.Contains("observe", note);
    }

    [Fact]
    public void SelfCheckReportIsPlainTextWithoutTokens()
    {
        var rows = new List<SelfCheckItemRow>
        {
            new(new SelfCheckItem("port", "端口", SelfCheckStatus.Ok, "8765 可用")),
            new(new SelfCheckItem("hotkey", "热键", SelfCheckStatus.Warn, "Ctrl+Alt+Shift+F12 被占用", "改热键")),
        };
        var report = DiagnosticsMapper.BuildSelfCheckReport(rows);
        Assert.Contains("WhaleGo 自检报告", report);
        Assert.Contains("端口", report);
        Assert.Contains("建议：改热键", report);
        Assert.DoesNotContain("wg_", report);
        Assert.DoesNotContain("wg-", report);
        // The report may *mention* that it contains no token, but must never contain a token value.
        Assert.DoesNotContain("wg_ab", report);
    }

    [Fact]
    public async Task DiagnosticsRefreshPopulatesRowsFromKernel()
    {
        var control = new FakeExecutorControl
        {
            Recent = [new RecentRequest(1_760_000_000_000, "go_act", "wg-8f21cd04", "click", "OK", 12)],
            Counters = new Dictionary<string, long>(StringComparer.Ordinal) { ["go_observe"] = 42, ["go_act"] = 7 },
        };
        var context = TestContexts.Create(control);
        context.ThemeSelfCheck = () => new SelfCheckItem("gui.theme", "主题资源", SelfCheckStatus.Ok, "token 齐全");
        var page = new DiagnosticsViewModel(context);

        await page.RefreshAsync();

        Assert.NotEmpty(page.StatusDetails);
        Assert.Single(page.RecentRequests);
        Assert.Equal("…21cd04", page.RecentRequests[0].SessionTag);
        Assert.Equal(2, page.ToolCounters.Count);
        Assert.NotNull(context.AuditDirectory);

        await page.RunSelfCheckAsync();

        Assert.Equal(2, page.SelfCheckItems.Count);
        Assert.Contains(page.SelfCheckItems, item => item.Id == "gui.theme");
        Assert.Contains("全部通过", page.SelfCheckSummary);
    }

    [Fact]
    public async Task CopyReportGetsPlainTextReport()
    {
        var context = TestContexts.Create();
        var page = new DiagnosticsViewModel(context);
        await page.RunSelfCheckAsync();
        page.CopyReportCommand.Execute(null);

        var clipboard = (FakeClipboardService)context.Clipboard;
        Assert.Single(clipboard.Texts);
        Assert.Contains("WhaleGo 自检报告", clipboard.Texts[0]);
    }
}

