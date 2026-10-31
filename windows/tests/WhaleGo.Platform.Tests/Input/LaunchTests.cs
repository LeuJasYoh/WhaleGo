using System.Diagnostics;
using System.Text.Json;
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

    [Fact]
    public async Task AllowlistLaunch_StartsFixtureAndFindsMainWindow()
    {
        if (!FixtureReady())
        {
            return;
        }

        var launcher = new AppLauncher(
            new[] { new LaunchAllowlistEntry("t3fixture", TestSupport.FixtureExe) },
            new EnumWindowCatalog(),
            new WindowActivator());

        int? launchedPid = null;
        try
        {
            var result = await launcher.LaunchAsync(new LaunchRequest("t3fixture", AppId: null, WaitMs: 20000), CancellationToken.None);
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
    public async Task AllowlistLaunch_UnknownName_IsFailure()
    {
        var launcher = new AppLauncher(Array.Empty<LaunchAllowlistEntry>(), new EnumWindowCatalog(), new WindowActivator());
        var result = await launcher.LaunchAsync(new LaunchRequest("definitely-not-allowed", AppId: null, WaitMs: 1000), CancellationToken.None);
        _output.WriteLine("launch result: " + JsonSerializer.Serialize(result));
        Assert.True(AppLauncher.IndicatesLaunchFailure(result));
    }

    [Fact]
    public async Task AllowlistLaunch_RelativePath_IsRejected()
    {
        var launcher = new AppLauncher(
            new[] { new LaunchAllowlistEntry("evil", "notepad.exe") },
            new EnumWindowCatalog(),
            new WindowActivator());
        var result = await launcher.LaunchAsync(new LaunchRequest("evil", AppId: null, WaitMs: 1000), CancellationToken.None);
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
        var launcher = new AppLauncher(Array.Empty<LaunchAllowlistEntry>(), new SingleWindowCatalog(window), new WindowActivator());

        var result = await launcher.LaunchAsync(new LaunchRequest(AppName: null, AppId: appId, WaitMs: 2000), CancellationToken.None);
        _output.WriteLine("activate result: " + JsonSerializer.Serialize(result));

        Assert.False(result.Launched);
        Assert.True(result.Activated, "existing window must be activated");
        Assert.NotNull(result.Window);
        Assert.Equal(appId, result.AppId);
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
}