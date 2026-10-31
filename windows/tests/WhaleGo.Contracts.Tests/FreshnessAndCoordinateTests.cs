using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Tests;

public sealed class FreshnessAndCoordinateTests
{
    private static readonly WindowInfo Window = new(
        "w-1", "win-exe:ab12cd34ef56", "title", "Notepad", 1234, true, false, true, true,
        new RectI(100, 100, 900, 700), "dwm", 120, 1_000_000);

    private static ObservationSnapshot Snapshot(long capturedAtMs = 1_000_000) => new(
        ProtocolInfo.ObserveSchemaVersion, "wg-1", "st-8f21cd04",
        new AppInfo("win-exe:ab12cd34ef56", "Notepad", 1234, null, null, false, true, []),
        Window, WindowResolution.Exact, [], OmissionInfo.None, null, [], capturedAtMs);

    private static WindowIdentity Identity(string windowId = "w-1", int pid = 1234, string className = "Notepad",
        RectI? bounds = null, uint dpi = 120) =>
        new(windowId, pid, className, bounds ?? new RectI(100, 100, 900, 700), dpi);

    private static FrameBinding Frame(bool actionable = true, long capturedAt = 1_000_000,
        RectI? bounds = null, uint dpi = 120) => new(
        "fr-1", "w-1", 1234, "Notepad", bounds ?? new RectI(100, 100, 900, 700), new RectI(110, 130, 890, 690),
        780, 560, 780, 560, dpi, capturedAt, actionable, "printwindow", "nonblank_ok");

    [Fact]
    public void FreshStatePasses()
    {
        var result = FreshnessRules.EvaluateState(Snapshot(), "st-8f21cd04", Identity(), 1_010_000, 120);
        Assert.True(result.IsFresh);
    }

    [Fact]
    public void MissingSnapshotOrMismatchedStateIsStale()
    {
        Assert.Equal(ErrorCodes.StaleState, FreshnessRules.EvaluateState(null, "st-8f21cd04", Identity(), 1_010_000, 120).ErrorCode);
        Assert.Equal(ErrorCodes.StaleState, FreshnessRules.EvaluateState(Snapshot(), "st-deadbeef", Identity(), 1_010_000, 120).ErrorCode);
    }

    [Fact]
    public void ExpiredStateIsStale()
    {
        var result = FreshnessRules.EvaluateState(Snapshot(), "st-8f21cd04", Identity(), 1_000_000 + 121_000, 120);
        Assert.Equal(FreshnessVerdict.Expired, result.Verdict);
        Assert.Equal(ErrorCodes.StaleState, result.ErrorCode);
    }

    [Fact]
    public void WindowIdentityChangeFailsClosed()
    {
        Assert.Equal(ErrorCodes.WindowChanged,
            FreshnessRules.EvaluateState(Snapshot(), "st-8f21cd04", Identity(pid: 5678), 1_010_000, 120).ErrorCode);
        Assert.Equal(ErrorCodes.WindowChanged,
            FreshnessRules.EvaluateState(Snapshot(), "st-8f21cd04", Identity(className: "Other"), 1_010_000, 120).ErrorCode);
        Assert.Equal(ErrorCodes.WindowNotFound,
            FreshnessRules.EvaluateState(Snapshot(), "st-8f21cd04", null, 1_010_000, 120).ErrorCode);
    }

    [Fact]
    public void TitleChangesDoNotInvalidate()
    {
        var renamed = Window with { Title = "renamed" };
        var snapshot = Snapshot() with { Window = renamed };
        Assert.True(FreshnessRules.EvaluateState(snapshot, "st-8f21cd04", Identity(), 1_010_000, 120).IsFresh);
    }

    [Fact]
    public void FrameRules()
    {
        Assert.True(FreshnessRules.EvaluateFrame(Frame(), Identity(), 1_010_000, 60).IsFresh);
        Assert.Equal(ErrorCodes.StaleState, FreshnessRules.EvaluateFrame(null, Identity(), 1_010_000, 60).ErrorCode);
        Assert.Equal(ErrorCodes.StaleState, FreshnessRules.EvaluateFrame(Frame(), Identity(), 1_060_001 + 60_000, 60).ErrorCode);
        Assert.Equal(ErrorCodes.FrameNotActionable, FreshnessRules.EvaluateFrame(Frame(actionable: false), Identity(), 1_010_000, 60).ErrorCode);
        Assert.Equal(ErrorCodes.StaleState,
            FreshnessRules.EvaluateFrame(Frame(bounds: new RectI(200, 100, 1000, 700)), Identity(), 1_010_000, 60).ErrorCode);
        Assert.Equal(ErrorCodes.StaleState,
            FreshnessRules.EvaluateFrame(Frame(dpi: 144), Identity(), 1_010_000, 60).ErrorCode);
        Assert.Equal(ErrorCodes.WindowChanged,
            FreshnessRules.EvaluateFrame(Frame(), Identity(pid: 999), 1_010_000, 60).ErrorCode);
    }

    [Fact]
    public void CoordinateMappingScalesAndRounds()
    {
        var frame = Frame() with { ImageWidth = 1024, ImageHeight = 768, ClientWidth = 800, ClientHeight = 600 };
        Assert.True(CoordinateMapper.TryMapImageToClient(frame, 512, 384, out var clientX, out var clientY));
        Assert.Equal(400, clientX);
        Assert.Equal(300, clientY);

        Assert.True(CoordinateMapper.TryMapImageToScreen(frame, 512, 384, out var screenX, out var screenY));
        Assert.Equal(510, screenX);
        Assert.Equal(430, screenY);
    }

    [Fact]
    public void CoordinateMappingRejectsOutOfBoundsAndNonActionableFrames()
    {
        var frame = Frame() with { ImageWidth = 100, ImageHeight = 100, ClientWidth = 80, ClientHeight = 80 };
        Assert.False(CoordinateMapper.TryMapImageToClient(frame, 100, 0, out _, out _));
        Assert.False(CoordinateMapper.TryMapImageToClient(frame, 0, -1, out _, out _));
        Assert.False(CoordinateMapper.TryMapImageToClient(frame with { Actionable = false }, 1, 1, out _, out _));
        Assert.False(CoordinateMapper.TryMapImageToClient(frame with { ImageWidth = 0 }, 1, 1, out _, out _));
    }

    [Fact]
    public void CoordinateMappingClampsToClientBounds()
    {
        var frame = Frame() with { ImageWidth = 1000, ImageHeight = 1000, ClientWidth = 500, ClientHeight = 500 };
        Assert.True(CoordinateMapper.TryMapImageToClient(frame, 999, 999, out var clientX, out var clientY));
        // Client pixel indices are 0..ClientWidth-1: the 500th pixel index is clamped to 499.
        Assert.Equal(499, clientX);
        Assert.Equal(499, clientY);
    }
}
