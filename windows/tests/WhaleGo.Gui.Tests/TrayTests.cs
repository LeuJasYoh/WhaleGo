using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Tests;

public sealed class TrayStateMapperTests
{
    private static TrayPresentation Map(StatusSnapshot snapshot, IReadOnlyList<RecentRequest>? recent = null, TrayAuxInfo? aux = null, long now = 1_760_000_300_000) =>
        TrayStateMapper.Map(snapshot, recent, now, aux ?? TrayAuxInfo.Default);

    [Theory]
    [InlineData("connected")]
    [InlineData("idle")]
    [InlineData("paused")]
    [InlineData("stopped")]
    [InlineData("error")]
    public void FiveTrayStatesAreMapped(string expectedState)
    {
        var state = expectedState switch
        {
            "connected" => TrayState.RunningConnected,
            "idle" => TrayState.RunningIdle,
            "paused" => TrayState.Paused,
            "stopped" => TrayState.Stopped,
            _ => TrayState.Error,
        };

        var snapshot = state switch
        {
            TrayState.RunningConnected => TestData.Status(sessions: 1),
            TrayState.RunningIdle => TestData.Status(sessions: 0),
            TrayState.Paused => TestData.Status(paused: true, accepting: false),
            TrayState.Stopped => TestData.Status(stopped: true, accepting: false),
            _ => TestData.Status(state: ControlState.Faulted, warnings: ["端口 8765 被占用"]),
        };

        var presentation = Map(snapshot);

        Assert.Equal(state, presentation.State);
    }

    [Fact]
    public void RecentRequestWithinFiveMinutesCountsAsConnected()
    {
        var recent = new List<RecentRequest> { new(1_760_000_100_000, "go_observe", "wg-abc12345", null, "OK", 12) };
        var presentation = Map(TestData.Status(sessions: 0), recent);
        Assert.Equal(TrayState.RunningConnected, presentation.State);
    }

    [Fact]
    public void StaleRequestsDoNotCountAsConnected()
    {
        var recent = new List<RecentRequest> { new(1_760_000_000_000 - (6 * 60 * 1000), "go_observe", "wg-abc12345", null, "OK", 12) };
        var presentation = Map(TestData.Status(sessions: 0), recent);
        Assert.Equal(TrayState.RunningIdle, presentation.State);
    }

    [Fact]
    public void StatusLinesAndPauseLabelsFollowState()
    {
        Assert.StartsWith("●", Map(TestData.Status(sessions: 1)).StatusLine);
        Assert.StartsWith("○", Map(TestData.Status()).StatusLine);
        Assert.StartsWith("⏸", Map(TestData.Status(paused: true)).StatusLine);
        Assert.StartsWith("■", Map(TestData.Status(stopped: true)).StatusLine);
        Assert.StartsWith("!", Map(TestData.Status(state: ControlState.Faulted, warnings: ["boom"])).StatusLine);

        var running = Map(TestData.Status());
        Assert.Equal("暂停", running.PauseResumeLabel);
        Assert.False(running.PauseResumeNeedsConfirmation);

        var paused = Map(TestData.Status(paused: true));
        Assert.Equal("恢复", paused.PauseResumeLabel);
        Assert.False(paused.PauseResumeNeedsConfirmation);

        var stopped = Map(TestData.Status(stopped: true));
        Assert.Equal("恢复（需确认）", stopped.PauseResumeLabel);
        Assert.True(stopped.PauseResumeNeedsConfirmation);
        Assert.False(stopped.EmergencyStopEnabled);
    }

    [Fact]
    public void TooltipIsShortAndContainsNoSessionOrToken()
    {
        var snapshot = TestData.Status(sessions: 2);
        var presentation = Map(snapshot);
        Assert.True(presentation.Tooltip.Length <= TrayStateMapper.TooltipMaxLength);
        Assert.DoesNotContain("wg_", presentation.Tooltip);
        Assert.DoesNotContain("wg-", presentation.Tooltip);
        Assert.Contains("WhaleGo", presentation.Tooltip);
    }

