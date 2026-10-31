using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>go_observe content ordering, three-switch screenshot AND, state/frame freshness.</summary>
public sealed class ObserveTests
{
    [Fact]
    public async Task Observe_NoScreenshot_ReturnsOkLineJsonAndTree()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var result = await environment.ObserveAsync(client);
        Assert.False(result.IsError == true);

        Assert.False(ToolResultsTest.HasImage(result));
        var texts = ToolResultsTest.TextBlocks(result);
        Assert.Equal(3, texts.Count);
        var first = texts[0].Text ?? string.Empty;
        Assert.StartsWith("OK:", first);
        Assert.Contains("state_id=st-", first);
        Assert.Contains("elements=4", first);
        Assert.Contains("sparse=no", first);
        Assert.Contains("screenshot=no", first);

        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-observe-v1", report["schema_version"]!.GetValue<string>());
        Assert.Equal(TestEnvironment.SessionA, report["session_id"]!.GetValue<string>());
        Assert.Matches("^st-[0-9a-f]{8}$", report["state_id"]!.GetValue<string>());
        Assert.Equal(TestData.FixtureWindowId, report["target"]!["window"]!["window_id"]!.GetValue<string>());
        Assert.Equal(TestData.FixtureAppId, report["target"]!["app"]!["app_id"]!.GetValue<string>());
        Assert.Equal(4, report["elements"]!.AsArray().Count);
        Assert.Null(report["frame"]);
        Assert.Empty(report["advisories"]!.AsArray());
        Assert.Equal(0, report["omitted"]!["count"]!.GetValue<int>());

        var tree = texts[2].Text ?? string.Empty;
        Assert.Contains("[window " + TestData.FixtureWindowId + "]", tree);
        Assert.Contains("[1] button \"Increment\" actions=[invoke]", tree);
        Assert.Contains("[2] edit \"Input\" value=\"hello\"", tree);
    }

    [Fact]
    public async Task Observe_WithScreenshot_PutsImageFirstAndFrameMetadata()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.ObserveAsync(client, screenshot: true);
        Assert.False(result.IsError == true);

        Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.True(ToolResultsTest.HasImage(result));
        var texts = ToolResultsTest.TextBlocks(result);
        Assert.Equal(3, texts.Count);
        Assert.StartsWith("OK:", texts[0].Text);
        Assert.Contains("screenshot=yes", texts[0].Text);

        var report = ToolResultsTest.Report(result);
        var frame = report["frame"]!;
        Assert.Equal(800, frame["image_width"]!.GetValue<int>());
        Assert.Equal(600, frame["image_height"]!.GetValue<int>());
        Assert.True(frame["actionable"]!.GetValue<bool>());
        Assert.Equal("client", frame["capture_region"]!.GetValue<string>());
        Assert.Equal("printwindow", frame["capture_method"]!.GetValue<string>());
        Assert.Equal("nonblank_ok", frame["signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Observe_ScreenshotDisabled_ReturnsTreeWithAdvisory()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"screenshot\":{\"enabled\":false}}");
        var client = await environment.ConnectAsync();
        var result = await environment.ObserveAsync(client, screenshot: true);
        Assert.False(result.IsError == true);
        Assert.False(ToolResultsTest.HasImage(result));

        var texts = ToolResultsTest.TextBlocks(result);
        Assert.Contains("screenshot=no(disabled)", texts[0].Text);
        var report = ToolResultsTest.Report(result);
        var advisories = report["advisories"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("screenshot_disabled", advisories);
        Assert.Null(report["frame"]);
    }

    [Fact]
    public async Task Observe_SensitiveWindowSuppressesScreenshot()
    {
        await using var environment = await TestEnvironment.StartAsync(configurePlatform: platform => platform.Capture.Suppressed = true);
        var client = await environment.ConnectAsync();
        var result = await environment.ObserveAsync(client, screenshot: true);
        Assert.False(result.IsError == true);
        Assert.False(ToolResultsTest.HasImage(result));
        Assert.Contains("screenshot=no(suppressed)", ToolResultsTest.FirstText(result));
        var advisories = ToolResultsTest.Report(result)["advisories"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("screenshot_suppressed", advisories);
    }

    [Fact]
    public async Task Observe_MaxValueCharsTruncatesElementValues()
    {
        await using var environment = await TestEnvironment.StartAsync(configurePlatform: platform =>
            platform.Engine.Elements[2] = platform.Engine.Elements[2] with { Value = new string('x', 200) });
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_observe", new Dictionary<string, object?>
        {
            ["window_id"] = TestData.FixtureWindowId,
            ["max_value_chars"] = 10,
        });
        Assert.False(result.IsError == true);
        var elements = ToolResultsTest.Report(result)["elements"]!.AsArray();
        var edit = elements.First(node => node!["index"]!.GetValue<int>() == 2)!;
        var value = edit["value"]!.GetValue<string>();
        Assert.Equal(10, value.EnumerateRunes().Count());
        Assert.True(edit["value_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Observe_AfterAct_SameStateIsStale()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);

        var accepted = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(accepted.IsError == true);

        var stale = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(stale.IsError == true);
        Assert.StartsWith("ERROR[STALE_STATE]:", ToolResultsTest.FirstText(stale));
        var error = ToolResultsTest.ErrorObject(stale);
        Assert.Equal("reobserve", error["retry"]!.GetValue<string>());
        Assert.Equal("not_sent", error["dispatch_status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Observe_StateTtlExpires()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.Clock.Advance(121_000);

        var stale = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(stale.IsError == true);
        Assert.StartsWith("ERROR[STALE_STATE]:", ToolResultsTest.FirstText(stale));
    }

    [Fact]
    public async Task Observe_NoScreenshotKeepsSameWindowFrameForCoordinateActions()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        await environment.ObserveAsync(client, screenshot: true);
        var second = await environment.ObserveAsync(client, screenshot: false);
        Assert.False(second.IsError == true);
        Assert.False(ToolResultsTest.HasImage(second));

        var accepted = await environment.ActCoordinateAsync(client, "click", 10, 10, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(accepted.IsError == true);
        Assert.Single(environment.CoordinateDispatcher.Calls);
    }

    [Fact]
    public async Task Observe_FrameTtlExpiresForCoordinateActions()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        await environment.ObserveAsync(client, screenshot: true);
        environment.Clock.Advance(61_000);

        var stale = await environment.ActCoordinateAsync(client, "click", 10, 10, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(stale.IsError == true);
        Assert.StartsWith("ERROR[STALE_STATE]:", ToolResultsTest.FirstText(stale));
    }

    [Fact]
    public async Task Observe_HiddenSessionIdBindsSession()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.ObserveAsync(client, hiddenSession: TestEnvironment.SessionC);
        Assert.False(result.IsError == true);
        Assert.Equal(TestEnvironment.SessionC, ToolResultsTest.Report(result)["session_id"]!.GetValue<string>());
    }
}