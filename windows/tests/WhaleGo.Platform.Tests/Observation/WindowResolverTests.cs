using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Tests.TestInfrastructure;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class WindowResolverTests
{
    [Fact]
    public void ResolvesExactWindowId()
    {
        using var harness = PlatformHarness.Start();
        var result = harness.Resolver.Resolve(harness.WindowId, null);
        Assert.True(result.IsResolved);
        Assert.Equal(WindowResolution.Exact, result.Resolution);
        Assert.False(result.MainFallback);
        Assert.Equal(harness.WindowId, result.Window!.WindowId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void MissingWindowIsWindowNotFound()
    {
        using var harness = PlatformHarness.Start();
        var result = harness.Resolver.Resolve("w-000000000001", null);
        Assert.False(result.IsResolved);
        Assert.Equal(WindowResolution.Unavailable, result.Resolution);
        Assert.Equal(ErrorCodes.WindowNotFound, result.ErrorCode);

        var missingApp = harness.Resolver.Resolve(null, "win-exe:000000000000");
        Assert.False(missingApp.IsResolved);
        Assert.Equal(ErrorCodes.WindowNotFound, missingApp.ErrorCode);
    }

    [Fact]
    public void BothTargetsIsInvalidArgument()
    {
        using var harness = PlatformHarness.Start();
        var result = harness.Resolver.Resolve(harness.WindowId, harness.Window.AppId);
        Assert.False(result.IsResolved);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public void MultipleWindowsForAppIdIsAmbiguousAppWithCandidates()
    {
        using var harness = PlatformHarness.Start();
        var secondSuffix = "two-" + harness.Suffix;
        using var second = StaFormHost.Start(form => TestFixtureForm.Configure(form, secondSuffix));
        var secondTitle = TestFixtureForm.BuildTitle(secondSuffix);
        var secondWindow = PlatformHarness.WaitForWindow(harness.Catalog, secondTitle, TimeSpan.FromSeconds(20));

        var windows = harness.Catalog.ListWindows(harness.Window.AppId);
        Assert.True(windows.Count >= 2, $"expected >=2 windows for {harness.Window.AppId}, got {windows.Count}");
        Assert.Contains(windows, w => w.WindowId == harness.WindowId);
        Assert.Contains(windows, w => w.WindowId == secondWindow.WindowId);

        var result = harness.Resolver.Resolve(null, harness.Window.AppId);
        Assert.False(result.IsResolved);
        Assert.Equal(WindowResolution.Unavailable, result.Resolution);
        Assert.Equal(ErrorCodes.AmbiguousApp, result.ErrorCode);
        Assert.NotNull(result.ErrorDetail);
        Assert.Contains(harness.WindowId, result.ErrorDetail);
        Assert.True(result.Candidates.Count >= 2);
        Assert.Contains(result.Candidates, w => w.WindowId == harness.WindowId);
        Assert.Contains(result.Candidates, w => w.WindowId == secondWindow.WindowId);

        // Explicit main-window fallback is opt-in and marked as fallback (never silent Exact).
        var main = harness.Resolver.ResolveMainWindow(harness.Window.AppId);
        Assert.True(main.IsResolved);
        Assert.True(main.MainFallback);
        Assert.Equal(WindowResolution.Fallback, main.Resolution);
        Assert.True(main.Candidates.Count >= 2);
    }

    [Fact]
    public void ForegroundFallbackIsExplicit()
    {
        using var harness = PlatformHarness.Start();
        var result = harness.Resolver.ResolveForeground();
        Assert.True(result.IsResolved);
        Assert.Equal(WindowResolution.Fallback, result.Resolution);
        Assert.True(result.MainFallback);
        Assert.False(string.IsNullOrEmpty(result.Window!.WindowId));
    }
}
