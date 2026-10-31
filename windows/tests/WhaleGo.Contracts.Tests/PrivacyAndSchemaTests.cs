using System.Text.Json.Nodes;
using WhaleGo.Contracts.Domain;

namespace WhaleGo.Contracts.Tests;

public sealed class PrivacyAndSchemaTests
{
    [Fact]
    public void PasswordValuesAreMasked()
    {
        Assert.Equal("***", PrivacyRules.MaskValue("secret123", isPassword: true, maskEnabled: true));
        Assert.Equal("secret123", PrivacyRules.MaskValue("secret123", isPassword: true, maskEnabled: false));
        Assert.Equal("hello", PrivacyRules.MaskValue("hello", isPassword: false, maskEnabled: true));
    }

    [Theory]
    [InlineData("*password*", "Enter your password", true)]
    [InlineData("*bank*", "Online Banking", true)]
    [InlineData("*bank*", "Notepad", false)]
    [InlineData("?ecure*", "Secure notes", true)]
    [InlineData("", "anything", false)]
    public void GlobMatching(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, PrivacyRules.GlobMatch(pattern, text));
    }

    [Fact]
    public void SensitiveWindowMatchesAnyPattern()
    {
        Assert.True(PrivacyRules.IsSensitiveWindow("My Password Manager", ["*password*", "*bank*"]));
        Assert.False(PrivacyRules.IsSensitiveWindow("Notepad", ["*password*", "*bank*"]));
        Assert.False(PrivacyRules.IsSensitiveWindow(null, ["*password*"]));
    }

    [Theory]
    [InlineData("WhaleGo.Executor", true)]
    [InlineData("WhaleGo.Gui", true)]
    [InlineData("WhaleGo", true)]
    [InlineData("whalego.executor", true)]
    [InlineData("WhaleGo.Fixtures.WinForms", false)]
    [InlineData("notepad", false)]
    public void SelfProcessDetection(string processName, bool expected)
    {
        Assert.Equal(expected, PrivacyRules.IsSelfProcess(processName));
    }

    [Fact]
    public void SanitizerRemovesInternalParamsAndRequiredEntries()
    {
        var tool = JsonNode.Parse("""
        {
          "name": "go_stop",
          "inputSchema": {
            "type": "object",
            "properties": {
              "reason": { "type": "string" },
              "scope": { "type": "string", "x-internal": true }
            },
            "required": ["reason", "scope"],
            "additionalProperties": false
          }
        }
        """)!.AsObject();

        var sanitized = ModelSchemaSanitizer.SanitizeTool(tool);
        var properties = sanitized["inputSchema"]!["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("reason"));
        Assert.False(properties.ContainsKey("scope"));
        var required = sanitized["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["reason"], required);
        Assert.Contains("scope", tool["inputSchema"]!["properties"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void SanitizerWalksNestedSchemas()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "nested": {
              "type": "object",
              "properties": {
                "session_id": { "type": "string", "x-internal": true },
                "visible": { "type": "string" }
              },
              "required": ["session_id", "visible"]
            }
          },
          "anyOf": [ { "required": ["nested"] } ]
        }
        """)!.AsObject();

        ModelSchemaSanitizer.SanitizeSchema(schema);
        var nested = schema["properties"]!["nested"]!;
        Assert.False(nested["properties"]!.AsObject().ContainsKey("session_id"));
        Assert.Equal(["visible"], nested["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void ForbiddenParamFinderDetectsViolations()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"session_id":{"type":"string"}}}""");
        var found = ModelSchemaSanitizer.FindForbiddenParams(schema, ["session_id", "request_id", "scope"]);
        Assert.Equal(["session_id"], found);
    }

    [Fact]
    public void ErrorCatalogPluginCodesAreMarkedPluginSide()
    {
        foreach (var code in Model.ErrorCodes.PluginCodes)
        {
            Assert.True(ErrorCatalog.Get(code).PluginSide);
        }
        foreach (var code in Model.ErrorCodes.ExecutorCodes)
        {
            Assert.False(ErrorCatalog.Get(code).PluginSide);
        }
    }
}
