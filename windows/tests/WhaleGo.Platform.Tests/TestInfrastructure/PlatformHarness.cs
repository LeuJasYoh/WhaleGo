using System.Diagnostics;
using System.Windows.Forms;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.TestInfrastructure;

/// <summary>
/// Wires the real observation platform against one in-process STA WinForms fixture window (no mocks), with optional
/// privacy patterns / engine options overrides.
/// </summary>
internal sealed class PlatformHarness : IDisposable
{
    private PlatformHarness(
        StaFormHost host,
        string suffix,
        Redactor redactor,
        UiaEngine uia,
        WindowCatalog catalog,
        WindowResolver resolver,
        UiaTreeBuilder treeBuilder,
        StateHandleStore store,
        PrintWindowCapture capture,
        UiaObservationEngine engine,
        WindowInfo window)
    {
        Host = host;
        Suffix = suffix;
        Redactor = redactor;
        Uia = uia;
        Catalog = catalog;
        Resolver = resolver;
        TreeBuilder = treeBuilder;
        Store = store;
        Capture = capture;
        Engine = engine;
        Window = window;
    }

    public StaFormHost Host { get; }

    public string Suffix { get; }

    public string Title => TestFixtureForm.BuildTitle(Suffix);

    public Redactor Redactor { get; }

    public UiaEngine Uia { get; }

    public WindowCatalog Catalog { get; }

    public WindowResolver Resolver { get; }

    public UiaTreeBuilder TreeBuilder { get; }

    public StateHandleStore Store { get; }

    public PrintWindowCapture Capture { get; }

    public UiaObservationEngine Engine { get; }

    public WindowInfo Window { get; }

    public string WindowId => Window.WindowId;

    public static PlatformHarness Start(
        string? suffix = null,
        IEnumerable<string>? sensitivePatterns = null,
        Action<ObservationEngineOptions>? configureOptions = null,
        bool maskPasswordFields = true,
        Action<Form>? customizeForm = null,
        TimeSpan? windowTimeout = null)
    {
        var suffixValue = suffix ?? Guid.NewGuid().ToString("N")[..8];
        var host = StaFormHost.Start(form => TestFixtureForm.Configure(form, suffixValue, customizeForm));

        var clock = PlatformClock.Instance;
        var redactor = new Redactor(sensitivePatterns, maskPasswordFields);
        var uia = new UiaEngine();
        var catalog = new WindowCatalog(clock, redactor, uia);
        var resolver = new WindowResolver(catalog);
        var treeBuilder = new UiaTreeBuilder(uia);
        var store = new StateHandleStore(clock);
        var capture = new PrintWindowCapture(catalog, redactor, clock);
        var options = new ObservationEngineOptions();
        configureOptions?.Invoke(options);
        var engine = new UiaObservationEngine(catalog, resolver, uia, treeBuilder, capture, store, clock, redactor, options);

        var window = WaitForWindow(catalog, host.Title, windowTimeout ?? TimeSpan.FromSeconds(20));
        return new PlatformHarness(host, suffixValue, redactor, uia, catalog, resolver, treeBuilder, store, capture, engine, window);
    }

    public ObservationSnapshot Observe(
        bool includeScreenshot = false,
        int maxElements = 300,
        int maxValueChars = 48,
        int settleMs = 0) =>
        Engine.Observe(new ObserveOptions(Window.WindowId, null, includeScreenshot, settleMs, maxElements, maxValueChars));

    public static WindowInfo WaitForWindow(IWindowCatalog catalog, string exactTitle, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var match = catalog.ListWindows().FirstOrDefault(w => string.Equals(w.Title, exactTitle, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }
            catch
            {
                // enumeration may transiently fail while the form is being created
            }

            Thread.Sleep(50);
        }

        var titles = string.Join(" | ", catalog.ListWindows().Select(w => w.Title));
        throw new TimeoutException($"Window '{exactTitle}' was not enumerated within {timeout.TotalSeconds:0}s. Enumerated: {titles}");
    }

    public void Dispose()
    {
        Uia.Dispose();
        Host.Dispose();
    }
}
