using System.Security.Cryptography;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// Observation engine (IObservationEngine): resolve target window → UIA tree → optional client-area screenshot → build the
/// ObservationSnapshot and register platform handles in <see cref="StateHandleStore"/> for (stateId, index) actions.
/// <para><b>Id generation (internal only; never leaves the executor):</b></para>
/// <list type="bullet">
/// <item><c>state_id = "st-" + 8 lowercase hex</c> from 4 CSPRNG bytes (<see cref="RandomNumberGenerator"/>), e.g. st-1a2b3c4d.
/// A new state id is minted for every observe call (the Application layer replaces the session state).</item>
/// <item><c>frame_id = "fr-" + 8 lowercase hex</c>, same generator; the capture layer also mints one and the engine always
/// re-mints it here. frame_id exists only server-side (logs/diagnostics) and is never sent to the model.</item>
/// <item><c>SessionId</c> is intentionally left empty (""): the Application layer (T4) owns session identity and overrides
/// it with the injected session_id.</item>
/// </list>
/// <para><b>Advisories produced:</b> tree_budget_exceeded, not_settled, uia_unavailable, screenshot_failed,
/// capture_degraded, main_fallback, screenshot_disabled, screenshot_suppressed (closed set in Contracts.Domain.Advisories).
/// Capture policy: when executor screenshot.enabled is false the tree is still returned with screenshot_disabled; sensitive
/// windows suppress the image via the capture layer (screenshot_suppressed).</para>
/// <para><b>Error channel:</b> Contracts IObservationEngine is non-throwing, so target resolution failures throw
/// <see cref="ObservationPlatformException"/> (WINDOW_NOT_FOUND / AMBIGUOUS_APP) for the tool layer to map to GoError.</para>
/// </summary>
public sealed class UiaObservationEngine : IObservationEngine
{
    public const string ObserveSchemaVersion = ProtocolInfo.ObserveSchemaVersion;
    public const string StateIdPrefix = "st-";
    public const string FrameIdPrefix = "fr-";

    private readonly WindowCatalog _catalog;
    private readonly WindowResolver _resolver;
    private readonly UiaEngine _uia;
    private readonly UiaTreeBuilder _treeBuilder;
    private readonly IScreenCapture _capture;
    private readonly StateHandleStore _handles;
    private readonly IPlatformClock _clock;
    private readonly Redactor _redactor;
    private readonly ObservationEngineOptions _options;

    public UiaObservationEngine(
        WindowCatalog catalog,
        WindowResolver resolver,
        UiaEngine uia,
        UiaTreeBuilder treeBuilder,
        IScreenCapture capture,
        StateHandleStore handles,
        IPlatformClock clock,
        Redactor? redactor = null,
        ObservationEngineOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
        _treeBuilder = treeBuilder ?? throw new ArgumentNullException(nameof(treeBuilder));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _handles = handles ?? throw new ArgumentNullException(nameof(handles));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _redactor = redactor ?? new Redactor();
        _options = options ?? new ObservationEngineOptions();
    }

