using System.Drawing;
using FlaUI.Core.AutomationElements;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Input;

namespace WhaleGo.Platform.Uia;

/// <summary>Resolve status for one (stateId, index) pair of a platform element handle.</summary>
public enum ElementResolveStatus
{
    Resolved = 0,

    /// <summary>The state id is unknown or expired (application freshness should have refused already).</summary>
    StaleState = 1,

    /// <summary>The state is live but the index is gone.</summary>
    ElementUnavailable = 2,
}

/// <summary>Platform element resolution: a live UIA element plus the window facts captured at observe time.</summary>
public sealed record ElementResolution(
    ElementResolveStatus Status,
    AutomationElement? Element = null,
    string? WindowId = null,
    int WindowPid = 0,
    nint Hwnd = 0,
    long CapturedAtMs = 0,
    int Index = -1)
{
    public static ElementResolution Stale() => new(ElementResolveStatus.StaleState);

    public static ElementResolution Missing() => new(ElementResolveStatus.ElementUnavailable);

    public static ElementResolution Ok(
        AutomationElement element,
        string? windowId,
        int windowPid,
        nint hwnd,
        long capturedAtMs,
        int index) =>
        new(ElementResolveStatus.Resolved, element, windowId, windowPid, hwnd, capturedAtMs, index);
}

/// <summary>Thin seam over the observation state-handle store (owned by T2); tests inject a fake.</summary>
public interface IElementHandleResolver
{
    ElementResolution Resolve(string stateId, int elementIndex);
}

/// <summary>
/// UIA element dispatch: resolve (stateId, index) -> UIA element, UIPI pre-check, pattern ladder,
/// readback verification and the narrow foreground fallback for typing/scrolling
/// (docs/ARCHITECTURE.md section 3.6 / docs/PROTOCOL.md section 3.7).
/// </summary>
public sealed class UiaActionExecutor : IElementActionDispatcher
{
    private const string MethodUiAutomation = "uia";
    private const string MethodSendInput = "send_input";
    private const string MethodPostMessage = "post_message";

    private readonly IElementHandleResolver _resolver;
    private readonly UipiGuard _uipi;
    private readonly ForegroundGuard _foreground;
    private readonly SendInputDispatcher _sendInput;
    private readonly PostMessageDispatcher _postMessage;
    private readonly IInputLane _lane;

