using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Tests.TestSupport;

/// <summary>Window/app facts used by every fake (no real Win32/UIA calls in tests).</summary>
public static class TestData
{
    public const string FixtureAppId = "win-exe:111111111111";
    public const string FixtureWindowId = "w-0000000000000064";
    public const string EditorAppId = "win-exe:222222222222";
    public const string EditorWindowA = "w-0000000000000065";
    public const string EditorWindowB = "w-0000000000000066";
    public const long BaseClockMs = 1_700_000_000_000;

    public static readonly RectI WindowBounds = new(100, 100, 900, 700);

    public static WindowInfo Window(
        string windowId,
        string appId,
        string title,
        string className,
        int pid,
        bool foreground,
        bool onscreen = true,
        bool minimized = false) =>
        new(
            WindowId: windowId,
            AppId: appId,
            Title: title,
            ClassName: className,
            Pid: pid,
            IsOnscreen: onscreen,
            IsMinimized: minimized,
            IsForeground: foreground,
            IsEnabled: true,
            Bounds: WindowBounds,
            GeometrySource: "dwm",
            Dpi: 96,
            CapturedAtMs: BaseClockMs);
}

public sealed class FakeWindowCatalog : IWindowCatalog
{
    public List<AppInfo> Apps { get; } = [];

    public List<WindowInfo> Windows { get; } = [];

    public IReadOnlyList<AppInfo> ListApps(ListAppsOptions options) => Apps;

    public IReadOnlyList<WindowInfo> ListWindows(string? appId = null) =>
        string.IsNullOrWhiteSpace(appId)
            ? Windows
            : Windows.Where(window => string.Equals(window.AppId, appId, StringComparison.Ordinal)).ToList();

    public WindowInfo? TryGetWindow(string windowId) =>
        Windows.FirstOrDefault(window => string.Equals(window.WindowId, windowId, StringComparison.Ordinal));

    public bool IsForeground(string windowId) => TryGetWindow(windowId)?.IsForeground ?? false;
}

/// <summary>Fake observation engine: one snapshot per Observe with a fresh state_id (mirrors T2's contract).</summary>
public sealed class FakeObservationEngine : IObservationEngine
{
    private readonly FakeWindowCatalog _catalog;
    private readonly FakeScreenCapture _capture;
    private int _stateCounter = 0x12345678;

    public FakeObservationEngine(FakeWindowCatalog catalog, FakeScreenCapture capture)
    {
        _catalog = catalog;
        _capture = capture;
    }

    public List<ElementNode> Elements { get; } = DefaultElements();

    public long CapturedAtMs { get; set; } = TestData.BaseClockMs;

    public ObserveOptions? LastOptions { get; private set; }

    public int Calls { get; private set; }

    public ObservationSnapshot Observe(ObserveOptions options)
    {
        Calls++;
        LastOptions = options;
        var window = options.WindowId is not null
            ? _catalog.TryGetWindow(options.WindowId) ?? throw new FakePlatformException(ErrorCodes.WindowNotFound)
            : throw new FakePlatformException(ErrorCodes.WindowNotFound);
        var app = _catalog.Apps.First(candidate => string.Equals(candidate.AppId, window.AppId, StringComparison.Ordinal));
        FrameInfo? frame = null;
        if (options.IncludeScreenshot)
        {
            var capture = _capture.Capture(window.WindowId, new CaptureOptions(1024, 200000, "printwindow", true));
            frame = capture.Frame;
        }

        var stateId = "st-" + Interlocked.Increment(ref _stateCounter).ToString("x8");
        return new ObservationSnapshot(
            SchemaVersion: ProtocolInfo.ObserveSchemaVersion,
            SessionId: string.Empty, // T2 leaves this empty; ObservationService overrides it.
            StateId: stateId,
            App: app,
            Window: window,
            Resolution: WindowResolution.Exact,
            Elements: Elements.ToList(),
            Omitted: OmissionInfo.None,
            Frame: frame,
            Advisories: [],
            CapturedAtMs: CapturedAtMs);
    }

    private static List<ElementNode> DefaultElements() =>
    [
        new(0, "window", "Fixture Form", null, false, null, [], true, false, new RectI(100, 100, 900, 700), 0),
        new(1, "button", "Increment", null, false, "btnIncrement", ["invoke"], true, false, new RectI(120, 140, 220, 180), 1),
        new(2, "edit", "Input", "hello", false, "txtInput", ["set_value", "scroll"], true, false, new RectI(120, 200, 420, 240), 1),
        new(3, "text", "Status", "count=0", false, null, [], true, false, new RectI(120, 260, 420, 280), 1),
    ];
}

public sealed class FakeScreenCapture : IScreenCapture
{
    public static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public bool Enabled { get; set; } = true;

    public bool Suppressed { get; set; }

    public FrameInfo? NextFrame { get; set; }

    public int Calls { get; private set; }

