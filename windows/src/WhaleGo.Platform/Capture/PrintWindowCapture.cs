using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Capture;

/// <summary>
/// Client-area-only single-window screenshot (docs/DESIGN §5.4, D8): PrintWindow(PW_RENDERFULLCONTENT) into a 32bpp DIB,
/// cropped to the client rect, with a BitBlt fallback. Encodes PNG without System.Drawing.Common, downscales to
/// limits.maxImageWidth (area average) and keeps re-encoding at smaller widths until limits.maxImageBytes fits.
/// <para>Privacy: sensitive windows (privacy.sensitiveWindows via <see cref="Redactor"/>) are never captured →
/// CaptureResult.FailureAdvisory = "screenshot_suppressed". Minimized/unavailable windows → "screenshot_failed".</para>
/// <para>Actionable: only validated hosts (Notepad, WindowsForms10.*, HwndWrapper* WPF, CabinetWClass, ApplicationFrameWindow,
/// #32770 dialog, CalcFrame) with a nonblank_ok signature and no BitBlt fallback; everything else returns frame
/// actionable=false (engine adds capture_degraded), never a silent success.</para>
/// </summary>
public sealed class PrintWindowCapture : IScreenCapture
{
    private const int MaxFullDimension = 16384;
    private const int MinScaledWidth = 16;
    private const string BitBltMethod = "bitblt";

    private readonly WindowCatalog _catalog;
    private readonly Redactor _redactor;
    private readonly IPlatformClock _clock;

    public PrintWindowCapture(WindowCatalog catalog, Redactor? redactor = null, IPlatformClock? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _redactor = redactor ?? new Redactor();
        _clock = clock ?? PlatformClock.Instance;
    }

    /// <summary>Fresh frame id ("fr-" + 8 lowercase hex); UiaObservationEngine re-mints it for snapshots.</summary>
    public static string NewFrameId() => "fr-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public CaptureResult Capture(string windowId, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!WindowIdCodec.TryParse(windowId, out var hwnd) || !Win32.IsWindow(hwnd))
        {
            return Failure(Advisories.ScreenshotFailed, $"Window '{windowId}' is not available.");
        }

        var window = _catalog.TryGetWindow(windowId);
        if (window is null)
        {
            return Failure(Advisories.ScreenshotFailed, $"Window '{windowId}' is not a live top-level window.");
        }

        if (window.IsMinimized)
        {
            return Failure(Advisories.ScreenshotFailed, "Window is minimized; restore it before requesting a screenshot.");
        }

        if (options.SuppressSensitiveWindows && _redactor.IsSensitiveWindow(window))
        {
            return new CaptureResult(null, null, Advisories.ScreenshotSuppressed, "Sensitive window matched privacy policy; screenshot suppressed.");
        }

        if (!Win32.GetClientRect(hwnd, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            return Failure(Advisories.ScreenshotFailed, "Client rect is empty.");
        }

        var clientWidth = clientRect.Width;
        var clientHeight = clientRect.Height;
        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(hwnd, ref origin))
        {
            return Failure(Advisories.ScreenshotFailed, "ClientToScreen failed.");
        }

        if (!Win32.GetWindowRect(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            return Failure(Advisories.ScreenshotFailed, "Window rect is empty.");
        }

        var fullWidth = windowRect.Width;
        var fullHeight = windowRect.Height;
        if (fullWidth > MaxFullDimension || fullHeight > MaxFullDimension)
        {
            return Failure(Advisories.ScreenshotFailed, $"Window is too large to capture ({fullWidth}x{fullHeight}).");
        }

        var offsetX = origin.X - windowRect.Left;
        var offsetY = origin.Y - windowRect.Top;
        if (!TryGrabClientPixels(hwnd, fullWidth, fullHeight, clientWidth, clientHeight, offsetX, offsetY, out var clientPixels, out var method, out var detail))
        {
            return Failure(Advisories.ScreenshotFailed, detail ?? "GDI capture failed.");
        }

        var validation = CaptureValidator.Validate(clientPixels, clientWidth, clientHeight, clientWidth * 4, window.IsMinimized);
        if (!validation.SaneDimensions)
        {
            return Failure(Advisories.ScreenshotFailed, "Captured bitmap has invalid dimensions.");
        }

        var usedFallback = string.Equals(method, BitBltMethod, StringComparison.Ordinal);
        var maxWidth = Math.Clamp(
            options.MaxImageWidth > 0 ? options.MaxImageWidth : ConfigDefaults.LimitsMaxImageWidth,
            MinScaledWidth,
            MaxFullDimension);
        var maxBytes = options.MaxImageBytes > 0 ? options.MaxImageBytes : ConfigDefaults.LimitsMaxImageBytes;

        var imageWidth = Math.Min(clientWidth, maxWidth);
        var imageHeight = Math.Max(1, (int)Math.Round(clientHeight * (double)imageWidth / clientWidth));
        byte[] png;
        while (true)
        {
            var pixels = imageWidth == clientWidth && imageHeight == clientHeight
                ? clientPixels
                : ImageScaler.Downscale(clientPixels, clientWidth, clientHeight, imageWidth, imageHeight);
            png = PngEncoder.EncodeBgra32(imageWidth, imageHeight, pixels, imageWidth * 4);
            if (png.Length <= maxBytes)
            {
                break;
            }

            if (imageWidth <= MinScaledWidth)
            {
                return Failure(Advisories.ScreenshotFailed, $"Screenshot still exceeds max_image_bytes ({maxBytes}) at {MinScaledWidth}px width.");
            }

            imageWidth = Math.Max(MinScaledWidth, (int)(imageWidth * 0.9));
            imageHeight = Math.Max(1, (int)Math.Round(clientHeight * (double)imageWidth / clientWidth));
        }

        var validatedHost = IsValidatedHost(window);
        var actionable = validation.Passed && validatedHost && !usedFallback && !window.IsMinimized;
        var frame = FrameCoordinateMapper.CreateFrame(
            NewFrameId(),
            window,
            imageWidth,
            imageHeight,
            clientWidth,
            clientHeight,
            origin.X,
            origin.Y,
            actionable,
            FrameCoordinateMapper.CaptureMethodPrintWindow,
            validation.Signature,
            _clock.NowMs,
            png);

        return new CaptureResult(frame, png, null, null);
    }

