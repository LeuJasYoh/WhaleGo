using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Capture;

/// <summary>
/// Frame geometry assembly + image-pixel → client-pixel → screen-pixel mapping (docs/PROTOCOL.md §3.6/§6, D8).
/// Model coordinates are image pixels with the image top-left as origin; internal mapping is
/// client_x = round(image_x * client_width / image_width) (same formula as Contracts.Domain.CoordinateMapper),
/// then screen = client origin + client offset. <c>scale_x/scale_y = image_width/client_width</c>
/// (image pixels per client pixel: 1.0 when the capture was not downscaled, &lt;1.0 when it was).
/// </summary>
public static class FrameCoordinateMapper
{
    public const string CaptureRegionClient = "client";
    public const string CaptureMethodPrintWindow = "printwindow";

    public static double ScaleX(int imageWidth, int clientWidth) =>
        clientWidth > 0 && imageWidth > 0 ? (double)imageWidth / clientWidth : 0d;

    public static double ScaleY(int imageHeight, int clientHeight) =>
        clientHeight > 0 && imageHeight > 0 ? (double)imageHeight / clientHeight : 0d;

    /// <summary>Build a FrameInfo for a client-area capture of <paramref name="window"/>.</summary>
    public static FrameInfo CreateFrame(
        string frameId,
        WindowInfo window,
        int imageWidth,
        int imageHeight,
        int clientWidth,
        int clientHeight,
        int clientOriginX,
        int clientOriginY,
        bool actionable,
        string captureMethod,
        string signature,
        long capturedAtMs,
        byte[]? pngBytes)
    {
        ArgumentNullException.ThrowIfNull(window);
        var clientOrigin = new RectI(clientOriginX, clientOriginY, clientOriginX + clientWidth, clientOriginY + clientHeight);
        return new FrameInfo(
            frameId,
            window.WindowId,
            imageWidth,
            imageHeight,
            clientWidth,
            clientHeight,
            ScaleX(imageWidth, clientWidth),
            ScaleY(imageHeight, clientHeight),
            CaptureRegionClient,
            actionable,
            captureMethod,
            signature,
            capturedAtMs,
            window.Bounds,
            clientOrigin,
            window.Dpi,
            pngBytes);
    }

    /// <summary>Build the internal FrameBinding consumed by freshness checks and the coordinate dispatcher (T3).</summary>
    public static FrameBinding ToBinding(FrameInfo frame, WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(window);
        return new FrameBinding(
            frame.FrameId,
            frame.WindowId,
            window.Pid,
            window.ClassName,
            frame.WindowBounds,
            frame.ClientOrigin,
            frame.ClientWidth,
            frame.ClientHeight,
            frame.ImageWidth,
            frame.ImageHeight,
            window.Dpi,
            frame.CapturedAtMs,
            frame.Actionable,
            frame.CaptureMethod,
            frame.Signature);
    }

    /// <summary>Image pixel → client pixel (fails for out-of-image coordinates or non-actionable frames).</summary>
    public static bool TryImageToClient(FrameInfo frame, int imageX, int imageY, out int clientX, out int clientY)
    {
        clientX = -1;
        clientY = -1;
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.Actionable ||
            frame.ImageWidth <= 0 || frame.ImageHeight <= 0 ||
            frame.ClientWidth <= 0 || frame.ClientHeight <= 0)
        {
            return false;
        }

        if (imageX < 0 || imageX >= frame.ImageWidth || imageY < 0 || imageY >= frame.ImageHeight)
        {
            return false;
        }

        clientX = (int)Math.Round(imageX * (double)frame.ClientWidth / frame.ImageWidth, MidpointRounding.AwayFromZero);
        clientY = (int)Math.Round(imageY * (double)frame.ClientHeight / frame.ImageHeight, MidpointRounding.AwayFromZero);
        clientX = Math.Clamp(clientX, 0, frame.ClientWidth - 1);
        clientY = Math.Clamp(clientY, 0, frame.ClientHeight - 1);
        return true;
    }

    /// <summary>Image pixel → physical screen pixel (client origin + mapped client offset).</summary>
    public static bool TryImageToScreen(FrameInfo frame, int imageX, int imageY, out int screenX, out int screenY)
    {
        screenX = -1;
        screenY = -1;
        if (!TryImageToClient(frame, imageX, imageY, out var clientX, out var clientY))
        {
            return false;
        }

        screenX = frame.ClientOrigin.Left + clientX;
        screenY = frame.ClientOrigin.Top + clientY;
        return true;
    }

    /// <summary>Contract-level mapping helper using the same rules (for callers that already hold a FrameBinding).</summary>
    public static bool TryImageToScreen(FrameBinding binding, int imageX, int imageY, out int screenX, out int screenY)
    {
        screenX = -1;
        screenY = -1;
        if (!CoordinateMapper.TryMapImageToScreen(binding, imageX, imageY, out screenX, out screenY))
        {
            return false;
        }

        return true;
    }
}
