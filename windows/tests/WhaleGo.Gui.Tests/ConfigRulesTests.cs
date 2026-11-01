using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.Tests;

public sealed class ConfigRulesTests
{
    [Theory]
    [InlineData(ConfigKeys.ListenPort, 1, null)]
    [InlineData(ConfigKeys.ListenPort, 65535, null)]
    [InlineData(ConfigKeys.ListenPort, 0, "范围")]
    [InlineData(ConfigKeys.ListenPort, 65536, "范围")]
    [InlineData(ConfigKeys.PolicyControllerWaitMs, 0, null)]
    [InlineData(ConfigKeys.PolicyActRatePerMinute, 601, "范围")]
    [InlineData(ConfigKeys.LimitsMaxObserveElements, 49, "范围")]
    [InlineData(ConfigKeys.LimitsToolTimeoutSeconds, 41, "范围")]
    public void IntRangesFollowSpec(string key, int value, string? expectedErrorFragment)
    {
        var error = ConfigRules.ValidateInt(key, value);
        if (expectedErrorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Contains(expectedErrorFragment, error);
        }
    }

    [Theory]
    [InlineData(ConfigKeys.ScreenshotMode, "printwindow", true)]
    [InlineData(ConfigKeys.ScreenshotMode, "wgc", true)]
    [InlineData(ConfigKeys.ScreenshotMode, "dxgi", false)]
    [InlineData(ConfigKeys.UiNotificationsLevel, "all", true)]
    [InlineData(ConfigKeys.UiNotificationsLevel, "loud", false)]
    [InlineData(ConfigKeys.LoggingLevel, "information", true)]
    [InlineData(ConfigKeys.LoggingLevel, "verbose", false)]
    public void EnumRulesFollowSpec(string key, string value, bool valid) =>
        Assert.Equal(valid, ConfigRules.ValidateEnum(key, value) is null);

    [Theory]
    [InlineData("Ctrl+Alt+Shift+F12", true)]
    [InlineData("Ctrl+F5", true)]
    [InlineData("Win+Shift+A", true)]
    [InlineData("F12", false)]
    [InlineData("Ctrl+Ctrl+F12", false)]
    [InlineData("Ctrl+Mouse", false)]
    [InlineData("Ctrl+Shift+", false)]
    public void HotkeyGestureSyntaxIsValidated(string gesture, bool valid) =>
        Assert.Equal(valid, ConfigRules.ValidateHotkeyGesture(gesture) is null);

    [Theory]
    [InlineData("*password*", true)]
    [InlineData("", false)]
    [InlineData("a,b", false)]
    [InlineData("aaaa,bbbb", false)]
    public void SensitiveWindowGlobsAreValidated(string glob, bool valid)
    {
        var tooLong = new string('x', 65);
        Assert.Equal(valid, ConfigRules.ValidateGlob(glob) is null);
        Assert.NotNull(ConfigRules.ValidateGlob(tooLong));
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("999.1.1.1", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    public void ListenAddressIsValidated(string address, bool valid) =>
        Assert.Equal(valid, ConfigRules.ValidateAddress(address) is null);

    [Theory]
    [InlineData("listen.port", "WHALEGO_LISTEN__PORT")]
    [InlineData("ui.theme", "WHALEGO_UI__THEME")]
    [InlineData("policy.actRatePerMinute", "WHALEGO_POLICY__ACTRATEPERMINUTE")]
    public void EnvironmentOverrideNameIsDerived(string key, string expected) =>
        Assert.Equal(expected, ConfigRules.EnvironmentVariableName(key));

    [Fact]
    public void RestartKeysMatchSpec()
    {
        Assert.True(ConfigRules.RequiresRestart(ConfigKeys.ListenAddress));
        Assert.True(ConfigRules.RequiresRestart(ConfigKeys.ListenPort));
        Assert.True(ConfigRules.RequiresRestart(ConfigKeys.ScreenshotMode));
        Assert.True(ConfigRules.RequiresRestart(ConfigKeys.AuditDir));
        Assert.True(ConfigRules.RequiresRestart(ConfigKeys.LoggingDir));
        Assert.False(ConfigRules.RequiresRestart(ConfigKeys.PolicyReadonly));
        Assert.False(ConfigRules.RequiresRestart(ConfigKeys.UiTheme));
    }

    [Fact]
    public void SourceOverridesAreReadOnly()
    {
        Assert.True(ConfigRules.IsEnvironmentOrCommandLine(ConfigSourceKinds.Environment));
        Assert.True(ConfigRules.IsEnvironmentOrCommandLine(ConfigSourceKinds.CommandLine));
        Assert.False(ConfigRules.IsEnvironmentOrCommandLine(ConfigSourceKinds.File));
        Assert.False(ConfigRules.IsEnvironmentOrCommandLine(ConfigSourceKinds.Default));
        Assert.False(ConfigRules.IsEnvironmentOrCommandLine(null));
    }

    [Fact]
    public void ListenWarningsExplainTheConsequences()
    {
        Assert.Contains("容器", ConfigRules.WarningForAddress("127.0.0.1"));
        Assert.Contains("所有网卡", ConfigRules.WarningForAddress("0.0.0.0"));
        Assert.NotNull(ConfigRules.WarningForPort(80));
        Assert.Null(ConfigRules.WarningForPort(8765));
        Assert.True(ConfigRules.IsLocalhostAddress("127.0.0.1"));
        Assert.False(ConfigRules.IsLocalhostAddress("0.0.0.0"));
    }

    [Fact]
    public void AuditDirectoryValidation()
    {
        Assert.NotNull(ConfigRules.ValidateAuditDirectory("relative\\logs"));
        Assert.NotNull(ConfigRules.ValidateAuditDirectory(""));
        Assert.Null(ConfigRules.ValidateAuditDirectory(@"%EXEDIR%\logs"));
    }

    [Fact]
    public void DefaultsComeFromContractsOrGuiFallbacks()
    {
        Assert.Equal(ConfigDefaults.ListenPort, GuiConfigDefaults.Get(ConfigKeys.ListenPort));
        Assert.Equal(ConfigDefaults.AuditDir, GuiConfigDefaults.Get(ConfigKeys.LoggingDir));
    }
}
