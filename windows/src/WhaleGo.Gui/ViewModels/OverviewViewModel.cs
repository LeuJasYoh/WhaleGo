using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Overview page: is the model able to act right now, who holds the lease, the two stop buttons, pairing entry and
/// "copy plugin config" (docs/GUI.md 6.1 / 9.1 / 10.4). All kernel calls are bounded and off the UI thread.
/// </summary>
public sealed class OverviewViewModel : PageBaseViewModel
{
    private readonly LocalHealthProbe _probe;
    private StatusSnapshot? _snapshot;
    private IReadOnlyList<TokenDescriptor>? _tokens;
    private bool _isBusy;
    private string _actionMessage = string.Empty;
    private string _hotkeyText = ConfigDefaults.HotkeysEmergencyStop;
    private string _hotkeyStatusText = "未注册";
    private int _port = ConfigDefaults.ListenPort;
    private string _listenAddress = ConfigDefaults.ListenAddress;

    public OverviewViewModel(GuiContext context)
        : base("概览", "概览", "现在能不能被模型操作？谁在操作？", context)
    {
        _probe = new LocalHealthProbe(context.Logger);
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, () => !IsBusy && _snapshot is not null);
        EmergencyStopCommand = new AsyncRelayCommand(EmergencyStopAsync, () => !IsBusy && _snapshot is not null);
        CopyPluginConfigCommand = new AsyncRelayCommand(CopyPluginConfigAsync, () => CanCopyPluginConfig);
        OpenWizardCommand = new RelayCommand(() => WizardRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        TogglePauseCommand.ExecutionFailed += OnCommandFailed;
        EmergencyStopCommand.ExecutionFailed += OnCommandFailed;
        CopyPluginConfigCommand.ExecutionFailed += OnCommandFailed;
    }

    /// <summary>The wizard window is owned by the shell/view; the presenter only asks for it.</summary>
    public event EventHandler? WizardRequested;

    public AsyncRelayCommand TogglePauseCommand { get; }

    public AsyncRelayCommand EmergencyStopCommand { get; }

    public AsyncRelayCommand CopyPluginConfigCommand { get; }

    public RelayCommand OpenWizardCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public StatusSnapshot? Snapshot => _snapshot;

    public StatusLevel Level { get; private set; } = StatusLevel.Idle;

    public string StatusLabel { get; private set; } = "未知";

    public string StatusDetail { get; private set; } = "正在读取状态…";

    public string ListenLine { get; private set; } = "—";

    public string VersionLine { get; private set; } = string.Empty;

    public string SessionsLine { get; private set; } = "—";

    public string PolicyLine { get; private set; } = string.Empty;

    public string PauseResumeLabel { get; private set; } = "暂停";

    public bool CanPause { get; private set; }

    public bool CanResume { get; private set; }

    public bool CanEmergencyStop { get; private set; }

    public string HotkeyText => _hotkeyText;

