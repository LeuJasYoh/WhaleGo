using WhaleGo.Contracts.Abstractions;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Process-wide serialized physical-input lane (docs/ARCHITECTURE.md section 3.9).
/// Only one physical dispatch (SendInput / activation / clipboard) may run at a time.
/// The lane is intentionally not a reentrancy mechanism: nested calls made from the same async
/// flow run inline inside the already-held lane slot instead of dead-locking on the semaphore.
/// </summary>
public sealed class InputLane : IInputLane
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly AsyncLocal<int> _depth = new();

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ct.ThrowIfCancellationRequested();

        if (_depth.Value > 0)
        {
            // Same async flow already owns the lane; run inline instead of dead-locking.
            return await work(ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _depth.Value++;
        try
        {
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            _depth.Value--;
            _gate.Release();
        }
    }

    /// <summary>Runs a synchronous physical-input section on a lane slot.</summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<T>(_ =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(work());
        }, ct);
    }
}
