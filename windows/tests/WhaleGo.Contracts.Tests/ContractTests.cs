using System.Text.Json;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Tests;

public sealed class ContractTests
{
    private static ContractDocument Load() => ContractCatalog.Load(Path.Combine(AppContext.BaseDirectory, "contracts", "whalego.tools.v1.json"));

    [Fact]
    public void ContractHasExpectedVersionAndTransport()
    {
        var contract = Load();
        Assert.Equal("whalego.tools", contract.Contract);
        Assert.Equal(ProtocolInfo.ContractVersion, contract.ContractVersion);
        Assert.Equal(ProtocolInfo.ProtocolVersion, contract.ProtocolVersion);
        Assert.Equal("mcp-streamable-http", contract.Transport);
    }

    [Fact]
    public void ContractDeclaresExactlySevenToolsInOrder()
    {
        var names = Load().ToolNames();
        Assert.Equal(
            ["go_health", "go_apps", "go_windows", "go_observe", "go_act", "go_launch", "go_stop"],
            names);
    }

    [Fact]
    public void ModelFacingSchemasNeverContainInternalParams()
    {
        var contract = Load();
        Assert.Empty(contract.FindForbiddenModelParams());

        var stopTool = contract.ModelTools().OfType<System.Text.Json.Nodes.JsonObject>()
            .Single(tool => tool["name"]!.GetValue<string>() == "go_stop");
        var properties = stopTool["inputSchema"]!["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("reason"));
        Assert.False(properties.ContainsKey("scope"));
    }

    [Fact]
    public void EveryToolHasNameDescriptionAndObjectSchema()
    {
        var contract = Load();
        foreach (var tool in contract.Tools.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool["name"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]?.GetValue<string>()));
            var schema = tool["inputSchema"]!.AsObject();
            Assert.Equal("object", schema["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ContractInternalParamsMatchProtocol()
    {
        var root = Load().Root;
        var internalParams = root["internal_params"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(["session_id", "request_id", "scope"], internalParams);
    }

    [Fact]
    public void ContractAdvisoriesMatchClosedSet()
    {
        var advisories = Load().Root["advisories"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(Advisories.All, advisories);
    }

    [Fact]
    public void ContractErrorCodesMatchCatalogRetryAndDispatch()
    {
        var errorCodes = Load().Root["error_codes"]!.AsObject();
        var executorCodes = errorCodes.Select(kv => kv.Key).ToArray();
        Assert.Equal(ErrorCodes.ExecutorCodes.OrderBy(code => code, StringComparer.Ordinal), executorCodes.OrderBy(code => code, StringComparer.Ordinal));

        foreach (var (code, node) in errorCodes)
        {
            var info = ErrorCatalog.Get(code);
            var retry = node!["retry"]!.GetValue<string>();
            var dispatch = node["dispatch"]!.GetValue<string>();
            Assert.Equal(MapRetry(info.Retry), retry);
            if (retry == "reobserve" && dispatch == "possibly_sent")
            {
                Assert.Equal(DispatchStatus.PossiblySent, info.DispatchStatus);
            }
            else if (dispatch == "not_sent_or_possibly_sent")
            {
                // INTERNAL may surface before or after entering native dispatch; the catalog default is not_sent.
                Assert.Equal(ErrorCodes.Internal, code);
                Assert.Equal(DispatchStatus.NotSent, info.DispatchStatus);
            }
            else
            {
                Assert.Equal(MapDispatch(info.DispatchStatus), dispatch);
            }
        }

        Assert.Equal(ErrorCodes.ExecutorCodes.Count, errorCodes.Count);
    }

    [Fact]
    public void ContractPluginErrorCodesMatch()
    {
        var codes = Load().Root["plugin_error_codes"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(ErrorCodes.PluginCodes, codes);
    }

    [Fact]
    public void ContractFreshnessMatchesRules()
    {
        var freshness = Load().Root["freshness"]!.AsObject();
        Assert.True(freshness["invalidated_by_any_act"]!.GetValue<bool>());
        Assert.False(freshness["title_change_invalidates"]!.GetValue<bool>());
        Assert.True(freshness["geometry_or_dpi_change_invalidates_frame"]!.GetValue<bool>());
        Assert.True(freshness["window_switch_drops_previous_state"]!.GetValue<bool>());
        Assert.True(freshness["no_screenshot_observe_keeps_frame"]!.GetValue<bool>());
        Assert.True(freshness["epoch_invalidation"]!.GetValue<bool>());
        Assert.True(freshness["stop_or_pause_invalidates"]!.GetValue<bool>());
        Assert.Equal(ConfigDefaults.PolicyStateTtlSeconds, freshness["state_ttl_seconds"]!.GetValue<int>());
        Assert.Equal(ConfigDefaults.PolicyFrameTtlSeconds, freshness["frame_ttl_seconds"]!.GetValue<int>());
    }

    [Fact]
    public void ContractPrivacyMatchesDayOneRules()
    {
        var privacy = Load().Root["privacy"]!.AsObject();
        Assert.True(privacy["mask_password_fields"]!.GetValue<bool>());
        Assert.True(privacy["screenshot_default"]!.GetValue<bool>());
        Assert.Equal("and", privacy["screenshot_switch_semantics"]!.GetValue<string>());
        Assert.True(privacy["self_process_windows_excluded"]!.GetValue<bool>());
        Assert.False(privacy["token_in_config"]!.GetValue<bool>());
        Assert.False(privacy["input_text_logged"]!.GetValue<bool>());
        Assert.Equal("suppressed_with_advisory", privacy["sensitive_window_screenshot"]!.GetValue<string>());
    }

    [Fact]
    public void ContractVerificationMatchesRules()
    {
        var verification = Load().Root["verification"]!.AsObject();
        Assert.Equal(["verified", "mismatched", "unverified"], verification["status_enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(verification["mismatched_replay_allowed"]!.GetValue<bool>());
    }

    [Fact]
    public void ContractToolSchemasRoundTripThroughJson()
    {
        var contract = Load();
        var json = WireJson.Serialize(contract.ModelTools());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(7, document.RootElement.GetArrayLength());
    }

    private static string MapRetry(RetryHint retry) => retry switch
    {
        RetryHint.Never => "never",
        RetryHint.Retry => "retry",
        RetryHint.Reobserve => "reobserve",
        _ => throw new ArgumentOutOfRangeException(nameof(retry)),
    };

    private static string MapDispatch(DispatchStatus status) => status switch
    {
        DispatchStatus.NotSent => "not_sent",
        DispatchStatus.Accepted => "accepted",
        DispatchStatus.PossiblySent => "possibly_sent",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
