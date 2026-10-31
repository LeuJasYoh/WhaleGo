using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>go_launch allowlist / write-action semantics and go_stop session-only kill switch.</summary>
public sealed class LaunchStopTests
{
    [Fact]
    public async Task Launch_AllowlistApp_Succeeds()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "notepad" });
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));
        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-launch-v1", report["schema_version"]!.GetValue<string>());
        Assert.True(report["launched"]!.GetValue<bool>());
        Assert.False(report["activated"]!.GetValue<bool>());
        Assert.True(report["settled"]!.GetValue<bool>());
        Assert.Equal(4321, report["app"]!["pid"]!.GetValue<int>());

        Assert.Equal(1, environment.Launcher.Calls);
        Assert.Equal("notepad", environment.Launcher.LastRequest!.AppName);
        Assert.Null(environment.Launcher.LastRequest.AppId);
        Assert.Contains(environment.Audit.Events, auditEvent => auditEvent.Tool == "go_launch");
    }

    [Fact]
    public async Task Launch_ExistingApp_Activates()
    {
        await using var environment = await TestEnvironment.StartAsync();
        environment.Launcher.Result = new LaunchResult(false, null, TestData.FixtureAppId, FakeAppLauncher.SampleWindow, true, true);
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app_id"] = TestData.FixtureAppId });
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));
        var report = ToolResultsTest.Report(result);
        Assert.True(report["activated"]!.GetValue<bool>());
        Assert.Equal(TestData.FixtureWindowId, report["window"]!["window_id"]!.GetValue<string>());
        Assert.Equal(TestData.FixtureAppId, environment.Launcher.LastRequest!.AppId);
    }

    [Fact]
    public async Task Launch_NotInAllowlist_IsInvalidArgument()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "evil.exe" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(result));
        Assert.Equal(0, environment.Launcher.Calls);
    }

    [Fact]
    public async Task Launch_UnknownAppId_IsAppNotFound()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app_id"] = "win-exe:missing" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[APP_NOT_FOUND]:", ToolResultsTest.FirstText(result));
        Assert.Equal(0, environment.Launcher.Calls);
    }

    [Fact]
    public async Task Launch_Timeout_IsRetryNever()
    {
        await using var environment = await TestEnvironment.StartAsync();
        environment.Launcher.Result = new LaunchResult(false, 777, TestData.FixtureAppId, null, false, false);
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "notepad" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[LAUNCH_TIMEOUT]:", ToolResultsTest.FirstText(result));
        var error = ToolResultsTest.ErrorObject(result);
        Assert.Equal("LAUNCH_TIMEOUT", error["code"]!.GetValue<string>());
        Assert.Equal("never", error["retry"]!.GetValue<string>());
        Assert.Contains("禁止重试", error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Launch_Failure_IsLaunchFailed()
    {
        await using var environment = await TestEnvironment.StartAsync();
        environment.Launcher.Result = new LaunchResult(false, null, null, null, false, true);
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "notepad" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[LAUNCH_FAILED]:", ToolResultsTest.FirstText(result));
    }

    [Fact]
    public async Task Launch_AuditUnavailable_FailsClosed()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        environment.Audit.ThrowOnWrite = true;
        var result = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "notepad" });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[AUDIT_UNAVAILABLE]:", ToolResultsTest.FirstText(result));
        Assert.Equal(0, environment.Launcher.Calls);
    }

    [Fact]
    public async Task Stop_StopsOnlyCurrentSession()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var stateId = await environment.ObserveStateIdAsync(client);

        var result = await environment.CallAsync(client, "go_stop", new Dictionary<string, object?> { ["reason"] = "user asked" });
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));
        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-stop-v1", report["schema_version"]!.GetValue<string>());
        Assert.True(report["ok"]!.GetValue<bool>());
        Assert.True(report["stopped"]!.GetValue<bool>());
        Assert.Equal("session", report["scope"]!.GetValue<string>());
        Assert.Equal(1, report["sessions_affected"]!.GetValue<int>());

        var act = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(act.IsError == true);
        Assert.StartsWith("ERROR[SESSION_STOPPED]:", ToolResultsTest.FirstText(act));
        Assert.Equal("never", ToolResultsTest.ErrorObject(act)["retry"]!.GetValue<string>());

        // Observations stay available while the session is stopped.
        var observe = await environment.ObserveAsync(client);
        Assert.False(observe.IsError == true);

        var health = ToolResultsTest.Report(await environment.CallAsync(client, "go_health"));
        Assert.True(health["session_stopped"]!.GetValue<bool>());
        Assert.False(health["accepting_actions"]!.GetValue<bool>());
        Assert.False(health["stopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stop_ScopeAllFromMcp_IsRejected()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var result = await environment.CallAsync(client, "go_stop", new Dictionary<string, object?>
        {
            ["reason"] = "try global",
            ["scope"] = "all",
            ["session_id"] = TestEnvironment.SessionA,
        });
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(result));
        var health = ToolResultsTest.Report(await environment.CallAsync(client, "go_health"));
        Assert.False(health["stopped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stop_Global_BlocksAllSessionsButObserveWorks()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.Control.Stop("test");

        var act = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(act.IsError == true);
        Assert.StartsWith("ERROR[SESSION_STOPPED]:", ToolResultsTest.FirstText(act));

        var observe = await environment.ObserveAsync(client);
        Assert.False(observe.IsError == true);
        var health = ToolResultsTest.Report(await environment.CallAsync(client, "go_health"));
        Assert.True(health["stopped"]!.GetValue<bool>());
        Assert.False(health["accepting_actions"]!.GetValue<bool>());
    }
}