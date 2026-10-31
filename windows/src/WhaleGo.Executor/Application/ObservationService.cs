using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;

namespace WhaleGo.Executor.Application;

public sealed record ObserveRequest(
    string? WindowId,
    string? AppId,
    bool IncludeScreenshot,
    int SettleMs,
    int MaxElements,
    int MaxValueChars);

public sealed record ObservationResult(
    ObservationSnapshot Snapshot,
    IReadOnlyList<ElementNode> Elements,
    OmissionInfo Omitted,
    string OkSummary,
    string TreeText,
    ObserveReport Report,
    FrameInfo? Frame,
    byte[]? PngBytes,
    string ScreenshotStatus,
    IReadOnlyList<string> Advisories);

/// <summary>
/// The single observation orchestration point: window resolution → platform snapshot → budget shaping →
/// screenshot three-switch AND → session state registration → model-facing text/metadata.
/// </summary>
public sealed class ObservationService
{
    private readonly SessionManager _sessions;
    private readonly WindowService _windows;
    private readonly IObservationEngine _engine;
    private readonly IScreenCapture _capture;
    private readonly ConfigService _config;
    private readonly AuditService _audit;
    private readonly IPlatformClock _clock;

    public ObservationService(
        SessionManager sessions,
        WindowService windows,
        IObservationEngine engine,
        IScreenCapture capture,
        ConfigService config,
        AuditService audit,
        IPlatformClock clock)
    {
        _sessions = sessions;
        _windows = windows;
        _engine = engine;
        _capture = capture;
        _config = config;
        _audit = audit;
        _clock = clock;
    }