    public ObservationSnapshot Observe(ObserveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var startedMs = _clock.NowMs;

        var resolved = _resolver.Resolve(options.WindowId, options.AppId);
        if (!resolved.IsResolved || resolved.Window is null)
        {
            throw new ObservationPlatformException(
                resolved.ErrorCode ?? ErrorCodes.WindowNotFound,
                resolved.ErrorDetail ?? "Target window could not be resolved.",
                resolved.Candidates);
        }

        var window = resolved.Window;
        var advisories = new List<string>(6);
        var resolution = resolved.MainFallback || resolved.Resolution == WindowResolution.Fallback
            ? WindowResolution.Fallback
            : WindowResolution.Exact;
        if (resolution == WindowResolution.Fallback)
        {
            advisories.Add(Advisories.MainFallback);
        }

        if (!WindowIdCodec.TryParse(window.WindowId, out var hwnd))
        {
            throw new ObservationPlatformException(ErrorCodes.WindowNotFound, $"window_id '{window.WindowId}' is not a parseable HWND locator.");
        }

        var maxValueChars = options.MaxValueChars > 0
            ? Math.Clamp(options.MaxValueChars, 1, 4000)
            : Math.Clamp(_options.DefaultMaxValueChars, 1, 4000);
        var maxElements = options.MaxElements > 0 ? options.MaxElements : _options.DefaultMaxElements;
        var settleMs = Math.Clamp(options.SettleMs, 0, 2000);

        var settle = WaitForSettled(hwnd, settleMs);
        if (settle == SettleResult.Unstable)
        {
            advisories.Add(Advisories.NotSettled);
        }

        var build = _treeBuilder.Build(
            hwnd,
            maxValueChars,
            _redactor.MaskPasswordFields,
            _options.MaxTreeNodes,
            UiaTreeBuilder.DefaultTimeBudget);
        if (build.UiaUnavailable)
        {
            advisories.Add(Advisories.UiaUnavailable);
        }

        if (build.BudgetExceeded)
        {
            advisories.Add(Advisories.TreeBudgetExceeded);
        }

        if (build.TimedOut && !advisories.Contains(Advisories.NotSettled))
        {
            advisories.Add(Advisories.NotSettled);
        }

        var elementNodes = new List<ElementNode>(build.Nodes.Count);
        foreach (var node in build.Nodes)
        {
            elementNodes.Add(new ElementNode(
                node.Index,
                node.Kind,
                node.Title,
                node.Value,
                node.ValueTruncated,
                node.AutomationId,
                node.Actions,
                node.Enabled,
                node.Selected,
                node.BoundsScreenDiag,
                node.Depth));
        }

        var shape = ObservationShaper.Shape(elementNodes, maxElements, _options.MaxObserveChars);
        if (shape.TreeBudgetExceeded && !advisories.Contains(Advisories.TreeBudgetExceeded))
        {
            advisories.Add(Advisories.TreeBudgetExceeded);
        }

        FrameInfo? frame = null;
        var capturedAtMs = startedMs;
        if (options.IncludeScreenshot)
        {
            if (!_options.ScreenshotEnabled)
            {
                advisories.Add(Advisories.ScreenshotDisabled);
            }
            else
            {
                var captureOptions = new CaptureOptions(
                    _options.MaxImageWidth,
                    _options.MaxImageBytes,
                    _options.ScreenshotMode,
                    _options.SuppressSensitiveWindows);
                var capture = _capture.Capture(window.WindowId, captureOptions);
                if (capture.Frame is null || capture.PngBytes is null)
                {
                    var advisory = capture.FailureAdvisory ?? Advisories.ScreenshotFailed;
                    if (!advisories.Contains(advisory))
                    {
                        advisories.Add(advisory);
                    }
                }
                else
                {
                    frame = capture.Frame with { FrameId = NewId(FrameIdPrefix), WindowId = window.WindowId };
                    if (frame.CapturedAtMs <= 0)
                    {
                        frame = frame with { CapturedAtMs = startedMs };
                    }

                    capturedAtMs = frame.CapturedAtMs;
                    if (!frame.Actionable && !advisories.Contains(Advisories.CaptureDegraded))
                    {
                        advisories.Add(Advisories.CaptureDegraded);
                    }
                }
            }
        }

        var stateId = NewId(StateIdPrefix);
        RegisterHandles(stateId, window, hwnd, build.Nodes, shape.Elements.Count, startedMs);

        var app = _catalog.TryGetApp(window.AppId)
                  ?? new AppInfo(
                      window.AppId,
                      window.Title,
                      window.Pid,
                      null,
                      null,
                      AppIdentity.IsAumidId(window.AppId),
                      window.IsForeground,
                      Array.Empty<WindowInfo>());

        return new ObservationSnapshot(
            ObserveSchemaVersion,
            string.Empty,
            stateId,
            app,
            window,
            resolution,
            shape.Elements,
            shape.Omitted,
            frame,
            advisories,
            capturedAtMs);
    }

    /// <summary>"st-" or "fr-" + 8 lowercase hex characters from 4 CSPRNG bytes.</summary>
    public static string NewId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void RegisterHandles(
        string stateId,
        WindowInfo window,
        IntPtr hwnd,
        IReadOnlyList<UiaRawNode> nodes,
        int keptCount,
        long capturedAtMs)
    {
        var kept = Math.Min(keptCount, nodes.Count);
        var handles = new List<ElementHandle>(kept);
        for (var i = 0; i < kept; i++)
        {
            var node = nodes[i];
            handles.Add(new ElementHandle(
                node.Element,
                node.Index,
                node.Kind,
                node.Title,
                node.AutomationId,
                node.ClassName,
                window.WindowId,
                window.Pid,
                hwnd,
                node.IsPassword,
                capturedAtMs));
        }

        _handles.Register(stateId, window, handles);
    }

    private SettleResult WaitForSettled(IntPtr hwnd, int settleMs)
    {
        if (settleMs <= 0)
        {
            return SettleResult.Stable;
        }

        var interval = Math.Clamp(_options.SettleSampleIntervalMs, 10, 500);
        var deadline = Environment.TickCount64 + settleMs;
        int? lastCount = null;
        while (true)
        {
            var root = _uia.TryGetRoot(hwnd);
            if (root is null)
            {
                return SettleResult.Unavailable;
            }

            var children = _uia.SafeRead(() => root.FindAllChildren());
            if (children is null)
            {
                return SettleResult.Unavailable;
            }

            var count = children.Length;
            if (lastCount == count)
            {
                return SettleResult.Stable;
            }

            lastCount = count;
            if (Environment.TickCount64 >= deadline)
            {
                return SettleResult.Unstable;
            }

            Thread.Sleep(interval);
        }
    }

    private enum SettleResult
    {
        Stable = 0,
        Unstable = 1,
        Unavailable = 2,
    }
}
