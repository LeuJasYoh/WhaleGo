using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels.Fields;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Five-step first-run pairing wizard (docs/GUI.md section 7): risk acknowledgement, listen + token, plugin config
/// snippet, connection test/wait, finish. Shares the same fields/validators with the settings pages.
/// </summary>
public sealed class PairingWizardViewModel : ObservableObject
{
    public const int StepCount = 5;
    public const string DefaultTokenName = "astrbot";

    private static readonly IReadOnlyList<string> StepTitles =
    [
        "欢迎与风险",
        "监听与 token",
        "复制插件配置",
        "连接测试",
        "完成",
    ];

    private readonly StringField _address;
    private readonly IntField _port;
    private readonly LocalHealthProbe _probe;
    private int _step = 1;
    private bool _acknowledged;
    private bool _isBusy;
    private bool _tokenAvailable;
    private string _tokenMasked = "尚未生成";
    private string _tokenPlaintext = string.Empty;
    private string _errorMessage = string.Empty;
    private string _actionMessage = string.Empty;
    private string _testResult = string.Empty;
    private bool _hasTestResult;
    private string _containerStatus = "尚未连接：容器里还没出现请求";
    private string _selfCheckSummary = "尚未运行自检";
    private bool _containerConnected;
    private bool _listenRestartRequired;
    private int _currentPort = ConfigDefaults.ListenPort;
    private string _currentAddress = ConfigDefaults.ListenAddress;

    public PairingWizardViewModel(GuiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        _probe = new LocalHealthProbe(context.Logger);
        _address = new StringField(
            ConfigKeys.ListenAddress,
            "监听地址",
            null,
            maxLength: 64,
            validator: ConfigRules.ValidateAddress,
            warningProvider: ConfigRules.WarningForAddress);
        _port = new IntField(ConfigKeys.ListenPort, "监听端口", null);
        NextCommand = new AsyncRelayCommand(NextAsync, () => CanGoNext && !IsBusy);
        BackCommand = new RelayCommand(Back, () => Step > 1 && !IsBusy);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        SkipCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        RetryTokenCommand = new AsyncRelayCommand(EnsureTokenAsync, () => !IsBusy);
        CopyTokenCommand = new AsyncRelayCommand(CopyTokenAsync, () => TokenAvailable && !IsBusy);
        CopyPluginConfigCommand = new AsyncRelayCommand(CopyPluginConfigAsync, () => TokenAvailable && !IsBusy);
        CopyDockerCommand = new AsyncRelayCommand(CopyDockerCommandAsync, () => !IsBusy);
        CopyFirewallCommand = new RelayCommand(() => CopyText(FirewallCommandText, "已复制防火墙命令（需管理员运行）"));
        TestCommand = new AsyncRelayCommand(RunLocalTestAsync, () => !IsBusy);
        RunSelfCheckCommand = new AsyncRelayCommand(RunSelfCheckAsync, () => !IsBusy);
        NextCommand.ExecutionFailed += (_, exception) => HandleFailure(exception);
        _address.PropertyChanged += (_, _) => UpdateAddressState();
        _port.PropertyChanged += (_, _) => UpdatePortState();
    }

    public event EventHandler? CloseRequested;

