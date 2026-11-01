using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Contracts.Domain;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>tools/list must equal contracts/whalego.tools.v1.json after x-internal stripping (single source of truth).</summary>
public sealed class ToolContractTests
{
    [Fact]
    public async Task ToolsList_MatchesContractModelTools()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var tools = await client.ListToolsAsync();

        var document = ContractCatalog.Load(TestEnvironment.ContractPath);
        var expectedTools = document.ModelTools().OfType<JsonObject>().ToList();

        Assert.Equal(7, tools.Count);
        Assert.Equal(expectedTools.Count, tools.Count);
        for (var index = 0; index < tools.Count; index++)
        {
            var actual = tools[index];
            var expected = expectedTools[index];
            Assert.Equal(expected["name"]!.GetValue<string>(), actual.Name);
            Assert.Equal(expected["description"]!.GetValue<string>(), actual.Description);

            var expectedSchema = JsonNode.Parse(expected["inputSchema"]!.ToJsonString())!;
            var actualSchema = JsonNode.Parse(actual.JsonSchema.GetRawText())!;
            Assert.True(
                JsonNode.DeepEquals(expectedSchema, actualSchema),
                "schema mismatch for " + actual.Name + "\nexpected: " + expectedSchema.ToJsonString() + "\nactual: " + actualSchema.ToJsonString());
        }
    }

    [Fact]
    public async Task ToolsList_HasNoInternalParams()
    {
        await using var environment = await TestEnvironment.StartAsync();
        var client = await environment.ConnectAsync();
        var tools = await client.ListToolsAsync();

        Assert.Equal(7, tools.Count);
        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema.GetRawText();
            Assert.DoesNotContain("session_id", schema);
            Assert.DoesNotContain("request_id", schema);
            Assert.DoesNotContain("\"scope\"", schema);
        }
    }

    [Fact]
    public void ContractFile_IsV13AndDeclaresSevenTools()
    {
        var document = ContractCatalog.Load(TestEnvironment.ContractPath);
        Assert.Equal("whalego.tools", document.Contract);
        Assert.Equal("1.3", document.ContractVersion);
        Assert.Equal(1, document.ProtocolVersion);
        Assert.Equal(7, document.ToolNames().Count);
        Assert.Empty(document.FindForbiddenModelParams());
        Assert.Contains("go_health", document.ToolNames());
        Assert.Contains("go_act", document.ToolNames());
        Assert.Contains("go_launch", document.ToolNames());
        Assert.Contains("go_stop", document.ToolNames());
    }
}