    [Fact]
    public void ErrorReasonComesFromWarnings()
    {
        var presentation = Map(TestData.Status(state: ControlState.Faulted, warnings: ["审计目录不可写"]));
        Assert.Contains("审计目录不可写", presentation.ErrorReason);
        Assert.Contains("审计目录不可写", presentation.StatusLine);
    }

    [Fact]
    public void AuxInfoDrivesWarningsAndCopyAvailability()
    {
        var aux = new TrayAuxInfo(
            TokenAvailable: true,
            HotkeyRegistered: false,
            HotkeyError: "Ctrl+Alt+Shift+F12 被占用",
            AuditDangerous: true,
            Version: "0.1.0");
        var presentation = Map(TestData.Status(), aux: aux);

        Assert.True(presentation.CopyConfigEnabled);
        Assert.Contains(presentation.WarningLines, line => line.Contains("热键"));
        Assert.Contains(presentation.WarningLines, line => line.Contains("审计"));
        Assert.Contains("审计非 fail-closed", presentation.Tooltip);
    }

    [Fact]
    public void IconSelectorUsesTaskbarAndHighContrast()
    {
        Assert.Equal(TrayIconVariant.LightTaskbar, TrayIconSelector.Select(appsUseLightTheme: true, highContrast: false));
        Assert.Equal(TrayIconVariant.DarkTaskbar, TrayIconSelector.Select(appsUseLightTheme: false, highContrast: false));
        Assert.Equal(TrayIconVariant.DarkTaskbar, TrayIconSelector.Select(appsUseLightTheme: true, highContrast: true));
    }

    [Fact]
    public void IconResourceNamesCoverFiveStatesAndTwoVariants()
    {
        var names = TrayIcons.AllResourceNames();
        Assert.Equal(10, names.Count);
        Assert.Equal(10, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in names)
        {
            Assert.StartsWith(TrayIcons.ResourcePrefix, name);
        }
    }
}

public sealed class BalloonPolicyTests
{
    [Fact]
    public void LevelFilteringFollowsConfig()
    {
        var policy = new BalloonPolicy(() => 0);
        Assert.False(policy.Evaluate(BalloonKind.EmergencyStop, BalloonLevel.Off));
        Assert.True(policy.Evaluate(BalloonKind.EmergencyStop, BalloonLevel.Important));
        Assert.False(policy.Evaluate(BalloonKind.AuthFailure, BalloonLevel.Important));
        Assert.True(policy.Evaluate(BalloonKind.AuthFailure, BalloonLevel.All));
        Assert.True(policy.Evaluate(BalloonKind.EmergencyStop, BalloonLevel.All));
    }

    [Fact]
    public void SameKindHasThirtySecondCooldown()
    {
        var now = 0L;
        var policy = new BalloonPolicy(() => now);
        Assert.True(policy.Evaluate(BalloonKind.AuditFailure, BalloonLevel.All));
        policy.RecordShown(BalloonKind.AuditFailure);

        now = 10_000;
        Assert.False(policy.Evaluate(BalloonKind.AuditFailure, BalloonLevel.All));

        now = 30_000;
        Assert.True(policy.Evaluate(BalloonKind.AuditFailure, BalloonLevel.All));
    }

    [Fact]
    public void GlobalLimitIsThreePerMinute()
    {
        var now = 0L;
        var policy = new BalloonPolicy(() => now);
        foreach (var kind in new[] { BalloonKind.EmergencyStop, BalloonKind.ModelStop, BalloonKind.StartupFailure })
        {
            Assert.True(policy.Evaluate(kind, BalloonLevel.All));
            policy.RecordShown(kind);
        }

        Assert.False(policy.Evaluate(BalloonKind.Info, BalloonLevel.All));

        now = 60_000;
        Assert.True(policy.Evaluate(BalloonKind.Info, BalloonLevel.All));
    }

