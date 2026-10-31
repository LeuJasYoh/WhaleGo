using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>String row with a page-supplied validator (listen.address, audit.dir, hotkey gesture, ...).</summary>
public sealed class StringField : ConfigField
{
    private readonly Func<string, string?>? _validator;
    private readonly Func<string, string?>? _warningProvider;
    private string _value = string.Empty;
    private string _loaded = string.Empty;

    public StringField(
        string key,
        string label,
        string? help = null,
        int maxLength = 512,
        Func<string, string?>? validator = null,
        Func<string, string?>? warningProvider = null)
        : base(key, label, help)
    {
        MaxLength = maxLength;
        _validator = validator;
        _warningProvider = warningProvider;
    }

    public int MaxLength { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                Error = null;
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public override bool IsDirty => !string.Equals(_value, _loaded, StringComparison.Ordinal);

    protected override void OnLoad(object? value)
    {
        _value = ConfigValue.AsString(value) ?? string.Empty;
        _loaded = _value;
        OnPropertyChanged(nameof(Value));
        UpdateWarning();
    }

    public override object? ToConfigValue() => _value.Trim();

    public override bool Validate()
    {
        var text = _value.Trim();
        string? error;
        if (text.Length == 0)
        {
            error = "不能为空";
        }
        else if (text.Length > MaxLength)
        {
            error = "最多 " + MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个字符";
        }
        else
        {
            error = _validator?.Invoke(text);
        }

        SetError(error);
        UpdateWarning();
        return error is null;
    }

    protected override void OnCommit()
    {
        _value = _value.Trim();
        _loaded = _value;
        OnPropertyChanged(nameof(Value));
    }

    public override void Revert()
    {
        _value = _loaded;
        OnPropertyChanged(nameof(Value));
        SetError(null);
        UpdateWarning();
    }

    private void UpdateWarning() => Warning = _warningProvider?.Invoke(_value.Trim());
}
