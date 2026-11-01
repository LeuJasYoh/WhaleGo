using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>Transport auth + /healthz + go_health state fields.</summary>
public sealed class AuthAndHealthTests
{
    [Fact]
    public async Task Mcp_WithoutToken_Returns401()
    {
        await using var environment = await TestEnvironment.StartAsync();
        using var http = environment.RawHttpClient();
        using var content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/mcp", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Mcp_WithWrongToken_Returns401()
    {
        await using var environment = await TestEnvironment.StartAsync();
        using var http = environment.RawHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wg_wrong_token");
        request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithValidToken_NegotiatesAndListsTools()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.Equal(7, tools.Count);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInfo?.Name));
        Assert.Equal("whalego", client.ServerInfo!.Name);
    }

    [Fact]
    public async Task Healthz_NoAuth_ReturnsMinimalPayload()
    {
        await using var environment = await TestEnvironment.StartAsync();
        using var http = environment.RawHttpClient();
        var response = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(7, json.AsObject().Count);
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("0.1.0", json["version"]!.GetValue<string>());
        Assert.Equal(1, json["protocol_version"]!.GetValue<int>());
        Assert.True(json["uptime_s"]!.GetValue<long>() >= 0);
        Assert.False(json["paused"]!.GetValue<bool>());
        Assert.False(json["stopped"]!.GetValue<bool>());
        Assert.True(json["accepting_actions"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Healthz_RequiresTokenWhenConfigured()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"listen\":{\"healthzRequireToken\":true}}");
        using var http = environment.RawHttpClient();
        var unauthenticated = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", environment.AuthToken);
        var authenticated = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task GoHealth_ReportsVersionProtocolAndCapabilities()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var result = await environment.CallAsync(client, "go_health");
        Assert.False(result.IsError == true);
        Assert.StartsWith("OK:", ToolResultsTest.FirstText(result));

        var report = ToolResultsTest.Report(result);
        Assert.Equal("whalego-health-v1", report["schema_version"]!.GetValue<string>());
        Assert.True(report["ok"]!.GetValue<bool>());
        Assert.Equal("0.1.0", report["version"]!.GetValue<string>());
        Assert.Equal(1, report["protocol_version"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(report["mcp_protocol_version"]!.GetValue<string>()));
        Assert.False(report["paused"]!.GetValue<bool>());
        Assert.False(report["stopped"]!.GetValue<bool>());
        Assert.False(report["session_stopped"]!.GetValue<bool>());
        Assert.True(report["accepting_actions"]!.GetValue<bool>());
        Assert.Equal(0, report["audit_dropped"]!.GetValue<long>());
        Assert.True(report["sessions"]!.GetValue<int>() >= 1);

        var launch = report["capabilities"]!["launch"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("app", launch);
        Assert.Contains("activate", launch);
        var actions = report["capabilities"]!["actions"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("hotkey", actions);

        var observation = report["capabilities"]!["observation"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("uia_tree", observation);
        Assert.Contains("screenshot_printwindow", observation);

        Assert.NotNull(report["limits"]!["max_observe_elements"]);
        Assert.Equal(120, report["limits"]!["state_ttl_seconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pause_IsVisibleAndBlocksActions()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        environment.Control.Pause("user_button");

        var healthResult = await environment.CallAsync(client, "go_health");
        var report = ToolResultsTest.Report(healthResult);
        Assert.True(report["paused"]!.GetValue<bool>());
        Assert.Equal("user_button", report["pause_reason"]!.GetValue<string>());
        Assert.False(report["accepting_actions"]!.GetValue<bool>());
        Assert.False(report["session_stopped"]!.GetValue<bool>());

        using var http = environment.RawHttpClient();
        var healthz = JsonNode.Parse(await http.GetStringAsync("/healthz"))!;
        Assert.True(healthz["paused"]!.GetValue<bool>());
        Assert.False(healthz["accepting_actions"]!.GetValue<bool>());

        var actResult = await environment.ActElementAsync(client, "click", "st-12345678", 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(actResult.IsError == true);
        Assert.StartsWith("ERROR[PAUSED]:", ToolResultsTest.FirstText(actResult));
        var error = ToolResultsTest.ErrorObject(actResult);
        Assert.Equal("PAUSED", error["code"]!.GetValue<string>());
        Assert.Equal("never", error["retry"]!.GetValue<string>());
        Assert.Equal("not_sent", error["dispatch_status"]!.GetValue<string>());

        environment.Control.Resume();
        var resumed = ToolResultsTest.Report(await environment.CallAsync(client, "go_health"));
        Assert.False(resumed["paused"]!.GetValue<bool>());
        Assert.True(resumed["accepting_actions"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GlobalStop_BlocksActionsAndPersistsAcrossHostRestart()
    {
        var dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whalego-executor-tests", Guid.NewGuid().ToString("N"));
        await using (var stopped = await TestEnvironment.StartAsync(dataDir: dataDir, deleteDataDir: false))
        {
            stopped.Control.Stop("test");
            Assert.True(System.IO.File.Exists(stopped.ControlStatePath));
            var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(stopped.ControlStatePath))!;
            Assert.True(state["stopped"]!.GetValue<bool>());

            using var http = stopped.RawHttpClient();
            var healthz = JsonNode.Parse(await http.GetStringAsync("/healthz"))!;
            Assert.True(healthz["stopped"]!.GetValue<bool>());
            Assert.False(healthz["accepting_actions"]!.GetValue<bool>());
        }

        await using (var restarted = await TestEnvironment.StartAsync(dataDir: dataDir))
        {
            var client = await restarted.ConnectAsync();
            var report = ToolResultsTest.Report(await restarted.CallAsync(client, "go_health"));
            Assert.True(report["stopped"]!.GetValue<bool>());
            Assert.False(report["accepting_actions"]!.GetValue<bool>());

            restarted.Control.ResetStop();
            var after = ToolResultsTest.Report(await restarted.CallAsync(client, "go_health"));
            Assert.False(after["stopped"]!.GetValue<bool>());
            Assert.True(after["accepting_actions"]!.GetValue<bool>());
        }
    }
}