    public UiaActionExecutor(
        IElementHandleResolver resolver,
        UipiGuard uipi,
        ForegroundGuard foreground,
        SendInputDispatcher sendInput,
        PostMessageDispatcher postMessage,
        IInputLane lane)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _uipi = uipi ?? throw new ArgumentNullException(nameof(uipi));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _sendInput = sendInput ?? throw new ArgumentNullException(nameof(sendInput));
        _postMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
    }

    public async Task<PlatformActionOutcome> ExecuteAsync(string stateId, int elementIndex, ActionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        var resolution = _resolver.Resolve(stateId, elementIndex);
        if (resolution.Status != ElementResolveStatus.Resolved || resolution.Element is null)
        {
            return resolution.Status == ElementResolveStatus.ElementUnavailable
                ? PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable)
                : PlatformActionOutcome.NotSent(ErrorCodes.StaleState);
        }

        var verdict = _uipi.Check(resolution.WindowPid, out var uipiDetail);
        if (UipiGuard.IsBlocked(verdict))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.UipiBlocked, uipiDetail);
        }

        var element = resolution.Element;
        try
        {
            return plan.Op switch
            {
                "click" => await Task.Run(() => Click(element, plan), ct).ConfigureAwait(false),
                "set_value" => await Task.Run(() => SetValue(element, plan), ct).ConfigureAwait(false),
                "type" => await TypeAsync(element, resolution, plan, ct).ConfigureAwait(false),
                "key" => await Task.Run(() => PressKey(element, resolution, plan), ct).ConfigureAwait(false),
                "hotkey" => await HotkeyAsync(element, resolution, plan, ct).ConfigureAwait(false),
                "scroll" => await Task.Run(() => Scroll(element, resolution, plan), ct).ConfigureAwait(false),
                _ => PlatformActionOutcome.NotSent(ErrorCodes.UnsupportedAction),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Platform must never leak exceptions to the application layer.
            return PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message);
        }
    }

    private static PlatformActionOutcome Click(AutomationElement element, ActionPlan plan)
    {
        if (!string.Equals(plan.ClickButton, "left", StringComparison.Ordinal) || plan.ClickCount != 1)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.UnsupportedAction);
        }

        if (!SafeRead(() => element.IsEnabled, fallback: true))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable);
        }

        var sent = false;
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                var invoke = element.Patterns.Invoke.Pattern;
                sent = true;
                invoke.Invoke();
                return AcceptUnverified(MethodUiAutomation, escalated: false, "click_no_readback");
            }

            if (element.Patterns.Toggle.IsSupported)
            {
                var toggle = element.Patterns.Toggle.Pattern;
                sent = true;
                toggle.Toggle();
                return AcceptUnverified(MethodUiAutomation, escalated: false, "click_no_readback");
            }

            if (element.Patterns.SelectionItem.IsSupported)
            {
                var selection = element.Patterns.SelectionItem.Pattern;
                sent = true;
                selection.Select();
                return AcceptUnverified(MethodUiAutomation, escalated: false, "click_no_readback");
            }

            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                var expand = element.Patterns.ExpandCollapse.Pattern;
                var state = SafeRead(() => expand.ExpandCollapseState.ValueOrDefault, FlaUI.Core.Definitions.ExpandCollapseState.Collapsed);
                sent = true;
                if (state == FlaUI.Core.Definitions.ExpandCollapseState.Collapsed)
                {
                    expand.Expand();
                }
                else
                {
                    expand.Collapse();
                }

                return AcceptUnverified(MethodUiAutomation, escalated: false, "click_no_readback");
            }

            return PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return sent
                ? PlatformActionOutcome.PossiblySent(MethodUiAutomation, escalated: false, "uia_after_dispatch: " + ex.Message)
                : PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, ex.Message);
        }
    }

    private static PlatformActionOutcome SetValue(AutomationElement element, ActionPlan plan)
    {
        var requested = plan.SetValue ?? string.Empty;
        if (!element.Patterns.Value.IsSupported)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, "value_pattern_unavailable");
        }

        var pattern = element.Patterns.Value.Pattern;
        if (SafeRead(() => pattern.IsReadOnly.ValueOrDefault, fallback: false))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, "value_pattern_readonly");
        }

        var sent = false;
        try
        {
            sent = true;
            pattern.SetValue(requested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return sent
                ? PlatformActionOutcome.PossiblySent(MethodUiAutomation, escalated: false, "uia_after_dispatch: " + ex.Message)
                : PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, ex.Message);
        }

        var readback = SafeReadValue(pattern);
        if (readback is null)
        {
            return AcceptUnverified(MethodUiAutomation, escalated: false, "readback_unavailable");
        }

        return string.Equals(readback, requested, StringComparison.Ordinal)
            ? AcceptedVerified(MethodUiAutomation, escalated: false, "verified")
            : AcceptedVerified(MethodUiAutomation, escalated: false, "mismatched", "readback_differs");
    }

    private async Task<PlatformActionOutcome> TypeAsync(AutomationElement element, ElementResolution resolution, ActionPlan plan, CancellationToken ct)
    {
        var text = plan.TypeText ?? string.Empty;
        if (text.Length == 0)
        {
            return new PlatformActionOutcome(true, false, "none", false, null, null, "unverified", "empty_text");
        }

        if (!SafeRead(() => element.IsEnabled, fallback: true))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable);
        }

        if (element.Patterns.Value.IsSupported)
        {
            var pattern = element.Patterns.Value.Pattern;
            if (!SafeRead(() => pattern.IsReadOnly.ValueOrDefault, fallback: false))
            {
                var current = SafeReadValue(pattern) ?? string.Empty;
                var sent = false;
                try
                {
                    sent = true;
                    pattern.SetValue(current + text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return sent
                        ? PlatformActionOutcome.PossiblySent(MethodUiAutomation, escalated: false, "uia_after_dispatch: " + ex.Message)
                        : PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, ex.Message);
                }

                var readback = SafeReadValue(pattern);
                if (readback is null)
                {
                    return AcceptUnverified(MethodUiAutomation, escalated: false, "readback_unavailable");
                }

                return readback.Contains(text, StringComparison.Ordinal)
                    ? AcceptedVerified(MethodUiAutomation, escalated: false, "verified")
                    : AcceptedVerified(MethodUiAutomation, escalated: false, "mismatched", "readback_missing_text");
            }
        }

        return await TypeThroughForegroundAsync(element, resolution, text, ct).ConfigureAwait(false);
    }

    private async Task<PlatformActionOutcome> TypeThroughForegroundAsync(AutomationElement element, ElementResolution resolution, string text, CancellationToken ct)
    {
        var hwnd = ResolveWindowHwnd(element, resolution);
        if (hwnd == 0)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "no_window_handle");
        }

        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired);
        }

        try
        {
            try
            {
                element.Focus();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "focus_failed: " + ex.Message);
            }

            var sent = false;
            try
            {
                await _lane.RunAsync<bool>(_ =>
                {
                    sent = true;
                    _sendInput.TypeUnicode(text);
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodSendInput, escalated: false, "send_input_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message);
            }

            var readback = element.Patterns.Value.IsSupported ? SafeReadValue(element.Patterns.Value.Pattern) : null;
            if (readback is null)
            {
                return AcceptUnverified(MethodSendInput, escalated: false, "readback_unavailable");
            }

            return readback.Contains(text, StringComparison.Ordinal)
                ? AcceptedVerified(MethodSendInput, escalated: false, "verified")
                : AcceptedVerified(MethodSendInput, escalated: false, "mismatched", "readback_missing_text");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private PlatformActionOutcome PressKey(AutomationElement element, ElementResolution resolution, ActionPlan plan)
    {
        if (!KeyChordParser.TryParse(plan.Key, out var stroke))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "unsupported_key");
        }

        var hwnd = ResolveWindowHwnd(element, resolution);
        if (hwnd == 0)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "no_window_handle");
        }

        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired);
        }

        try
        {
            try
            {
                element.Focus();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "focus_failed: " + ex.Message);
            }

            var targetHwnd = ResolveElementHwnd(element, hwnd);
            var result = _postMessage.PressKey(targetHwnd, stroke);
            if (result.Delivered)
            {
                return AcceptUnverified(MethodPostMessage, escalated: false, "key_no_readback");
            }

            return result.AnyQueued
                ? PlatformActionOutcome.PossiblySent(MethodPostMessage, escalated: false, "post_message_partial: " + result.Error)
                : PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, result.Error);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<PlatformActionOutcome> HotkeyAsync(
        AutomationElement element,
        ElementResolution resolution,
        ActionPlan plan,
        CancellationToken ct)
    {
        if (!KeyChordParser.TryParseHotkey(plan.Hotkey, out var chord))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "unsupported_hotkey");
        }

        var hwnd = ResolveWindowHwnd(element, resolution);
        if (hwnd == 0)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "no_window_handle");
        }

        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired);
        }

        try
        {
            try
            {
                element.Focus();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "focus_failed: " + ex.Message);
            }

            var sent = false;
            try
            {
                await _lane.RunAsync<bool>(
                    _ =>
                    {
                        sent = true;
                        _sendInput.SendChord(chord);
                        return Task.FromResult(true);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodSendInput, escalated: false, "send_input_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message);
            }

            return AcceptUnverified(MethodSendInput, escalated: false, "hotkey_no_readback");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private PlatformActionOutcome Scroll(AutomationElement element, ElementResolution resolution, ActionPlan plan)
    {
        var direction = plan.ScrollDirection;
        if (!string.Equals(direction, "up", StringComparison.Ordinal) && !string.Equals(direction, "down", StringComparison.Ordinal))
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.InvalidArgument, "invalid_scroll_direction");
        }

        if (element.Patterns.Scroll.IsSupported)
        {
            var pattern = element.Patterns.Scroll.Pattern;
            var steps = plan.ScrollPages >= 1 ? (int)Math.Round(plan.ScrollPages, MidpointRounding.AwayFromZero) : 1;
            var increment = string.Equals(direction, "down", StringComparison.Ordinal)
                ? FlaUI.Core.Definitions.ScrollAmount.LargeIncrement
                : FlaUI.Core.Definitions.ScrollAmount.LargeDecrement;
            if (plan.ScrollPages < 1)
            {
                increment = string.Equals(direction, "down", StringComparison.Ordinal)
                    ? FlaUI.Core.Definitions.ScrollAmount.SmallIncrement
                    : FlaUI.Core.Definitions.ScrollAmount.SmallDecrement;
            }

            var sent = false;
            try
            {
                sent = true;
                for (var i = 0; i < steps; i++)
                {
                    pattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, increment);
                }

                return AcceptUnverified(MethodUiAutomation, escalated: false, "scroll_no_readback");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodUiAutomation, escalated: false, "uia_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.ElementActionUnavailable, ex.Message);
            }
        }

        var hwnd = ResolveWindowHwnd(element, resolution);
        if (hwnd == 0)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "no_window_handle");
        }

        var bounds = SafeRead(() => element.BoundingRectangle, Rectangle.Empty);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ElementUnavailable, "empty_bounds");
        }

        var centerX = bounds.Left + (bounds.Width / 2);
        var centerY = bounds.Top + (bounds.Height / 2);
        ForegroundGuard.ForegroundLease? lease;
        try
        {
            lease = _foreground.IsForeground(hwnd) ? null : _foreground.TryEnter(hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired, ex.Message);
        }

        if (!_foreground.IsForeground(hwnd) && lease is null)
        {
            return PlatformActionOutcome.NotSent(ErrorCodes.ForegroundRequired);
        }

        try
        {
            var delta = SendInputDispatcher.WheelDeltaFor(direction, plan.ScrollPages);
            var sent = false;
            try
            {
                sent = true;
                _sendInput.ScrollAt(centerX, centerY, delta);
                return AcceptUnverified(MethodSendInput, escalated: false, "scroll_no_readback");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return sent
                    ? PlatformActionOutcome.PossiblySent(MethodSendInput, escalated: false, "send_input_after_dispatch: " + ex.Message)
                    : PlatformActionOutcome.NotSent(ErrorCodes.Internal, ex.Message);
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static nint ResolveWindowHwnd(AutomationElement element, ElementResolution resolution)
    {
        if (resolution.Hwnd != 0)
        {
            return resolution.Hwnd;
        }

        return ResolveElementHwnd(element, 0);
    }

    private static nint ResolveElementHwnd(AutomationElement element, nint fallbackWindowHwnd)
    {
        var current = element;
        for (var i = 0; i < 16 && current is not null; i++)
        {
            var handle = SafeRead(() => current.Properties.NativeWindowHandle.ValueOrDefault, nint.Zero);
            if (handle != 0)
            {
                return handle;
            }

            current = SafeRead<AutomationElement?>(() => current.Parent, null);
        }

        return fallbackWindowHwnd;
    }

    private static string? SafeReadValue(FlaUI.Core.Patterns.IValuePattern pattern) =>
        SafeRead<string?>(() =>
        {
            var value = pattern.Value;
            return value.IsSupported ? value.Value : null;
        }, null);

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static PlatformActionOutcome AcceptUnverified(string method, bool escalated, string reason) =>
        new(true, false, method, escalated, FailureCode: null, FailureDetail: null, VerificationStatus: "unverified", VerificationReason: reason);

    private static PlatformActionOutcome AcceptedVerified(string method, bool escalated, string status, string? reason = null) =>
        new(true, false, method, escalated, FailureCode: null, FailureDetail: null, VerificationStatus: status, VerificationReason: reason);
}