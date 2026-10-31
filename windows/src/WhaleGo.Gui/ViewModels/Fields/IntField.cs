using System.Globalization;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>Integer row with the documented range check (docs/GUI.md 6.2).</summary>
public sealed class IntField : ConfigField
{
    private string _text = string.Empty;
    private int _loaded;

    public IntField(string key, string label, string? help = null)
        : base(key, label, help)
    {
        if (!ConfigRules.IntRanges.TryGetValue(key, out var range))
        {
            Range = null;
        }
        else
        {
            Range = range;
        }
    }

    public (int Min, int Max)? Range { get; }

    public string RangeHint => Range is null
        ? string.Empty
        : "范围 " + Range.Value.Min.ToString(CultureInfo.InvariantCulture) + "–" + Range.Value.Max.ToString(CultureInfo.InvariantCulture);

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Error = null;
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public int? NumericValue => int.TryParse(_text.Trim(), out var parsed) ? parsed : null;

    public override bool IsDirty => NumericValue != _loaded;

    protected override void OnLoad(object? value)
    {
        var loaded = ConfigValue.AsInt(value);
        _loaded = loaded ?? 0;
        _text = loaded?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        OnPropertyChanged(nameof(Text));
        UpdateWarning();
    }

    public override object? ToConfigValue() => NumericValue;

    public override bool Validate()
    {
        var error = ConfigRules.ValidateInt(Key, NumericValue);
        SetError(error);
        UpdateWarning();
        return error is null;
    }

    public override void Revert()
    {
        _text = _loaded.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Text));
        SetError(null);
        UpdateWarning();
    }

    protected override void OnCommit()
    {
        _loaded = NumericValue ?? _loaded;
        _text = _loaded.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Text));
    }

    private void UpdateWarning()
    {
        Warning = Key == WhaleGo.Contracts.Model.ConfigKeys.ListenPort
            ? ConfigRules.WarningForPort(NumericValue)
            : null;
    }
}
