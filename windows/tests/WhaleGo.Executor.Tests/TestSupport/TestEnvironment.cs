using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Host;
using WhaleGo.Executor.Infrastructure;

namespace WhaleGo.Executor.Tests.TestSupport;

/// <summary>Starts a real executor host (ephemeral port, temp data dir, fake platform services) and provides MCP clients.</summary>
public sealed class TestEnvironment : IAsyncDisposable
{
    public const string DefaultAuthToken = "wg_0123456789abcdef0123456789abcdef";
    public const string SessionA = "wg-aaaaaaaaaaaaaaaa";
    public const string SessionB = "wg-bbbbbbbbbbbbbbbb";
    public const string SessionC = "wg-cccccccccccccccc";

    private readonly List<McpClient> _clients = [];
    private readonly List<HttpClient> _httpClients = [];
    private bool _deleteDataDir = true;
    private bool _disposed;

    private TestEnvironment(string dataDir)
    {
        DataDir = dataDir;
    }

    public ExecutorHostInstance Host { get; private set; } = null!;

    public TestPlatform Platform { get; } = new();

    public FakeClock Clock { get; } = new();

    public FakeAuditSink Audit { get; } = new();

    public FakeSystemProbe Probe => Platform.Probe;

    public FakeElementActionDispatcher ElementDispatcher => Platform.ElementDispatcher;

    public FakeCoordinateActionDispatcher CoordinateDispatcher => Platform.CoordinateDispatcher;

    public FakeAppLauncher Launcher => Platform.Launcher;

    public FakeInstalledAppCatalog InstalledApps => Platform.InstalledApps;

    public FakeScreenCapture Capture => Platform.Capture;

    public FakeObservationEngine Engine => Platform.Engine;

    public string DataDir { get; }

    public string AuthToken { get; set; } = DefaultAuthToken;

    public string ConfigPath => Host.ConfigPath;

    public string SecretsPath => Host.SecretsPath;

    public string McpEndpoint => Host.McpEndpoint ?? throw new InvalidOperationException("MCP listener not started");

    public string BaseUrl => Host.BaseUrl ?? throw new InvalidOperationException("MCP listener not started");

    public string ControlStatePath => Path.Combine(DataDir, "control-state.json");

