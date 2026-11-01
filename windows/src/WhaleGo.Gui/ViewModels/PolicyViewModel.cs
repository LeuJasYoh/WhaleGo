using System.Collections.Generic;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Execution policy page: policy.* + limits.* + screenshot.* + readonly toggle (docs/GUI.md 6.1 / 6.2 / 9.8).
/// Screenshot defaults are "enabled but not auto-attached". go_launch has no allowlist.
/// </summary>
public sealed class PolicyViewModel : ConfigPageViewModel
{
    public const string ScreenshotPrivacyNote =
        "截图默认开启，但默认不自动附图：模型需要显式 include_screenshot=true 才会收到图片；" +
        "敏感窗口自动抑制、本程序窗口永不截图。图片含隐私，不得转发。";

    private readonly BoolField _readonly;
    private readonly IntField _toolTimeout;
    private readonly ChoiceField _screenshotMode;
    private readonly BoolField _screenshotEnabled;

    public PolicyViewModel(GuiContext context)
        : this(
            context,
            new BoolField(ConfigKeys.PolicyReadonly, "只读模式", "开启后模型只能观察，所有动作被拒绝（插件侧 readonly 是双保险）", "开", "关"),
            new IntField(ConfigKeys.PolicyActRatePerMinute, "每分钟动作上限", "与插件全局并发限制共同限流"),
            new IntField(ConfigKeys.PolicyControllerWaitMs, "等待租约上限(ms)", "只影响等待其它会话释放租约的时间，租约 TTL 固定 60s"),
            new IntField(ConfigKeys.PolicyStateTtlSeconds, "state 有效期(秒)", "过大容易引发 STALE_STATE 风暴"),
            new IntField(ConfigKeys.PolicyFrameTtlSeconds, "frame 有效期(秒)", "坐标动作依赖最近一次带截图的观察"),
            new IntField(ConfigKeys.LimitsMaxObserveElements, "单次观察元素上限", "协议上限 2000"),
            new IntField(ConfigKeys.LimitsMaxObserveChars, "单次观察文本上限", "超出会截断并给出 tree_budget_exceeded"),
            new IntField(ConfigKeys.LimitsMaxImageWidth, "截图最大宽度(px)", "按此宽度压缩，坐标仍按图片像素换算"),
            new IntField(ConfigKeys.LimitsMaxImageBytes, "截图最大字节", "约 200 KB 默认，超限压缩"),
            new IntField(ConfigKeys.LimitsToolTimeoutSeconds, "单工具预算(秒)", "必须小于插件 timeout 至少 5s（插件默认 45s）"),
            new BoolField(ConfigKeys.ScreenshotEnabled, "启用截图", "执行器总开关；关闭后模型请求截图只会得到树 + screenshot_disabled", "启用", "关闭"),
            new ChoiceField(
                ConfigKeys.ScreenshotMode,
                "截图方式",
                [
                    new ChoiceOption("printwindow", "PrintWindow（默认）", null),
                    new ChoiceOption("wgc", "WGC（P2 占位）", "wgc 为 P2 占位：选择后会回退到 printwindow 并提示"),
                ],
                "切换方式需要重启执行器"),
            new BoolField(
                ConfigKeys.ScreenshotDefaultInclude,
                "默认自动附图",
                "开启后模型省略 include_screenshot 时也会附图（仍受三开关 AND 约束）；默认关闭更省 token、更少隐私暴露",
                "开启",
                "关闭"))
    {
    }

    private PolicyViewModel(
        GuiContext context,
        BoolField readonlyField,
        IntField actRate,
        IntField controllerWait,
        IntField stateTtl,
        IntField frameTtl,
        IntField maxObserveElements,
        IntField maxObserveChars,
        IntField maxImageWidth,
        IntField maxImageBytes,
        IntField toolTimeout,
        BoolField screenshotEnabled,
        ChoiceField screenshotMode,
        BoolField defaultInclude)
        : base("执行策略", "执行策略", "动作、预算与截图", context,
            BuildGroups(
                readonlyField, actRate, controllerWait, stateTtl, frameTtl,
                maxObserveElements, maxObserveChars, maxImageWidth, maxImageBytes, toolTimeout,
                screenshotEnabled, screenshotMode, defaultInclude))
    {
        _readonly = readonlyField;
        _toolTimeout = toolTimeout;
        _screenshotMode = screenshotMode;
        _screenshotEnabled = screenshotEnabled;
        _screenshotMode.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChoiceField.SelectedOption) or nameof(ChoiceField.Value))
            {
                OnPropertyChanged(nameof(ScreenshotModeNote));
            }
        };
    }

    public string ScreenshotModeNote => _screenshotMode.SelectedOption?.Note ?? string.Empty;

    public bool ScreenshotEnabled => _screenshotEnabled.Value;

    public bool ReadonlyEnabled => _readonly.Value;

    protected override void OnLoaded()
    {
        UpdateWarnings();
    }

    protected override string? ValidatePage()
    {
        UpdateWarnings();
        return null;
    }

    private void UpdateWarnings()
    {
        var warnings = new List<string>();
        if (_toolTimeout.NumericValue is > 35)
        {
            warnings.Add("单工具预算超过 35 秒时，插件默认 timeout(45s) 可能先超时：建议 ≤ 35s 并保持插件 timeout ≥ 预算 + 5s");
        }

        PageWarning = warnings.Count > 0 ? string.Join("；", warnings) : null;
        OnPropertyChanged(nameof(ScreenshotModeNote));
        OnPropertyChanged(nameof(ScreenshotEnabled));
        OnPropertyChanged(nameof(ReadonlyEnabled));
    }

    private static IReadOnlyList<ConfigFieldGroup> BuildGroups(
        BoolField readonlyField,
        IntField actRate,
        IntField controllerWait,
        IntField stateTtl,
        IntField frameTtl,
        IntField maxObserveElements,
        IntField maxObserveChars,
        IntField maxImageWidth,
        IntField maxImageBytes,
        IntField toolTimeout,
        BoolField screenshotEnabled,
        ChoiceField screenshotMode,
        BoolField defaultInclude) =>
    [
        new ConfigFieldGroup("执行策略", "热生效；只读与插件侧开关任一开启即拒绝动作", [readonlyField, actRate, controllerWait, stateTtl, frameTtl]),
        new ConfigFieldGroup("预算", "限制单次观察与动作的资源占用，全部热生效", [maxObserveElements, maxObserveChars, maxImageWidth, maxImageBytes, toolTimeout]),
        new ConfigFieldGroup("截图", "截图默认开启但不自动附图（三开关 AND）", [screenshotEnabled, screenshotMode, defaultInclude]),
    ];
}
