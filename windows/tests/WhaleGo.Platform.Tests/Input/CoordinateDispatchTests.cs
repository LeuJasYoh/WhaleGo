using System.Text.Json;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Input;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class CoordinateDispatchTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly UiaEngine _engine;
    private readonly UiaHitTester _hitTester;
    private readonly PostMessageDispatcher _postMessage;
    private readonly CoordinateDispatcher _dispatcher;

    public CoordinateDispatchTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _engine = new UiaEngine();
        _hitTester = new UiaHitTester(_engine);
        _postMessage = new PostMessageDispatcher();
        _dispatcher = new CoordinateDispatcher(
            _hitTester,
            _hitTester,
            new UipiGuard(),
            new ForegroundGuard(new WindowActivator()),
            new SendInputDispatcher { UnicodePacingMs = 5 },
            _postMessage,
            new InputLane());
    }

    [Fact]
    public async Task FullChain_ImageToClientToScreen_PostMessageClick_IncrementsPanelClicks()
    {
        if (!Ready())
        {
            return;
        }

        if (!EnsureFixtureForeground())
        {
            return;
        }

        var before = _fixture.ReadClickCounts().Panel;
        var target = BuildFrameFor("pnlClick");
        var outcome = await _dispatcher.ExecuteAsync(
            target.Frame,
            target.ImageX,
            target.ImageY,
            new ActionPlan("click", TargetKind.Coordinate, X: target.ImageX, Y: target.ImageY),
            CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.True(outcome.Sent, "coordinate click must be sent");
        Assert.Equal("post_message", outcome.Method);
        Assert.False(outcome.Escalated);
        Assert.True(
            TestSupport.WaitUntil(() => _fixture.ReadClickCounts().Panel == before + 1, 4000),
            $"panel click count did not increment (before={before})");
    }

    [Fact]
    public void PostMessage_DirectClickAtScreenPoint_IncrementsPanelClicks()
    {
        if (!Ready())
        {
            return;
        }

        if (!EnsureFixtureForeground())
        {
            return;
        }

        var before = _fixture.ReadClickCounts().Panel;
        var center = ElementCenterScreen("pnlClick");
        _output.WriteLine($"panel center screen=({center.X},{center.Y})");
        var window = _fixture.CreateWindowInfo();
        Assert.True(
            _hitTester.TryResolveChildHwnd(window, center.X, center.Y, out var child, out var clientX, out var clientY),
            "child hwnd must be resolvable from the window domain");
        var panel = _fixture.Find("pnlClick");
        var panelHwnd = panel.Properties.NativeWindowHandle.ValueOrDefault;
        _output.WriteLine($"child=0x{child:X} client=({clientX},{clientY}) panelHwnd=0x{panelHwnd:X}");
        var result = _postMessage.ClickClient(panelHwnd != 0 ? panelHwnd : child, clientX, clientY);
        _output.WriteLine($"post result: delivered={result.Delivered} anyQueued={result.AnyQueued} error={result.Error}");

        Assert.True(result.Delivered);
        Assert.True(
            TestSupport.WaitUntil(() => _fixture.ReadClickCounts().Panel == before + 1, 4000),
            "panel click count did not increment");
    }

    [Fact]
    public async Task OutOfImageBounds_IsNotSentWithInvalidArgument()
    {
        if (!Ready())
        {
            return;
        }

        var target = BuildFrameFor("pnlClick");
        var outcome = await _dispatcher.ExecuteAsync(
            target.Frame,
            target.Frame.ImageWidth + 10,
            target.ImageY,
            new ActionPlan("click", TargetKind.Coordinate),
            CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Equal(ErrorCodes.InvalidArgument, outcome.FailureCode);
    }

    [Fact]
    public async Task NonActionableFrame_IsNotSentWithFrameNotActionable()
    {
        if (!Ready())
        {
            return;
        }

        var target = BuildFrameFor("pnlClick");
        var frame = target.Frame with { Actionable = false };
        var outcome = await _dispatcher.ExecuteAsync(
            frame,
            target.ImageX,
            target.ImageY,
            new ActionPlan("click", TargetKind.Coordinate),
            CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Equal(ErrorCodes.FrameNotActionable, outcome.FailureCode);
    }

    private bool EnsureFixtureForeground()
    {
        if (!TestSupport.IsInteractiveDesktop)
        {
            _output.WriteLine("SKIP (no interactive desktop for posted-mouse test precondition)");
            return false;
        }

        if (!_fixture.TryActivateFixture())
        {
            _output.WriteLine("SKIP (fixture window could not be activated)");
            return false;
        }

        return true;
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

    private (FrameBinding Frame, int ImageX, int ImageY) BuildFrameFor(string automationId)
    {
        var geometry = _fixture.RefreshGeometry();
        var center = ElementCenterScreen(automationId);
        var clientX = center.X - geometry.ClientRect.Left;
        var clientY = center.Y - geometry.ClientRect.Top;
        Assert.True(clientX >= 0 && clientY >= 0, $"element center {center} is outside client rect {geometry.ClientRect}");
        Assert.True(clientX < geometry.ClientRect.Width && clientY < geometry.ClientRect.Height, "element center outside client");

        var frame = new FrameBinding(
            FrameId: "fr-00000001",
            WindowId: _fixture.WindowId,
            OwnerPid: _fixture.Pid,
            ClassName: TestSupport.GetClassNameText(_fixture.Hwnd),
            WindowBounds: geometry.WindowRect,
            ClientOrigin: RectI.FromSize(geometry.ClientRect.Left, geometry.ClientRect.Top, geometry.ClientRect.Width, geometry.ClientRect.Height),
            ClientWidth: geometry.ClientRect.Width,
            ClientHeight: geometry.ClientRect.Height,
            ImageWidth: geometry.ClientRect.Width,
            ImageHeight: geometry.ClientRect.Height,
            Dpi: geometry.Dpi,
            CapturedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Actionable: true,
            CaptureMethod: "test",
            Signature: "nonblank_ok");
        return (frame, clientX, clientY);
    }

    private (int X, int Y) ElementCenterScreen(string automationId)
    {
        var element = _fixture.Find(automationId);
        var rect = element.BoundingRectangle;
        return (rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
    }
}