    public static string ContractPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth < 10 && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, "contracts", "whalego.tools.v1.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("contracts/whalego.tools.v1.json not found relative to test output");
        }
    }

    public static async Task<TestEnvironment> StartAsync(
        Action<TestPlatform>? configurePlatform = null,
        string? configJson = null,
        Action<IServiceCollection>? configureServices = null,
        string? dataDir = null,
        bool startMcp = true,
        string? authToken = null,
        bool useFakeAudit = true,
        bool deleteDataDir = true)
    {
        var directory = dataDir ?? Path.Combine(Path.GetTempPath(), "whalego-executor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var environment = new TestEnvironment(directory)
        {
            AuthToken = authToken ?? DefaultAuthToken,
            _deleteDataDir = deleteDataDir,
        };
        configurePlatform?.Invoke(environment.Platform);

        var options = new ExecutorHostOptions
        {
            Headless = true,
            NoHotkey = true,
            StartMcp = startMcp,
            DataDirectory = directory,
            AuthToken = environment.AuthToken,
            ConfigJson = configJson,
            ContractPath = ContractPath,
            Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            QuietLogging = true,
            ListenAddress = startMcp ? "127.0.0.1" : null,
            ListenPort = startMcp ? 0 : null,
            ConfigureServices = services =>
            {
                services.AddSingleton<IWindowCatalog>(environment.Platform.Catalog);
                services.AddSingleton<IObservationEngine>(environment.Platform.Engine);
                services.AddSingleton<IScreenCapture>(environment.Platform.Capture);
                services.AddSingleton<IElementActionDispatcher>(environment.Platform.ElementDispatcher);
                services.AddSingleton<ICoordinateActionDispatcher>(environment.Platform.CoordinateDispatcher);
                services.AddSingleton<IInputLane>(new FakeInputLane());
                services.AddSingleton<ISystemProbe>(environment.Platform.Probe);
                services.AddSingleton<IPlatformClock>(environment.Clock);
                services.AddSingleton<IAppLauncher>(environment.Platform.Launcher);
                services.AddSingleton<IInstalledAppCatalog>(environment.Platform.InstalledApps);
                services.AddSingleton<IWindowTheme>(environment.Platform.Theme);
                services.AddSingleton<IHotkeyRegistrar>(environment.Platform.Hotkeys);
                if (useFakeAudit)
                {
                    services.AddSingleton<IAuditSink>(environment.Audit);
                }

                configureServices?.Invoke(services);
            },
        };

        environment.Host = await ExecutorHost.StartAsync(options);
        return environment;
    }

    public IExecutorControl Control => Host.Control;

    public async Task<McpClient> ConnectAsync(string? sessionHeader = null, string? token = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + (token ?? AuthToken),
        };
        if (!string.IsNullOrWhiteSpace(sessionHeader))
        {
            headers["X-WhaleGo-Session-Id"] = sessionHeader;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(McpEndpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
        });
        var client = await McpClient.CreateAsync(transport);
        _clients.Add(client);
        return client;
    }

    public HttpClient RawHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        _httpClients.Add(client);
        return client;
    }

    public Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null) =>
        client.CallToolAsync(tool, arguments ?? new Dictionary<string, object?>()).AsTask();

    public async Task<CallToolResult> ObserveAsync(McpClient client, bool screenshot = false, string? hiddenSession = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["window_id"] = TestData.FixtureWindowId,
            ["include_screenshot"] = screenshot,
        };
        if (hiddenSession is not null)
        {
            arguments["session_id"] = hiddenSession;
        }

        return await CallAsync(client, "go_observe", arguments);
    }

    public async Task<string> ObserveStateIdAsync(McpClient client, bool screenshot = false, string? hiddenSession = null)
    {
        var result = await ObserveAsync(client, screenshot, hiddenSession);
        Assert.False(result.IsError == true, "observe failed: " + ToolResultsTest.FirstText(result));
        return ToolResultsTest.StateId(result);
    }

    public Task<CallToolResult> ActElementAsync(McpClient client, string op, string stateId, int index, Dictionary<string, object?>? args = null, string? hiddenSession = null)
    {
        var target = new Dictionary<string, object?>
        {
            ["kind"] = "element",
            ["index"] = index,
            ["state_id"] = stateId,
        };
        var arguments = new Dictionary<string, object?>
        {
            ["op"] = op,
            ["target"] = target,
        };
        if (args is not null)
        {
            arguments["args"] = args;
        }

        if (hiddenSession is not null)
        {
            arguments["session_id"] = hiddenSession;
        }

        return CallAsync(client, "go_act", arguments);
    }

    public Task<CallToolResult> ActCoordinateAsync(McpClient client, string op, int x, int y, Dictionary<string, object?>? args = null)
    {
        var target = new Dictionary<string, object?>
        {
            ["kind"] = "coordinate",
            ["x"] = x,
            ["y"] = y,
        };
        var arguments = new Dictionary<string, object?>
        {
            ["op"] = op,
            ["target"] = target,
        };
        if (args is not null)
        {
            arguments["args"] = args;
        }

        return CallAsync(client, "go_act", arguments);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // best effort
            }
        }

        foreach (var client in _httpClients)
        {
            client.Dispose();
        }

        try
        {
            await Host.DisposeAsync();
        }
        catch (Exception)
        {
            // best effort
        }

        if (_deleteDataDir)
        {
            TryDeleteDirectory(DataDir);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Test platform bundle: fake catalog/engine/capture/dispatchers/launcher/probe/theme/hotkeys.</summary>
public sealed class TestPlatform
{
    public FakeWindowCatalog Catalog { get; } = new();

    public FakeScreenCapture Capture { get; } = new();

    public FakeObservationEngine Engine { get; }

    public FakeElementActionDispatcher ElementDispatcher { get; } = new();

    public FakeCoordinateActionDispatcher CoordinateDispatcher { get; } = new();

    public FakeAppLauncher Launcher { get; } = new();

    public FakeInstalledAppCatalog InstalledApps { get; } = new();

    public FakeSystemProbe Probe { get; } = new();

    public FakeWindowTheme Theme { get; } = new();

    public FakeHotkeyRegistrar Hotkeys { get; } = new();

    public TestPlatform()
    {
        Seed();
        Engine = new FakeObservationEngine(Catalog, Capture);
    }

    private void Seed()
    {
        var fixture = TestData.Window(TestData.FixtureWindowId, TestData.FixtureAppId, "Fixture Form", "WindowsForms10.Window.8.app.0.0", 200, foreground: true);
        var editorA = TestData.Window(TestData.EditorWindowA, TestData.EditorAppId, "Editor A", "Notepad", 201, foreground: false);
        var editorB = TestData.Window(TestData.EditorWindowB, TestData.EditorAppId, "Editor B", "Notepad", 201, foreground: false, minimized: true);
        Catalog.Windows.AddRange([fixture, editorA, editorB]);
        Catalog.Apps.Add(new AppInfo(TestData.FixtureAppId, "WhaleGo Fixture", 200, "C:/fixtures/WhaleGo.Fixtures.WinForms.exe", null, false, true, [fixture]));
        Catalog.Apps.Add(new AppInfo(TestData.EditorAppId, "Editor", 201, "C:/Windows/System32/notepad.exe", null, false, false, [editorA, editorB]));
    }
}

public static class ToolResultsTest
{
    public static IReadOnlyList<TextContentBlock> TextBlocks(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().ToList();

    public static string FirstText(CallToolResult result) =>
        TextBlocks(result).FirstOrDefault()?.Text ?? string.Empty;

    public static string SecondText(CallToolResult result) =>
        TextBlocks(result).Skip(1).FirstOrDefault()?.Text ?? string.Empty;

    public static bool HasImage(CallToolResult result) => result.Content.Any(block => block is ImageContentBlock);

    public static JsonNode Report(CallToolResult result) =>
        JsonNode.Parse(SecondText(result)) ?? throw new InvalidOperationException("missing JSON payload");

    public static JsonNode ErrorObject(CallToolResult result) =>
        JsonNode.Parse(SecondText(result)) ?? throw new InvalidOperationException("missing error JSON");

    public static string StateId(CallToolResult result)
    {
        var match = System.Text.RegularExpressions.Regex.Match(FirstText(result), "state_id=(st-[0-9a-f]{8})");
        if (!match.Success)
        {
            throw new InvalidOperationException("observe text has no state_id: " + FirstText(result));
        }

        return match.Groups[1].Value;
    }
}