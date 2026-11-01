using System.Diagnostics;
using System.Text.Json;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Apps;
using WhaleGo.Platform.Windows;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class LaunchTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LaunchTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static LaunchTarget Executable(string path, string name = "app") =>
        new(name, path, LaunchTargetKind.Executable);

    [Fact]
    public async Task Launch_Executable_StartsFixtureAndFindsMainWindow()
    {
        if (!FixtureReady())
        {
            return;
        }

        var launcher = new AppLauncher(new EnumWindowCatalog(), new WindowActivator());

        int? launchedPid = null;
        try
        {
            var result = await launcher.LaunchAsync(
                new LaunchRequest(Executable(TestSupport.FixtureExe, "t3fixture"), AppId: null, WaitMs: 20000),
                CancellationToken.None);
            _output.WriteLine("launch result: " + JsonSerializer.Serialize(result));
            launchedPid = result.Pid;

            Assert.True(result.Launched, "launch must report a main window");
            Assert.NotNull(result.Pid);
            Assert.NotNull(result.Window);
            Assert.True(result.Settled);
            Assert.Equal("WhaleGo Fixture", result.Window!.Title);
        }
        finally
        {
            KillProcess(launchedPid);
        }
    }

    [Fact]
    public void Catalog_EnumeratesSharedFixtureWindow()
    {
        if (!FixtureReady())
        {
            return;
        }

        var catalog = new EnumWindowCatalog();
        var windows = catalog.ListWindows();
        _output.WriteLine($"enumerated windows={windows.Count}");
        foreach (var window in windows.Where(w => w.Pid == _fixture.Pid))
        {
            _output.WriteLine($"fixture window: {window.WindowId} '{window.Title}' class={window.ClassName} onscreen={window.IsOnscreen} enabled={window.IsEnabled} kind={window.Kind}");
        }

        Assert.Contains(windows, window => window.Pid == _fixture.Pid);
    }

    [Fact]
    public async Task Launch_RelativePath_IsRejected()
    {
        var launcher = new AppLauncher(new EnumWindowCatalog(), new WindowActivator());
        var result = await launcher.LaunchAsync(
            new LaunchRequest(Executable("notepad.exe", "evil"), AppId: null, WaitMs: 1000), CancellationToken.None);
        Assert.True(AppLauncher.IndicatesLaunchFailure(result));
    }

    [Fact]
    public async Task AppIdPath_ActivatesExistingFixtureWindow()
    {
        if (!FixtureReady())
        {
            return;
        }

        if (!TestSupport.IsInteractiveDesktop)
        {
            _output.WriteLine("SKIP: no interactive desktop for window activation");
            return;
        }

        var fixtureWindow = _fixture.CreateWindowInfo();
        var appId = AppIdentity.ForExePath(TestSupport.FixtureExe);
        var window = fixtureWindow with { AppId = appId };
        var launcher = new AppLauncher(new SingleWindowCatalog(window), new WindowActivator());

        var result = await launcher.LaunchAsync(new LaunchRequest(Target: null, AppId: appId, WaitMs: 2000), CancellationToken.None);
        _output.WriteLine("activate result: " + JsonSerializer.Serialize(result));

        Assert.False(result.Launched);
        Assert.True(result.Activated, "existing window must be activated");
        Assert.NotNull(result.Window);
        Assert.Equal(appId, result.AppId);
        Assert.True(result.Settled);
    }

    [Fact]
    public void InstalledAppCatalog_Resolve_AbsoluteExecutablePath()
    {
        if (!FixtureReady())
        {
            return;
        }

        var catalog = new InstalledAppCatalog();
        var resolved = catalog.Resolve(TestSupport.FixtureExe);
        Assert.True(resolved.Ok);
        Assert.Equal(LaunchTargetKind.Executable, resolved.App!.Kind);
        Assert.Equal(Path.GetFullPath(TestSupport.FixtureExe), Path.GetFullPath(resolved.App.LaunchPath));
    }

    [Fact]
    public void InstalledAppCatalog_Resolve_SystemAlias()
    {
        var catalog = new InstalledAppCatalog();
        var resolved = catalog.Resolve("notepad");
        Assert.True(resolved.Ok);
        Assert.Equal(LaunchTargetKind.Alias, resolved.App!.Kind);
        Assert.Equal("notepad.exe", resolved.App.LaunchPath);
    }

    [Fact]
    public void InstalledAppCatalog_Resolve_UnknownName_IsMissing()
    {
        var catalog = new InstalledAppCatalog();
        var resolved = catalog.Resolve("whalego-no-such-app-" + Guid.NewGuid().ToString("N"));
        Assert.False(resolved.Ok);
        Assert.Empty(resolved.Candidates);
    }

    [Fact]
    public async Task Launch_SingleInstanceHandoff_ResolvesNewWindowByAppId()
    {
        // Regression for live single-instance apps (Edge/WeChat): the started stub may exit and the visible
        // window belongs to another pid. A NEW window whose app_id matches the executable is a success.
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        if (!File.Exists(exe))
        {
            _output.WriteLine("SKIP: whoami.exe unavailable");
            return;
        }

        var expectedAppId = AppIdentity.ForExePath(exe);
        var window = new WindowInfo(
            "w-0000000000FAKE",
            expectedAppId,
            "fake handoff window",
            "Fake",
            424_242,
            IsOnscreen: true,
            IsMinimized: false,
            IsForeground: false,
            IsEnabled: true,
            new RectI(0, 0, 100, 100),
            "dwm",
            96,
            0,
            "top-level");

        var launcher = new AppLauncher(new NewWindowCatalog(window), new WindowActivator());
        var result = await launcher.LaunchAsync(
            new LaunchRequest(Executable(exe, "whoami"), AppId: null, WaitMs: 3000), CancellationToken.None);
        _output.WriteLine("handoff result: " + JsonSerializer.Serialize(result));

        Assert.True(result.Launched, "a new window of the executable identity must count as launched");
        Assert.NotNull(result.Window);
        Assert.Equal(window.WindowId, result.Window!.WindowId);
        Assert.Equal(424_242, result.Pid);
        Assert.True(result.Settled);
    }

    private bool FixtureReady()
    {
        if (_fixture.Available)
        {
            return true;
        }

        _output.WriteLine("SKIP (cross-process fixture unavailable): " + (_fixture.SkipReason ?? "unknown reason"));
        return false;
    }

    private void KillProcess(int? pid)
    {
        if (pid is null or <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // already gone
        }
    }

    /// <summary>First ListWindows call models the pre-start snapshot (empty); afterwards a new window appears.</summary>
    private sealed class NewWindowCatalog : IWindowCatalog
    {
        private readonly WindowInfo _window;
        private int _calls;

        public NewWindowCatalog(WindowInfo window) => _window = window;

        public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options) => Array.Empty<AppInfo>();

        public IReadOnlyList<WindowInfo> ListWindows(string? appId = null)
        {
            _calls++;
            if (_calls <= 1)
            {
                return Array.Empty<WindowInfo>();
            }

            return string.IsNullOrEmpty(appId) || string.Equals(appId, _window.AppId, StringComparison.Ordinal)
                ? new[] { _window }
                : Array.Empty<WindowInfo>();
        }

        public WindowInfo? TryGetWindow(string windowId) =>
            string.Equals(windowId, _window.WindowId, StringComparison.Ordinal) ? _window : null;

        public bool IsForeground(string windowId) => false;
    }
}
