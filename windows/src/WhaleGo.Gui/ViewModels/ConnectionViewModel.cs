using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Connection &amp; pairing page: listen.* configuration, token list (masked / copy / rotate), loopback connection
/// test, risk banner and firewall snippets (docs/GUI.md 6.1 / 7.2 / 7.4 / 9.6).
/// </summary>
public sealed class ConnectionViewModel : ConfigPageViewModel
{
    private readonly StringField _address;
    private readonly IntField _port;
    private readonly BoolField _healthzRequireToken;
    private readonly LocalHealthProbe _probe;
    private UiSeverity _riskSeverity = UiSeverity.Warning;
    private string _riskBannerText = string.Empty;
    private string? _selectedAddressSuggestion;
    private string _localTestResult = string.Empty;
    private string _actionMessage = string.Empty;
    private bool _isTesting;
    private bool _canCopyPluginConfig;

    public ConnectionViewModel(GuiContext context)
        : this(
            context,
            new StringField(
                ConfigKeys.ListenAddress,
                "监听地址",
                "0.0.0.0 监听所有网卡（容器可连）；127.0.0.1 仅本机；也可填本机网卡地址（推荐配合防火墙）",
                maxLength: 64,
                validator: ConfigRules.ValidateAddress,
                warningProvider: ConfigRules.WarningForAddress),
            new IntField(ConfigKeys.ListenPort, "监听端口", "1–65535；<1024 通常需要管理员权限。修改后需重启执行器"),
            new BoolField(
                ConfigKeys.ListenHealthzRequireToken,
                "healthz 要求 token",
                "高级项：开启后容器内探测 /healthz 也必须带 Bearer token",
                "要求",
                "不要求"))
    {
    }

