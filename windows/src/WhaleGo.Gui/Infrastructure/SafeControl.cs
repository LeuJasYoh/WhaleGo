using System.Threading.Tasks;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>Outcome of one Isolated kernel call. The UI never blocks on the kernel; every call is bounded.</summary>
public sealed record ControlCallResult<T>(bool Success, bool TimedOut, T? Value, string? ErrorMessage)
{
    public static ControlCallResult<T> Ok(T value) => new(true, false, value, null);

    public static ControlCallResult<T> Timeout() => new(false, true, default, "内核调用超时（超过 1 秒）");

    public static ControlCallResult<T> Failure(string message) => new(false, false, default, message);
}

/// <summary>
/// Single funnel for every IExecutorControl call coming from the GUI: executes off the UI thread, enforces a
/// short timeout (docs/GUI.md section 3.3) and converts all exceptions into a result. Nothing thrown here ever
/// reaches the dispatcher. Note: for mutating calls a timeout means "may still complete later" (Linux-style
/// best effort) — the GUI re-reads status afterwards instead of assuming success.
/// </summary>
public sealed class SafeControl
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    private readonly IExecutorControl _control;
    private readonly TimeSpan _timeout;
    private readonly IAppLogger _logger;

    public SafeControl(IExecutorControl control, TimeSpan? timeout, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(logger);
        _control = control;
        _timeout = timeout ?? DefaultTimeout;
        _logger = logger;
    }

    public IExecutorControl Control => _control;

    public TimeSpan Timeout => _timeout;

    public async Task<ControlCallResult<T>> CallAsync<T>(string operation, Func<IExecutorControl, T> call)
    {
        try
        {
            var value = await Task.Run(() => call(_control)).WaitAsync(_timeout).ConfigureAwait(false);
            return ControlCallResult<T>.Ok(value);
        }
        catch (TimeoutException)
        {
            _logger.Warn("kernel call timed out: " + operation);
            return ControlCallResult<T>.Timeout();
        }
        catch (Exception ex)
        {
            _logger.Error("kernel call failed: " + operation, ex);
            return ControlCallResult<T>.Failure(Describe(ex));
        }
    }

    public Task<ControlCallResult<StatusSnapshot>> GetStatusAsync() =>
        CallAsync("GetStatus", static c => c.GetStatus());

    public Task<ControlCallResult<IReadOnlyList<RecentRequest>>> GetRecentRequestsAsync(int limit) =>
        CallAsync("GetRecentRequests", c => c.GetRecentRequests(limit));

    public Task<ControlCallResult<IReadOnlyDictionary<string, long>>> GetToolCountersAsync() =>
        CallAsync("GetToolCounters", static c => c.GetToolCounters());

    public Task<ControlCallResult<ConfigPatch>> GetConfigAsync() =>
        CallAsync("GetConfig", static c => c.GetConfig());

    public Task<ControlCallResult<IReadOnlyDictionary<string, string>>> GetConfigSourcesAsync() =>
        CallAsync("GetConfigSources", static c => c.GetConfigSources());

    public Task<ControlCallResult<IReadOnlyList<TokenDescriptor>>> GetTokensAsync() =>
        CallAsync("GetTokens", static c => c.GetTokens());

    public Task<ControlCallResult<SelfCheckReport>> RunSelfCheckAsync() =>
        CallAsync("RunSelfCheck", static c => c.RunSelfCheck());

    public Task<ControlCallResult<IReadOnlyList<string>>> ReadAuditTailAsync(int lines) =>
        CallAsync("ReadAuditTail", c => c.ReadAuditTail(lines));

    public Task<ControlCallResult<bool>> PauseAsync(string reason) =>
        CallAsync("Pause", c => { c.Pause(reason); return true; });

    public Task<ControlCallResult<bool>> ResumeAsync() =>
        CallAsync("Resume", c => { c.Resume(); return true; });

    public Task<ControlCallResult<bool>> StopAsync(string reason) =>
        CallAsync("Stop", c => { c.Stop(reason); return true; });

    public Task<ControlCallResult<bool>> ResetStopAsync() =>
        CallAsync("ResetStop", c => { c.ResetStop(); return true; });

    public Task<ControlCallResult<bool>> SetConfigAsync(ConfigPatch patch) =>
        CallAsync("SetConfig", c => { c.SetConfig(patch); return true; });

    public Task<ControlCallResult<string>> RotateTokenAsync(string name) =>
        CallAsync("RotateToken", c => c.RotateToken(name));

    public Task<ControlCallResult<string>> RevealTokenForCopyAsync() =>
        CallAsync("RevealTokenForCopy", static c => c.RevealTokenForCopy());

    public Task<ControlCallResult<bool>> OpenDataFolderAsync() =>
        CallAsync("OpenDataFolder", c => { c.OpenDataFolder(); return true; });

    public Task<ControlCallResult<bool>> RequestRestartAsync() =>
        CallAsync("RequestRestart", c => { c.RequestRestart(); return true; });

    private static string Describe(Exception exception) => exception switch
    {
        TimeoutException => "内核调用超时（超过 1 秒）",
        _ => exception.Message,
    };
}
