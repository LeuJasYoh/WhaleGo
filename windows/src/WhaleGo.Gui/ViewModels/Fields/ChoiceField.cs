using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>One selectable enum value with an optional note (e.g. the P2 placeholder screenshot mode "wgc").</summary>
public sealed record ChoiceOption(string Value, string Label, string? Note = null);

/// <summary>Enum picker (screenshot.mode, ui.notificationsLevel, logging.level).</summary>
public sealed class ChoiceField : ConfigField
{
    private string _value = string.Empty;
    private string _loaded = string.Empty;

    public ChoiceField(string key, string label, IReadOnlyList<ChoiceOption> options, string? help = null)
        : base(key, label, help)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <summary>Bound to ComboBox.SelectedValue (SelectedValuePath="Value").</summary>
    public string Value
    {
        get => _value;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _value, normalized))
            {
                Error = null;
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(SelectedOption));
            }
        }
    }

    public ChoiceOption? SelectedOption => Options.FirstOrDefault(option => string.Equals(option.Value, _value, StringComparison.Ordinal));

    public override bool IsDirty => !string.Equals(_value, _loaded, StringComparison.Ordinal);

    protected override void OnLoad(object? value)
    {
        _value = ConfigValue.AsString(value) ?? Options.FirstOrDefault()?.Value ?? string.Empty;
        _loaded = _value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SelectedOption));
        Validate();
    }

    public override object? ToConfigValue() => _value;

    public override bool Validate()
    {
        var error = ConfigRules.ValidateEnum(Key, _value);
        SetError(error);
        Warning = SelectedOption?.Note;
        return error is null;
    }

    protected override void OnCommit()
    {
        _loaded = _value;
    }

    public override void Revert()
    {
        _value = _loaded;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SelectedOption));
        SetError(null);
        Validate();
    }
}