    private ConnectionViewModel(GuiContext context, StringField address, IntField port, BoolField healthzRequireToken)
        : base("连接与配对", "连接与配对", "监听地址、token 与 AstrBot 插件配对", context,
            BuildGroups(address, port, healthzRequireToken))
    {
        _address = address;
        _port = port;
        _healthzRequireToken = healthzRequireToken;
        _probe = new LocalHealthProbe(context.Logger);
        TokenRows = [];
        AddressSuggestions = NetworkAddressProvider.ListenAddressOptions();
        CopyTokenCommand = new RelayCommand<TokenRowViewModel>(row => _ = CopyTokenAsync(row));
        RotateTokenCommand = new RelayCommand<TokenRowViewModel>(row => _ = RotateTokenAsync(row));
        CopyPluginConfigCommand = new AsyncRelayCommand(CopyPluginConfigAsync, () => CanCopyPluginConfig);
        CopyFirewallCommand = new RelayCommand(() => CopyToClipboard(FirewallCommandText, "已复制防火墙命令（需以管理员身份运行）"));
        CopyNetshCommand = new RelayCommand(() => CopyToClipboard(NetshCommandText, "已复制 netsh 防火墙命令（需以管理员身份运行）"));
        CopyDockerCommand = new RelayCommand(() => _ = CopyDockerCommandAsync());
        RunLocalTestCommand = new AsyncRelayCommand(RunLocalTestAsync, () => !IsTesting);
        OpenWizardCommand = new RelayCommand(() => WizardRequested?.Invoke(this, EventArgs.Empty));
        _address.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConfigField.Warning) or nameof(StringField.Value))
            {
                UpdateRiskBanner();
            }
        };
        _port.PropertyChanged += (_, _) => UpdateCommands();
        _healthzRequireToken.PropertyChanged += (_, _) => UpdateCommands();
        UpdateRiskBanner();
        UpdateCommands();
    }

    public event EventHandler? WizardRequested;

    public ObservableCollection<TokenRowViewModel> TokenRows { get; }

    public ICommand CopyTokenCommand { get; }

    public ICommand RotateTokenCommand { get; }

    public AsyncRelayCommand CopyPluginConfigCommand { get; }

    public RelayCommand CopyFirewallCommand { get; }

    public RelayCommand CopyNetshCommand { get; }

    public RelayCommand CopyDockerCommand { get; }

    public AsyncRelayCommand RunLocalTestCommand { get; }

    public RelayCommand OpenWizardCommand { get; }

    public IReadOnlyList<string> AddressSuggestions { get; }

    /// <summary>Selecting a suggested NIC address writes it into listen.address (docs/GUI.md 9.6).</summary>
    public string? SelectedAddressSuggestion
    {
        get => _selectedAddressSuggestion;
        set
        {
            if (!SetProperty(ref _selectedAddressSuggestion, value) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            _address.Value = value;
        }
    }

    public string TokenSummary => TokenAccess.Summary(TokenRows.Select(static row => row.Descriptor).ToList());

    public bool CanCopyPluginConfig
    {
        get => _canCopyPluginConfig;
        private set
        {
            if (SetProperty(ref _canCopyPluginConfig, value))
            {
                CopyPluginConfigCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowPairingGuide));
            }
        }
    }

    public bool ShowPairingGuide => !CanCopyPluginConfig;

    public UiSeverity RiskSeverity
    {
        get => _riskSeverity;
        private set => SetProperty(ref _riskSeverity, value);
    }

    public string RiskBannerText
    {
        get => _riskBannerText;
        private set => SetProperty(ref _riskBannerText, value);
    }

    public bool ShowLocalhostWarning => ConfigRules.IsLocalhostAddress(_address.Value);

    public string FirewallCommandText => PluginConfigSnippet.BuildNetshFirewallCommand(EffectivePort);

    public string NetshCommandText => PluginConfigSnippet.BuildNetshFirewallCommand(EffectivePort);

    public string DockerCommandText => PluginConfigSnippet.BuildDockerHealthCommand(EffectivePort, null);

    public string PluginUrl => PluginConfigSnippet.BuildUrl(EffectivePort);

    public int EffectivePort => _port.NumericValue is > 0 and <= 65535 ? _port.NumericValue!.Value : ConfigDefaults.ListenPort;

    public bool HealthzRequiresToken => _healthzRequireToken.Value;

    public string LocalTestResult
    {
        get => _localTestResult;
        private set
        {
            if (SetProperty(ref _localTestResult, value))
            {
                OnPropertyChanged(nameof(HasLocalTestResult));
            }
        }
    }

    public bool HasLocalTestResult => !string.IsNullOrEmpty(LocalTestResult);

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                RunLocalTestCommand.RaiseCanExecuteChanged();
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

    public override async Task RefreshAsync()
    {
        await base.RefreshAsync().ConfigureAwait(true);
        await LoadTokensAsync().ConfigureAwait(true);
    }

    public async Task LoadTokensAsync()
    {
        var tokens = await Context.Control.GetTokensAsync().ConfigureAwait(true);
        TokenRows.Clear();
        if (tokens.Success && tokens.Value is not null)
        {
            foreach (var descriptor in tokens.Value)
            {
                TokenRows.Add(new TokenRowViewModel(descriptor, CopyTokenCommand, RotateTokenCommand));
            }
        }

        CanCopyPluginConfig = TokenAccess.CanCopyPluginConfig(tokens.Value);
        OnPropertyChanged(nameof(TokenSummary));
        OnPropertyChanged(nameof(ShowPairingGuide));
    }

    protected override void OnLoaded()
    {
        UpdateRiskBanner();
        UpdateCommands();
    }

    protected override async Task OnBeforeApplyAsync(IDictionary<string, object?> values)
    {
        if (_port.NumericValue is { } port && port is >= 1 and <= 65535)
        {
            Context.Logger.Info("listen port changing to " + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    protected override void OnApplied(IReadOnlyDictionary<string, object?> applied)
    {
        UpdateRiskBanner();
        UpdateCommands();
    }

    private static IReadOnlyList<ConfigFieldGroup> BuildGroups(StringField address, IntField port, BoolField healthz)
    {
        return
        [
            new ConfigFieldGroup("监听", "容器通过 host.docker.internal 访问；修改地址/端口需要重启执行器", [address, port]),
            new ConfigFieldGroup("高级", "常规使用保持默认即可", [healthz]),
        ];
    }

    private async Task CopyTokenAsync(TokenRowViewModel? row)
    {
        var token = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
        if (!token.Success || string.IsNullOrEmpty(token.Value))
        {
            ActionMessage = "复制失败：token 不可读（只能轮换）";
            return;
        }

        Context.Clipboard.SetText(token.Value, TimeSpan.FromSeconds(60));
        ActionMessage = "已复制 token（剪贴板 60 秒后自动清空）";
        Context.Logger.Info("token copied from settings window");
    }

    private async Task RotateTokenAsync(TokenRowViewModel? row)
    {
        var name = row?.Name ?? "astrbot";
        if (!Context.Dialogs.Confirm(
                "轮换 token",
                "旧 token 会立即失效，AstrBot 插件里配置的 token 需要同步改成新的。确定轮换吗？",
                "轮换",
                danger: true))
        {
            return;
        }

        var rotated = await Context.Control.RotateTokenAsync(name).ConfigureAwait(true);
        if (!rotated.Success || string.IsNullOrEmpty(rotated.Value))
        {
            ActionMessage = "轮换失败：" + (rotated.ErrorMessage ?? "未知错误");
            return;
        }

        Context.Clipboard.SetText(rotated.Value, TimeSpan.FromSeconds(60));
        Context.Dialogs.ShowTokenOnce(
            "新的 token（只显示这一次）",
            "已复制到剪贴板（60 秒后自动清空）。请立即粘进 AstrBot 插件配置，旧 token 已失效。",
            rotated.Value);
        ActionMessage = "已轮换 token；旧 token 立即失效";
        Context.Logger.Info("token rotated from settings window");
        await LoadTokensAsync().ConfigureAwait(true);
    }

    private async Task CopyPluginConfigAsync()
    {
        var token = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
        var plaintext = token.Success ? token.Value : null;
        var json = PluginConfigSnippet.BuildJson(EffectivePort, plaintext);
        Context.Clipboard.SetText(json, TimeSpan.FromSeconds(60));
        ActionMessage = "已复制插件配置（含 token，剪贴板 60 秒后自动清空）";
        if (ShowLocalhostWarning)
        {
            ActionMessage += "；当前仅本机监听，容器连不上";
        }
    }

    private async Task CopyDockerCommandAsync()
    {
        var token = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
        var command = PluginConfigSnippet.BuildDockerHealthCommand(EffectivePort, token.Success ? token.Value : null);
        Context.Clipboard.SetText(command, TimeSpan.FromSeconds(60));
        ActionMessage = "已复制容器内探测命令（请在宿主机执行，不会自动运行）";
    }

    private async Task RunLocalTestAsync()
    {
        IsTesting = true;
        LocalTestResult = "测试中…";
        try
        {
            string? token = null;
            var revealed = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
            if (revealed.Success)
            {
                token = revealed.Value;
            }

            var result = await _probe.ProbeAsync(EffectivePort, token, HealthzRequiresToken).ConfigureAwait(true);
            LocalTestResult = result.Detail;
            LocalTestResult = (result.Ok ? "✅ " : "❌ ") + LocalTestResult;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        Context.Clipboard.SetText(text, TimeSpan.FromSeconds(60));
        ActionMessage = successMessage;
    }

    private void UpdateRiskBanner()
    {
        var text = ConfigRules.WarningForAddress(_address.Value);
        var isLocalhost = ConfigRules.IsLocalhostAddress(_address.Value);
        RiskSeverity = isLocalhost ? UiSeverity.Danger : string.Equals(_address.Value.Trim(), "0.0.0.0", StringComparison.Ordinal) ? UiSeverity.Warning : UiSeverity.Info;
        RiskBannerText = text ?? "监听地址仅绑定指定网卡：" + _address.Value.Trim();
        OnPropertyChanged(nameof(ShowLocalhostWarning));
        OnPropertyChanged(nameof(PluginUrl));
        OnPropertyChanged(nameof(RiskBannerText));
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(FirewallCommandText));
        OnPropertyChanged(nameof(NetshCommandText));
        OnPropertyChanged(nameof(DockerCommandText));
        OnPropertyChanged(nameof(PluginUrl));
        OnPropertyChanged(nameof(EffectivePort));
        OnPropertyChanged(nameof(HealthzRequiresToken));
        CopyFirewallCommand.RaiseCanExecuteChanged();
        CopyNetshCommand.RaiseCanExecuteChanged();
        CopyDockerCommand.RaiseCanExecuteChanged();
    }
}
