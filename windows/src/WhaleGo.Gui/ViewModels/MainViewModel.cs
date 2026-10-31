using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Shell presenter: navigation, top-level status bar, theme selector and the 2s status poll. It owns the six page
/// presenters and never touches WPF types (the window binds to it).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IUiStateStore _uiState;
    private PageBaseViewModel _currentPage;
    private StatusSnapshot? _lastSnapshot;
    private ThemePreference _themePreference;
    private bool _isRefreshing;
    private string _statusMessage = "就绪";
    private StatusLevel _statusLevel = StatusLevel.Idle;
    private string _statusLabel = "未知";
    private string _statusDetail = string.Empty;
    private string _listenText = string.Empty;
    private string _versionText = string.Empty;

    public MainViewModel(GuiContext context, IUiStateStore uiState)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uiState);
        Context = context;
        _uiState = uiState;
        Overview = new OverviewViewModel(context);
        Connection = new ConnectionViewModel(context);
        Policy = new PolicyViewModel(context);
        Privacy = new PrivacyViewModel(context);
        Diagnostics = new DiagnosticsViewModel(context);
        About = new AboutViewModel(context);
        Pages = [Overview, Connection, Policy, Privacy, Diagnostics, About];
        _themePreference = context.Theme.Preference;
        var saved = uiState.Load();
        _currentPage = Pages[Math.Clamp(saved.LastPageIndex, 0, Pages.Count - 1)];
        HideCommand = new RelayCommand(() => HideRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand = new AsyncRelayCommand(RefreshCurrentAsync, () => !_isRefreshing);
        ApplyCurrentCommand = new AsyncRelayCommand(ApplyCurrentAsync, () => CurrentPage is ConfigPageViewModel);
        RefreshCommand.ExecutionFailed += (_, exception) => HandleFailure("刷新", exception);
        ApplyCurrentCommand.ExecutionFailed += (_, exception) => HandleFailure("应用当前页配置", exception);
        ConnectPageEvents();
        context.Theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Raised on every successful status poll (the shell feeds the tray with it).</summary>
    public event EventHandler<StatusSnapshot>? StatusUpdated;

    /// <summary>Raised when the user wants to hide the window to the tray (Esc / X).</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised when any page asks for the pairing wizard.</summary>
    public event EventHandler? WizardRequested;

    public GuiContext Context { get; }

    public OverviewViewModel Overview { get; }

    public ConnectionViewModel Connection { get; }

    public PolicyViewModel Policy { get; }

    public PrivacyViewModel Privacy { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public AboutViewModel About { get; }

    public IReadOnlyList<PageBaseViewModel> Pages { get; }

    public RelayCommand HideCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ApplyCurrentCommand { get; }

    public PageBaseViewModel CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is null || ReferenceEquals(value, _currentPage))
            {
                return;
            }

            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPageIndex));
            ApplyCurrentCommand.RaiseCanExecuteChanged();
            StatusMessage = "已切换到「" + value.Title + "」";
            var state = _uiState.Load();
            _uiState.Save(state with { LastPageIndex = Pages.ToList().IndexOf(value) });
            _ = RefreshPageAsync(value);
        }
    }

    public int CurrentPageIndex => Pages.ToList().IndexOf(_currentPage);

    public StatusSnapshot? LastSnapshot => _lastSnapshot;

    public StatusLevel StatusLevel
    {
        get => _statusLevel;
        private set => SetProperty(ref _statusLevel, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string ListenText
    {
        get => _listenText;
        private set => SetProperty(ref _listenText, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy => _isRefreshing;

    public ThemePreference ThemePreference => _themePreference;

    public string ThemePreferenceText => _themePreference.DisplayName();

    public bool IsSystemTheme
    {
        get => _themePreference == ThemePreference.System;
        set
        {
            if (value)
            {
                RequestTheme(ThemePreference.System);
            }
        }
    }

    public bool IsLightTheme
    {
        get => _themePreference == ThemePreference.Light;
        set
        {
            if (value)
            {
                RequestTheme(ThemePreference.Light);
            }
        }
    }

    public bool IsDarkTheme
    {
        get => _themePreference == ThemePreference.Dark;
        set
        {
            if (value)
            {
                RequestTheme(ThemePreference.Dark);
            }
        }
    }

    public async Task InitializeAsync()
    {
        await RefreshStatusAsync().ConfigureAwait(true);
        await CurrentPage.RefreshAsync().ConfigureAwait(true);
        SetHotkeyStatus(StatusMessage);
    }

    /// <summary>Poll hook (2s): status only, one bounded kernel call.</summary>
    public async Task RefreshStatusAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        RefreshCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            var status = await Context.Control.GetStatusAsync().ConfigureAwait(true);
            if (status.Success && status.Value is not null)
            {
                ApplySnapshot(status.Value);
            }
            else
            {
                StatusMessage = "无法读取执行器状态：" + (status.ErrorMessage ?? "未知错误");
            }
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public void ApplySnapshot(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _lastSnapshot = snapshot;
        StatusLevel = StatusText.LevelOf(snapshot);
        StatusLabel = StatusText.Label(snapshot);
        StatusDetail = StatusText.Detail(snapshot);
        ListenText = StatusText.ListenLine(snapshot);
        VersionText = StatusText.VersionLine(snapshot, Context.Options);
        StatusMessage = StatusDetail;
        Overview.ApplySnapshot(snapshot);
        CurrentPage.OnStatusUpdated(snapshot);
        StatusUpdated?.Invoke(this, snapshot);
    }

    public void SetHotkeyStatus(string text)
    {
        Overview.SetHotkeyStatus(text);
        Diagnostics.SetHotkeyStatus(text);
        About.SetHotkeyStatus(text);
    }

    private async Task RefreshCurrentAsync()
    {
        await RefreshStatusAsync().ConfigureAwait(true);
        await CurrentPage.RefreshAsync().ConfigureAwait(true);
    }

    private async Task ApplyCurrentAsync()
    {
        if (CurrentPage is ConfigPageViewModel configPage)
        {
            await configPage.ApplyAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshPageAsync(PageBaseViewModel page)
    {
        try
        {
            await page.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Context.Logger.Error("page refresh failed: " + page.Title, ex);
            StatusMessage = "刷新页面失败：" + ex.Message;
        }
    }

    private void RequestTheme(ThemePreference preference)
    {
        _ = ApplyThemeAsync(preference);
    }

    private async Task ApplyThemeAsync(ThemePreference preference)
    {
        if (preference == _themePreference)
        {
            return;
        }

        var accepted = await Context.Theme.SetPreferenceAsync(preference).ConfigureAwait(true);
        _themePreference = Context.Theme.Preference;
        RaiseThemeProperties();
        StatusMessage = accepted
            ? "主题已切换：" + _themePreference.DisplayName() + "（已写入 ui.theme）"
            : "主题切换失败，已回滚到 " + _themePreference.DisplayName();
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        _themePreference = Context.Theme.Preference;
        RaiseThemeProperties();
    }

    private void RaiseThemeProperties()
    {
        OnPropertyChanged(nameof(ThemePreference));
        OnPropertyChanged(nameof(ThemePreferenceText));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private void ConnectPageEvents()
    {
        Overview.WizardRequested += (_, _) => WizardRequested?.Invoke(this, EventArgs.Empty);
        Connection.WizardRequested += (_, _) => WizardRequested?.Invoke(this, EventArgs.Empty);
        Overview.EditPolicyRequested += (_, _) => CurrentPage = Policy;
    }

    private void HandleFailure(string operation, Exception exception)
    {
        Context.Logger.Error("shell command failed: " + operation, exception);
        StatusMessage = operation + "失败：" + exception.Message;
    }
}
