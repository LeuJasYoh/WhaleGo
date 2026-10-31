using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WhaleGo.Gui.Theming;
using WhaleGo.Gui.ViewModels;
using WhaleGo.Gui.Views.Dialogs;

namespace WhaleGo.Gui.Views;

/// <summary>
/// The "WhaleGo 设置" window: left nav + content host + status bar + theme segment. Closing the window hides it to
/// the tray (the only real exit path is the tray menu); the shell owns the application lifetime.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _themeService;
    private readonly DispatcherTimer _statusTimer;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, ThemeService themeService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(themeService);
        InitializeComponent();
        _viewModel = viewModel;
        _themeService = themeService;
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnStatusTick, Dispatcher);
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _statusTimer.Stop();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.WizardRequested += (_, _) => ShowPairingWizard();
        _themeService.ThemeChanged += OnThemeChanged;
    }

    public MainViewModel ViewModel => _viewModel;

    /// <summary>True once the shell decided to really exit (tray exit path).</summary>
    public bool IsClosingForShutdown => _allowClose;

    /// <summary>Show / restore / bring to front (tray double-click and single-instance activation).</summary>
    public void ShowSettings() => ShowSettingsCore();

    /// <summary>First run: show the window and (optionally) the pairing wizard modal.</summary>
    public void ShowStartup(bool showPairingWizard)
    {
        ShowSettingsCore();
        if (showPairingWizard)
        {
            ShowPairingWizard();
        }
    }

    public void HideToTray() => Hide();

    public void ShowPairingWizard()
    {
        ShowSettingsCore();
        var wizardViewModel = new PairingWizardViewModel(_viewModel.Context);
        var wizard = new PairingWizardWindow(wizardViewModel) { Owner = this };
        wizard.ShowDialog();
    }

    /// <summary>Closes for real (tray exit). Any other close request hides to tray instead.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        _statusTimer.Stop();
        Close();
    }

    private void ShowSettingsCore()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _themeService.RegisterTitleBarHandle(hwnd);
        }
        catch (Exception ex)
        {
            _viewModel.Context.Logger.Error("failed to register title bar handle", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Start();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.Context.Logger.Error("settings window initialization failed", ex);
        }
    }

    private async void OnStatusTick(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.Context.Logger.Error("status poll failed", ex);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            AnimatePageTransition();
        }
    }

    private void AnimatePageTransition()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            PageHost.Opacity = 1;
            return;
        }

        PageHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ThemeVeil.Opacity = 0;
            return;
        }

        // Soft 150ms wash: the new dictionary is already applied, the veil fades out to smooth the swap.
        ThemeVeil.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
