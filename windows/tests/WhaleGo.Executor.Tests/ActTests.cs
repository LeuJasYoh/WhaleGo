using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>go_act single channel: receipt three states, freshness, policy, lease, audit fail-closed.</summary>
public sealed class ActTests
{
    private static Dictionary<string, object?> ClickArgs() => new() { ["button"] = "left", ["count"] = 1 };

    [Fact]
    public async Task Act_ElementAccepted_ReturnsReceiptAndInvalidatesState()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var stateId = await environment.ObserveStateIdAsync(client);

        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));

        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-act-v1", report["schema_version"]!.GetValue<string>());
        Assert.Equal(TestEnvironment.SessionA, report["session_id"]!.GetValue<string>());
        Assert.Equal("click", report["op"]!.GetValue<string>());
        Assert.StartsWith("req-", report["request_id"]!.GetValue<string>());

        var receipt = report["action_receipt"]!;
        Assert.True(receipt["action_sent"]!.GetValue<bool>());
        Assert.Equal("accepted", receipt["dispatch_status"]!.GetValue<string>());
        Assert.False(receipt["retry_action"]!.GetValue<bool>());
        Assert.Equal("element", receipt["target_kind"]!.GetValue<string>());
        Assert.False(receipt["escalated"]!.GetValue<bool>());
        Assert.False(receipt["state_available"]!.GetValue<bool>());
        Assert.Equal("unverified", receipt["verification"]!["status"]!.GetValue<string>());
        Assert.Equal("click_no_readback", receipt["verification"]!["reason"]!.GetValue<string>());

        Assert.Single(environment.ElementDispatcher.Calls);
        var call = environment.ElementDispatcher.Calls[0];
        Assert.Equal(stateId, call.StateId);
        Assert.Equal(1, call.Index);
        Assert.Equal("click", call.Op);
        Assert.Contains(environment.Audit.Events, auditEvent => auditEvent.Tool == "go_act" && auditEvent.DispatchStatus == DispatchStatus.Accepted);
    }

    [Fact]
    public async Task Act_NotSentUipi_ReturnsErrorButKeepsState()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.ElementDispatcher.NextOutcome = PlatformActionOutcome.NotSent(ErrorCodes.UipiBlocked);

        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[UIPI_BLOCKED]:", ToolResultsTest.FirstText(result));
        var error = ToolResultsTest.ErrorObject(result);
        Assert.Equal("UIPI_BLOCKED", error["code"]!.GetValue<string>());
        Assert.Equal("never", error["retry"]!.GetValue<string>());
        Assert.False(error["action_sent"]!.GetValue<bool>());
        Assert.Equal("not_sent", error["dispatch_status"]!.GetValue<string>());
        var receipt = error["action_receipt"]!;
        Assert.Equal("not_sent", receipt["dispatch_status"]!.GetValue<string>());
        Assert.False(receipt["retry_action"]!.GetValue<bool>());

        // A confirmed not_sent action must NOT invalidate the observed state.
        environment.ElementDispatcher.NextOutcome = PlatformActionOutcome.Accepted("uia", false, VerificationInfo.Unverified);
        var retried = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.False(retried.IsError == true);
        Assert.Equal(2, environment.ElementDispatcher.Calls.Count);
    }

    [Fact]
    public async Task Act_PossiblySent_ReturnsDispatchUncertainAndInvalidatesState()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.ElementDispatcher.NextOutcome = PlatformActionOutcome.PossiblySent("post_message", true, "timeout_after_dispatch");

        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[DISPATCH_UNCERTAIN]:", ToolResultsTest.FirstText(result));
        var error = ToolResultsTest.ErrorObject(result);
        Assert.Equal("DISPATCH_UNCERTAIN", error["code"]!.GetValue<string>());
        Assert.Equal("reobserve", error["retry"]!.GetValue<string>());
        Assert.Null(error["retry_after_ms"]);
        Assert.True(error["action_sent"]!.GetValue<bool>());
        Assert.Equal("possibly_sent", error["dispatch_status"]!.GetValue<string>());
        var receipt = error["action_receipt"]!;
        Assert.Equal("possibly_sent", receipt["dispatch_status"]!.GetValue<string>());
        Assert.False(receipt["retry_action"]!.GetValue<bool>());
        Assert.True(receipt["action_sent"]!.GetValue<bool>());
        Assert.True(receipt["escalated"]!.GetValue<bool>());
        Assert.Equal("unverified", receipt["verification"]!["status"]!.GetValue<string>());
        Assert.Equal("timeout_after_dispatch", receipt["verification"]!["reason"]!.GetValue<string>());

        var stale = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(stale.IsError == true);
        Assert.StartsWith("ERROR[STALE_STATE]:", ToolResultsTest.FirstText(stale));
    }

    [Fact]
    public async Task Act_ReadonlyMode_RejectsWithoutDispatch()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"policy\":{\"readonly\":true}}");
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);

        var act = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(act.IsError == true);
        Assert.StartsWith("ERROR[READONLY_MODE]:", ToolResultsTest.FirstText(act));
        var error = ToolResultsTest.ErrorObject(act);
        Assert.Equal("never", error["retry"]!.GetValue<string>());
        Assert.Equal("not_sent", error["dispatch_status"]!.GetValue<string>());
        Assert.Empty(environment.ElementDispatcher.Calls);

        var launch = await environment.CallAsync(client, "go_launch", new Dictionary<string, object?> { ["app"] = "notepad" });
        Assert.True(launch.IsError == true);
        Assert.StartsWith("ERROR[READONLY_MODE]:", ToolResultsTest.FirstText(launch));
        Assert.Equal(0, environment.Launcher.Calls);
    }

    [Fact]
    public async Task Act_RateLimited_ReturnsRetryHint()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"policy\":{\"actRatePerMinute\":1}}");
        var client = await environment.ConnectAsync();
        var first = await environment.ObserveStateIdAsync(client);
        var accepted = await environment.ActElementAsync(client, "click", first, 1, ClickArgs());
        Assert.False(accepted.IsError == true);

        var second = await environment.ObserveStateIdAsync(client);
        var limited = await environment.ActElementAsync(client, "click", second, 1, ClickArgs());
        Assert.True(limited.IsError == true);
        Assert.StartsWith("ERROR[RATE_LIMITED]:", ToolResultsTest.FirstText(limited));
        var error = ToolResultsTest.ErrorObject(limited);
        Assert.Equal("retry", error["retry"]!.GetValue<string>());
        Assert.True(error["retry_after_ms"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Act_SessionLocked_Rejects()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.Probe.Facts = environment.Probe.Facts with { IsLocked = true };

        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[SESSION_LOCKED]:", ToolResultsTest.FirstText(result));
        Assert.Equal("never", ToolResultsTest.ErrorObject(result)["retry"]!.GetValue<string>());
        Assert.Empty(environment.ElementDispatcher.Calls);
    }

    [Fact]
    public async Task Act_InvalidArguments_AreRejected()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);

        var missingTarget = await environment.CallAsync(client, "go_act", new Dictionary<string, object?> { ["op"] = "click" });
        Assert.True(missingTarget.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(missingTarget));

        var unsupported = await environment.CallAsync(client, "go_act", new Dictionary<string, object?>
        {
            ["op"] = "drag",
            ["target"] = new Dictionary<string, object?> { ["kind"] = "element", ["index"] = 1, ["state_id"] = stateId },
        });
        Assert.True(unsupported.IsError == true);
        Assert.StartsWith("ERROR[UNSUPPORTED_ACTION]:", ToolResultsTest.FirstText(unsupported));
        Assert.Equal("never", ToolResultsTest.ErrorObject(unsupported)["retry"]!.GetValue<string>());

        var typeOnCoordinate = await environment.CallAsync(client, "go_act", new Dictionary<string, object?>
        {
            ["op"] = "type",
            ["target"] = new Dictionary<string, object?> { ["kind"] = "coordinate", ["x"] = 1, ["y"] = 2 },
            ["args"] = new Dictionary<string, object?> { ["text"] = "hi" },
        });
        Assert.True(typeOnCoordinate.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(typeOnCoordinate));
        Assert.Empty(environment.ElementDispatcher.Calls);
    }

    [Fact]
    public async Task Act_ControllerBusy_TwoSessions()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"policy\":{\"controllerWaitMs\":50}}");
        var clientA = await environment.ConnectAsync(TestEnvironment.SessionA);
        var clientB = await environment.ConnectAsync(TestEnvironment.SessionB);

        var stateA = await environment.ObserveStateIdAsync(clientA);
        var acceptedA = await environment.ActElementAsync(clientA, "click", stateA, 1, ClickArgs());
        Assert.False(acceptedA.IsError == true);

        var stateB = await environment.ObserveStateIdAsync(clientB);
        var busy = await environment.ActElementAsync(clientB, "click", stateB, 1, ClickArgs());
        Assert.True(busy.IsError == true);
        Assert.StartsWith("ERROR[CONTROLLER_BUSY]:", ToolResultsTest.FirstText(busy));
        var error = ToolResultsTest.ErrorObject(busy);
        Assert.Equal("retry", error["retry"]!.GetValue<string>());
        Assert.True(error["retry_after_ms"]!.GetValue<long>() > 0);
        Assert.False(error["details"]!["controller"]!["mine"]!.GetValue<bool>());
        Assert.Single(environment.ElementDispatcher.Calls);

        // Session B keeps its state while CONTROLLER_BUSY; releasing A's lease lets B proceed with the same state.
        var stoppedA = await environment.CallAsync(clientA, "go_stop", new Dictionary<string, object?> { ["reason"] = "test" });
        Assert.False(stoppedA.IsError == true);
        var acceptedB = await environment.ActElementAsync(clientB, "click", stateB, 1, ClickArgs());
        Assert.False(acceptedB.IsError == true);
        Assert.Equal(2, environment.ElementDispatcher.Calls.Count);
    }

    [Fact]
    public async Task Act_AuditUnavailable_FailsClosedWithoutDispatch()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.Audit.ThrowOnWrite = true;

        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[AUDIT_UNAVAILABLE]:", ToolResultsTest.FirstText(result));
        var error = ToolResultsTest.ErrorObject(result);
        Assert.Equal("retry", error["retry"]!.GetValue<string>());
        Assert.Equal("not_sent", error["dispatch_status"]!.GetValue<string>());
        Assert.Empty(environment.ElementDispatcher.Calls);

        environment.Audit.ThrowOnWrite = false;
        var retried = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs());
        Assert.False(retried.IsError == true);
        Assert.Single(environment.ElementDispatcher.Calls);
    }

    [Fact]
    public async Task Act_Coordinate_UsesSessionFrame()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        await environment.ObserveAsync(client, screenshot: true);

        var result = await environment.ActCoordinateAsync(client, "click", 100, 200, ClickArgs());
        Assert.False(result.IsError == true);
        var receipt = ToolResultsTest.Report(result)["action_receipt"]!;
        Assert.Equal("coordinate", receipt["target_kind"]!.GetValue<string>());
        Assert.Equal("accepted", receipt["dispatch_status"]!.GetValue<string>());
        Assert.Single(environment.CoordinateDispatcher.Calls);
        var call = environment.CoordinateDispatcher.Calls[0];
        Assert.Equal(100, call.X);
        Assert.Equal(200, call.Y);
        Assert.True(call.Frame.Actionable);
    }

    [Fact]
    public async Task Act_CoordinateOutOfBounds_IsInvalidArgument()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        await environment.ObserveAsync(client, screenshot: true);

        var result = await environment.ActCoordinateAsync(client, "click", 5000, 10, ClickArgs());
        Assert.True(result.IsError == true);
        Assert.StartsWith("ERROR[INVALID_ARGUMENT]:", ToolResultsTest.FirstText(result));
        Assert.Empty(environment.CoordinateDispatcher.Calls);
    }

    [Fact]
    public async Task Act_Verification_ReportsReadback()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client);
        environment.ElementDispatcher.NextOutcome = PlatformActionOutcome.Accepted("uia", false, new VerificationInfo("verified"));

        var result = await environment.ActElementAsync(client, "set_value", stateId, 2, new Dictionary<string, object?> { ["value"] = "abc" });
        Assert.False(result.IsError == true);
        var receipt = ToolResultsTest.Report(result)["action_receipt"]!;
        Assert.Equal("accepted", receipt["dispatch_status"]!.GetValue<string>());
        Assert.Equal("verified", receipt["verification"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Act_UsesHiddenInjectedSessionId()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var stateId = await environment.ObserveStateIdAsync(client, hiddenSession: TestEnvironment.SessionC);
        var result = await environment.ActElementAsync(client, "click", stateId, 1, ClickArgs(), hiddenSession: TestEnvironment.SessionC);
        Assert.False(result.IsError == true);
        Assert.Equal(TestEnvironment.SessionC, ToolResultsTest.Report(result)["session_id"]!.GetValue<string>());
    }
}
