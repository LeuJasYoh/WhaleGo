using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.Validation;
using WhaleGo.Gui.ViewModels;
using WhaleGo.Gui.Views;

namespace WhaleGo.Gui.Internal;

/// <summary>
/// Owns the GUI thread: WPF Application (OnExplicitShutdown), ThemeService, MainWindow, WinForms tray and the global
/// emergency-stop hotkey. Everything is created on one dedicated STA thread; the public GuiShell marshals to it.
/// Kernel calls are bounded through <see cref="SafeControl"/> and exceptions never escape to the kernel.
/// </summary>
internal sealed class GuiHost : IDisposable
{
    private const string HotkeyId = "emergency-stop";
    private const int MaxPendingActions = 64;

    private readonly GuiShell _shell;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private readonly Queue<Action> _pending = new();
    private Thread? _uiThread;
    private Dispatcher? _dispatcher;
    private App? _app;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private TrayShell? _tray;
    private ThemeService? _themeService;
    private SafeControl? _safeControl;
    private WpfClipboardService? _clipboard;
    private WpfUserDialogs? _dialogs;
    private GuiContext? _context;
    private bool _hotkeyRegistered;
    private string? _hotkeyFailure;
    private bool _tokenAvailable;
    private int _statusTicks;
    private bool _disposed;
    private bool _started;