    public GuiContext Context { get; }

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsFirstStep));
                OnPropertyChanged(nameof(IsLastStep));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepIndicator));
                OnPropertyChanged(nameof(NextButtonText));
                OnPropertyChanged(nameof(CanGoNext));
                BackCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFirstStep => Step == 1;

    public bool IsLastStep => Step == StepCount;

    public string StepTitle => StepTitles[Math.Clamp(Step - 1, 0, StepTitles.Count - 1)];

    public string StepIndicator => "第 " + Step.ToString(CultureInfo.InvariantCulture) + " / " + StepCount.ToString(CultureInfo.InvariantCulture) + " 步";

    public string NextButtonText => IsLastStep ? "完成" : "下一步";

    public bool Acknowledged
    {
        get => _acknowledged;
        set
        {
            if (SetProperty(ref _acknowledged, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public StringField ListenAddressField => _address;

    public IntField ListenPortField => _port;

    public string AddressText
    {
        get => _address.Value;
        set => _address.Value = value;
    }

    public string PortText
    {
        get => _port.Text;
        set => _port.Text = value;
    }

    public string? AddressError => _address.Error;

    public string? PortError => _port.Error;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NextCommand.RaiseCanExecuteChanged();
                BackCommand.RaiseCanExecuteChanged();
                RetryTokenCommand.RaiseCanExecuteChanged();
                CopyTokenCommand.RaiseCanExecuteChanged();
                CopyPluginConfigCommand.RaiseCanExecuteChanged();
                CopyDockerCommand.RaiseCanExecuteChanged();
                TestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool TokenAvailable
    {
        get => _tokenAvailable;
        private set
        {
            if (SetProperty(ref _tokenAvailable, value))
            {
                CopyTokenCommand.RaiseCanExecuteChanged();
                CopyPluginConfigCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowTokenWarning));
            }
        }
    }

    public bool ShowTokenWarning => !TokenAvailable;

    public string TokenMasked
    {
        get => _tokenMasked;
        private set => SetProperty(ref _tokenMasked, value);
    }

    public bool ContainerConnected
    {
        get => _containerConnected;
        private set
        {
            if (SetProperty(ref _containerConnected, value))
            {
                OnPropertyChanged(nameof(ContainerStatus));
            }
        }
    }

    public string ContainerStatus
    {
        get => _containerConnected
            ? "✅ 容器已连上：检测到活跃会话 / 最近请求"
            : _containerStatus;
        private set => SetProperty(ref _containerStatus, value);
    }

    public bool HasTestResult
    {
        get => _hasTestResult;
        private set => SetProperty(ref _hasTestResult, value);
    }

    public string TestResult
    {
        get => _testResult;
        private set
        {
            if (SetProperty(ref _testResult, value))
            {
                HasTestResult = !string.IsNullOrEmpty(value);
            }
        }
    }

    public bool ListenRestartRequired
    {
        get => _listenRestartRequired;
        private set
        {
            if (SetProperty(ref _listenRestartRequired, value))
            {
                OnPropertyChanged(nameof(FinishRestartNote));
            }
        }
    }

    public string FinishRestartNote => ListenRestartRequired
        ? "监听地址/端口已修改，需重启执行器后生效（可在「概览」或托盘退出后重新启动）。"
        : "监听配置未修改。";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

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

    public bool ShowLocalhostWarning => ConfigRules.IsLocalhostAddress(_address.Value);

    public string PluginUrl => PluginConfigSnippet.BuildUrl(EffectivePort);

    public int EffectivePort => _port.NumericValue is > 0 and <= 65535 ? _port.NumericValue!.Value : _currentPort;

    public string PluginPreviewJson => PluginConfigSnippet.BuildJson(EffectivePort, null);

    public string FirewallCommandText => PluginConfigSnippet.BuildNetshFirewallCommand(EffectivePort);

    public string LogsDirectory => Context.LogsDirectory ?? "%LOCALAPPDATA%\\WhaleGo\\logs";

    public string AuditDirectory => Context.AuditDirectory ?? LogsDirectory;

    public static string RiskIntro =>
        "WhaleGo 让 AstrBot 里的模型观察并操作这台 Windows：看屏幕、点鼠标、敲键盘。它必须在本机以你的用户身份运行，" +
        "模型只能通过 token 访问。截图默认开启但默认不自动附图（模型按需请求；敏感窗口自动抑制；截图不得转发）。" +
        "任何时候可以按 Ctrl+Alt+Shift+F12 紧急停止，恢复只能在本机确认。";

    public static IReadOnlyList<string> ContainerSteps { get; } =
    [
        "打开 AstrBot WebUI →「插件管理」→「已安装插件」→ WhaleGo",
        "点「配置」，把上一步复制的 JSON 粘进去（url + token 必填，admin_only 保持 true）",
        "保存 →「重载插件」，日志出现已连接即成功",
        "可选验证：对机器人发送 /whalego health",
    ];

    public static IReadOnlyList<string> FinishSteps { get; } =
    [
        "在 AstrBot WebUI 保存并重载 WhaleGo 插件",
        "对机器人发送 /whalego health（或 /wg health）确认连通",
        "需要时双击托盘图标打开设置，随时可暂停/紧急停止",
        "WhaleGo 不会自启：下次开机请手动启动 WhaleGo.Executor.exe",
    ];

    public AsyncRelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand SkipCommand { get; }

    public AsyncRelayCommand RetryTokenCommand { get; }

    public AsyncRelayCommand CopyTokenCommand { get; }

    public AsyncRelayCommand CopyPluginConfigCommand { get; }

    public AsyncRelayCommand CopyDockerCommand { get; }

    public RelayCommand CopyFirewallCommand { get; }

    public AsyncRelayCommand TestCommand { get; }

    public AsyncRelayCommand RunSelfCheckCommand { get; }

    /// <summary>Compact kernel self-check summary shown on step 4 ("连不上卡在哪").</summary>
    public string SelfCheckSummary
    {
        get => _selfCheckSummary;
        private set => SetProperty(ref _selfCheckSummary, value);
    }

    public bool CanGoNext => Step switch
    {
        1 => Acknowledged,
        2 => _address.Validate() & _port.Validate(),
        3 => true,
        4 => true,
        5 => true,
        _ => false,
    };

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var config = await Context.Control.GetConfigAsync().ConfigureAwait(true);
            if (config.Success && config.Value is not null)
            {
                var addressValue = config.Value.Values.TryGetValue(ConfigKeys.ListenAddress, out var addressRaw) ? addressRaw : null;
                var portValue = config.Value.Values.TryGetValue(ConfigKeys.ListenPort, out var portRaw) ? portRaw : null;
                _address.LoadFrom(addressValue ?? ConfigDefaults.ListenAddress);
                _port.LoadFrom(portValue ?? ConfigDefaults.ListenPort);
                _currentAddress = _address.Value;
                _currentPort = _port.NumericValue ?? ConfigDefaults.ListenPort;
                Context.AuditDirectory = ConfigValue.AsString(
                    config.Value.Values.TryGetValue(ConfigKeys.AuditDir, out var auditRaw) ? auditRaw : null);
            }

            await LoadTokensAsync().ConfigureAwait(true);
            if (!TokenAvailable)
            {
                await EnsureTokenAsync().ConfigureAwait(true);
            }

            OnPropertyChanged(nameof(AddressText));
            OnPropertyChanged(nameof(PortText));
            OnPropertyChanged(nameof(PluginUrl));
            OnPropertyChanged(nameof(PluginPreviewJson));
            OnPropertyChanged(nameof(FirewallCommandText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Polled by the wizard window every 2s while step 4 is visible.</summary>
    public async Task PollConnectionAsync()
    {
        var status = await Context.Control.GetStatusAsync().ConfigureAwait(true);
        if (!status.Success || status.Value is null)
        {
            return;
        }

        var recent = await Context.Control.GetRecentRequestsAsync(50).ConfigureAwait(true);
        var hasRecent = recent.Success && recent.Value is not null && recent.Value.Count > 0;
        var connected = status.Value.Sessions > 0 || hasRecent;
        if (connected)
        {
            ContainerConnected = true;
        }
        else
        {
            ContainerStatus = "等待容器连接：请在 AstrBot WebUI 粘贴插件配置并重载插件";
        }
    }

    private async Task NextAsync()
    {
        ErrorMessage = string.Empty;
        if (Step == 2 && !await ApplyListenAsync().ConfigureAwait(true))
        {
            return;
        }

        if (Step == 3 && !TokenAvailable)
        {
            ErrorMessage = "token 不可读：请点「重新生成 token」后再继续";
            return;
        }

        if (Step == StepCount)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Step++;
        if (Step == 4)
        {
            await PollConnectionAsync().ConfigureAwait(true);
        }
    }

    private void Back()
    {
        ErrorMessage = string.Empty;
        if (Step > 1)
        {
            Step--;
        }
    }

    private async Task<bool> ApplyListenAsync()
    {
        if (!_address.Validate() | !_port.Validate())
        {
            ErrorMessage = _address.Error ?? _port.Error ?? "监听配置不合法";
            return false;
        }

        var address = _address.Value.Trim();
        var port = _port.NumericValue!.Value;
        if (string.Equals(address, _currentAddress, StringComparison.Ordinal) && port == _currentPort)
        {
            return true;
        }

        var patch = new ConfigPatch(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ConfigKeys.ListenAddress] = address,
            [ConfigKeys.ListenPort] = port,
        });
        var result = await Context.Control.SetConfigAsync(patch).ConfigureAwait(true);
        if (!result.Success)
        {
            ErrorMessage = "保存监听配置失败：" + (result.ErrorMessage ?? "未知错误");
            return false;
        }

        _currentAddress = address;
        _currentPort = port;
        ListenRestartRequired = true;
        OnPropertyChanged(nameof(PluginUrl));
        OnPropertyChanged(nameof(PluginPreviewJson));
        OnPropertyChanged(nameof(FirewallCommandText));
        return true;
    }

    private async Task LoadTokensAsync()
    {
        var tokens = await Context.Control.GetTokensAsync().ConfigureAwait(true);
        var descriptors = tokens.Success && tokens.Value is not null ? tokens.Value : [];
        var primary = TokenAccess.Primary(descriptors);
        TokenAvailable = TokenAccess.AnyAvailable(descriptors);
        TokenMasked = primary is null ? "尚未生成" : TokenMasker.Display(primary);
        if (TokenAvailable)
        {
            var revealed = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
            _tokenPlaintext = revealed.Success ? revealed.Value ?? string.Empty : string.Empty;
        }
    }

    private async Task EnsureTokenAsync()
    {
        IsBusy = true;
        try
        {
            if (!Context.Dialogs.Confirm(
                    "生成 token",
                    "将为 AstrBot 生成一个访问 token（旧 token 若存在会失效）。生成后只会显示一次掩码，请立即复制插件配置。",
                    "生成"))
            {
                return;
            }

            var rotated = await Context.Control.RotateTokenAsync(DefaultTokenName).ConfigureAwait(true);
            if (!rotated.Success || string.IsNullOrEmpty(rotated.Value))
            {
                ErrorMessage = "生成 token 失败：" + (rotated.ErrorMessage ?? "未知错误");
                return;
            }

            _tokenPlaintext = rotated.Value;
            TokenAvailable = true;
            TokenMasked = TokenMasker.Mask(rotated.Value);
            Context.Clipboard.SetText(rotated.Value, TimeSpan.FromSeconds(60));
            ActionMessage = "已生成 token 并复制到剪贴板（60 秒后自动清空）";
            Context.Balloon?.Invoke(BalloonKind.Info, "WhaleGo 已生成 token", "请立即复制插件配置并粘进 AstrBot。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CopyTokenAsync()
    {
        if (string.IsNullOrEmpty(_tokenPlaintext))
        {
            var revealed = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
            _tokenPlaintext = revealed.Success ? revealed.Value ?? string.Empty : string.Empty;
        }

        if (string.IsNullOrEmpty(_tokenPlaintext))
        {
            ErrorMessage = "token 不可读，请重新生成";
            return;
        }

        Context.Clipboard.SetText(_tokenPlaintext, TimeSpan.FromSeconds(60));
        ActionMessage = "已复制 token（60 秒后自动清空）";
    }

    private async Task CopyPluginConfigAsync()
    {
        if (string.IsNullOrEmpty(_tokenPlaintext))
        {
            var revealed = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
            _tokenPlaintext = revealed.Success ? revealed.Value ?? string.Empty : string.Empty;
        }

        var json = PluginConfigSnippet.BuildJson(EffectivePort, _tokenPlaintext);
        Context.Clipboard.SetText(json, TimeSpan.FromSeconds(60));
        ActionMessage = ShowLocalhostWarning
            ? "已复制插件配置；但当前仅本机监听，容器连不上，请改监听地址"
            : "已复制插件配置（含 token，60 秒后自动清空）";
    }

    private async Task CopyDockerCommandAsync()
    {
        if (string.IsNullOrEmpty(_tokenPlaintext))
        {
            var revealed = await Context.Control.RevealTokenForCopyAsync().ConfigureAwait(true);
            _tokenPlaintext = revealed.Success ? revealed.Value ?? string.Empty : string.Empty;
        }

        var command = PluginConfigSnippet.BuildDockerHealthCommand(EffectivePort, _tokenPlaintext);
        Context.Clipboard.SetText(command, TimeSpan.FromSeconds(60));
        ActionMessage = "已复制容器内探测命令（在宿主机执行，不会自动运行）";
    }

    private async Task RunLocalTestAsync()
    {
        IsBusy = true;
        TestResult = "测试中…";
        try
        {
            var token = string.IsNullOrEmpty(_tokenPlaintext) ? null : _tokenPlaintext;
            var result = await _probe.ProbeAsync(EffectivePort, token, false).ConfigureAwait(true);
            TestResult = (result.Ok ? "✅ " : "❌ ") + result.Detail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunSelfCheckAsync()
    {
        IsBusy = true;
        SelfCheckSummary = "自检中…";
        try
        {
            var report = await Context.Control.RunSelfCheckAsync().ConfigureAwait(true);
            if (!report.Success || report.Value is null)
            {
                SelfCheckSummary = "自检失败：" + (report.ErrorMessage ?? "未知错误");
                return;
            }

            var failed = report.Value.Items.Count(static item => item.Status == SelfCheckStatus.Fail);
            var warned = report.Value.Items.Count(static item => item.Status == SelfCheckStatus.Warn);
            SelfCheckSummary = failed > 0
                ? "自检：❌ " + failed.ToString(CultureInfo.InvariantCulture) + " 项失败（请在「诊断与日志」查看建议）"
                : warned > 0
                    ? "自检：⚠️ " + warned.ToString(CultureInfo.InvariantCulture) + " 项警告（可在「诊断与日志」查看）"
                    : "自检：✅ 全部通过（" + report.Value.Items.Count.ToString(CultureInfo.InvariantCulture) + " 项）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyText(string text, string message)
    {
        Context.Clipboard.SetText(text, TimeSpan.FromSeconds(60));
        ActionMessage = message;
    }

    private void UpdateAddressState()
    {
        _address.Validate();
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(AddressError));
        OnPropertyChanged(nameof(ShowLocalhostWarning));
        OnPropertyChanged(nameof(PluginUrl));
        OnPropertyChanged(nameof(PluginPreviewJson));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void UpdatePortState()
    {
        _port.Validate();
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(PortError));
        OnPropertyChanged(nameof(PluginUrl));
        OnPropertyChanged(nameof(PluginPreviewJson));
        OnPropertyChanged(nameof(FirewallCommandText));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void HandleFailure(Exception exception)
    {
        Context.Logger.Error("pairing wizard command failed", exception);
        ErrorMessage = "操作失败：" + exception.Message;
        IsBusy = false;
    }
}
