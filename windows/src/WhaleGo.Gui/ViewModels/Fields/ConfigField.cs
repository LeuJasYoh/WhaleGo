using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>
/// One editable configuration row. Fields own their validation + dirty tracking so every page can apply atomically
/// (docs/GUI.md 6.4): only changed keys are patched, and source badges lock environment/command-line overrides.
/// </summary>
public abstract class ConfigField : ObservableObject
{
    private string _sourceKind = ConfigSourceKinds.Default;
    private string? _error;
    private string? _warning;

    protected ConfigField(string key, string label, string? help = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
        Label = label;
        Help = help;
    }

    public string Key { get; }

    public string Label { get; }

    public string? Help { get; }

    public bool RestartRequired => ConfigRules.RequiresRestart(Key);

    public string RestartBadge => RestartRequired ? "重启生效" : string.Empty;

    public bool ShowRestartBadge => RestartRequired;

    public string SourceKind
    {
        get => _sourceKind;
        internal set
        {
            if (SetProperty(ref _sourceKind, value))
            {
                OnPropertyChanged(nameof(SourceLabel));
                OnPropertyChanged(nameof(SourceDetail));
                OnPropertyChanged(nameof(IsOverridden));
                OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public string SourceLabel => SourceKind switch
    {
        ConfigSourceKinds.File => "配置文件",
        ConfigSourceKinds.Environment => "环境变量",
        ConfigSourceKinds.CommandLine => "命令行",
        ConfigSourceKinds.Unavailable => "不可用",
        _ => "默认",
    };

    /// <summary>For overridden keys: the concrete override name ("改 GUI 不生效").</summary>
    public string SourceDetail => IsOverridden ? ConfigRules.EnvironmentVariableName(Key) : string.Empty;

    public bool IsOverridden => ConfigRules.IsEnvironmentOrCommandLine(SourceKind) || SourceKind == ConfigSourceKinds.Unavailable;

    /// <summary>Environment/command-line overrides are read-only (docs/GUI.md 6.4); the kernel would ignore the patch.</summary>
    public bool IsReadOnly => IsOverridden;

    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string? Warning
    {
        get => _warning;
        protected set
        {
            if (SetProperty(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public abstract bool IsDirty { get; }

    /// <summary>Loads a value coming from ConfigPatch (defensive conversion) and resets dirty state.</summary>
    public void LoadFrom(object? value)
    {
        Error = null;
        Warning = null;
        OnLoad(value);
        OnPropertyChanged(nameof(IsDirty));
    }

    protected abstract void OnLoad(object? value);

    public abstract object? ToConfigValue();

    /// <summary>Updates <see cref="Error"/>; returns true when the value is valid.</summary>
    public abstract bool Validate();

    public abstract void Revert();

    /// <summary>Marks the current values as the persisted baseline (after a successful apply).</summary>
    public void Commit()
    {
        OnCommit();
        Error = null;
        OnPropertyChanged(nameof(IsDirty));
    }

    protected abstract void OnCommit();

    protected void SetError(string? error) => Error = error;
}