    public string HotkeyStatusText => _hotkeyStatusText;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                TogglePauseCommand.RaiseCanExecuteChanged();
                EmergencyStopCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (SetProperty(ref _actionMessage, value))
            {
                OnPropertyChanged(nameof(HasActionMessage));
            }
        }
    }

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessage);

    public bool CanCopyPluginConfig { get; private set; }

    public string TokenSummary { get; private set; } = "尚未生成 token";

    public bool ShowPairingGuide => !CanCopyPluginConfig;

    public string ListenerSummary => _listenAddress + ":" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var status = await Context.Control.GetStatusAsync().ConfigureAwait(true);
            if (status.Success && status.Value is not null)
            {
                ApplySnapshot(status.Value);
            }
            else
            {
                StatusDetail = "无法读取状态：" + (status.ErrorMessage ?? "未知错误");
            }

            var tokens = await Context.Control.GetTokensAsync().ConfigureAwait(true);
            if (tokens.Success)
            {
                ApplyTokens(tokens.Value);
            }

            var config = await Context.Control.GetConfigAsync().ConfigureAwait(true);
            if (config.Success && config.Value is not null)
            {
                ApplyConfig(config.Value);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplySnapshot(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        var level = StatusText.LevelOf(snapshot);
        if (Level != level)
        {
            Level = level;
            OnPropertyChanged(nameof(Level));
        }

        StatusLabel = StatusText.Label(snapshot);
        StatusDetail = StatusText.Detail(snapshot);
        ListenLine = StatusText.ListenLine(snapshot);
        VersionLine = StatusText.VersionLine(snapshot, Context.Options);
        SessionsLine = StatusText.SessionsLine(snapshot);
        PolicyLine = StatusText.PolicyLine(snapshot);
        PauseResumeLabel = StatusText.PauseResumeLabel(snapshot);
        CanPause = StatusText.CanPause(snapshot) && !IsBusy;
        CanResume = StatusText.CanResume(snapshot) && !IsBusy;
        CanEmergencyStop = StatusText.CanEmergencyStop(snapshot) && !IsBusy;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ListenLine));
        OnPropertyChanged(nameof(VersionLine));
        OnPropertyChanged(nameof(SessionsLine));
        OnPropertyChanged(nameof(PolicyLine));
        OnPropertyChanged(nameof(PauseResumeLabel));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanEmergencyStop));
        TogglePauseCommand.RaiseCanExecuteChanged();
        EmergencyStopCommand.RaiseCanExecuteChanged();
    }

    public void ApplyTokens(IReadOnlyList<TokenDescriptor>? tokens)
    {
        _tokens = tokens;
        CanCopyPluginConfig = TokenAccess.CanCopyPluginConfig(tokens);
        TokenSummary = TokenAccess.Summary(tokens);
        OnPropertyChanged(nameof(CanCopyPluginConfig));
        OnPropertyChanged(nameof(TokenSummary));
        OnPropertyChanged(nameof(ShowPairingGuide));
        CopyPluginConfigCommand.RaiseCanExecuteChanged();
    }

    public void ApplyConfig(ConfigPatch config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Values.TryGetValue(ConfigKeys.ListenPort, out var portValue))
        {
            _port = ConfigValue.AsInt(portValue) ?? ConfigDefaults.ListenPort;
        }

        if (config.Values.TryGetValue(ConfigKeys.ListenAddress, out var addressValue))
        {
            _listenAddress = ConfigValue.AsString(addressValue) ?? ConfigDefaults.ListenAddress;
        }

        if (config.Values.TryGetValue(ConfigKeys.HotkeysEmergencyStop, out var hotkeyValue))
        {
            _hotkeyText = ConfigValue.AsString(hotkeyValue) ?? ConfigDefaults.HotkeysEmergencyStop;
        }

        OnPropertyChanged(nameof(ListenerSummary));
        OnPropertyChanged(nameof(HotkeyText));
    }

    public void SetHotkeyStatus(string text)
    {
        _hotkeyStatusText = text;
        OnPropertyChanged(nameof(HotkeyStatusText));
    }

    private async Task TogglePauseAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (_snapshot.Stopped)
            {
                if (!Context.Dialogs.Confirm(
                        "恢复操作",
                        "解除紧急停止后，模型将重新可以操作你的桌面。确定恢复吗？",
                        "恢复",
                        danger: true))
                {
                    return;
                }

                var reset = await Context.Control.ResetStopAsync().ConfigureAwait(true);
                ActionMessage = reset.Success ? "已恢复：模型可以继续操作桌面" : "恢复失败：" + (reset.ErrorMessage ?? "未知错误");
                if (reset.Success)
                {
                    Context.Balloon?.Invoke(BalloonKind.Info, "WhaleGo 已恢复", "模型可以继续操作桌面。");
                }
            }
            else if (_snapshot.Paused)
            {
                var resume = await Context.Control.ResumeAsync().ConfigureAwait(true);
                ActionMessage = resume.Success ? "已恢复：模型可以继续操作桌面" : "恢复失败：" + (resume.ErrorMessage ?? "未知错误");
            }
            else
            {
                var pause = await Context.Control.PauseAsync("user_button").ConfigureAwait(true);
                ActionMessage = pause.Success ? "已暂停：模型动作会被拒绝，观察仍可用" : "暂停失败：" + (pause.ErrorMessage ?? "未知错误");
                if (pause.Success)
                {
                    Context.Balloon?.Invoke(BalloonKind.Info, "WhaleGo 已暂停", "模型动作会被拒绝，点击托盘「恢复」即可继续。");
                }
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EmergencyStopAsync()
    {
        if (!Context.Dialogs.Confirm(
                "紧急停止",
                "立即停止所有模型动作，桌面控制被锁定。恢复必须在本机二次确认。",
                "紧急停止",
                danger: true,
                detail: "正在进行的动作可能已发出，不会自动回滚。"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var stop = await Context.Control.StopAsync("gui").ConfigureAwait(true);
            ActionMessage = stop.Success ? "已紧急停止：模型无法再操作桌面" : "停止失败：" + (stop.ErrorMessage ?? "未知错误");
            if (stop.Success)
            {
                Context.Balloon?.Invoke(
                    BalloonKind.EmergencyStop,
                    "WhaleGo 已紧急停止",
                    "模型无法再操作桌面。恢复请在托盘点「恢复」。");
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CopyPluginConfigAsync()
    {
        var token = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
        var plaintext = token.Success ? token.Value : null;
        var json = PluginConfigSnippet.BuildJson(_port, plaintext);
        Context.Clipboard.SetText(json, TimeSpan.FromSeconds(60));
        ActionMessage = string.IsNullOrEmpty(plaintext)
            ? "已复制插件配置（token 不可读，请先在「连接与配对」轮换 token）"
            : "已复制插件配置（含 token，剪贴板 60 秒后自动清空）";
        if (ConfigRules.IsLocalhostAddress(_listenAddress))
        {
            ActionMessage += "；注意当前仅本机监听，容器连不上";
        }
    }

    private void OnCommandFailed(object? sender, Exception exception)
    {
        Context.Logger.Error("overview command failed", exception);
        ActionMessage = "操作失败：" + exception.Message;
        IsBusy = false;
    }
}
