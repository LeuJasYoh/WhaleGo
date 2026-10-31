using System.Collections.Generic;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Privacy &amp; audit page: privacy.* + audit.*, including the dangerous fail-closed switch that stays locked
/// behind a type-to-confirm gate ("我了解风险", docs/GUI.md 9.7).
/// </summary>
public sealed class PrivacyViewModel : ConfigPageViewModel
{
    public const string RequiredRiskText = "我了解风险";

    private readonly BoolField _maskPasswordFields;
    private readonly GlobListField _sensitiveWindows;
    private readonly BoolField _auditEnabled;
    private readonly StringField _auditDir;
    private readonly BoolField _requireDurable;
    private readonly ConfigFieldGroup _advancedGroup;
    private bool _advancedUnlocked;
    private string _actionMessage = string.Empty;

    public PrivacyViewModel(GuiContext context)
        : this(
            context,
            new BoolField(
                ConfigKeys.PrivacyMaskPasswordFields,
                "密码字段脱敏",
                "关闭后 UIA 密码框的值可能被读取（违反 day-1 隐私规则，不推荐）",
                "脱敏",
                "不脱敏"),
            new GlobListField(
                ConfigKeys.PrivacySensitiveWindows,
                "敏感窗口标题匹配",
                "glob 通配，每项 1–64 字符；命中时只返回树、不返回截图（screenshot_suppressed）"),
            new BoolField(ConfigKeys.AuditEnabled, "启用审计", "关闭后动作不落盘，事故无法复盘（不推荐）", "启用", "关闭"),
            new StringField(
                ConfigKeys.AuditDir,
                "审计目录",
                "必须可写；不可写时动作会 fail-closed 被拒绝。支持 %LOCALAPPDATA% 等环境变量。修改需重启",
                maxLength: 260,
                validator: ConfigRules.ValidateAuditDirectory),
            new BoolField(
                ConfigKeys.AuditRequireDurableForActions,
                "动作审计 fail-closed",
                "开启时：动作审计无法落盘就拒绝动作（推荐）。关闭后：磁盘写不进去也会照做，事故无法复盘。",
                "fail-closed",
                "非 fail-closed"))
    {
    }

