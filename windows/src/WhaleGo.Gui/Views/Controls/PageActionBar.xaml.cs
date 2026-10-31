using System.Windows;
using System.Windows.Controls;
using WhaleGo.Gui.ViewModels;

namespace WhaleGo.Gui.Views.Controls;

/// <summary>Bottom action bar shared by every configuration page (docs/GUI.md 10.4).</summary>
public partial class PageActionBar : UserControl
{
    public static readonly DependencyProperty ShowRestartActionProperty = DependencyProperty.Register(
        nameof(ShowRestartAction), typeof(bool), typeof(PageActionBar), new PropertyMetadata(true));

    public PageActionBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public bool ShowRestartAction
    {
        get => (bool)GetValue(ShowRestartActionProperty);
        set => SetValue(ShowRestartActionProperty, value);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ConfigPageViewModel page)
        {
            BindingOperationsShim.BindBanner(SuccessBanner, page.PageSuccess, UiSeverity.Info);
            BindingOperationsShim.BindBanner(WarningBanner, page.PageWarning, UiSeverity.Warning);
            BindingOperationsShim.BindBanner(ErrorBanner, page.PageError, UiSeverity.Danger);
            BindingOperationsShim.BindRestart(RestartBanner, page.RestartHint, page.HasRestartPending, ShowRestartAction, page.RequestRestartCommand);
            page.PropertyChanged += (_, _) =>
            {
                BindingOperationsShim.BindBanner(SuccessBanner, page.PageSuccess, UiSeverity.Info);
                BindingOperationsShim.BindBanner(WarningBanner, page.PageWarning, UiSeverity.Warning);
                BindingOperationsShim.BindBanner(ErrorBanner, page.PageError, UiSeverity.Danger);
                BindingOperationsShim.BindRestart(RestartBanner, page.RestartHint, page.HasRestartPending, ShowRestartAction, page.RequestRestartCommand);
            };
        }
    }

    /// <summary>Tiny helper that mirrors VM strings into the banner control (banners are not bindable to two props at once).</summary>
    private static class BindingOperationsShim
    {
        public static void BindBanner(InlineBanner banner, string? message, UiSeverity severity)
        {
            banner.Severity = severity;
            banner.Message = message ?? string.Empty;
        }

        public static void BindRestart(InlineBanner banner, string hint, bool visible, bool showAction, System.Windows.Input.ICommand? command)
        {
            banner.Severity = UiSeverity.Warning;
            banner.Message = visible ? hint : string.Empty;
            banner.ActionText = showAction ? "重启执行器" : string.Empty;
            banner.ActionCommand = showAction ? command : null;
        }
    }
}
