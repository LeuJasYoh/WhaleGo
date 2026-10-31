using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using WhaleGo.Gui.Infrastructure;
using WpfSystemParameters = System.Windows.SystemParameters;

namespace WhaleGo.Gui.Tray;

/// <summary>Tray menu callbacks provided by the shell (all invoked on the UI thread).</summary>
public sealed class TrayCallbacks
{
    public Action? OpenSettings { get; init; }

    public Action? PauseOrResume { get; init; }

    public Action? EmergencyStop { get; init; }

    public Action? CopyPluginConfig { get; init; }

    public Action? OpenLogsDirectory { get; init; }

    public Action? OpenAuditDirectory { get; init; }

    public Action? ShowAbout { get; init; }

    /// <summary>Shows the exit confirmation dialog; returns true when the user really wants to exit.</summary>
    public Func<bool>? ConfirmExit { get; init; }

    /// <summary>Raised after the exit confirmation was accepted; the host performs graceful shutdown.</summary>
    public Action? ExitConfirmed { get; init; }

    public Action? BalloonClicked { get; init; }
}

/// <summary>
/// WinForms NotifyIcon tray: five states, system-rendered context menu, balloon policy and the only exit path.
/// Living on the WPF dispatcher thread is fine (NotifyIcon needs a message loop; the dispatcher provides one).
/// </summary>
public sealed class TrayShell : IDisposable
{
    private readonly TrayIconProvider _icons;
    private readonly BalloonPolicy _balloonPolicy;
    private readonly IAppLogger _logger;
    private readonly List<ToolStripMenuItem> _warningItems = [];
    private TrayCallbacks? _callbacks;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _emergencyItem;
    private ToolStripMenuItem? _copyItem;
    private bool _disposed;

    public TrayShell(IAppLogger logger, Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _icons = new TrayIconProvider();
        _balloonPolicy = new BalloonPolicy(nowMs ?? (() => Environment.TickCount64));
    }

    /// <summary>ui.notificationsLevel mapped to balloon filtering.</summary>
    public BalloonLevel NotificationsLevel { get; set; } = BalloonLevel.Important;

    public bool IsInitialized => _notifyIcon is not null;

    /// <summary>Creates the NotifyIcon + menu and shows the tray icon. Must run on a thread with a message pump.</summary>
    public void Initialize(TrayCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        if (_notifyIcon is not null || _disposed)
        {
            return;
        }

        _callbacks = callbacks;
        _menu = BuildMenu(callbacks);
        _notifyIcon = new NotifyIcon
        {
            Text = "WhaleGo",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => SafeInvoke(callbacks.OpenSettings, "open settings");
        _notifyIcon.BalloonTipClicked += (_, _) => SafeInvoke(callbacks.BalloonClicked ?? callbacks.OpenSettings, "balloon click");
        ApplyIcon(TrayState.RunningIdle);
        _logger.Info("tray icon initialized");
    }

    /// <summary>Applies an immutable presentation produced by <see cref="TrayStateMapper"/>.</summary>
    public void Update(TrayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (_disposed || _notifyIcon is null || _menu is null)
        {
            return;
        }

        try
        {
            ApplyIcon(presentation.State);
            var tooltip = presentation.Tooltip;
            _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
            if (_statusItem is not null)
            {
                _statusItem.Text = presentation.StatusLine;
            }

            for (var i = 0; i < _warningItems.Count; i++)
            {
                var item = _warningItems[i];
                if (i < presentation.WarningLines.Count)
                {
                    item.Text = presentation.WarningLines[i];
                    item.Visible = true;
                }
                else
                {
                    item.Visible = false;
                }
            }

            if (_pauseItem is not null)
            {
                _pauseItem.Text = presentation.PauseResumeLabel;
                _pauseItem.Enabled = presentation.PauseResumeEnabled;
            }

            if (_emergencyItem is not null)
            {
                _emergencyItem.Enabled = presentation.EmergencyStopEnabled;
            }

            if (_copyItem is not null)
            {
                _copyItem.Enabled = presentation.CopyConfigEnabled;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("tray update failed: " + ex.Message);
        }
    }

    /// <summary>Shows a balloon when the configured level + cooldowns allow it; returns whether it was shown.</summary>
    public bool ShowBalloon(string title, string message, BalloonKind kind)
    {
        if (_disposed || _notifyIcon is null || !_notifyIcon.Visible)
        {
            return false;
        }

        if (!_balloonPolicy.Evaluate(kind, NotificationsLevel))
        {
            return false;
        }

        var text = BalloonPolicy.Sanitize(message);
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                title,
                text,
                BalloonLevels.IsImportant(kind) ? ToolTipIcon.Warning : ToolTipIcon.Info);
            _balloonPolicy.RecordShown(kind);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("balloon failed: " + ex.Message);
            return false;
        }
    }

