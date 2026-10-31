using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Shared presenter for the configuration pages: load + source badges, per-page dirty tracking, atomic apply of
/// only the changed keys, restart badges, rollback via "恢复", and safe error reporting. All kernel interaction
/// goes through <see cref="SafeControl"/> (bounded, never on the UI thread).
/// </summary>
public abstract class ConfigPageViewModel : PageBaseViewModel
{
    private bool _isBusy;
    private string? _pageError;
    private string? _pageWarning;
    private string? _pageSuccess;
    private IReadOnlyList<string> _restartPendingKeys = [];

    protected ConfigPageViewModel(
        string title,
        string navLabel,
        string description,
        GuiContext context,
        IReadOnlyList<ConfigFieldGroup> groups)
        : base(title, navLabel, description, context)
    {
        Groups = groups;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => IsDirty && !IsBusy);
        RevertCommand = new RelayCommand(RevertAll, () => IsDirty && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(ForceReloadAsync, () => !IsBusy);
        RequestRestartCommand = new AsyncRelayCommand(RequestRestartAsync, () => HasRestartPending);
        ApplyCommand.ExecutionFailed += (_, exception) => HandleCommandFailure("应用配置", exception);
        RefreshCommand.ExecutionFailed += (_, exception) => HandleCommandFailure("重新加载配置", exception);
        RequestRestartCommand.ExecutionFailed += (_, exception) => HandleCommandFailure("重启执行器", exception);
        foreach (var field in Fields)
        {
            field.PropertyChanged += OnFieldPropertyChanged;
        }
    }

    public IReadOnlyList<ConfigFieldGroup> Groups { get; }

    public virtual IEnumerable<ConfigField> Fields => Groups.SelectMany(static group => group.Fields);

    public override bool SupportsApply => true;

    public AsyncRelayCommand ApplyCommand { get; }

    public RelayCommand RevertCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RequestRestartCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public bool IsDirty => Fields.Any(static item => item.IsDirty);

    public bool IsApplyEnabled => IsDirty && !IsBusy;

    public string? PageError
    {
        get => _pageError;
        private set
        {
            if (SetProperty(ref _pageError, value))
            {
                OnPropertyChanged(nameof(HasPageError));
            }
        }
    }

    public bool HasPageError => !string.IsNullOrEmpty(PageError);

    public string? PageWarning
    {
        get => _pageWarning;
        protected set
        {
            if (SetProperty(ref _pageWarning, value))
            {
                OnPropertyChanged(nameof(HasPageWarning));
            }
        }
    }

    public bool HasPageWarning => !string.IsNullOrEmpty(PageWarning);

    public string? PageSuccess
    {
        get => _pageSuccess;
        private set
        {
            if (SetProperty(ref _pageSuccess, value))
            {
                OnPropertyChanged(nameof(HasPageSuccess));
            }
        }
    }

    public bool HasPageSuccess => !string.IsNullOrEmpty(PageSuccess);

