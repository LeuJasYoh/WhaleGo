using System.Diagnostics;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Capture;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Observation;

/// <summary>
/// Cross-process acceptance against the T6 WinForms fixture (optional dependency: skipped silently when the fixture
/// Release build output is absent). Verifies enumeration, UIA tree, password masking, PrintWindow capture and handles.
/// </summary>
public sealed class FixtureExeTests
{
    [Fact]
    public void ObservesAndCapturesExternalWinFormsFixture()
    {
        var repoRoot = FindRepoRoot();
        var fixtureExe = Path.Combine(
            repoRoot,
            "windows",
            "tests",
            "WhaleGo.Fixtures.WinForms",
            "bin",
            "Release",
            "net10.0-windows",
            "WhaleGo.Fixtures.WinForms.exe");
        if (!File.Exists(fixtureExe))
        {
            // Optional integration dependency (T6 build output); the in-process STA harness covers the same ground.
            return;
        }

        var suffix = "t2obs-" + Guid.NewGuid().ToString("N")[..8];
        var expectedTitle = "WhaleGo Fixture - " + suffix;
        var tempDir = Path.Combine(repoRoot, ".tmp", "tg-platform-obs", "fixture-" + suffix);
        Directory.CreateDirectory(tempDir);
        var readyFile = Path.Combine(tempDir, "ready.txt");
        Process? process = null;
        UiaEngine? uia = null;
        try
        {
            var startInfo = new ProcessStartInfo(fixtureExe)
            {
                UseShellExecute = false,
                WorkingDirectory = repoRoot,
            };
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(suffix);
            startInfo.ArgumentList.Add("--ready-file");
            startInfo.ArgumentList.Add(readyFile);
            startInfo.ArgumentList.Add("--auto-close-ms");
            startInfo.ArgumentList.Add("60000");
            process = Process.Start(startInfo);
            Assert.NotNull(process);
            Assert.True(WaitForFile(readyFile, TimeSpan.FromSeconds(30)), "fixture ready file was not written: " + fixtureExe);

            var clock = PlatformClock.Instance;
            var redactor = new Redactor();
            uia = new UiaEngine();
            var catalog = new WindowCatalog(clock, redactor, uia);
            var resolver = new WindowResolver(catalog);
            var treeBuilder = new UiaTreeBuilder(uia);
            var store = new StateHandleStore(clock);
            var capture = new PrintWindowCapture(catalog, redactor, clock);
            var engine = new UiaObservationEngine(catalog, resolver, uia, treeBuilder, capture, store, clock, redactor, new ObservationEngineOptions());

            var window = WaitForWindow(catalog, expectedTitle, TimeSpan.FromSeconds(20));
            Assert.Matches("^w-[0-9A-F]{12}$", window.WindowId);
            Assert.Equal(process.Id, window.Pid);
            Assert.StartsWith("win-exe:", window.AppId);
            Assert.Contains("WindowsForms10.", window.ClassName, StringComparison.Ordinal);

            var snapshot = engine.Observe(new ObserveOptions(window.WindowId, null, IncludeScreenshot: true, SettleMs: 100, MaxElements: 300, MaxValueChars: 48));
            Assert.Equal(WindowResolution.Exact, snapshot.Resolution);
            Assert.Equal(window.WindowId, snapshot.Window.WindowId);

            var increment = snapshot.Elements.FirstOrDefault(e => string.Equals(e.Title, "Increment", StringComparison.Ordinal));
            Assert.NotNull(increment);
            Assert.Equal("button", increment.Kind);
            Assert.Contains("invoke", increment.Actions);

            Assert.Contains(snapshot.Elements, e => e.Kind == "listitem" && e.Title == "A");
            Assert.Contains(snapshot.Elements, e => e.Kind == "checkbox" && e.Title == "Toggle");
            Assert.Contains(snapshot.Elements, e => e.Kind == "button" && e.Title == "Disabled" && !e.Enabled);

            var status = snapshot.Elements.FirstOrDefault(e => e.Title is not null && e.Title.StartsWith("counter=0;check=off", StringComparison.Ordinal));
            Assert.NotNull(status);
            Assert.Contains("suffix=" + suffix, status.Title);

            var edits = snapshot.Elements.Where(e => e.Kind == "edit").ToList();
            var masked = edits
                .Where(e => store.TryResolve(snapshot.StateId, e.Index, out var handle, out _) && handle!.IsPassword)
                .ToList();
            Assert.Single(masked);
            Assert.Equal("***", masked[0].Value);
            Assert.All(snapshot.Elements, e => Assert.DoesNotContain("secret123", e.Value ?? string.Empty));

            Assert.NotNull(snapshot.Frame);
            var frame = snapshot.Frame!;
            Assert.Equal("nonblank_ok", frame.Signature);
            Assert.True(frame.Actionable, $"advisories={string.Join(',', snapshot.Advisories)}");
            Assert.NotNull(frame.PngBytes);
            Assert.Equal(0x89, frame.PngBytes![0]);
            Assert.True(frame.ImageWidth <= 1024);
            Assert.True(store.TryGetWindow(snapshot.StateId, out _));
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // process may already have exited
                }

                process.Dispose();
            }

            uia?.Dispose();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // temp cleanup is best effort
            }
        }
    }

    private static WindowInfo WaitForWindow(IWindowCatalog catalog, string exactTitle, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var match = catalog.ListWindows().FirstOrDefault(w => string.Equals(w.Title, exactTitle, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            Thread.Sleep(100);
        }

        var titles = string.Join(" | ", catalog.ListWindows().Select(w => w.Title));
        throw new Xunit.Sdk.XunitException($"Fixture window '{exactTitle}' was not enumerated. Enumerated: {titles}");
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // file may be mid-write
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "windows", "src", "WhaleGo.Platform")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
