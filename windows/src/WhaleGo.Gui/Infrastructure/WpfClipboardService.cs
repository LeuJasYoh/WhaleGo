using System.Windows;
using System.Windows.Threading;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>Real clipboard implementation; marshals to the UI dispatcher, never throws into callers.</summary>
public sealed class WpfClipboardService : IClipboardService, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAppLogger _logger;
    private DispatcherTimer? _clearTimer;
    private string? _copiedText;

    public WpfClipboardService(Dispatcher dispatcher, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public void SetText(string text) => SetText(text, TimeSpan.Zero);

    public void SetText(string text, TimeSpan autoClearAfter)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetText(text, autoClearAfter));
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.Warn("clipboard write failed: " + ex.Message);
            return;
        }

        if (autoClearAfter <= TimeSpan.Zero)
        {
            return;
        }

        _copiedText = text;
        _clearTimer?.Stop();
        _clearTimer = new DispatcherTimer(autoClearAfter, DispatcherPriority.Background, OnClearTimer, _dispatcher);
        _clearTimer.Start();
    }

    private void OnClearTimer(object? sender, EventArgs e)
    {
        _clearTimer?.Stop();
        try
        {
            var current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (ClipboardClearPolicy.ShouldClear(current, _copiedText))
            {
                Clipboard.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("clipboard clear failed: " + ex.Message);
        }
        finally
        {
            _copiedText = null;
        }
    }

    public void Dispose()
    {
        _clearTimer?.Stop();
    }
}
