using WhaleGo.Contracts.Abstractions;
using WhaleGo.Gui.Internal;

namespace WhaleGo.Gui;

/// <summary>Options passed by the Executor composition root into the GUI host.</summary>
public sealed record GuiOptions(
    bool Headless,
    bool NoHotkey,
    string Version,
    string ProtocolVersion,
    string ExecutablePath,
    string ConfigPath,
    string LogsDirectory);

/// <summary>
/// Public API between the Executor composition root (T4) and the GUI (T7).
/// Creates the WPF settings window + WinForms tray on a dedicated STA thread, exposes only thread-safe entry points
/// and raises <see cref="ExitRequested"/> when the user confirmed exit from the tray menu. All kernel access goes
/// through the injected <see cref="IExecutorControl"/> port (GUI references Contracts only).
/// </summary>
public sealed class GuiShell : IDisposable
{
    private readonly GuiHost? _host;
    private bool _disposed;

    public GuiShell(IExecutorControl control, IWindowTheme windowTheme, IHotkeyRegistrar hotkeys, GuiOptions options)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(windowTheme);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(options);
        Control = control;
        WindowTheme = windowTheme;
        Hotkeys = hotkeys;
        Options = options;
        if (!options.Headless)
        {
            _host = new GuiHost(this);
            _host.Start();
        }
    }

    public IWindowTheme WindowTheme { get; }

    public IHotkeyRegistrar Hotkeys { get; }

    public GuiOptions Options { get; }

    public IExecutorControl Control { get; }

    /// <summary>Raised (on a background thread) when the tray exit was confirmed; the host performs graceful shutdown.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Show / restore / activate the settings window (also used for single-instance activation).</summary>
    public void ShowSettings() => _host?.PostShowSettings();

    /// <summary>Hide the settings window to the tray (never terminates the process).</summary>
    public void HideToTray() => _host?.PostHideToTray();

    /// <summary>System balloon notification (cooldown/level policy applied inside the tray).</summary>
    public void ShowBalloon(string title, string message, bool important) => _host?.PostBalloon(title, message, important);

    /// <summary>Forces an immediate status refresh (tray + current page).</summary>
    public void RefreshStatus() => _host?.PostRefreshStatus();

    /// <summary>Local-only exit request; the host should stop the kernel and dispose this shell.</summary>
    public void RequestExit() => RaiseExitRequested();

    internal void RaiseExitRequested()
    {
        if (_disposed)
        {
            return;
        }

        // Marshal off the UI thread so a slow host shutdown handler can never freeze the dispatcher.
        ThreadPool.QueueUserWorkItem(_ => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host?.Dispose();
    }
}
