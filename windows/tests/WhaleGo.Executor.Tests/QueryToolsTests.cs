using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>go_apps / go_windows / target resolution and argument rejection.</summary>
public sealed class QueryToolsTests
{
    [Fact]
    public async Task GoApps_ListsAppsWithForegroundFirstAndRecommendedWindow()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_apps", new Dictionary<string, object?> { ["include_windows"] = true });
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));

        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-apps-v1", report["schema_version"]!.GetValue<string>());
        var apps = report["apps"]!.AsArray();
        Assert.Equal(2, apps.Count);
        var first = apps[0]!;
        Assert.True(first["foreground"]!.GetValue<bool>());
        Assert.Equal("WhaleGo Fixture", first["name"]!.GetValue<string>());
        Assert.Equal(TestData.FixtureWindowId, first["recommended_window_id"]!.GetValue<string>());
        Assert.Equal(1, first["windows_count"]!.GetValue<int>());
        Assert.Equal(TestData.FixtureAppId, first["app_id"]!.GetValue<string>());

        var second = apps[1]!;
        Assert.False(second["foreground"]!.GetValue<bool>());
        Assert.Equal(2, second["windows_count"]!.GetValue<int>());
        Assert.Equal(TestData.EditorWindowA, second["recommended_window_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GoWindows_ListsForegroundFirstAndFiltersByAppId()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();

        var all = ToolResultsTest.Report(await environment.CallAsync(client, "go_windows"));
        Assert.Equal("whalego-windows-v1", all["schema_version"]!.GetValue<string>());
        var windows = all["windows"]!.AsArray();
        Assert.Equal(3, windows.Count);
        Assert.True(windows[0]!["foreground"]!.GetValue<bool>());
        Assert.Equal(TestData.FixtureWindowId, windows[0]!["window_id"]!.GetValue<string>());

        var filtered = ToolResultsTest.Report(await environment.CallAsync(client, "go_windows", new Dictionary<string, object?> { ["app_id"] = TestData.EditorAppId }));
        var editorWindows = filtered["windows"]!.AsArray();
        Assert.Equal(2, editorWindows.Count);
        Assert.All(editorWindows, node => Assert.Equal(TestData.EditorAppId, node!["app_id"]!.GetValue<string>()));

        var empty = ToolResultsTest.Report(await environment.CallAsync(client, "go_windows", new Dictionary<string, object?> { ["app_id"] = "win-exe:doesnotexist" }));
        Assert.Empty(empty["windows"]!.AsArray());
    }

    [Fact]
    public async Task GoObserve_AmbiguousAppIdFailsClosed()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_observe", new Dictionary<string, object?> { ["app_id"] = TestData.EditorAppId });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[AMBIGUOUS_APP]:", ToolResultsTest.FirstText(result));
        var error = ToolResultsTest.ErrorObject(result);
        Assert.Equal("AMBIGUOUS_APP", error["code"]!.GetValue<string>());
        Assert.Equal("never", error["retry"]!.GetValue<string>());
    }

    [Fact]
    public async Task GoObserve_UnknownWindowFailsClosed()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_observe", new Dictionary<string, object?> { ["window_id"] = "w-0000000000009999" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[WINDOW_NOT_FOUND]:", ToolResultsTest.FirstText(result));
    }

    [Fact]
    public async Task GoObserve_AppIdWithSingleWindowResolves()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_observe", new Dictionary<string, object?> { ["app_id"] = TestData.FixtureAppId });
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));
        var report = ToolResultsTest.Report(result);
        Assert.Equal(TestData.FixtureWindowId, report["target"]!["window"]!["window_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownArgumentsAndToolsAreRejected()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();

        var badArg = await environment.CallAsync(client, "go_apps", new Dictionary<string, object?> { ["bogus"] = true });
        Assert.True(badArg.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(badArg));

        var unknownTool = await environment.CallAsync(client, "go_nope");
        Assert.True(unknownTool.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(unknownTool));

        var bothWindows = await environment.CallAsync(client, "go_observe", new Dictionary<string, object?>());
        Assert.True(bothWindows.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(bothWindows));
    }
}