    public Task<ObservationResult> ObserveAsync(string sessionId, ObserveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var config = _config.Config;
        var session = _sessions.GetOrCreate(sessionId);
        var window = _windows.ResolveForObserve(request.WindowId, request.AppId);
        var now = _clock.NowMs;
        var shouldCapture = config.ScreenshotEnabled && (request.IncludeScreenshot || config.ScreenshotDefaultInclude);
        var maxValueChars = Math.Clamp(request.MaxValueChars <= 0 ? 48 : request.MaxValueChars, 1, 4000);
        var maxElements = Math.Clamp(
            request.MaxElements <= 0 ? config.MaxObserveElements : Math.Min(request.MaxElements, config.MaxObserveElements),
            1,
            2000);

        ObservationSnapshot snapshot;
        try
        {
            snapshot = _engine.Observe(new ObserveOptions(
                WindowId: window.WindowId,
                AppId: null,
                IncludeScreenshot: shouldCapture,
                SettleMs: request.SettleMs,
                MaxElements: maxElements,
                MaxValueChars: maxValueChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = PlatformErrors.TryGetCode(ex) ?? ErrorCodes.Internal;
            throw WhaleGoToolException.FromCode(code);
        }

        var truncatedElements = snapshot.Elements
            .Select(element => TruncateElementValue(element, maxValueChars))
            .ToList();
        var shape = ObservationShaper.Shape(truncatedElements, maxElements, config.MaxObserveChars);
        var omitted = MergeOmissions(snapshot.Omitted, shape.Omitted);

        var frame = snapshot.Frame;
        string? captureAdvisory = null;
        if (shouldCapture && frame is null)
        {
            try
            {
                var capture = _capture.Capture(window.WindowId, new CaptureOptions(
                    MaxImageWidth: config.MaxImageWidth,
                    MaxImageBytes: config.MaxImageBytes,
                    Mode: config.ScreenshotMode,
                    SuppressSensitiveWindows: true));
                frame = capture.Frame;
                captureAdvisory = capture.FailureAdvisory;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                captureAdvisory = Advisories.ScreenshotFailed;
            }
        }

        var imageBytes = frame?.PngBytes;
        var imagePresent = shouldCapture && imageBytes is { Length: > 0 } && imageBytes.Length <= config.MaxImageBytes;
        if (shouldCapture && imageBytes is { Length: > 0 } && !imagePresent)
        {
            captureAdvisory = Advisories.ScreenshotFailed;
        }

        var advisories = new List<string>(snapshot.Advisories);
        if (shape.TreeBudgetExceeded)
        {
            AddAdvisory(advisories, Advisories.TreeBudgetExceeded);
        }

        string screenshotStatus;
        if (!shouldCapture)
        {
            if (request.IncludeScreenshot && !config.ScreenshotEnabled)
            {
                screenshotStatus = "no(disabled)";
                AddAdvisory(advisories, Advisories.ScreenshotDisabled);
            }
            else
            {
                screenshotStatus = "no";
            }
        }
        else if (imagePresent)
        {
            screenshotStatus = "yes";
        }
        else
        {
            var failure = captureAdvisory ?? FindScreenshotAdvisory(advisories) ?? Advisories.ScreenshotFailed;
            AddAdvisory(advisories, failure);
            screenshotStatus = string.Equals(failure, Advisories.ScreenshotSuppressed, StringComparison.Ordinal)
                ? "no(suppressed)"
                : "no(failed)";
        }

        var frameBinding = imagePresent || frame is not null
            ? BuildFrameBinding(frame, snapshot, window)
            : null;
        var shapedSnapshot = snapshot with
        {
            SessionId = sessionId,
            Elements = shape.Elements,
            Omitted = omitted,
            Frame = frame,
            Advisories = advisories,
            CapturedAtMs = snapshot.CapturedAtMs > 0 ? snapshot.CapturedAtMs : now,
        };

        session.Capture(shapedSnapshot, frameBinding, now);

        var report = BuildReport(shapedSnapshot, frame, imagePresent, omitted, advisories);
        // The privacy hint is owned by the AstrBot plugin (PROTOCOL §11.5); the executor keeps its output contract-clean.
        var okSummary = TextTreeRenderer.FirstLine(
            window.WindowId,
            shapedSnapshot.StateId,
            shape.Elements.Count,
            omitted.SparseIndices,
            screenshotStatus,
            includePrivacyHint: false);
        var treeText = TextTreeRenderer.Render(shapedSnapshot, shape.Elements, omitted, screenshotStatus, includePrivacyHint: false);

        _audit.RecordObserve(new AuditEvent(
            TsMs: now,
            Kind: AuditEvent.KindObserve,
            SessionId: sessionId,
            Tool: "go_observe",
            WindowId: window.WindowId,
            AppId: window.AppId,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            Event: "observe",
            ImageBytes: imagePresent ? imageBytes!.Length : null));

        return Task.FromResult(new ObservationResult(
            Snapshot: shapedSnapshot,
            Elements: shape.Elements,
            Omitted: omitted,
            OkSummary: okSummary,
            TreeText: treeText,
            Report: report,
            Frame: frame,
            PngBytes: imagePresent ? imageBytes : null,
            ScreenshotStatus: screenshotStatus,
            Advisories: advisories));
    }

    private static ElementNode TruncateElementValue(ElementNode element, int maxValueChars)
    {
        if (element.Value is null || TextUtil.RuneCount(element.Value) <= maxValueChars)
        {
            return element;
        }

        return element with
        {
            Value = TextUtil.TruncateRunes(element.Value, maxValueChars, addEllipsis: true),
            ValueTruncated = true,
        };
    }

    private static OmissionInfo MergeOmissions(OmissionInfo platform, OmissionInfo shaped)
    {
        var total = platform.OmittedCount + shaped.OmittedCount;
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in platform.ByKind)
        {
            byKind[pair.Key] = byKind.TryGetValue(pair.Key, out var count) ? count + pair.Value : pair.Value;
        }

        foreach (var pair in shaped.ByKind)
        {
            byKind[pair.Key] = byKind.TryGetValue(pair.Key, out var count) ? count + pair.Value : pair.Value;
        }

        var reason = shaped.OmittedCount > 0 ? shaped.Reason : platform.Reason;
        return new OmissionInfo(total, byKind, reason, shaped.SparseIndices || platform.SparseIndices);
    }

    private static FrameBinding BuildFrameBinding(FrameInfo? frame, ObservationSnapshot snapshot, WindowInfo window)
    {
        var bounds = frame is not null && frame.WindowBounds != default ? frame.WindowBounds : window.Bounds;
        var dpi = frame is not null && frame.Dpi != 0 ? frame.Dpi : window.Dpi;
        var capturedAt = frame is not null && frame.CapturedAtMs > 0 ? frame.CapturedAtMs : snapshot.CapturedAtMs;
        return new FrameBinding(
            FrameId: frame?.FrameId ?? string.Empty,
            WindowId: window.WindowId,
            OwnerPid: snapshot.Window.Pid,
            ClassName: snapshot.Window.ClassName,
            WindowBounds: bounds,
            ClientOrigin: frame?.ClientOrigin ?? default,
            ClientWidth: frame?.ClientWidth ?? 0,
            ClientHeight: frame?.ClientHeight ?? 0,
            ImageWidth: frame?.ImageWidth ?? 0,
            ImageHeight: frame?.ImageHeight ?? 0,
            Dpi: dpi,
            CapturedAtMs: capturedAt,
            Actionable: frame?.Actionable ?? false,
            CaptureMethod: frame?.CaptureMethod ?? string.Empty,
            Signature: frame?.Signature ?? string.Empty);
    }

    private static ObserveReport BuildReport(
        ObservationSnapshot snapshot,
        FrameInfo? frame,
        bool imagePresent,
        OmissionInfo omitted,
        IReadOnlyList<string> advisories)
    {
        var resolution = snapshot.Resolution switch
        {
            WindowResolution.Exact => "exact",
            WindowResolution.Fallback => "fallback",
            _ => "unavailable",
        };

        ObserveFrameReport? frameReport = null;
        if (frame is not null)
        {
            frameReport = new ObserveFrameReport(
                ImageWidth: frame.ImageWidth,
                ImageHeight: frame.ImageHeight,
                ClientWidth: frame.ClientWidth,
                ClientHeight: frame.ClientHeight,
                ScaleX: frame.ScaleX,
                ScaleY: frame.ScaleY,
                CaptureRegion: frame.CaptureRegion,
                Actionable: frame.Actionable,
                CaptureMethod: frame.CaptureMethod,
                Signature: frame.Signature,
                CapturedAtMs: frame.CapturedAtMs);
        }

        var app = snapshot.App;
        var window = snapshot.Window;
        return new ObserveReport(
            SchemaVersion: ProtocolInfo.ObserveSchemaVersion,
            SessionId: snapshot.SessionId,
            StateId: snapshot.StateId,
            Frame: frameReport,
            Target: new ObserveTargetReport(
                App: new ObserveAppReport(app.AppId, app.Pid, app.Name),
                Window: new ObserveWindowReport(
                    WindowId: window.WindowId,
                    Title: window.Title,
                    Onscreen: window.IsOnscreen,
                    Foreground: window.IsForeground,
                    Bounds: [window.Bounds.Left, window.Bounds.Top, window.Bounds.Right, window.Bounds.Bottom],
                    Dpi: window.Dpi,
                    GeometrySource: window.GeometrySource),
                Resolution: resolution),
            Elements: snapshot.Elements.Select(element => new ObserveElementReport(
                Index: element.Index,
                Kind: element.Kind,
                Title: element.Title,
                Value: element.Value,
                ValueTruncated: element.ValueTruncated,
                Actions: element.Actions.ToArray(),
                Enabled: element.Enabled,
                Selected: element.Selected,
                AutomationId: element.AutomationId,
                BoundsScreenDiag:
                [
                    element.BoundsScreenDiag.Left, element.BoundsScreenDiag.Top,
                    element.BoundsScreenDiag.Right, element.BoundsScreenDiag.Bottom,
                ],
                Depth: element.Depth)).ToList(),
            Omitted: new ObserveOmittedReport(
                Count: omitted.OmittedCount,
                ByKind: omitted.ByKind,
                Reason: omitted.Reason,
                SparseIndices: omitted.SparseIndices),
            Advisories: advisories,
            CapturedAtMs: snapshot.CapturedAtMs);
    }

    private static void AddAdvisory(List<string> advisories, string advisory)
    {
        if (!advisories.Contains(advisory, StringComparer.Ordinal))
        {
            advisories.Add(advisory);
        }
    }

    private static string? FindScreenshotAdvisory(IReadOnlyList<string> advisories)
    {
        foreach (var advisory in advisories)
        {
            if (advisory is Advisories.ScreenshotFailed or Advisories.ScreenshotSuppressed or Advisories.ScreenshotDisabled or Advisories.CaptureDegraded)
            {
                return advisory;
            }
        }
        return null;
    }
}