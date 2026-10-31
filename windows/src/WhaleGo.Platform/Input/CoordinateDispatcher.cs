using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Coordinate dispatch bound to an internal frame (docs/ARCHITECTURE.md section 3.6 / docs/PROTOCOL.md section 6).
/// image px -> client px (Contracts CoordinateMapper) -> screen px -> hit-test -> host matrix:
/// deliverable => PostMessage then (only on a definite not-sent) foreground SendInput escalation;
/// droppable/unknown => foreground SendInput. Never auto-observes.
/// </summary>
public sealed class CoordinateDispatcher : ICoordinateActionDispatcher
{
    private const string MethodPostMessage = "post_message";
    private const string MethodSendInput = "send_input";

    private readonly IUiHitTester _hitTester;
    private readonly IWindowPointResolver _pointResolver;
    private readonly UipiGuard _uipi;
    private readonly ForegroundGuard _foreground;
    private readonly SendInputDispatcher _sendInput;
    private readonly PostMessageDispatcher _postMessage;
    private readonly IInputLane _lane;

    public CoordinateDispatcher(
        IUiHitTester hitTester,
        IWindowPointResolver pointResolver,
        UipiGuard uipi,
        ForegroundGuard foreground,
        SendInputDispatcher sendInput,
        PostMessageDispatcher postMessage,
        IInputLane lane)
    {
        _hitTester = hitTester ?? throw new ArgumentNullException(nameof(hitTester));
        _pointResolver = pointResolver ?? throw new ArgumentNullException(nameof(pointResolver));
        _uipi = uipi ?? throw new ArgumentNullException(nameof(uipi));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _sendInput = sendInput ?? throw new ArgumentNullException(nameof(sendInput));
        _postMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
    }