    /// <summary>Hosts whose PrintWindow output is known to be reliable for coordinate actions (D8/P1 matrix).</summary>
    public static bool IsValidatedHost(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var className = window.ClassName ?? string.Empty;
        if (className.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("HwndWrapper", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        switch (className)
        {
            case "Notepad":
            case "CabinetWClass":
            case "ApplicationFrameWindow":
            case "#32770":
            case "CalcFrame":
                return true;
        }

        if (Win32.TryGetProcessName(window.Pid, out var processName))
        {
            return processName.Equals("notepad", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("calc", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("CalculatorApp", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryGrabClientPixels(
        IntPtr hwnd,
        int fullWidth,
        int fullHeight,
        int clientWidth,
        int clientHeight,
        int offsetX,
        int offsetY,
        out byte[] pixels,
        out string method,
        out string? detail)
    {
        pixels = Array.Empty<byte>();
        method = FrameCoordinateMapper.CaptureMethodPrintWindow;
        detail = null;

        var fullStride = fullWidth * 4;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            detail = "GetDC(screen) failed.";
            return false;
        }

        var memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        var bits = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = Win32.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                detail = "CreateCompatibleDC failed.";
                return false;
            }

            var bitmapInfo = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = fullWidth,
                    biHeight = -fullHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                    biSizeImage = fullStride * fullHeight,
                },
            };

            dib = Win32.CreateDIBSection(screenDc, ref bitmapInfo, Win32.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                detail = "CreateDIBSection failed.";
                return false;
            }

            previousObject = Win32.SelectObject(memoryDc, dib);
            if (Win32.PrintWindow(hwnd, memoryDc, Win32.PW_RENDERFULLCONTENT))
            {
                pixels = CopyClientRegion(bits, fullStride, fullWidth, fullHeight, offsetX, offsetY, clientWidth, clientHeight);
                return pixels.Length == clientWidth * 4 * clientHeight;
            }

            method = BitBltMethod;
            var windowDc = Win32.GetDC(hwnd);
            try
            {
                if (windowDc == IntPtr.Zero)
                {
                    detail = "GetDC(window) failed.";
                    return false;
                }

                if (!Win32.BitBlt(memoryDc, 0, 0, clientWidth, clientHeight, windowDc, 0, 0, Win32.SRCCOPY))
                {
                    detail = "PrintWindow and BitBlt both failed.";
                    return false;
                }
            }
            finally
            {
                Win32.ReleaseDC(hwnd, windowDc);
            }

            pixels = CopyClientRegion(bits, fullStride, fullWidth, fullHeight, 0, 0, clientWidth, clientHeight);
            return pixels.Length == clientWidth * 4 * clientHeight;
        }
        catch (Exception exception)
        {
            detail = exception.Message;
            return false;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previousObject != IntPtr.Zero)
            {
                Win32.SelectObject(memoryDc, previousObject);
            }

            if (dib != IntPtr.Zero)
            {
                Win32.DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                Win32.DeleteDC(memoryDc);
            }

            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] CopyClientRegion(
        IntPtr bits,
        int fullStride,
        int fullWidth,
        int fullHeight,
        int offsetX,
        int offsetY,
        int clientWidth,
        int clientHeight)
    {
        var result = new byte[clientWidth * 4 * clientHeight];
        var x = Math.Clamp(offsetX, 0, Math.Max(0, fullWidth - clientWidth));
        var y = Math.Clamp(offsetY, 0, Math.Max(0, fullHeight - clientHeight));
        var rowBytes = clientWidth * 4;
        for (var row = 0; row < clientHeight; row++)
        {
            var source = IntPtr.Add(bits, ((y + row) * fullStride) + (x * 4));
            Marshal.Copy(source, result, row * rowBytes, rowBytes);
        }

        return result;
    }

    private static CaptureResult Failure(string advisory, string detail) => new(null, null, advisory, detail);
}
