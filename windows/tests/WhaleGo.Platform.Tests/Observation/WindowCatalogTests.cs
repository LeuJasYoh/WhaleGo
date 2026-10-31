using System.Diagnostics;
using WhaleGo.Platform.Tests.TestInfrastructure;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class WindowCatalogTests
{
    [Fact]
    public void EnumeratesOwnStaFormWindowWithIdentityFacts()
    {
        using var harness = PlatformHarness.Start();

        var window = harness.Catalog.TryGetWindow(harness.WindowId);
        Assert.NotNull(window);
        Assert.Matches("^w-[0-9A-F]{12}$", window.WindowId);
        Assert.Equal(harness.WindowId, window.WindowId);
        Assert.Equal(harness.Title, window.Title);
        Assert.Matches("^win-exe:[0-9a-f]{12}$", window.AppId);
        Assert.Contains(window.GeometrySource, new[] { "dwm", "win32" });
        Assert.True(window.Bounds.Width > 0, $"bounds={window.Bounds}");
        Assert.True(window.Bounds.Height > 0, $"bounds={window.Bounds}");
        Assert.True(window.Dpi >= 96, $"dpi={window.Dpi}");
        Assert.True(window.IsOnscreen);
        Assert.False(window.IsMinimized);
        Assert.True(window.CapturedAtMs > 0);
        Assert.Equal("top-level", window.Kind);
        Assert.Null(window.OwnerWindowId);
        Assert.True(harness.Catalog.TryGetHwnd(harness.WindowId, out var hwnd));
        Assert.Equal(harness.Host.Handle, hwnd);
        Assert.Equal(WhaleGo.Platform.Tests.TestInfrastructure.TestWin32.GetClassName(hwnd), window.ClassName);

        // app_id must be the sha1 of the normalized absolute exe path (testhost.exe for the in-process form).
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        Assert.False(string.IsNullOrEmpty(exePath));
        Assert.Equal(AppIdentity.ForExePath(exePath!), window.AppId);

        // Identity is stable across enumerations (title is not part of it).
        Assert.Equal(window.AppId, harness.Catalog.TryGetWindow(harness.WindowId)!.AppId);
    }

    [Fact]
    public void ListsAppsWithWindowsAndSortsNothingAway()
    {
        using var harness = PlatformHarness.Start();

        var windows = harness.Catalog.ListWindows();
        Assert.Contains(windows, w => w.WindowId == harness.WindowId);
        Assert.All(windows, w => Assert.Contains(w.GeometrySource, new[] { "dwm", "win32" }));

        var apps = harness.Catalog.ListApps(new WhaleGo.Contracts.Model.ListAppsOptions(IncludeWindows: true));
        var app = Assert.Single(apps, a => a.AppId == harness.Window.AppId);
        Assert.False(string.IsNullOrWhiteSpace(app.Name));
        Assert.NotNull(app.ExecutablePath);
        Assert.Contains(app.Windows, w => w.WindowId == harness.WindowId);

        var appWithoutWindows = harness.Catalog.TryGetApp(harness.Window.AppId);
        Assert.NotNull(appWithoutWindows);
        Assert.Empty(appWithoutWindows.Windows);

        Assert.Null(harness.Catalog.TryGetApp("win-exe:000000000000"));
        Assert.Empty(harness.Catalog.ListWindows("win-exe:000000000000"));
    }

    [Fact]
    public void SelfProcessNamesAreExcludedByPrivacyRules()
    {
        Assert.True(WindowCatalog.IsSelfProcessName("WhaleGo"));
        Assert.True(WindowCatalog.IsSelfProcessName("WhaleGo.Executor"));
        Assert.True(WindowCatalog.IsSelfProcessName("WhaleGo.Executor.Headless"));
        Assert.True(WindowCatalog.IsSelfProcessName("WhaleGo.Gui"));
        Assert.False(WindowCatalog.IsSelfProcessName("notepad"));
        Assert.False(WindowCatalog.IsSelfProcessName("WhaleGo.Fixtures.WinForms"));
        Assert.False(WindowCatalog.IsSelfProcessName(null));

        using var harness = PlatformHarness.Start();
        // Our own test window must still be enumerated (testhost is not a WhaleGo.* self process).
        Assert.Contains(harness.Catalog.ListWindows(), w => w.WindowId == harness.WindowId);
    }

    [Fact]
    public void TryGetWindowRejectsUnknownOrMalformedIds()
    {
        using var harness = PlatformHarness.Start();
        Assert.Null(harness.Catalog.TryGetWindow("bogus"));
        Assert.Null(harness.Catalog.TryGetWindow("w-000000000001")); // well-formed, not live
        Assert.False(harness.Catalog.TryGetHwnd("bogus", out _));
        Assert.Equal("w-000000000001", WindowIdCodec.Format(new IntPtr(1)));
        Assert.True(WindowIdCodec.TryParse("w-000000000001", out var parsed));
        Assert.Equal(new IntPtr(1), parsed);
        Assert.False(WindowIdCodec.TryParse("w-00000000001", out _));
        Assert.False(WindowIdCodec.TryParse(null, out _));
    }

    [Fact]
    public void AppIdentityHashMatchesProtocolRules()
    {
        var id1 = AppIdentity.ForExePath(@"C:\Windows\System32\notepad.exe");
        var id2 = AppIdentity.ForExePath(@"c:/windows/system32/NOTEPAD.EXE");
        Assert.Equal(id1, id2);
        Assert.Matches("^win-exe:[0-9a-f]{12}$", id1);
        Assert.Equal("win-aumid:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", AppIdentity.ForAumid("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Assert.True(AppIdentity.IsAumidId("win-aumid:x"));
        Assert.True(AppIdentity.IsExeId(id1));
    }
}