    [Fact]
    public void SanitizeFlattensRedactsAndTruncates()
    {
        var sanitized = BalloonPolicy.Sanitize("line1\nline2 wg_abcdef123456\tend");
        Assert.DoesNotContain("\n", sanitized);
        Assert.DoesNotContain("abcdef123456", sanitized);
        Assert.Contains("wg_***", sanitized);

        var longText = new string('x', 200);
        var truncated = BalloonPolicy.Sanitize(longText);
        Assert.Equal(BalloonPolicy.MaxMessageChars, truncated.Length);
        Assert.EndsWith("…", truncated);
    }

    [Fact]
    public void LevelParsingDefaultsToImportant()
    {
        Assert.Equal(BalloonLevel.Off, BalloonLevels.ParseConfig("off"));
        Assert.Equal(BalloonLevel.All, BalloonLevels.ParseConfig("ALL"));
        Assert.Equal(BalloonLevel.Important, BalloonLevels.ParseConfig("important"));
        Assert.Equal(BalloonLevel.Important, BalloonLevels.ParseConfig(null));
    }
}

public sealed class TokenAndSnippetTests
{
    [Fact]
    public void MaskKeepsFirstFiveAndLastFour()
    {
        var masked = TokenMasker.Mask("wg_abcdefgh1234");
        Assert.StartsWith("wg_ab", masked);
        Assert.EndsWith("1234", masked);
        Assert.Contains("•", masked);
        Assert.DoesNotContain("cdefgh", masked);
    }

    [Fact]
    public void AlreadyMaskedTokenIsPreserved()
    {
        var already = "wg_ab12••••••••9f3c";
        Assert.Equal(already, TokenMasker.Mask(already));
    }

    [Fact]
    public void UnavailableTokenIsNotDisplayed()
    {
        var descriptor = new TokenDescriptor("astrbot", string.Empty, "2026-10-31", false);
        Assert.Equal(TokenMasker.UnavailableText, TokenMasker.Display(descriptor));
        Assert.False(TokenAccess.AnyAvailable([descriptor]));
        Assert.False(TokenAccess.CanCopyPluginConfig([descriptor]));
    }

    [Fact]
    public void AvailableTokenEnablesCopying()
    {
        var tokens = new List<TokenDescriptor> { new("astrbot", "wg_ab12••••9f3c", "2026-10-31", true) };
        Assert.True(TokenAccess.AnyAvailable(tokens));
        Assert.True(TokenAccess.CanCopyPluginConfig(tokens));
        Assert.Contains("1 个 token", TokenAccess.Summary(tokens));
    }

    [Fact]
    public void PluginSnippetMatchesTheDocumentedContract()
    {
        var json = PluginConfigSnippet.BuildJson(8765, null);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("http://host.docker.internal:8765/mcp", root.GetProperty("url").GetString());
        Assert.Equal(PluginConfigSnippet.TokenPlaceholder, root.GetProperty("token").GetString());
        Assert.Equal(45, root.GetProperty("timeout").GetInt32());
        Assert.False(root.GetProperty("readonly").GetBoolean());
        Assert.True(root.GetProperty("admin_only").GetBoolean());
        Assert.True(root.GetProperty("allow_screenshot").GetBoolean());
        Assert.Equal(7, root.GetProperty("allowed_tools").GetArrayLength());

        var withToken = PluginConfigSnippet.BuildJson(8766, "wg_secret");
        Assert.Contains("wg_secret", withToken);
        Assert.Contains("8766", withToken);
    }

    [Fact]
    public void DockerCommandAndFirewallCommandAreCopyOnlyHelpers()
    {
        var docker = PluginConfigSnippet.BuildDockerHealthCommand(8765, null);
        Assert.Contains("docker exec", docker);
        Assert.Contains("http://host.docker.internal:8765/healthz", docker);
        Assert.Contains("<token>", docker);

        var firewall = PluginConfigSnippet.BuildNetshFirewallCommand(8765);
        Assert.Contains("WhaleGo-MCP", firewall);
        Assert.Contains("8765", firewall);
    }
}
