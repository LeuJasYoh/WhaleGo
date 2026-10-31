using System.Windows;

namespace WhaleGo.Gui;

/// <summary>
/// WPF application object for the WhaleGo settings window. Created and driven by <see cref="GuiShell"/>
/// on a dedicated STA thread; no StartupUri and <see cref="ApplicationShutdownMode.OnExplicitShutdown"/> so that
/// closing the settings window never terminates the executor process (it hides to tray instead).
/// </summary>
public partial class App : Application
{
    public App()
    {
        // ShutdownMode is also set in App.xaml; kept here so a programmatic new App() in tests behaves the same.
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
    }
}
