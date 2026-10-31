using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Theming;
using WhaleGo.Gui.Tray;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// Everything the settings presenters need. Deliberately small and port-based so every page view model can be
/// unit tested with fakes (no window, no clipboard, no kernel).
/// </summary>
public sealed class GuiContext
{
    public GuiContext(
        SafeControl control,
        IUserDialogs dialogs,
        IClipboardService clipboard,
        IThemeController theme,
        IAppLogger logger,
        GuiOptions options,
        Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        Control = control;
        Dialogs = dialogs;
        Clipboard = clipboard;
        Theme = theme;
        Logger = logger;
        Options = options;
        NowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public SafeControl Control { get; }

    public IUserDialogs Dialogs { get; }

    public IClipboardService Clipboard { get; }

    public IThemeController Theme { get; }

    public IAppLogger Logger { get; }

    public GuiOptions Options { get; }

    public Func<long> NowMs { get; }

    /// <summary>Where the GUI log file lives (from GuiOptions.LogsDirectory).</summary>
    public string? LogsDirectory => string.IsNullOrWhiteSpace(Options.LogsDirectory) ? null : Options.LogsDirectory;

    /// <summary>Set after the latest config load; used by diagnostics "打开审计目录".</summary>
    public string? AuditDirectory { get; set; }

    /// <summary>Optional extra self-check item contributed by the GUI itself (theme resources, docs/GUI.md 8.4).</summary>
    public Func<SelfCheckItem?>? ThemeSelfCheck { get; set; }

    /// <summary>Balloon sink wired by the shell (kind, title, message); null in tests.</summary>
    public Action<BalloonKind, string, string>? Balloon { get; set; }
}