    private ContextMenuStrip BuildMenu(TrayCallbacks callbacks)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        _statusItem = new ToolStripMenuItem("WhaleGo 正在启动…") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(_statusItem);

        // Two optional warning rows (hotkey not registered / audit not fail-closed), hidden by default.
        for (var i = 0; i < 2; i++)
        {
            var warning = new ToolStripMenuItem(string.Empty) { Enabled = false, Visible = false };
            _warningItems.Add(warning);
            menu.Items.Add(warning);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开设置...", null, (_, _) => SafeInvoke(callbacks.OpenSettings, "open settings"))
        {
            ShortcutKeyDisplayString = "双击托盘",
        });
        _pauseItem = new ToolStripMenuItem("暂停", null, (_, _) => SafeInvoke(callbacks.PauseOrResume, "pause/resume"));
        menu.Items.Add(_pauseItem);
        _emergencyItem = new ToolStripMenuItem("紧急停止(&S)", null, (_, _) => SafeInvoke(callbacks.EmergencyStop, "emergency stop"));
        menu.Items.Add(_emergencyItem);
        menu.Items.Add(new ToolStripSeparator());
        _copyItem = new ToolStripMenuItem("复制插件配置", null, (_, _) => SafeInvoke(callbacks.CopyPluginConfig, "copy plugin config"))
        {
            Enabled = false,
        };
        menu.Items.Add(_copyItem);
        menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, (_, _) => SafeInvoke(callbacks.OpenLogsDirectory, "open logs")));
        menu.Items.Add(new ToolStripMenuItem("打开审计目录", null, (_, _) => SafeInvoke(callbacks.OpenAuditDirectory, "open audit")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("关于 WhaleGo", null, (_, _) => SafeInvoke(callbacks.ShowAbout, "about")));
        menu.Items.Add(new ToolStripMenuItem("退出(&X)", null, (_, _) => RequestExit(callbacks)));
        return menu;
    }

    private void RequestExit(TrayCallbacks callbacks)
    {
        // Defer to the dispatcher so the context menu closes before the modal confirmation opens.
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (callbacks.ConfirmExit?.Invoke() != true)
                {
                    return;
                }

                _logger.Info("tray exit confirmed");
                SafeInvoke(callbacks.ExitConfirmed, "exit confirmed");
            }
            catch (Exception ex)
            {
                _logger.Error("tray exit failed", ex);
            }
        }));
    }

    private void ApplyIcon(TrayState state)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var variant = TrayIconSelector.Select(TrayIconSelector.ReadAppsUseLightTheme(), WpfSystemParameters.HighContrast);
        var icon = _icons.Get(state, variant);
        if (icon is not null)
        {
            _notifyIcon.Icon = icon;
        }
    }

    private void SafeInvoke(Action? action, string operation)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error("tray action failed: " + operation, ex);
        }
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
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _menu?.Dispose();
            _menu = null;
            _icons.Dispose();
            _callbacks = null;
        }
        catch (Exception ex)
        {
            _logger.Warn("tray dispose failed: " + ex.Message);
        }
    }
}