    private PrivacyViewModel(
        GuiContext context,
        BoolField maskPasswordFields,
        GlobListField sensitiveWindows,
        BoolField auditEnabled,
        StringField auditDir,
        BoolField requireDurable)
        : base("隐私与审计", "隐私与审计", "脱敏、敏感窗口与审计 fail-closed", context,
            BuildGroups(maskPasswordFields, sensitiveWindows, auditEnabled, auditDir))
    {
        _maskPasswordFields = maskPasswordFields;
        _sensitiveWindows = sensitiveWindows;
        _auditEnabled = auditEnabled;
        _auditDir = auditDir;
        _requireDurable = requireDurable;
        _advancedGroup = new ConfigFieldGroup(
            "高级（不推荐）",
            "默认锁定；解锁后仍需输入「我了解风险」才能保存。",
            [requireDurable]);
        _requireDurable.PropertyChanged += (_, _) => RaiseStateChanged();
        UnlockAdvancedCommand = new RelayCommand(UnlockAdvanced);
        LockAdvancedCommand = new RelayCommand(LockAdvanced, () => AdvancedUnlocked);
        _auditEnabled.PropertyChanged += (_, _) => UpdateBanners();
        _requireDurable.PropertyChanged += (_, _) => UpdateBanners();
        _auditDir.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(StringField.Value) or nameof(ConfigField.Warning))
            {
                Context.AuditDirectory = _auditDir.Value;
            }
        };
        UpdateBanners();
    }

    public ConfigFieldGroup AdvancedGroup => _advancedGroup;

    /// <summary>The dangerous switch lives in its own locked card but must take part in dirty/apply semantics.</summary>
    public override IEnumerable<ConfigField> Fields => _advancedGroup is null
        ? base.Fields
        : base.Fields.Concat(_advancedGroup.Fields);

    public bool AdvancedUnlocked
    {
        get => _advancedUnlocked;
        private set
        {
            if (SetProperty(ref _advancedUnlocked, value))
            {
                OnPropertyChanged(nameof(AdvancedLocked));
                LockAdvancedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AdvancedLocked => !AdvancedUnlocked;

    public string AdvancedSummary => _requireDurable.Value
        ? "当前：fail-closed 已开启（推荐）"
        : "当前：fail-closed 已关闭（危险）";

    public RelayCommand UnlockAdvancedCommand { get; }

    public RelayCommand LockAdvancedCommand { get; }

    public UiSeverity AuditSeverity => !_auditEnabled.Value || !_requireDurable.Value ? UiSeverity.Danger : UiSeverity.Info;

    public string AuditBannerText => !_auditEnabled.Value
        ? "审计已关闭：动作不会留下可复盘记录，强烈建议重新启用。"
        : !_requireDurable.Value
            ? "fail-closed 已关闭：磁盘写入失败时动作仍会执行，事故可能无法复盘。"
            : "审计开启且 fail-closed：动作用于落盘失败时会被拒绝（推荐）。";

    public bool ShowAuditBanner => !_auditEnabled.Value || !_requireDurable.Value;

    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (SetProperty(ref _actionMessage, value))
            {
                OnPropertyChanged(nameof(HasActionMessage));
            }
        }
    }

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessage);

    protected override void OnLoaded()
    {
        AdvancedUnlocked = false;
        Context.AuditDirectory = _auditDir.Value;
        UpdateBanners();
    }

    protected override string? ValidatePage()
    {
        UpdateBanners();
        return null;
    }

    protected override async Task<bool> ConfirmApplyAsync()
    {
        if (_maskPasswordFields.IsDirty && !_maskPasswordFields.Value &&
            !Context.Dialogs.Confirm(
                "关闭密码字段脱敏",
                "关闭后，UIA 密码框的输入可能被模型读取。这违反 day-1 隐私规则，确定继续吗？",
                "关闭脱敏",
                danger: true))
        {
            return false;
        }

        if (_auditEnabled.IsDirty && !_auditEnabled.Value &&
            !Context.Dialogs.Confirm(
                "关闭审计",
                "关闭后所有动作不再写审计文件，事故无法复盘。确定关闭吗？",
                "关闭审计",
                danger: true))
        {
            return false;
        }

        if (_requireDurable.IsDirty && !_requireDurable.Value &&
            !Context.Dialogs.ConfirmWithTypedText(
                "关闭动作审计 fail-closed",
                "关闭后，即使审计写不进去，动作仍会执行，事故可能无法复盘。请输入「我了解风险」确认。",
                RequiredRiskText,
                detail: "该开关属于「高级（不推荐）」，建议保持 fail-closed。"))
        {
            return false;
        }

        await Task.CompletedTask.ConfigureAwait(true);
        return true;
    }

    protected override void OnApplied(IReadOnlyDictionary<string, object?> applied)
    {
        Context.AuditDirectory = _auditDir.Value;
        UpdateBanners();
        if (!_requireDurable.Value || !_auditEnabled.Value)
        {
            Context.Balloon?.Invoke(
                WhaleGo.Gui.Tray.BalloonKind.Info,
                "WhaleGo 审计状态已变更",
                "当前审计非 fail-closed / 已关闭，请在「隐私与审计」页确认这是预期配置。");
        }
    }

    private void UnlockAdvanced()
    {
        if (Context.Dialogs.ConfirmWithTypedText(
                "解锁高级设置",
                "该区域包含危险开关：关闭 fail-closed 后，磁盘写入失败时动作仍会执行，事故无法复盘。请输入「我了解风险」解锁。",
                RequiredRiskText,
                detail: "解锁只影响本页显示；每次保存危险值仍需再次输入确认。"))
        {
            AdvancedUnlocked = true;
            ActionMessage = "已解锁高级设置；请谨慎修改";
        }
    }

    private void LockAdvanced()
    {
        _requireDurable.Revert();
        AdvancedUnlocked = false;
        ActionMessage = "已重新锁定高级设置";
    }

    private void UpdateBanners()
    {
        OnPropertyChanged(nameof(AdvancedSummary));
        OnPropertyChanged(nameof(AuditBannerText));
        OnPropertyChanged(nameof(AuditSeverity));
        OnPropertyChanged(nameof(ShowAuditBanner));
    }

    private static IReadOnlyList<ConfigFieldGroup> BuildGroups(
        BoolField maskPasswordFields,
        GlobListField sensitiveWindows,
        BoolField auditEnabled,
        StringField auditDir)
    {
        return
        [
            new ConfigFieldGroup("隐私", "day-1 隐私规则，热生效", [maskPasswordFields, sensitiveWindows]),
            new ConfigFieldGroup("审计", "动作审计在返回前落盘；观察审计可异步", [auditEnabled, auditDir]),
        ];
    }
}
