using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Executor.Infrastructure;

namespace WhaleGo.Executor.Host;

/// <summary>A started executor host: the WebApplication kernel plus the resolved control surface and test metadata.</summary>
public sealed class ExecutorHostInstance : IAsyncDisposable
{
    private readonly ExecutorHostOptions _options;
    private bool _disposed;

    internal ExecutorHostInstance(WebApplication app, ExecutorPaths paths, ExecutorHostOptions options, string? authToken)
    {
        App = app;
        Paths = paths;
        _options = options;
        AuthToken = authToken;
    }

    public WebApplication App { get; }

    public IServiceProvider Services => App.Services;

    public ExecutorPaths Paths { get; }

    public ExecutorHostOptions Options => _options;

    public IExecutorControl Control => (IExecutorControl)(Services.GetService(typeof(IExecutorControl))
        ?? throw new InvalidOperationException("IExecutorControl is not registered"));

    public string? AuthToken { get; }

    public int ActualPort { get; private set; }

    public string? BaseUrl { get; private set; }

    public string? McpEndpoint => BaseUrl is null ? null : BaseUrl.TrimEnd('/') + "/mcp";

    public string DataDirectory => Paths.DataRoot;

    public string ConfigPath => Paths.ConfigPath;

    public string SecretsPath => Paths.SecretsPath;

    internal void SetListening(string? url, int port)
    {
        BaseUrl = url;
        ActualPort = port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            await App.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // shutting down best-effort
        }

        await App.DisposeAsync().ConfigureAwait(false);
    }
}