    public async Task<PlatformActionOutcome> ExecuteAsync(FrameBinding frame, int imageX, int imageY, ActionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        if (!frame.Actionable)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.FrameNotActionable);
        }

        if (imageX < 0 || imageX >= frame.ImageWidth || imageY < 0 || imageY >= frame.ImageHeight)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "image_point_out_of_bounds");
        }

        if (!CoordinateMapper.TryMapImageToClient(frame, imageX, imageY, out var clientX, out var clientY))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.FrameNotActionable, "frame_geometry_unusable");
        }

        var screenX = frame.ClientOrigin.Left + clientX;
        var screenY = frame.ClientOrigin.Top + clientY;

        if (!WindowIdCodec.TryParse(frame.WindowId, out var hwnd) || hwnd == 0 || !NativeInput.IsWindow(hwnd))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.WindowNotFound);
        }

        var verdict = _uipi.Check(frame.OwnerPid, out var uipiDetail);
        if (UipiGuard.IsBlocked(verdict))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.UipiBlocked, uipiDetail);
        }

        var windowInfo = new WindowInfo(
            frame.WindowId,
            AppId: string.Empty,
            Title: string.Empty,
            ClassName: frame.ClassName,
            Pid: frame.OwnerPid,
            IsOnscreen: true,
            IsMinimized: false,
            IsForeground: false,
            IsEnabled: true,
            Bounds: frame.WindowBounds,
            GeometrySource: "frame",
            Dpi: frame.Dpi,
            CapturedAtMs: frame.CapturedAtMs);

        var hit = _hitTester.HitTest(windowInfo, screenX, screenY);
        if (!hit.WithinWindow)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "point_outside_window");
        }

        if (!hit.WithinClient)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.UnsupportedAction, "point_outside_client_area");
        }

        try
        {
            return plan.Op switch
            {
                "click" => await ClickAsync(plan, hit, hwnd, screenX, screenY, ct).ConfigureAwait(false),
                "scroll" => await ScrollAsync(plan, hit, hwnd, screenX, screenY, ct).ConfigureAwait(false),
                _ => PlatformActionOutcome.NotSent(ErrorCodes.UnsupportedAction),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Platform must never leak exceptions to the application layer.
            return PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message);
        }
    }

    private async Task<PlatformActionOutcome> ClickAsync(
        ActionPlan plan,
        HitTestResult hit,
        nint hwnd,
        int screenX,
        int screenY,
        CancellationToken ct)
    {
        if (!string.Equals(plan.ClickButton, "left", StringComparison.Ordinal) || plan.ClickCount != 1)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.UnsupportedAction);
        }

        if (hit.HostClass == HostClass.Deliverable)
        {
            var post = ClickViaPointResolver(hwnd, screenX, screenY);
            if (post.Delivered)
            {
                return AcceptUnverified(MethodPostMessage, escalated: false, "click_no_readback");
            }

            if (post.AnyQueued)
            {
                return PlatformActionOutcome.PossiblySent(MethodPostMessage, escalated: false, "post_message_partial: " + post.Error);
            }

            // Only a definite not-sent may escalate.
            return await ForegroundClickAsync(hwnd, screenX, screenY, escalated: true, ct).ConfigureAwait(false);
        }

        return await ForegroundClickAsync(hwnd, screenX, screenY, escalated: false, ct).ConfigureAwait(false);
    }

    private async Task<PlatformActionOutcome> ForegroundClickAsync(
        nint hwnd,
        int screenX,
        int screenY,
        bool escalated,
        CancellationToken ct)
    {
        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message, escalated);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, escalated: escalated);
        }

        try
        {
            var sent = false;
            try
            {
                await _lane.RunAsync<bool>(_ =>
                {
                    sent = true;
                    _sendInput.ClickLeftAt(screenX, screenY);
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodSendInput, escalated, "send_input_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message, escalated);
            }

            return AcceptUnverified(MethodSendInput, escalated, "click_no_readback");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<PlatformActionOutcome> ScrollAsync(
        ActionPlan plan,
        HitTestResult hit,
        nint hwnd,
        int screenX,
        int screenY,
        CancellationToken ct)
    {
        var direction = plan.ScrollDirection;
        if (!string.Equals(direction, "up", StringComparison.Ordinal) && !string.Equals(direction, "down", StringComparison.Ordinal))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "invalid_scroll_direction");
        }

        var delta = SendInputDispatcher.WheelDeltaFor(direction, plan.ScrollPages);
        var wpfLike = hit.ClassName is not null &&
            (hit.ClassName.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase) ||
             hit.ClassName.StartsWith("Microsoft.UI.Content", StringComparison.OrdinalIgnoreCase) ||
             hit.ClassName.StartsWith("DesktopWindowXamlSource", StringComparison.OrdinalIgnoreCase));

        if (hit.HostClass == HostClass.Deliverable && wpfLike)
        {
            var post = WheelViaPointResolver(hwnd, screenX, screenY, delta);
            if (post.Delivered)
            {
                return AcceptUnverified(MethodPostMessage, escalated: false, "scroll_no_readback");
            }

            if (post.AnyQueued)
            {
                return PlatformActionOutcome.PossiblySent(MethodPostMessage, escalated: false, "post_message_partial: " + post.Error);
            }

            return await ForegroundWheelAsync(hwnd, screenX, screenY, delta, escalated: true, ct).ConfigureAwait(false);
        }

        return await ForegroundWheelAsync(hwnd, screenX, screenY, delta, escalated: false, ct).ConfigureAwait(false);
    }

    private async Task<PlatformActionOutcome> ForegroundWheelAsync(
        nint hwnd,
        int screenX,
        int screenY,
        int delta,
        bool escalated,
        CancellationToken ct)
    {
        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message, escalated);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, escalated: escalated);
        }

        try
        {
            var sent = false;
            try
            {
                await _lane.RunAsync<bool>(_ =>
                {
                    sent = true;
                    _sendInput.ScrollAt(screenX, screenY, delta);
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodSendInput, escalated, "send_input_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message, escalated);
            }

            return AcceptUnverified(MethodSendInput, escalated, "scroll_no_readback");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private PostResult ClickViaPointResolver(nint hwnd, int screenX, int screenY)
    {
        if (!TryBuildWindowInfo(hwnd, out var window))
        {
            // Fall back to the native-only resolver when HWND facts are unavailable.
            return _postMessage.ClickAtScreenPoint(hwnd, screenX, screenY);
        }

        return _pointResolver.TryResolveChildHwnd(window, screenX, screenY, out var child, out var clientX, out var clientY)
            ? _postMessage.ClickClient(child, clientX, clientY)
            : PostResult.NotSent("point_not_in_window");
    }

    private PostResult WheelViaPointResolver(nint hwnd, int screenX, int screenY, int delta)
    {
        if (!TryBuildWindowInfo(hwnd, out var window))
        {
            return _postMessage.WheelAtScreenPoint(hwnd, screenX, screenY, delta);
        }

        return _pointResolver.TryResolveChildHwnd(window, screenX, screenY, out var child, out _, out _)
            ? _postMessage.WheelAtScreenPoint(child, screenX, screenY, delta)
            : PostResult.NotSent("point_not_in_window");
    }

    private static bool TryBuildWindowInfo(nint hwnd, out WindowInfo window)
    {
        window = null!;
        if (hwnd == 0 || !NativeInput.IsWindow(hwnd))
        {
            return false;
        }

        var bounds = NativeInput.GetWindowScreenRect(hwnd);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        _ = NativeInput.GetWindowThreadProcessId(hwnd, out var pid);
        window = new WindowInfo(
            WindowIdCodec.Format(hwnd),
            AppId: string.Empty,
            Title: string.Empty,
            ClassName: NativeInput.GetWindowClassName(hwnd),
            Pid: (int)pid,
            IsOnscreen: true,
            IsMinimized: false,
            IsForeground: false,
            IsEnabled: true,
            Bounds: RectI.FromSize(bounds.left, bounds.top, bounds.Width, bounds.Height),
            GeometrySource: "win32",
            Dpi: (uint)NativeInput.GetWindowDpi(hwnd),
            CapturedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    private static PlatformActionOutcome AcceptUnverified(string method, bool escalated, string reason) =>
        new(true, false, method, escalated, null, null, "unverified", reason);
}