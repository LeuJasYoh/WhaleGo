using System.Collections.ObjectModel;
using System.Windows.Input;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>One glob row (privacy.sensitiveWindows).</summary>
public sealed class GlobItem : ObservableObject
{
    private string _value = string.Empty;
    private string? _error;

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                Error = null;
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
}

/// <summary>Editable list of window-title glob patterns; 1–64 chars each, no commas (docs/GUI.md 6.2).</summary>
public sealed class GlobListField : ConfigField
{
    private IReadOnlyList<string> _loaded = [];

    public GlobListField(string key, string label, string? help = null)
        : base(key, label, help)
    {
        Items = [];
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        AddCommand = new RelayCommand(() => Add());
        RemoveCommand = new RelayCommand<GlobItem>(Remove);
    }

    public ObservableCollection<GlobItem> Items { get; }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public override bool IsDirty
    {
        get
        {
            var current = Items.Select(static item => item.Value.Trim()).ToList();
            return !current.SequenceEqual(_loaded, StringComparer.Ordinal);
        }
    }

    public void Add(string value = "")
    {
        var item = new GlobItem { Value = value };
        Items.Add(item);
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GlobItem.Value))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
    }

    private void Remove(GlobItem item)
    {
        Items.Remove(item);
        OnPropertyChanged(nameof(IsDirty));
    }

    protected override void OnLoad(object? value)
    {
        Items.Clear();
        _loaded = ConfigValue.AsStringList(value).Select(static item => item.Trim()).Where(static item => item.Length > 0).ToList();
        foreach (var item in _loaded)
        {
            Add(item);
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    public override object? ToConfigValue() => Items.Select(static item => item.Value.Trim()).Where(static item => item.Length > 0).ToArray();

    public override bool Validate()
    {
        var valid = true;
        foreach (var item in Items)
        {
            item.Error = ConfigRules.ValidateGlob(item.Value);
            if (item.Error is not null)
            {
                valid = false;
            }
        }

        SetError(valid ? null : "有匹配规则不合法");
        return valid;
    }

    protected override void OnCommit()
    {
        _loaded = Items.Select(static item => item.Value.Trim()).Where(static item => item.Length > 0).ToList();
    }

    public override void Revert()
    {
        Items.Clear();
        foreach (var item in _loaded)
        {
            Add(item);
        }

        SetError(null);
        OnPropertyChanged(nameof(IsDirty));
    }
}
