using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Uia;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class UiHitTestTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly UiaEngine _engine;
    private readonly UiaHitTester _hitTester;

    public UiHitTestTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _engine = new UiaEngine();
        _hitTester = new UiaHitTester(_engine);
    }

    [Fact]
    public void HitTest_ClientInterior_ReportsWindowClientAndDeliverable()
    {
        if (!Ready())
        {
            return;
        }

        var center = CenterScreen("pnlClick");
        var result = _hitTester.HitTest(_fixture.CreateWindowInfo(), center.X, center.Y);
        _output.WriteLine($"hit: withinWindow={result.WithinWindow} withinClient={result.WithinClient} host={result.HostClass} control={result.ControlType} id={result.AutomationId} class={result.ClassName}");

        Assert.True(result.WithinWindow);
        Assert.True(result.WithinClient);
        Assert.Equal(HostClass.Deliverable, result.HostClass);
        Assert.Equal("pnlClick", result.AutomationId);
        Assert.False(string.IsNullOrEmpty(result.ControlType));
    }

    [Fact]
    public void HitTest_TitleBar_IsWithinWindowButNotClient()
    {
        if (!Ready())
        {
            return;
        }

        var geometry = _fixture.RefreshGeometry();
        var titlePointX = geometry.WindowRect.Left + (geometry.WindowRect.Width / 2);
        var titlePointY = geometry.WindowRect.Top + 3;
        var result = _hitTester.HitTest(_fixture.CreateWindowInfo(), titlePointX, titlePointY);
        _output.WriteLine($"hit: withinWindow={result.WithinWindow} withinClient={result.WithinClient} host={result.HostClass}");

        Assert.True(result.WithinWindow);
        Assert.False(result.WithinClient);
    }

    [Fact]
    public void HitTest_OutsideWindow_ReportsNotWithin()
    {
        if (!Ready())
        {
            return;
        }

        var geometry = _fixture.RefreshGeometry();
        var x = geometry.WindowRect.Right + 40;
        var y = geometry.WindowRect.Bottom + 40;
        var result = _hitTester.HitTest(_fixture.CreateWindowInfo(), x, y);
        Assert.False(result.WithinWindow);
        Assert.False(result.WithinClient);
        Assert.Equal(HostClass.Unknown, result.HostClass);
    }

    [Fact]
    public void HitTest_DoesNotThrowForInvalidWindowId()
    {
        var window = new WindowInfo("bogus-id", "app", "t", "cls", 0, false, false, false, false, default, "win32", 96, 0);
        var result = _hitTester.HitTest(window, 0, 0);
        Assert.False(result.WithinWindow);
    }

    private bool Ready()
    {
        if (_fixture.Available)
        {
            return true;
        }

        _output.WriteLine("SKIP (cross-process fixture unavailable): " + (_fixture.SkipReason ?? "unknown reason"));
        return false;
    }

    private (int X, int Y) CenterScreen(string automationId)
    {
        var element = _fixture.Find(automationId);
        var rect = element.BoundingRectangle;
        return (rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
    }
}