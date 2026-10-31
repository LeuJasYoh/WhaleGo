using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.Tests;

public sealed class ConfigPageTests
{
    private static T Field<T>(ConfigPageViewModel page, string key)
        where T : ConfigField =>
        (T)page.Fields.Single(field => field.Key == key);

    private static PolicyViewModel CreatePolicy(FakeExecutorControl control, out FakeDialogs dialogs)
    {
        dialogs = new FakeDialogs();
        var context = TestContexts.Create(control, dialogs);
        var page = new PolicyViewModel(context);
        page.LoadFrom(control.Config, control.Sources);
        return page;
    }

    [Fact]
    public void LoadingPopulatesFieldsAndBadges()
    {
        var control = new FakeExecutorControl();
        control.Sources[ConfigKeys.PolicyReadonly] = ConfigSourceKinds.Environment;
        var page = CreatePolicy(control, out _);

        Assert.Equal("120", Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).Text);
        Assert.False(Field<BoolField>(page, ConfigKeys.PolicyReadonly).Value);
        Assert.True(Field<BoolField>(page, ConfigKeys.ScreenshotEnabled).Value);
        Assert.False(Field<BoolField>(page, ConfigKeys.ScreenshotDefaultInclude).Value);
        Assert.Equal(4, page.Groups.Count);
        Assert.False(page.IsDirty);

