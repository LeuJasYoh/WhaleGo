using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>Boolean toggle (policy.readonly, screenshot.enabled, ...).</summary>
public sealed class BoolField : ConfigField
{
    private bool _value;
    private bool _loaded;

    public BoolField(string key, string label, string? help = null, string? onText = null, string? offText = null)
        : base(key, label, help)
    {
        OnText = onText ?? "开";
        OffText = offText ?? "关";
    }

    public string OnText { get; }

    public string OffText { get; }

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => _value ? OnText : OffText;

    public override bool IsDirty => _value != _loaded;

    protected override void OnLoad(object? value)
    {
        _value = ConfigValue.AsBool(value) ?? false;
        _loaded = _value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(StateText));
    }

    public override object? ToConfigValue() => _value;

    public override bool Validate() => true;

    public override void Revert()
    {
        Value = _loaded;
        SetError(null);
    }

    protected override void OnCommit()
    {
        _loaded = _value;
    }
}