    public CaptureResult Capture(string windowId, CaptureOptions options)
    {
        Calls++;
        if (!Enabled)
        {
            return new CaptureResult(null, null, Advisories.ScreenshotFailed, "disabled in test");
        }

        if (Suppressed)
        {
            return new CaptureResult(null, null, Advisories.ScreenshotSuppressed, "sensitive window");
        }

        var frame = NextFrame ?? new FrameInfo(
            FrameId: "fr-1234abcd",
            WindowId: windowId,
            ImageWidth: 800,
            ImageHeight: 600,
            ClientWidth: 800,
            ClientHeight: 600,
            ScaleX: 1,
            ScaleY: 1,
            CaptureRegion: "client",
            Actionable: true,
            CaptureMethod: "printwindow",
            Signature: "nonblank_ok",
            CapturedAtMs: TestData.BaseClockMs,
            WindowBounds: TestData.WindowBounds,
            ClientOrigin: new RectI(100, 100, 900, 700),
            Dpi: 96,
            PngBytes: OnePixelPng);
        return new CaptureResult(frame, frame.PngBytes, null, null);
    }
}

public sealed class FakePlatformException : Exception
{
    public FakePlatformException(string code) : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class FakeElementActionDispatcher : IElementActionDispatcher
{
    public PlatformActionOutcome NextOutcome { get; set; } =
        PlatformActionOutcome.Accepted("uia", false, VerificationInfo.Unverified);

    public Exception? NextException { get; set; }

    public List<(string StateId, int Index, string Op)> Calls { get; } = [];

    public Task<PlatformActionOutcome> ExecuteAsync(string stateId, int elementIndex, ActionPlan plan, CancellationToken ct)
    {
        Calls.Add((stateId, elementIndex, plan.Op));
        if (NextException is not null)
        {
            throw NextException;
        }

        return Task.FromResult(NextOutcome);
    }
}

public sealed class FakeCoordinateActionDispatcher : ICoordinateActionDispatcher
{
    public PlatformActionOutcome NextOutcome { get; set; } =
        PlatformActionOutcome.Accepted("send_input", false, VerificationInfo.Unverified);

    public List<(FrameBinding Frame, int X, int Y, string Op)> Calls { get; } = [];

    public Task<PlatformActionOutcome> ExecuteAsync(FrameBinding frame, int imageX, int imageY, ActionPlan plan, CancellationToken ct)
    {
        Calls.Add((frame, imageX, imageY, plan.Op));
        return Task.FromResult(NextOutcome);
    }
}

public sealed class FakeInputLane : IInputLane
{
    public int Calls { get; private set; }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        Calls++;
        return await work(ct);
    }
}

public sealed class FakeSystemProbe : ISystemProbe
{
    public SystemFacts Facts { get; set; } = new(
        WindowsVersion: "10.0.26300",
        WindowsSessionId: 1,
        IsInteractive: true,
        IsLocked: false,
        Integrity: "medium",
        DpiAwareness: "per_monitor_v2",
        ProcessId: 4242,
        IsRemoteSession: false,
        UptimeMs: 60_000);

    public SystemFacts GetFacts() => Facts;
}

public sealed class FakeAppLauncher : IAppLauncher
{
    public static WindowInfo SampleWindow => TestData.Window(TestData.FixtureWindowId, TestData.FixtureAppId, "Fixture Form", "WindowsForms", 200, true);

    public LaunchResult Result { get; set; } = new(true, 4321, TestData.FixtureAppId, SampleWindow, false, true);

    public Exception? NextException { get; set; }

    public int Calls { get; private set; }

    public LaunchRequest? LastRequest { get; private set; }

    public Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct)
    {
        Calls++;
        LastRequest = request;
        if (NextException is not null)
        {
            throw NextException;
        }

        return Task.FromResult(Result);
    }
}

public sealed class FakeInstalledAppCatalog : IInstalledAppCatalog
{
    public Dictionary<string, InstalledAppInfo> Apps { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = new("notepad", @"C:\Windows\System32\notepad.exe", LaunchTargetKind.Alias, "alias"),
        ["calc"] = new("calc", @"C:\Windows\System32\calc.exe", LaunchTargetKind.Alias, "alias"),
    };

    public InstalledAppResolveResult Resolve(string name) =>
        Apps.TryGetValue((name ?? string.Empty).Trim(), out var app)
            ? InstalledAppResolveResult.Found(app)
            : InstalledAppResolveResult.Missing();
}

public sealed class FakeAuditSink : IAuditSink
{
    private readonly object _lock = new();

    public List<AuditEvent> Events { get; } = [];

    public bool ThrowOnWrite { get; set; }

    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        if (ThrowOnWrite)
        {
            throw new IOException("audit directory unavailable (test)");
        }

        lock (_lock)
        {
            Events.Add(auditEvent);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class FakeClock : IPlatformClock
{
    public long NowMs { get; set; } = TestData.BaseClockMs;

    public void Advance(long milliseconds) => NowMs += milliseconds;
}

public sealed class FakeWindowTheme : IWindowTheme
{
    public bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark) => true;
}

public sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public bool RegisterResult { get; set; } = true;

    public List<(string Id, string Gesture)> Registered { get; } = [];

    public bool TryRegister(string id, string gesture, Action onPressed)
    {
        if (RegisterResult)
        {
            Registered.Add((id, gesture));
        }

        return RegisterResult;
    }

    public void Unregister(string id)
    {
    }
}