    public IReadOnlyList<string> RestartPendingKeys
    {
        get => _restartPendingKeys;
        private set
        {
            if (SetProperty(ref _restartPendingKeys, value))
            {
                OnPropertyChanged(nameof(HasRestartPending));
                OnPropertyChanged(nameof(RestartHint));
                RequestRestartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasRestartPending => RestartPendingKeys.Count > 0;

    public string RestartHint => HasRestartPending
        ? "已保存；以下配置需重启执行器后生效：" + string.Join("、", RestartPendingKeys)
        : string.Empty;

    /// <summary>Loads config + sources when the page becomes visible (keeps unsaved edits).</summary>
    public override async Task RefreshAsync()
    {
        if (IsDirty)
        {
            return;
        }

        await LoadFromKernelAsync().ConfigureAwait(true);
    }

    /// <summary>Loads values + source badges from the kernel (virtual for page-specific post-processing).</summary>
    public async Task LoadFromKernelAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        PageError = null;
        try
        {
            var config = await Context.Control.GetConfigAsync().ConfigureAwait(true);
            if (!config.Success || config.Value is null)
            {
                PageError = "读取配置失败：" + (config.ErrorMessage ?? "未知错误");
                return;
            }

            var sources = await Context.Control.GetConfigSourcesAsync().ConfigureAwait(true);
            LoadFrom(config.Value, sources.Success ? sources.Value : null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Pure load (unit-testable): values from ConfigPatch + source kinds from GetConfigSources.</summary>
    public void LoadFrom(ConfigPatch patch, IReadOnlyDictionary<string, string>? sources)
    {
        ArgumentNullException.ThrowIfNull(patch);
        foreach (var field in Fields)
        {
            var hasValue = patch.Values.TryGetValue(field.Key, out var raw);
            field.LoadFrom(hasValue ? raw : GuiConfigDefaults.Get(field.Key));
            field.SourceKind = ResolveSource(field.Key, sources);
        }

        RestartPendingKeys = [];
        ClearMessages();
        OnLoaded();
        RaiseStateChanged();
    }

    public void RevertAll()
    {
        foreach (var field in Fields)
        {
            field.Revert();
        }

        ClearMessages();
        RestartPendingKeys = [];
        PageSuccess = "已恢复到上次加载的值";
        RaiseStateChanged();
    }

    public async Task ApplyAsync()
    {
        ClearMessages();
        var pageError = ValidatePage();
        if (pageError is not null)
        {
            PageError = pageError;
            return;
        }

        var valid = true;
        foreach (var field in Fields)
        {
            if (field.IsReadOnly)
            {
                continue;
            }

            if (!field.Validate())
            {
                valid = false;
            }
        }

        if (!valid)
        {
            PageError = "有字段未通过校验，请按红色提示修改后再保存";
            return;
        }

        if (!IsDirty)
        {
            PageSuccess = "没有需要保存的修改";
            return;
        }

        if (!await ConfirmApplyAsync().ConfigureAwait(true))
        {
            PageError = "已取消保存";
            return;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var restartKeys = new List<string>();
        foreach (var field in Fields)
        {
            if (!field.IsDirty)
            {
                continue;
            }

            values[field.Key] = field.ToConfigValue();
            if (field.RestartRequired)
            {
                restartKeys.Add(field.Key);
            }
        }

        await OnBeforeApplyAsync(values).ConfigureAwait(true);
        if (values.Count == 0)
        {
            PageSuccess = "没有需要保存的修改";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Context.Control.SetConfigAsync(new ConfigPatch(values)).ConfigureAwait(true);
            if (!result.Success)
            {
                PageError = "保存失败：" + (result.ErrorMessage ?? "未知错误") + "（修改未提交，可修正后重试或点「恢复」）";
                return;
            }

            foreach (var field in Fields)
            {
                if (!field.IsReadOnly)
                {
                    field.Commit();
                }
            }

            RestartPendingKeys = restartKeys;
            PageSuccess = restartKeys.Count == 0
                ? "已保存并立即生效"
                : "已保存；其中 " + restartKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 项需重启后生效";
            OnApplied(values);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ForceReloadAsync()
    {
        if (IsDirty && !Context.Dialogs.Confirm(
                "放弃未保存的修改",
                "重新加载会丢弃当前页未保存的修改，确定继续吗？",
                "重新加载"))
        {
            return;
        }

        await LoadFromKernelAsync().ConfigureAwait(true);
    }

    private async Task RequestRestartAsync()
    {
        var result = await Context.Control.RequestRestartAsync().ConfigureAwait(true);
        if (result.Success)
        {
            PageSuccess = "已请求重启执行器；新实例会重新加载配置";
        }
        else
        {
            PageError = "重启请求失败：" + (result.ErrorMessage ?? "未知错误");
        }
    }

    protected virtual void OnLoaded()
    {
    }

    protected virtual string? ValidatePage() => null;

    protected virtual Task<bool> ConfirmApplyAsync() => Task.FromResult(true);

    protected virtual Task OnBeforeApplyAsync(IDictionary<string, object?> values) => Task.CompletedTask;

    protected virtual void OnApplied(IReadOnlyDictionary<string, object?> applied)
    {
    }

    protected void ClearMessages()
    {
        PageError = null;
        PageWarning = null;
        PageSuccess = null;
    }

    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfigField.IsDirty) or nameof(ConfigField.HasError) or nameof(ConfigField.HasWarning)
            or nameof(ConfigField.Warning))
        {
            RaiseStateChanged();
        }
    }

    protected void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsApplyEnabled));
        ApplyCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        RequestRestartCommand.RaiseCanExecuteChanged();
    }

    private void HandleCommandFailure(string operation, Exception exception)
    {
        Context.Logger.Error("settings command failed: " + operation, exception);
        PageError = operation + "失败：" + exception.Message;
        IsBusy = false;
    }

    private static string ResolveSource(string key, IReadOnlyDictionary<string, string>? sources)
    {
        if (sources is not null && sources.TryGetValue(key, out var source) && !string.IsNullOrWhiteSpace(source))
        {
            return source switch
            {
                ConfigSourceKinds.File => ConfigSourceKinds.File,
                ConfigSourceKinds.Environment => ConfigSourceKinds.Environment,
                ConfigSourceKinds.CommandLine => ConfigSourceKinds.CommandLine,
                ConfigSourceKinds.Unavailable => ConfigSourceKinds.Unavailable,
                _ => ConfigSourceKinds.Default,
            };
        }

        return ConfigSourceKinds.Default;
    }
}
