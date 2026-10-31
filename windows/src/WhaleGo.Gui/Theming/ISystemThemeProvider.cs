using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WhaleGo.Gui.Theming;

/// <summary>
/// System light/dark + high contrast facts. Abstracted so the ThemeService decision matrix is unit-testable
/// without touching the registry or a real desktop.
/// </summary>
public interface ISystemThemeProvider
{
    bool IsSystemDarkMode { get; }

    bool IsHighContrast { get; }

    /// <summary>Raised (on the UI thread) when the OS theme or high contrast mode changed.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Live provider: reads HKCU\...\Themes\Personalize\AppsUseLightTheme for light/dark and
/// SystemParameters.HighContrast, re-checking on SystemParameters changes plus a cheap 1s poll so the UI
/// follows Windows within ~1s (docs/GUI.md section 10.7). No extra NuGet dependency (no Microsoft.Win32.SystemEvents).
/// </summary>
public sealed class LiveSystemThemeProvider : ISystemThemeProvider, IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _lastDark;
    private bool _lastHighContrast;
    private bool _disposed;

    public LiveSystemThemeProvider(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _lastDark = ReadSystemDarkMode();
        _lastHighContrast = SystemParameters.HighContrast;
        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimer, dispatcher);
        _timer.Start();
    }

    public bool IsSystemDarkMode => ReadSystemDarkMode();

    public bool IsHighContrast => SystemParameters.HighContrast;

    public event EventHandler? Changed;

    private void OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Recheck();

    private void OnTimer(object? sender, EventArgs e) => Recheck();

    private void Recheck()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Recheck);
            return;
        }

        var dark = ReadSystemDarkMode();
        var highContrast = SystemParameters.HighContrast;
        if (dark == _lastDark && highContrast == _lastHighContrast)
        {
            return;
        }

        _lastDark = dark;
        _lastHighContrast = highContrast;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            // AppsUseLightTheme == 0 means dark; missing value / missing key => Windows default (light).
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;
    }
}