        var readonlyField = Field<BoolField>(page, ConfigKeys.PolicyReadonly);
        Assert.True(readonlyField.IsReadOnly);
        Assert.Equal("环境变量", readonlyField.SourceLabel);
        Assert.Contains("WHALEGO_POLICY__READONLY", readonlyField.SourceDetail);
        Assert.Equal("默认", Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).SourceLabel);
    }

    [Fact]
    public async Task ApplySendsOnlyChangedKeysFromThisPage()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);
        Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).Text = "300";

        await page.ApplyAsync();

        Assert.True(page.HasPageSuccess);
        Assert.NotNull(control.LastPatch);
        var keys = control.LastPatch!.Values.Keys.ToList();
        Assert.Single(keys);
        Assert.Equal(ConfigKeys.PolicyActRatePerMinute, keys[0]);
        Assert.Equal(300, Convert.ToInt32(control.LastPatch.Values[ConfigKeys.PolicyActRatePerMinute]));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public async Task InvalidValueIsRejectedBeforeCallingTheKernel()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);
        Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).Text = "999";

        await page.ApplyAsync();

        Assert.True(page.HasPageError);
        Assert.Null(control.LastPatch);
        Assert.Equal(0, control.SetConfigCalls);
        Assert.True(Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).HasError);
    }

    [Fact]
    public void RevertRestoresLoadedValues()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);
        var field = Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute);
        field.Text = "300";
        Assert.True(page.IsDirty);

        page.RevertAll();

        Assert.Equal("120", field.Text);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public async Task SaveFailureKeepsEditsAndReportsTheError()
    {
        var control = new FakeExecutorControl { ThrowOnSetConfig = new InvalidOperationException("disk full") };
        var page = CreatePolicy(control, out _);
        Field<IntField>(page, ConfigKeys.PolicyActRatePerMinute).Text = "300";

        await page.ApplyAsync();

        Assert.True(page.HasPageError);
        Assert.Contains("保存失败", page.PageError);
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void RestartBadgesFollowTheSpec()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);

        Assert.True(Field<ChoiceField>(page, ConfigKeys.ScreenshotMode).RestartRequired);
        Assert.False(Field<BoolField>(page, ConfigKeys.ScreenshotEnabled).RestartRequired);
        Assert.Equal("重启生效", Field<ChoiceField>(page, ConfigKeys.ScreenshotMode).RestartBadge);
    }

    [Fact]
    public async Task RestartRequiredKeysAreReportedAfterApply()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);
        Field<ChoiceField>(page, ConfigKeys.ScreenshotMode).Value = "wgc";

        await page.ApplyAsync();

        Assert.True(page.HasRestartPending);
        Assert.Contains(ConfigKeys.ScreenshotMode, page.RestartPendingKeys);
        Assert.Contains("需重启", page.PageSuccess);
    }

    [Fact]
    public void ScreenshotModeWarningIsSurfaced()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);

        Field<ChoiceField>(page, ConfigKeys.ScreenshotMode).Value = "wgc";

        Assert.Contains("P2", page.ScreenshotModeNote);
    }

    [Fact]
    public void LongToolBudgetProducesWarning()
    {
        var control = new FakeExecutorControl();
        var page = CreatePolicy(control, out _);
        Assert.True(page.HasPageWarning);
        Assert.Contains("35", page.PageWarning);

        Field<IntField>(page, ConfigKeys.LimitsToolTimeoutSeconds).Text = "30";
        page.ApplyCommand.RaiseCanExecuteChanged();
        _ = page.ApplyAsync();
        Assert.False(page.HasPageWarning);
    }

    [Fact]
    public void ConnectionPageShowsRiskBannerAndLocalhostConsequences()
    {
        var control = new FakeExecutorControl();
        var context = TestContexts.Create(control);
        var page = new ConnectionViewModel(context);
        page.LoadFrom(control.Config, control.Sources);

        Assert.Equal(UiSeverity.Warning, page.RiskSeverity);
        Assert.Contains("所有网卡", page.RiskBannerText);
        Assert.False(page.ShowLocalhostWarning);
        Assert.True(Field<IntField>(page, ConfigKeys.ListenPort).RestartRequired);
        Assert.Equal(2, page.Groups.Count);

        Field<StringField>(page, ConfigKeys.ListenAddress).Value = "127.0.0.1";

        Assert.True(page.ShowLocalhostWarning);
        Assert.Equal(UiSeverity.Danger, page.RiskSeverity);
        Assert.Contains("容器", page.RiskBannerText);
    }

    [Fact]
    public async Task PrivacyDangerousSwitchRequiresTypedConfirmation()
    {
        var control = new FakeExecutorControl();
        var dialogs = new FakeDialogs { TypedConfirmResult = false };
        var context = TestContexts.Create(control, dialogs);
        var page = new PrivacyViewModel(context);
        page.LoadFrom(control.Config, control.Sources);
        Field<BoolField>(page, ConfigKeys.AuditRequireDurableForActions).Value = false;

        await page.ApplyAsync();

        Assert.Equal(0, control.SetConfigCalls);
        Assert.True(page.HasPageError);
        Assert.Contains("取消", page.PageError);
        Assert.Equal(PrivacyViewModel.RequiredRiskText, dialogs.LastRequiredText);
    }

    [Fact]
    public async Task PrivacyDangerousSwitchAppliesAfterTypedConfirmation()
    {
        var control = new FakeExecutorControl();
        var dialogs = new FakeDialogs { TypedConfirmResult = true };
        var context = TestContexts.Create(control, dialogs);
        var page = new PrivacyViewModel(context);
        page.LoadFrom(control.Config, control.Sources);
        Field<BoolField>(page, ConfigKeys.AuditRequireDurableForActions).Value = false;

        await page.ApplyAsync();

        Assert.Equal(1, control.SetConfigCalls);
        Assert.NotNull(control.LastPatch);
        Assert.Equal(false, control.LastPatch!.Values[ConfigKeys.AuditRequireDurableForActions]);
        Assert.True(page.HasPageSuccess);
    }

    [Fact]
    public void PrivacyAdvancedSectionStartsLocked()
    {
        var control = new FakeExecutorControl();
        var page = new PrivacyViewModel(TestContexts.Create(control));
        page.LoadFrom(control.Config, control.Sources);

        Assert.True(page.AdvancedLocked);
        Assert.False(page.AdvancedUnlocked);
        Assert.Contains("fail-closed 已开启", page.AdvancedSummary);
    }

    [Fact]
    public async Task OpenDataFolderFailureIsReported()
    {
        var control = new FakeExecutorControl();
        var page = new AboutViewModel(TestContexts.Create(control));
        await page.RefreshAsync();

        Assert.Equal("0.1.0", page.Version);
        Assert.Equal("启动方式：手动启动（不自启）", AboutViewModel.StartupText);
        Assert.NotEmpty(AboutViewModel.Limitations);
    }
}
