using System;
using System.Windows;
using System.Windows.Threading;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Views.Dialogs;

/// <summary>Modal five-step pairing wizard window; owns the 2s "wait for container" poll while step 4 is open.</summary>
public partial class PairingWizardWindow : Window
{
    private readonly PairingWizardViewModel _viewModel;
    private readonly DispatcherTimer _pollTimer;

    public PairingWizardWindow(PairingWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnPollTick, Dispatcher);
        Loaded += OnLoaded;
        Closed += (_, _) => _pollTimer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Start();
        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.Context.Logger.Error("pairing wizard initialization failed", ex);
        }
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_viewModel.Step != 4)
        {
            return;
        }

        try
        {
            await _viewModel.PollConnectionAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.Context.Logger.Error("pairing wizard poll failed", ex);
        }
    }
}
