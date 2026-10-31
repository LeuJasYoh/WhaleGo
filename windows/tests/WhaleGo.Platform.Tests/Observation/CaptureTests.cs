using System.Drawing;
using System.Windows.Forms;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.Tests.TestInfrastructure;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class CaptureTests
{
    private static CaptureOptions DefaultOptions => new(1024, 200_000, "printwindow", SuppressSensitiveWindows: true);

    [Fact]
    public void CapturesClientAreaPngWithConsistentGeometry()
    {
        using var harness = PlatformHarness.Start();
        var window = harness.Catalog.TryGetWindow(harness.WindowId);
        Assert.NotNull(window);

        var client = TestWin32.GetClientRectValue(harness.Host.Handle);
        var clientScreen = TestWin32.GetClientScreenRect(harness.Host.Handle);

        var result = harness.Capture.Capture(harness.WindowId, DefaultOptions);
        Assert.Null(result.FailureAdvisory);
        Assert.NotNull(result.Frame);
        Assert.NotNull(result.PngBytes);
        var frame = result.Frame!;

        Assert.Equal("client", frame.CaptureRegion);
        Assert.Equal("printwindow", frame.CaptureMethod);
        Assert.Contains(frame.Signature, new[] { "nonblank_ok", "blank", "black", "failed" });
        Assert.Equal("nonblank_ok", frame.Signature);
        Assert.True(frame.Actionable, $"expected an actionable WinForms capture; signature={frame.Signature} class={window!.ClassName}");

        Assert.Equal(client.Width, frame.ClientWidth);
        Assert.Equal(client.Height, frame.ClientHeight);
        Assert.Equal(clientScreen.Left, frame.ClientOrigin.Left);
        Assert.Equal(clientScreen.Top, frame.ClientOrigin.Top);
        Assert.Equal(clientScreen.Right, frame.ClientOrigin.Right);
        Assert.Equal(clientScreen.Bottom, frame.ClientOrigin.Bottom);
        Assert.Equal(window.Bounds, frame.WindowBounds);
        Assert.True(frame.Dpi >= 96);

        var expectedWidth = Math.Min(client.Width, 1024);
        var expectedHeight = Math.Max(1, (int)Math.Round(client.Height * (double)expectedWidth / client.Width));
        Assert.Equal(expectedWidth, frame.ImageWidth);
        Assert.Equal(expectedHeight, frame.ImageHeight);
        Assert.Equal(frame.ImageWidth / (double)frame.ClientWidth, frame.ScaleX, 6);
        Assert.Equal(frame.ImageHeight / (double)frame.ClientHeight, frame.ScaleY, 6);

        Assert.NotNull(result.PngBytes);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, result.PngBytes!.Take(8).ToArray());
        Assert.True(result.PngBytes.Length <= 200_000);
    }

    [Fact]
    public void ImageCoordinatesMapToClientAndScreen()
    {
        using var harness = PlatformHarness.Start();
        var result = harness.Capture.Capture(harness.WindowId, DefaultOptions);
        var frame = result.Frame!;
        var clientScreen = TestWin32.GetClientScreenRect(harness.Host.Handle);

        Assert.True(FrameCoordinateMapper.TryImageToClient(frame, 0, 0, out var left, out var top));
        Assert.Equal(0, left);
        Assert.Equal(0, top);

        Assert.True(FrameCoordinateMapper.TryImageToClient(frame, frame.ImageWidth - 1, frame.ImageHeight - 1, out var right, out var bottom));
        Assert.Equal(frame.ClientWidth - 1, right);
        Assert.Equal(frame.ClientHeight - 1, bottom);

        Assert.False(FrameCoordinateMapper.TryImageToClient(frame, -1, 0, out _, out _));
        Assert.False(FrameCoordinateMapper.TryImageToClient(frame, frame.ImageWidth, 0, out _, out _));

        Assert.True(FrameCoordinateMapper.TryImageToScreen(frame, frame.ImageWidth / 2, frame.ImageHeight / 2, out var screenX, out var screenY));
        Assert.True(clientScreen.Contains(screenX, screenY), $"point {screenX},{screenY} not inside client {clientScreen}");
    }

    [Fact]
    public void DownscalesToMaxImageWidthAndShrinksForByteBudget()
    {
        using var harness = PlatformHarness.Start();
        var client = TestWin32.GetClientRectValue(harness.Host.Handle);

        var narrow = harness.Capture.Capture(harness.WindowId, new CaptureOptions(64, 200_000, "printwindow", true));
        Assert.NotNull(narrow.Frame);
        Assert.True(narrow.Frame!.ImageWidth <= 64, $"image width={narrow.Frame.ImageWidth}");
        Assert.True(narrow.Frame.ScaleX < 1.0);
        Assert.Equal(Math.Min(client.Width, 64), narrow.Frame.ImageWidth);

        var tinyBudget = harness.Capture.Capture(harness.WindowId, new CaptureOptions(1024, 2_000, "printwindow", true));
        Assert.NotNull(tinyBudget.Frame);
        Assert.NotNull(tinyBudget.PngBytes);
        Assert.True(tinyBudget.PngBytes!.Length <= 2_000, $"png bytes={tinyBudget.PngBytes.Length}");
        Assert.True(tinyBudget.Frame!.ImageWidth < client.Width);
    }

    [Fact]
    public void MinimizedWindowCaptureFailsHonestly()
    {
        using var harness = PlatformHarness.Start();
        harness.Host.Invoke(form => form.WindowState = FormWindowState.Minimized);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            WindowInfo? refreshed = null;
            while (DateTime.UtcNow < deadline)
            {
                refreshed = harness.Catalog.TryGetWindow(harness.WindowId);
                if (refreshed is { IsMinimized: true })
                {
                    break;
                }

                Thread.Sleep(50);
            }

            Assert.NotNull(refreshed);
            Assert.True(refreshed!.IsMinimized);
            var result = harness.Capture.Capture(harness.WindowId, DefaultOptions);
            Assert.Null(result.Frame);
            Assert.Null(result.PngBytes);
            Assert.Equal(Advisories.ScreenshotFailed, result.FailureAdvisory);
        }
        finally
        {
            harness.Host.Invoke(form => form.WindowState = FormWindowState.Normal);
        }
    }

    [Fact]
    public void SensitiveWindowCaptureIsSuppressed()
    {
        var suffix = "bank-capture-" + Guid.NewGuid().ToString("N")[..8];
        using var harness = PlatformHarness.Start(suffix: suffix, sensitivePatterns: ["*bank*"]);
        var result = harness.Capture.Capture(harness.WindowId, DefaultOptions);
        Assert.Null(result.Frame);
        Assert.Null(result.PngBytes);
        Assert.Equal(Advisories.ScreenshotSuppressed, result.FailureAdvisory);
    }

    [Fact]
    public void FrameStaysFreshUntilGeometryChanges()
    {
        using var harness = PlatformHarness.Start();
        var window = harness.Catalog.TryGetWindow(harness.WindowId);
        Assert.NotNull(window);
        var result = harness.Capture.Capture(harness.WindowId, DefaultOptions);
        var frame = result.Frame;
        Assert.NotNull(frame);
        Assert.True(frame!.Actionable);

        var binding = FrameCoordinateMapper.ToBinding(frame, window!);
        var now = PlatformClock.Instance.NowMs;
        Assert.Equal(FreshnessVerdict.Fresh, FreshnessRules.EvaluateFrame(binding, WindowIdentity.From(window!), now, 60).Verdict);

        var notActionable = FreshnessRules.EvaluateFrame(binding with { Actionable = false }, WindowIdentity.From(window!), now, 60);
        Assert.Equal(FreshnessVerdict.NotActionable, notActionable.Verdict);
        Assert.Equal(ErrorCodes.FrameNotActionable, notActionable.ErrorCode);

        var ttlExpired = FreshnessRules.EvaluateFrame(binding, WindowIdentity.From(window!), now + 61_000, 60);
        Assert.Equal(FreshnessVerdict.Expired, ttlExpired.Verdict);
        Assert.Equal(ErrorCodes.StaleState, ttlExpired.ErrorCode);

        var originalBounds = window!.Bounds;
        harness.Host.ResizeClient(harness.Host.ClientSize.Width + 80, harness.Host.ClientSize.Height + 60);
        var resized = WaitForBoundsChange(harness, originalBounds);
        var geometryChanged = FreshnessRules.EvaluateFrame(binding, WindowIdentity.From(resized), PlatformClock.Instance.NowMs, 60);
        Assert.Equal(FreshnessVerdict.Expired, geometryChanged.Verdict);
        Assert.Equal(ErrorCodes.StaleState, geometryChanged.ErrorCode);
    }

    [Fact]
    public void CaptureValidatorClassifiesSignatures()
    {
        var white = new byte[4 * 4 * 8];
        Array.Fill(white, (byte)0xFF);
        Assert.Equal("blank", CaptureValidator.Validate(white, 4, 8, 16).Signature);

        var black = new byte[4 * 4 * 8];
        Assert.Equal("black", CaptureValidator.Validate(black, 4, 8, 16).Signature);

        var mixed = new byte[4 * 4 * 8];
        for (var i = 0; i < mixed.Length; i += 4)
        {
            mixed[i] = (byte)(i % 255);
            mixed[i + 1] = 32;
            mixed[i + 2] = 64;
        }

        var mixedValidation = CaptureValidator.Validate(mixed, 4, 8, 16);
        Assert.Equal("nonblank_ok", mixedValidation.Signature);
        Assert.True(mixedValidation.Passed);

        Assert.Equal("failed", CaptureValidator.Validate(Array.Empty<byte>(), 4, 8, 16).Signature);
        Assert.Equal("failed", CaptureValidator.Validate(white, 1, 8, 4).Signature);
        Assert.Equal("failed", CaptureValidator.Validate(white, 4, 8, 16, windowMinimized: true).Signature);
    }

    private static WindowInfo WaitForBoundsChange(PlatformHarness harness, RectI originalBounds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = harness.Catalog.TryGetWindow(harness.WindowId);
            if (current is not null && current.Bounds != originalBounds)
            {
                return current;
            }

            Thread.Sleep(50);
        }

        throw new Xunit.Sdk.XunitException("Window bounds did not change after resize.");
    }
}
