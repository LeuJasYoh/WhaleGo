using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WhaleGo.Gui.Theming;
using WhaleGo.Gui.Tray;
using WhaleGo.Gui.ViewModels;
using WhaleGo.Gui.Views;

namespace WhaleGo.Gui.Tests;

/// <summary>
/// STA construction smoke (docs/GUI.md 12.2): create the WPF Application from App.xaml, verify theme dictionaries swap
/// through real ResourceDictionaries, construct MainWindow + every page view, and require AutomationProperties.Name on
/// interactive controls. No window is ever shown.
/// </summary>
public sealed class StaSmokeTests
{
    [Fact]
    public void WpfSurfacesConstructOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnSta();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "STA smoke test timed out");

        Assert.Null(failure);
    }

    private static void RunOnSta()
    {
        var app = new App();
        app.InitializeComponent();
        Assert.NotNull(app.TryFindResource("BoolToVisibility"));

        VerifyThemeDictionariesLoad(app);
        VerifyWindowAndPages(app);
        VerifyTrayConstructsWithoutShowing();
        VerifyVisualTreeAutomationNames(app);
        app.Shutdown();
    }

    private static void VerifyThemeDictionariesLoad(Application app)
    {
        var host = new WpfThemeResourceHost(app);
        host.Apply(AppTheme.Dark);
        var dark = ((SolidColorBrush)app.FindResource("Brush.Canvas")).Color;
        host.Apply(AppTheme.Light);
        var light = ((SolidColorBrush)app.FindResource("Brush.Canvas")).Color;
        host.Apply(AppTheme.HighContrast);
        Assert.NotNull(app.FindResource("Brush.Canvas"));
        Assert.NotNull(app.FindResource("Brush.Text.Primary"));
        host.Apply(AppTheme.Light);

        // Brushes are replaced by the theme dictionary, so a theme swap must change the resolved colours.
        Assert.NotEqual(dark, light);
        Assert.NotNull(app.FindResource("Effect.Card"));
    }

    private static void VerifyWindowAndPages(Application app)
    {
        var themeService = new ThemeService(
            new FakeThemeResources(),
            new FakeSystemThemeProvider(),
            new FakeWindowTheme(),
            WhaleGo.Gui.Infrastructure.NullAppLogger.Instance);
        Assert.True(themeService.Initialize(ThemePreference.System));

        var viewModel = new MainViewModel(TestContexts.Create(), new InMemoryUiStateStore());
        var window = new MainWindow(viewModel, themeService);
        window.ApplyTemplate();
        Assert.Equal("WhaleGo 设置", window.Title);
        Assert.Equal(6, window.ViewModel.Pages.Count);

        var pages = new FrameworkElement[]
        {
            new OverviewPage { DataContext = window.ViewModel.Overview },
            new ConnectionPage { DataContext = window.ViewModel.Connection },
            new PolicyPage { DataContext = window.ViewModel.Policy },
            new PrivacyPage { DataContext = window.ViewModel.Privacy },
            new DiagnosticsPage { DataContext = window.ViewModel.Diagnostics },
            new AboutPage { DataContext = window.ViewModel.About },
        };

        foreach (var page in pages)
        {
            var host = new Grid { Width = 640, Height = 900 };
            host.Children.Add(page);
            host.Measure(new Size(640, 900));
            host.Arrange(new Rect(0, 0, 640, 900));
            host.UpdateLayout();
            Assert.True(page.ActualWidth > 0 || page.DesiredSize.Width > 0, page.GetType().Name + " did not measure");
        }
    }

    private static void VerifyTrayConstructsWithoutShowing()
    {
        using var tray = new TrayShell(WhaleGo.Gui.Infrastructure.NullAppLogger.Instance);
        Assert.False(tray.IsInitialized);
        tray.Update(TrayStateMapper.Map(TestData.Status(), null, 0, TrayAuxInfo.Default));
        Assert.False(tray.ShowBalloon("t", "m", BalloonKind.Info));
    }

    private static void VerifyVisualTreeAutomationNames(Application app)
    {
        var window = new MainWindow(
            new MainViewModel(TestContexts.Create(), new InMemoryUiStateStore()),
            new ThemeService(new FakeThemeResources(), new FakeSystemThemeProvider(), new FakeWindowTheme(),
                WhaleGo.Gui.Infrastructure.NullAppLogger.Instance));
        window.ApplyTemplate();

        var missing = new List<string>();
        var root = (DependencyObject)window.Content;
        Walk(root, element =>
        {
            if (element is not Control control || !IsEffectivelyVisible(control))
            {
                return;
            }

            var needsName = control is Button or TextBox or ComboBox or CheckBox or RadioButton;
            if (!needsName)
            {
                return;
            }

            var name = AutomationProperties.GetName(control);
            if (string.IsNullOrWhiteSpace(name))
            {
                missing.Add(control.GetType().Name + ": " + (control as ContentControl)?.Content);
            }
        });

        Assert.Empty(missing);
    }

    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return true;
    }

    private static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        visit(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(root, i), visit);
        }
    }
}
