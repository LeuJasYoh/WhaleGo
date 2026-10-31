using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>Config precedence/persistence, token hygiene, control surface and self-check.</summary>
public sealed class ControlAndConfigTests
{
    [Fact]
    public async Task ConfigLoader_AppliesPrecedenceWithSourceBadges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "whalego-executor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath, """
        {
          "listen": { "address": "127.0.0.1", "port": 1111 },
          "policy": { "readonly": false },
          "ui": { "theme": "dark" }
        }
        """);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WHALEGO_POLICY__READONLY"] = "true",
            ["WHALEGO_LISTEN__PORT"] = "2222",
            ["WHALEGO_UI__THEME"] = "light",
        };
        var commandLine = new Dictionary<string, string?> { ["listen.port"] = "3333" };
        var load = ConfigLoader.Load(configPath, environment, commandLine);
        var config = ConfigValues.Build(load);

        Assert.True(config.Readonly);
        Assert.Equal(3333, config.ListenPort);
        Assert.Equal("light", config.UiTheme);
        Assert.Equal("127.0.0.1", config.ListenAddress);

        Assert.Equal(ConfigSourceKinds.Environment, load.Sources[ConfigKeys.PolicyReadonly]);
        Assert.Equal(ConfigSourceKinds.CommandLine, load.Sources[ConfigKeys.ListenPort]);
        Assert.Equal(ConfigSourceKinds.Environment, load.Sources[ConfigKeys.UiTheme]);
        Assert.Equal(ConfigSourceKinds.Default, load.Sources[ConfigKeys.PolicyStateTtlSeconds]);
    }

    [Fact]
    public async Task ConfigService_SavePersistsAndHotApplies()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var configService = (ConfigService)environment.Host.Services.GetService(typeof(ConfigService))!;

        var patch = new ConfigPatch(new Dictionary<string, object?>
        {
            [ConfigKeys.PolicyReadonly] = true,
            [ConfigKeys.UiTheme] = "dark",
        });
        environment.Control.SetConfig(patch);

        var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(environment.ConfigPath))!;
        Assert.True(onDisk["policy"]!["readonly"]!.GetValue<bool>());
        Assert.Equal("dark", onDisk["ui"]!["theme"]!.GetValue<string>());
        Assert.Equal("file", configService.SourceOf(ConfigKeys.PolicyReadonly));

        var stateId = await environment.ObserveStateIdAsync(client);
        var act = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.True(act.IsError == true);
        Assert.StartsWith("ERROR[READONLY_MODE]:", ToolResultsTest.FirstText(act));

        environment.Control.SetConfig(new ConfigPatch(new Dictionary<string, object?>
        {
            [ConfigKeys.PolicyReadonly] = false,
        }));
        var newState = await environment.ObserveStateIdAsync(client);
        var accepted = await environment.ActElementAsync(client, "click", newState, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(accepted.IsError == true);
    }

    [Fact]
    public async Task FirstRun_CreatesConfigAndSecretsWithHashedToken()
    {
        await using var environment = await TestEnvironment.StartAsync();
        Assert.True(File.Exists(environment.ConfigPath));
        Assert.True(File.Exists(environment.SecretsPath));

        var configText = await File.ReadAllTextAsync(environment.ConfigPath);
        var secretsText = await File.ReadAllTextAsync(environment.SecretsPath);
        Assert.DoesNotContain(environment.AuthToken, configText);
        Assert.Contains(environment.AuthToken, secretsText);

        var config = JsonNode.Parse(configText)!;
        var configuredHash = config["auth"]!["tokens"]![0]!["sha256"]!.GetValue<string>();
        Assert.Equal(64, configuredHash.Length);
        Assert.Equal(TokenService.HashToken(environment.AuthToken), configuredHash);
        Assert.All(configuredHash.ToCharArray(), character => Assert.True(char.IsAsciiHexDigitLower(character) || char.IsDigit(character)));
    }

    [Fact]
    public async Task ControlSurface_ExposesStatusCountersRecentRequestsAndTokens()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        await environment.CallAsync(client, "go_health");
        await environment.CallAsync(client, "go_apps");

        var control = environment.Control;
        var status = control.GetStatus();
        Assert.Equal("0.1.0", status.Version);
        Assert.Equal("1", status.ProtocolVersion);
        Assert.Equal(ControlState.Running, status.State);
        Assert.True(status.AcceptingActions);
        Assert.True(status.ScreenshotEnabled);
        Assert.False(status.Readonly);
        Assert.True(status.Sessions >= 1);
        Assert.False(string.IsNullOrWhiteSpace(status.ListenActual));

        var counters = control.GetToolCounters();
        Assert.True(counters.ContainsKey("go_health"));
        Assert.True(counters["go_health"] >= 1);

        var recent = control.GetRecentRequests(10);
        Assert.NotEmpty(recent);
        Assert.Contains(recent, request => request.Tool == "go_health" && request.Result == "ok");

        var config = control.GetConfig();
        Assert.True(config.Values.ContainsKey(ConfigKeys.PolicyReadonly));
        Assert.False(config.Values.ContainsKey(ConfigKeys.AuthTokens));
        var sources = control.GetConfigSources();
        Assert.True(sources.ContainsKey(ConfigKeys.UiTheme));

        var tokens = control.GetTokens();
        Assert.Single(tokens);
        Assert.True(tokens[0].Available);
        Assert.NotEqual(environment.AuthToken, tokens[0].Masked);
        Assert.Equal(environment.AuthToken, control.RevealTokenForCopy());
    }

    [Fact]
    public async Task ControllerStatus_IsExposedThroughHealthAfterAct()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync(TestEnvironment.SessionA);
        var stateId = await environment.ObserveStateIdAsync(client);
        var result = await environment.ActElementAsync(client, "click", stateId, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(result.IsError == true);

        var health = ToolResultsTest.Report(await environment.CallAsync(client, "go_health"));
        var controller = health["controller"]!;
        Assert.True(controller["held"]!.GetValue<bool>());
        Assert.True(controller["mine"]!.GetValue<bool>());
        Assert.True(controller["expires_in_ms"]!.GetValue<long>() > 0);
        // No business session id is leaked through health.controller.
        Assert.Null(controller["session_id"]);
    }

    [Fact]
    public async Task Pause_ReleasesControllerLease()
    {
        await using var environment = await TestEnvironment.StartAsync(configJson: "{\"policy\":{\"controllerWaitMs\":50}}");
        var clientA = await environment.ConnectAsync(TestEnvironment.SessionA);
        var clientB = await environment.ConnectAsync(TestEnvironment.SessionB);
        var stateA = await environment.ObserveStateIdAsync(clientA);
        Assert.False((await environment.ActElementAsync(clientA, "click", stateA, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 })).IsError == true);

        var stateB = await environment.ObserveStateIdAsync(clientB);
        var busy = await environment.ActElementAsync(clientB, "click", stateB, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.StartsWith("ERROR[CONTROLLER_BUSY]:", ToolResultsTest.FirstText(busy));

        environment.Control.Pause("user_button");
        environment.Control.Resume();
        // Pause invalidates every session's state; a re-observe is mandatory, then the released lease allows B.
        var resumedStateB = await environment.ObserveStateIdAsync(clientB);
        var acceptedB = await environment.ActElementAsync(clientB, "click", resumedStateB, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(acceptedB.IsError == true, "act after pause/resume: " + ToolResultsTest.FirstText(acceptedB));
    }

    [Fact]
    public async Task SelfCheck_IsStableAndNeverLeaksToken()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var report = environment.Control.RunSelfCheck();
        Assert.True(report.Ok);
        var ids = report.Items.Select(item => item.Id).ToList();
        Assert.Equal(
            ["config", "data_dir", "audit", "port", "uia", "dpi", "interactive_session", "screen_lock", "hotkey", "token", "contract", "theme"],
            ids);
        Assert.All(report.Items.Where(item => item.Id is not ("hotkey" or "theme")), item => Assert.Equal(SelfCheckStatus.Ok, item.Status));
        Assert.Equal(SelfCheckStatus.Warn, report.Items.First(item => item.Id == "hotkey").Status);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var json = JsonSerializer.Serialize(report, options);
        Assert.DoesNotContain(environment.AuthToken, json);
        var parsed = JsonNode.Parse(json)!;
        Assert.True(parsed["ok"]!.GetValue<bool>());
        Assert.Equal(12, parsed["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task SelfCheck_WorksWithoutStartingListener()
    {
        var port = FreePort();
        await using var environment = await TestEnvironment.StartAsync(
            startMcp: false,
            configJson: "{\"listen\":{\"address\":\"127.0.0.1\",\"port\":" + port + "}}");
        Assert.Null(environment.Host.BaseUrl);
        var report = environment.Control.RunSelfCheck();
        Assert.True(report.Ok);
        Assert.Equal(SelfCheckStatus.Ok, report.Items.First(item => item.Id == "port").Status);
    }

    [Fact]
    public async Task AuditTail_RecordsInputTextAsLengthAndHashOnly()
    {
        await using var environment = await TestEnvironment.StartAsync(useFakeAudit: false);
        var client = await environment.ConnectAsync();

        // click -> accepted durable audit record
        var clickState = await environment.ObserveStateIdAsync(client);
        var click = await environment.ActElementAsync(client, "click", clickState, 1, new Dictionary<string, object?> { ["button"] = "left", ["count"] = 1 });
        Assert.False(click.IsError == true);

        // set_value -> text_length/text_sha256_prefix, never plaintext
        var setState = await environment.ObserveStateIdAsync(client);
        var setValue = await environment.ActElementAsync(client, "set_value", setState, 2, new Dictionary<string, object?> { ["value"] = "plain-secret" });
        Assert.False(setValue.IsError == true);

        // type -> text_length/text_sha256_prefix, never plaintext
        var typeState = await environment.ObserveStateIdAsync(client);
        var type = await environment.ActElementAsync(client, "type", typeState, 2, new Dictionary<string, object?> { ["text"] = "typed-text-42" });
        Assert.False(type.IsError == true);

        var tail = environment.Control.ReadAuditTail(20);
        Assert.NotEmpty(tail);
        Assert.Contains(tail, line => line.Contains("\"tool\":\"go_act\"", StringComparison.Ordinal));
        Assert.Contains(tail, line => line.Contains("\"dispatch_status\":\"accepted\"", StringComparison.Ordinal));
        Assert.DoesNotContain(tail, line => line.Contains(environment.AuthToken, StringComparison.Ordinal));

        // D-1: input text is audited only as a length + sha256 prefix; no plaintext anywhere.
        Assert.All(tail, line => Assert.DoesNotContain("plain-secret", line, StringComparison.Ordinal));
        Assert.All(tail, line => Assert.DoesNotContain("typed-text-42", line, StringComparison.Ordinal));
        Assert.Contains(tail, line => line.Contains("\"op\":\"set_value\"", StringComparison.Ordinal));
        Assert.Contains(tail, line => line.Contains("\"op\":\"type\"", StringComparison.Ordinal));

        var setJson = JsonNode.Parse(tail.First(line => line.Contains("\"op\":\"set_value\"", StringComparison.Ordinal)))!;
        Assert.Equal("plain-secret".Length, setJson["text_length"]!.GetValue<int>());
        Assert.Equal(HashPrefix("plain-secret"), setJson["text_sha256_prefix"]!.GetValue<string>());
        Assert.Null(setJson["text_plain"]);

        var typeJson = JsonNode.Parse(tail.First(line => line.Contains("\"op\":\"type\"", StringComparison.Ordinal)))!;
        Assert.Equal("typed-text-42".Length, typeJson["text_length"]!.GetValue<int>());
        Assert.Equal(HashPrefix("typed-text-42"), typeJson["text_sha256_prefix"]!.GetValue<string>());
    }

    private static string HashPrefix(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..8];

    [Fact]
    public async Task TokenRotation_InvalidatesOldTokenImmediately()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var oldToken = environment.AuthToken;
        var newToken = environment.Control.RotateToken("astrbot");
        Assert.NotEqual(oldToken, newToken);
        Assert.StartsWith("wg_", newToken);

        var tokenService = (TokenService)environment.Host.Services.GetService(typeof(TokenService))!;
        Assert.False(tokenService.Verify(oldToken));
        Assert.True(tokenService.Verify(newToken));
        Assert.Equal(newToken, environment.Control.RevealTokenForCopy());
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}