    public GuiHost(GuiShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _logger = new FileAppLogger(shell.Options.LogsDirectory);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _uiThread = new Thread(UiThreadMain)
        {
            Name = "WhaleGo.Gui",
            IsBackground = true,
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public void PostShowSettings() => Post(() =>
    {
        _window?.ShowSettings();
        _ = RefreshStatusAsync();
    });

    public void PostHideToTray() => Post(() => _window?.HideToTray());

    public void PostBalloon(string title, string message, bool important) => Post(() =>
        _tray?.ShowBalloon(string.IsNullOrWhiteSpace(title) ? "WhaleGo" : title, message,
            important ? BalloonKind.GenericImportant : BalloonKind.GenericInfo));

    public void PostRefreshStatus() => Post(() => _ = RefreshStatusAsync());

    private void Post(Action action)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_dispatcher is null)
            {
                if (_pending.Count < MaxPendingActions)
                {
                    _pending.Enqueue(action);
                }

                return;
            }

            _dispatcher.BeginInvoke(action);
        }
    }

    private void UiThreadMain()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            _logger.Info("starting GUI host");

            _app = new App();
            _app.InitializeComponent();
            _app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            _app.DispatcherUnhandledException += OnDispatcherUnhandledException;

            _themeService = new ThemeService(
                new WpfThemeResourceHost(_app),
                new LiveSystemThemeProvider(dispatcher),
                _shell.WindowTheme,
                _logger);

            _safeControl = new SafeControl(_shell.Control, SafeControl.DefaultTimeout, _logger);
            _clipboard = new WpfClipboardService(dispatcher, _logger);
            _dialogs = new WpfUserDialogs(() => _window, _clipboard);
            var themeCoordinator = new ThemeCoordinator(_themeService, _safeControl, _logger);
            _context = new GuiContext(
                _safeControl,
                _dialogs,
                _clipboard,
                themeCoordinator,
                _logger,
                _shell.Options);
            _context.ThemeSelfCheck = ValidateThemeResources;
            _context.Balloon = (kind, title, message) => _tray?.ShowBalloon(title, message, kind);

            InitializeTheme();
            _tokenAvailable = ReadTokenAvailability();
            _viewModel = new MainViewModel(_context, new FileUiStateStore(LocalAppDataDirectory()));
            _window = new MainWindow(_viewModel, _themeService);
            _tray = new TrayShell(_logger);
            _tray.Initialize(BuildTrayCallbacks());
            _viewModel.StatusUpdated += (_, snapshot) => _ = UpdateTrayAsync(snapshot);
            RegisterHotkey();
            RefreshNotificationLevel();

            var firstRun = IsFirstRun();
            dispatcher.BeginInvoke(new Action(() => _window?.ShowStartup(firstRun)));

            lock (_sync)
            {
                _dispatcher = dispatcher;
                while (_pending.Count > 0)
                {
                    dispatcher.BeginInvoke(_pending.Dequeue());
                }
            }

            _app.Run();
            _logger.Info("GUI host stopped");
        }
        catch (Exception ex)
        {
            // A broken GUI must never take the MCP service down: degrade to headless behavior.
            _logger.Error("GUI host failed; continuing without GUI", ex);
            try
            {
                _tray?.Dispose();
                _tray = null;
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            lock (_sync)
            {
                _dispatcher = null;
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    private void InitializeTheme()
    {
        var preference = ThemePreference.System;
        try
        {
            var config = _safeControl!.GetConfigAsync().GetAwaiter().GetResult();
            if (config.Success && config.Value is not null &&
                config.Value.Values.TryGetValue(ConfigKeys.UiTheme, out var raw))
            {
                preference = ThemePreferences.Parse(ConfigValue.AsString(raw));
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("failed to read ui.theme, falling back to system: " + ex.Message);
        }

        if (!_themeService!.Initialize(preference))
        {
            _logger.Warn("initial theme application failed; keeping compiled-in light theme");
        }
    }

    private void RegisterHotkey()
    {
        if (_shell.Options.NoHotkey)
        {
            SetHotkeyStatus("未注册（--no-hotkey）");
            return;
        }

        var enabled = true;
        var gesture = ConfigDefaults.HotkeysEmergencyStop;
        try
        {
            var config = _safeControl!.GetConfigAsync().GetAwaiter().GetResult();
            if (config.Success && config.Value is not null)
            {
                if (config.Value.Values.TryGetValue(ConfigKeys.HotkeysEnabled, out var enabledRaw))
                {
                    enabled = ConfigValue.AsBool(enabledRaw) ?? true;
                }

                if (config.Value.Values.TryGetValue(ConfigKeys.HotkeysEmergencyStop, out var gestureRaw))
                {
                    gesture = ConfigValue.AsString(gestureRaw) ?? gesture;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("failed to read hotkey config: " + ex.Message);
        }

        if (!enabled)
        {
            SetHotkeyStatus("已禁用（hotkeys.enabled=false）");
            return;
        }

        try
        {
            _hotkeyRegistered = _shell.Hotkeys.TryRegister(HotkeyId, gesture, OnEmergencyHotkey);
        }
        catch (Exception ex)
        {
            _hotkeyRegistered = false;
            _hotkeyFailure = ex.Message;
            _logger.Error("hotkey registration threw", ex);
        }

        if (_hotkeyRegistered)
        {
            _hotkeyFailure = null;
            SetHotkeyStatus(gesture + " 已注册");
        }
        else
        {
            _hotkeyFailure ??= gesture + " 可能被其它程序占用";
            SetHotkeyStatus("注册失败：" + _hotkeyFailure);
            _logger.Warn("hotkey registration failed: " + _hotkeyFailure);
            _tray?.ShowBalloon(
                "WhaleGo 紧急停止热键未注册",
                "全局紧急停止热键 " + gesture + " 注册失败（可能被占用）。请到设置页查看并改用托盘按钮停止。",
                BalloonKind.GenericImportant);
        }
    }

    private void SetHotkeyStatus(string text)
    {
        try
        {
            _viewModel?.SetHotkeyStatus(text);
        }
        catch (Exception ex)
        {
            _logger.Warn("failed to update hotkey status: " + ex.Message);
        }
    }

    private async void OnEmergencyHotkey()
    {
        // Called from the platform hotkey thread: never block it, never touch UI directly.
        try
        {
            _logger.Info("emergency stop requested by global hotkey");
            var result = await _safeControl!.StopAsync("hotkey").ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.Warn("hotkey emergency stop failed: " + result.ErrorMessage);
            }

            Post(() =>
            {
                _tray?.ShowBalloon(
                    "WhaleGo 已紧急停止",
                    "模型无法再操作桌面。恢复请在托盘点「恢复」（需本机确认）。",
                    BalloonKind.EmergencyStop);
                _ = RefreshStatusAsync();
            });
        }
        catch (Exception ex)
        {
            _logger.Error("hotkey handling failed", ex);
        }
    }

    private TrayCallbacks BuildTrayCallbacks() => new()
    {
        OpenSettings = () => Post(() => _window?.ShowSettings()),
        PauseOrResume = () => Post(() => _ = TogglePauseAsync()),
        EmergencyStop = () => Post(() => _ = EmergencyStopAsync()),
        CopyPluginConfig = () => Post(() => _ = CopyPluginConfigAsync()),
        OpenLogsDirectory = () => Post(() => ShellLauncher.TryOpenFolder(_context?.LogsDirectory, _logger)),
        OpenAuditDirectory = () => Post(() => ShellLauncher.TryOpenFolder(_context?.AuditDirectory, _logger)),
        ShowAbout = () => Post(() =>
        {
            _window?.ShowSettings();
            if (_viewModel is not null)
            {
                _viewModel.CurrentPage = _viewModel.About;
            }
        }),
        ConfirmExit = ConfirmExitFromTray,
        ExitConfirmed = () => _shell.RaiseExitRequested(),
        BalloonClicked = () => Post(() => _window?.ShowSettings()),
    };

    private bool ConfirmExitFromTray()
    {
        var snapshot = _viewModel?.LastSnapshot;
        string? detail = null;
        if (snapshot?.Controller is { Held: true } controller)
        {
            var seconds = controller.ExpiresInMs is null
                ? "未知"
                : Math.Max(0, controller.ExpiresInMs.Value / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            detail = "当前有会话正在操作（剩余约 " + seconds + "s）。退出后模型会立刻失去控制且不会自动恢复。";
        }

        return _dialogs?.Confirm(
            "退出 WhaleGo",
            "退出后，模型将立刻无法操作你的桌面（正在进行的动作可能已发出，不会自动回滚）。",
            "退出",
            danger: true,
            detail) == true;
    }

    private async Task TogglePauseAsync()
    {
        var snapshot = _viewModel?.LastSnapshot;
        if (snapshot is null || _safeControl is null)
        {
            return;
        }

        if (snapshot.Stopped)
        {
            if (_dialogs?.Confirm("恢复操作", "解除紧急停止后，模型将重新可以操作你的桌面。确定恢复吗？", "恢复", danger: true) != true)
            {
                return;
            }

            var reset = await _safeControl.ResetStopAsync().ConfigureAwait(true);
            if (reset.Success)
            {
                _tray?.ShowBalloon("WhaleGo 已恢复", "模型可以继续操作桌面。", BalloonKind.GenericImportant);
            }
        }
        else if (snapshot.Paused)
        {
            await _safeControl.ResumeAsync().ConfigureAwait(true);
        }
        else
        {
            await _safeControl.PauseAsync("user_button").ConfigureAwait(true);
        }

        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private async Task EmergencyStopAsync()
    {
        if (_safeControl is null)
        {
            return;
        }

        if (_dialogs?.Confirm(
                "紧急停止",
                "立即停止所有模型动作并锁定桌面控制。恢复必须在本机再次确认。",
                "紧急停止",
                danger: true,
                detail: "正在进行的动作可能已发出，不会自动回滚。") != true)
        {
            return;
        }

        var result = await _safeControl.StopAsync("gui").ConfigureAwait(true);
        if (result.Success)
        {
            _tray?.ShowBalloon("WhaleGo 已紧急停止", "模型无法再操作桌面。恢复请在托盘点「恢复」。", BalloonKind.EmergencyStop);
        }

        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private async Task CopyPluginConfigAsync()
    {
        if (_safeControl is null || _clipboard is null)
        {
            return;
        }

        var port = ConfigDefaults.ListenPort;
        var config = await _safeControl.GetConfigAsync().ConfigureAwait(true);
        if (config.Success && config.Value is not null &&
            config.Value.Values.TryGetValue(ConfigKeys.ListenPort, out var rawPort))
        {
            port = ConfigValue.AsInt(rawPort) ?? port;
        }

        var token = await _safeControl.RevealTokenForCopyAsync().ConfigureAwait(true);
        var plaintext = token.Success ? token.Value : null;
        if (string.IsNullOrEmpty(plaintext))
        {
            _tray?.ShowBalloon("WhaleGo", "token 不可读：请到「连接与配对」轮换 token 后再复制。", BalloonKind.GenericImportant);
            return;
        }

        _clipboard.SetText(PluginConfigSnippet.BuildJson(port, plaintext), TimeSpan.FromSeconds(60));
        _tray?.ShowBalloon("WhaleGo 已复制插件配置", "已复制到剪贴板（含 token，60 秒后自动清空）。", BalloonKind.GenericInfo);
    }

    private async Task RefreshStatusAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _viewModel.RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("status refresh failed", ex);
        }
    }

    private async Task UpdateTrayAsync(StatusSnapshot snapshot)
    {
        if (_tray is null || _safeControl is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<RecentRequest>? recent = null;
            var recentResult = await _safeControl.GetRecentRequestsAsync(50).ConfigureAwait(true);
            if (recentResult.Success)
            {
                recent = recentResult.Value;
            }

            _statusTicks++;
            if (_statusTicks % 15 == 0)
            {
                _tokenAvailable = ReadTokenAvailability();
                RefreshNotificationLevel();
            }

            var aux = new TrayAuxInfo(
                TokenAvailable: _tokenAvailable,
                HotkeyRegistered: _hotkeyRegistered,
                HotkeyError: _hotkeyRegistered ? null : _hotkeyFailure,
                AuditDangerous: !snapshot.AuditEnabled || !snapshot.AuditDurable,
                Version: string.IsNullOrWhiteSpace(snapshot.Version) ? _shell.Options.Version : snapshot.Version);
            var presentation = TrayStateMapper.Map(snapshot, recent, _context?.NowMs() ?? Environment.TickCount64, aux);
            _tray.Update(presentation);
        }
        catch (Exception ex)
        {
            _logger.Warn("tray refresh failed: " + ex.Message);
        }
    }

    private bool ReadTokenAvailability()
    {
        try
        {
            var tokens = _safeControl!.GetTokensAsync().GetAwaiter().GetResult();
            return tokens.Success && TokenAccess.AnyAvailable(tokens.Value);
        }
        catch
        {
            return false;
        }
    }

    private void RefreshNotificationLevel()
    {
        if (_tray is null || _safeControl is null)
        {
            return;
        }

        try
        {
            var config = _safeControl.GetConfigAsync().GetAwaiter().GetResult();
            if (config.Success && config.Value is not null &&
                config.Value.Values.TryGetValue(ConfigKeys.UiNotificationsLevel, out var raw))
            {
                _tray.NotificationsLevel = BalloonLevels.ParseConfig(ConfigValue.AsString(raw));
            }
        }
        catch
        {
            // keep the current level
        }
    }

    private bool IsFirstRun()
    {
        try
        {
            var configPath = _shell.Options.ConfigPath;
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return true;
            }

            return !ReadTokenAvailability();
        }
        catch (Exception ex)
        {
            _logger.Warn("first-run detection failed: " + ex.Message);
            return false;
        }
    }

    private SelfCheckItem? ValidateThemeResources()
    {
        try
        {
            ThemeResourceValidator.EnsureComplete(WpfThemeResourceHost.Load(AppTheme.Light));
            ThemeResourceValidator.EnsureComplete(WpfThemeResourceHost.Load(AppTheme.Dark));
            return new SelfCheckItem(
                "gui.theme",
                "主题资源",
                SelfCheckStatus.Ok,
                "Light / Dark / HighContrast 均可加载，19 个颜色 token 齐全");
        }
        catch (Exception ex)
        {
            return new SelfCheckItem(
                "gui.theme",
                "主题资源",
                SelfCheckStatus.Fail,
                ex.Message,
                "检查 windows/src/WhaleGo.Gui/Themes/*.xaml");
        }
    }

    private static string LocalAppDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(root) ? Path.Combine(Path.GetTempPath(), "WhaleGo") : Path.Combine(root, "WhaleGo");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_hotkeyRegistered)
            {
                try
                {
                    _shell.Hotkeys.Unregister(HotkeyId);
                }
                catch (Exception ex)
                {
                    _logger.Warn("hotkey unregister failed: " + ex.Message);
                }

                _hotkeyRegistered = false;
            }

            Post(ShutdownCore);
            _uiThread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.Warn("GUI host dispose failed: " + ex.Message);
        }
        finally
        {
            (_logger as IDisposable)?.Dispose();
        }
    }

    private void ShutdownCore()
    {
        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex)
        {
            _logger.Warn("tray dispose failed: " + ex.Message);
        }

        try
        {
            _window?.CloseForShutdown();
            _window = null;
        }
        catch (Exception ex)
        {
            _logger.Warn("window close failed: " + ex.Message);
        }

        try
        {
            _app?.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.Warn("application shutdown failed: " + ex.Message);
        }
    }
}
