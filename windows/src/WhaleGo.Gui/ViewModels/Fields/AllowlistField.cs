using System.Collections.ObjectModel;
using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>One launch.allowlist row (name + absolute existing path).</summary>
public sealed class AllowlistEntryViewModel : ObservableObject
{
    private readonly Func<string?>? _pickFile;
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string? _nameError;
    private string? _pathError;

    public AllowlistEntryViewModel(string name, string path, Func<string?>? pickFile)
    {
        _name = name;
        _path = path;
        _pickFile = pickFile;
        RemoveCommand = new RelayCommand(() => RemoveRequested?.Invoke(this, EventArgs.Empty));
        BrowseCommand = new RelayCommand(Browse);
    }

    public event EventHandler? RemoveRequested;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                NameError = null;
            }
        }
    }

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value ?? string.Empty))
            {
                PathError = null;
            }
        }
    }

    public string? NameError
    {
        get => _nameError;
        set
        {
            if (SetProperty(ref _nameError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string? PathError
    {
        get => _pathError;
        set
        {
            if (SetProperty(ref _pathError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(NameError) || !string.IsNullOrEmpty(PathError);

    public ICommand RemoveCommand { get; }

    public ICommand BrowseCommand { get; }

    private void Browse()
    {
        var picked = _pickFile?.Invoke();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Path = picked;
        }
    }
}

/// <summary>
/// launch.allowlist editor: the ONLY programs the model may start (PROTOCOL 3.8). Name is the model-facing key,
/// path must be absolute and exist; duplicates are rejected.
/// </summary>
public sealed class AllowlistField : ConfigField
{
    private readonly Func<string?>? _pickFile;
    private IReadOnlyList<LaunchAllowlistEntry> _loaded = [];
    private bool _suppressDirty;

    public AllowlistField(string key, string label, string? help, Func<string?>? pickFile)
        : base(key, label, help)
    {
        _pickFile = pickFile;
        Entries = [];
        Entries.CollectionChanged += (_, _) =>
        {
            if (!_suppressDirty)
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
        AddCommand = new RelayCommand(() => Add(string.Empty, string.Empty));
    }

    public ObservableCollection<AllowlistEntryViewModel> Entries { get; }

    public ICommand AddCommand { get; }

    public override bool IsDirty
    {
        get
        {
            var current = Entries.Select(static entry => entry.Name.Trim() + "\u0000" + entry.Path.Trim()).ToList();
            var loaded = _loaded.Select(static entry => entry.Name.Trim() + "\u0000" + entry.Path.Trim()).ToList();
            return !current.SequenceEqual(loaded, StringComparer.Ordinal);
        }
    }

    public void Add(string name, string path)
    {
        var entry = new AllowlistEntryViewModel(name, path, _pickFile);
        entry.RemoveRequested += (sender, _) =>
        {
            if (sender is AllowlistEntryViewModel removed)
            {
                Entries.Remove(removed);
            }
        };
        entry.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AllowlistEntryViewModel.Name) or nameof(AllowlistEntryViewModel.Path) && !_suppressDirty)
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
        Entries.Add(entry);
    }

    protected override void OnLoad(object? value)
    {
        _suppressDirty = true;
        try
        {
            Entries.Clear();
            _loaded = ConfigValue.AsAllowlist(value);
            foreach (var entry in _loaded)
            {
                Add(entry.Name, entry.Path);
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    public override object? ToConfigValue() =>
        Entries.Select(static entry => new LaunchAllowlistEntry(entry.Name.Trim(), entry.Path.Trim())).ToList();

    public override bool Validate()
    {
        var valid = true;
        var names = new List<string>();
        foreach (var entry in Entries)
        {
            entry.NameError = ConfigRules.ValidateAllowlistName(entry.Name, names);
            if (entry.NameError is not null)
            {
                valid = false;
            }
            else
            {
                names.Add(entry.Name.Trim());
            }

            entry.PathError = ConfigRules.ValidateAllowlistPath(entry.Path);
            if (entry.PathError is not null)
            {
                valid = false;
            }
        }

        SetError(valid ? null : "白名单有未通过校验的条目");
        return valid;
    }

    protected override void OnCommit()
    {
        _loaded = Entries.Select(static entry => new LaunchAllowlistEntry(entry.Name.Trim(), entry.Path.Trim())).ToList();
    }

    public override void Revert()
    {
        _suppressDirty = true;
        try
        {
            Entries.Clear();
            foreach (var entry in _loaded)
            {
                Add(entry.Name, entry.Path);
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        SetError(null);
        OnPropertyChanged(nameof(IsDirty));
    }
}
