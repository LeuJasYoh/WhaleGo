using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Tests.Input;

/// <summary>Geometry snapshot of the fixture window in physical screen pixels.</summary>
public sealed record FixtureGeometry(RectI WindowRect, RectI ClientRect, uint Dpi, string Title, string ClassName);

/// <summary>
/// One shared WinForms fixture process (windows/tests/WhaleGo.Fixtures.WinForms) for all cross-process T3 tests.
/// The fixture is a normal visible window; tests need an interactive desktop (same session as the test host).
/// </summary>
public sealed class InputFixture : IDisposable
{
    private readonly Process? _process;
    private readonly UIA3Automation? _automation;
    private readonly string? _tempDir;

    public InputFixture()
    {
        TestSupport.EnsureProcessDpiAware();

        if (!TestSupport.FixtureExists)
        {
            SkipReason = "fixture exe missing: " + TestSupport.FixtureExe + " (build windows/tests/WhaleGo.Fixtures.WinForms -c Release)";
            return;
        }

        _tempDir = TestSupport.CreateTempDir("fixture");
        var readyFile = Path.Combine(_tempDir, "ready.txt");
        var clickLog = Path.Combine(_tempDir, "clicks.log");
        var startInfo = new ProcessStartInfo(TestSupport.FixtureExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(TestSupport.FixtureExe) ?? string.Empty,
        };
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add("t3input");
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFile);
        startInfo.ArgumentList.Add("--auto-close-ms");
        startInfo.ArgumentList.Add("120000");
        startInfo.ArgumentList.Add("--click-log");
        startInfo.ArgumentList.Add(clickLog);

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            SkipReason = "could not start fixture: " + ex.Message;
            return;
        }

        if (_process is null)
        {
            SkipReason = "fixture process did not start";
            return;
        }

        ClickLogPath = clickLog;
        Pid = _process.Id;
        if (!TestSupport.WaitUntil(() => File.Exists(readyFile) && (TestSupport.TryReadAllText(readyFile)?.Contains("ready=true", StringComparison.Ordinal) ?? false), 25000))
        {
            SkipReason = "fixture did not write its ready file within 25s";
            return;
        }

        var ready = TestSupport.TryReadAllText(readyFile) ?? string.Empty;
        Hwnd = ParseReadyHwnd(ready);
        if (Hwnd == IntPtr.Zero)
        {
            Hwnd = TestSupport.FindTopLevelWindow(Pid);
        }

        if (Hwnd == IntPtr.Zero)
        {
            SkipReason = "fixture window not found";
            return;
        }

        Title = ParseReadyValue(ready, "title") ?? string.Empty;
        ClassName = TestSupport.GetClassNameText(Hwnd);
        _automation = new UIA3Automation();
        if (!TestSupport.WaitUntil(() => TryFind("btnIncrement") is not null, 8000))
        {
            SkipReason = "fixture UIA tree not available (btnIncrement not found)";
            return;
        }

        Available = true;
    }

    public bool Available { get; }

    public string? SkipReason { get; }

    public IntPtr Hwnd { get; }

    public int Pid { get; }

    public string Title { get; } = string.Empty;

    public string ClassName { get; } = string.Empty;

    public string WindowId => Hwnd == IntPtr.Zero ? string.Empty : WindowIdCodec.Format(Hwnd);

    public string? ClickLogPath { get; }

    public UIA3Automation Automation => _automation ?? throw new InvalidOperationException("fixture UIA automation unavailable");

    public FixtureGeometry RefreshGeometry()
    {
        var windowRect = TestSupport.GetWindowScreenRect(Hwnd);
        var clientRect = TestSupport.GetClientScreenRect(Hwnd);
        var dpi = TestSupport.GetDpi(Hwnd);
        return new FixtureGeometry(windowRect, clientRect, dpi, Title, TestSupport.GetClassNameText(Hwnd));
    }

    public WindowInfo CreateWindowInfo()
    {
        var geometry = RefreshGeometry();
        return new WindowInfo(
            WindowId,
            AppId: string.Empty,
            Title: Title,
            ClassName: TestSupport.GetClassNameText(Hwnd),
            Pid: Pid,
            IsOnscreen: true,
            IsMinimized: false,
            IsForeground: WindowIdCodec.TryParse(WindowId, out var hwnd) && hwnd == TestSupport.GetForegroundWindow(),
            IsEnabled: true,
            Bounds: geometry.WindowRect,
            GeometrySource: "win32",
            Dpi: geometry.Dpi,
            CapturedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public AutomationElement? TryFind(string automationId)
    {
        try
        {
            var root = Automation.FromHandle(Hwnd);
            return root?.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId(automationId));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public AutomationElement Find(string automationId, int timeoutMs = 8000)
    {
        AutomationElement? element = null;
        if (!TestSupport.WaitUntil(() => (element = TryFind(automationId)) is not null, timeoutMs))
        {
            throw new InvalidOperationException($"fixture element '{automationId}' not found within {timeoutMs}ms");
        }

        return element!;
    }

    public string ReadName(string automationId) => Find(automationId).Name;

    public string ReadValue(string automationId)
    {
        var element = Find(automationId);
        return element.Patterns.Value.IsSupported
            ? element.Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty
            : element.Name;
    }

    public string ReadStatus() => ReadName("lblStatus");

    public int ReadCounter() => int.Parse(ReadName("lblCounter"), CultureInfo.InvariantCulture);

    public (int Panel, int Target) ReadClickCounts() => TestSupport.ParseClickCounts(ReadName("lblClickCount"));

    public void SetInputText(string text)
    {
        var input = Find("txtInput");
        var pattern = input.Patterns.Value.Pattern;
        pattern.SetValue(text);
    }

    public bool TryActivateFixture()
    {
        var activator = new WindowActivator();
        return activator.TryActivate(Hwnd, 1000);
    }

    public bool IsFixtureForeground()
    {
        var activator = new WindowActivator();
        return activator.IsForeground(Hwnd);
    }

    public void Dispose()
    {
        _automation?.Dispose();
        if (_process is { HasExited: false })
        {
            try
            {
                _ = _process.CloseMainWindow();
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // best effort teardown
            }
        }

        _process?.Dispose();
        if (_tempDir is not null)
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
                // keep logs for debugging
            }
        }
    }

    private static string? ParseReadyValue(string ready, string key)
    {
        var prefix = key + "=";
        foreach (var line in ready.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
    }

    private static IntPtr ParseReadyHwnd(string ready)
    {
        foreach (var line in ready.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("hwnd=0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(line[7..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return new IntPtr(value);
            }
        }

        return IntPtr.Zero;
    }
}

/// <summary>xunit collection: all cross-process T3 tests share one fixture and run sequentially.</summary>
[CollectionDefinition("T3Input")]
public sealed class InputCollection : ICollectionFixture<InputFixture>
{
}