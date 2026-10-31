using System;
using System.Collections.Generic;
using System.Threading;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Application;

/// <summary>
/// Per business-session state: the single current window state/frame, input gate, rate window and stop flag.
/// Session objects only ever hold DTOs; platform handles stay inside the platform layer.
/// </summary>
public sealed class GoSession : IDisposable
{
    private readonly object _rateLock = new();
    private readonly Queue<long> _actTimestamps = new();

    public GoSession(string sessionId, int epoch, long nowMs)
    {
        SessionId = sessionId;
        Epoch = epoch;
        CreatedAtMs = nowMs;
        LastUsedMs = nowMs;
    }

    public string SessionId { get; }

    public int Epoch { get; set; }

    public long CreatedAtMs { get; }

    public long LastUsedMs { get; private set; }

    public string? CurrentWindowId { get; private set; }

    public ObservationSnapshot? State { get; private set; }

    public FrameBinding? Frame { get; private set; }

    public long ObservedAtMs { get; private set; }

    public SemaphoreSlim InputGate { get; } = new(1, 1);

    public bool SessionStopped { get; private set; }

    public string? StopReason { get; private set; }

    public long? StoppedAtMs { get; private set; }

    public void Touch(long nowMs) => LastUsedMs = nowMs;

    /// <summary>Replace the current state; a screenshot-less observe keeps an existing same-window frame (D8).</summary>
    public void Capture(ObservationSnapshot snapshot, FrameBinding? frame, long nowMs)
    {
        var windowChanged = !string.Equals(CurrentWindowId, snapshot.Window.WindowId, StringComparison.Ordinal);
        CurrentWindowId = snapshot.Window.WindowId;
        State = snapshot;
        ObservedAtMs = nowMs;
        LastUsedMs = nowMs;
        if (frame is not null)
        {
            Frame = frame;
        }
        else if (windowChanged)
        {
            Frame = null;
        }
    }

    public void InvalidateState()
    {
        State = null;
        ObservedAtMs = 0;
    }

    public void InvalidateFrame() => Frame = null;

    /// <summary>Any action that entered dispatch invalidates state and frame immediately.</summary>
    public void InvalidateAfterAct()
    {
        State = null;
        Frame = null;
        ObservedAtMs = 0;
    }

    public void MarkStopped(string? reason, long nowMs)
    {
        SessionStopped = true;
        StopReason = reason;
        StoppedAtMs = nowMs;
        InvalidateAfterAct();
    }

    public void ClearStopped()
    {
        SessionStopped = false;
        StopReason = null;
        StoppedAtMs = null;
    }

    /// <summary>Record one rate-limited action timestamp and prune entries older than one minute.</summary>
    public void RecordAct(long nowMs)
    {
        lock (_rateLock)
        {
            PruneRateLocked(nowMs);
            _actTimestamps.Enqueue(nowMs);
        }
    }

    /// <summary>Returns the retry-after hint when the per-session action rate is exhausted.</summary>
    public bool TryCheckActRate(long nowMs, int perMinute, out long retryAfterMs)
    {
        retryAfterMs = 0;
        lock (_rateLock)
        {
            PruneRateLocked(nowMs);
            if (_actTimestamps.Count < perMinute)
            {
                return true;
            }

            var oldest = _actTimestamps.Peek();
            retryAfterMs = Math.Clamp(60_000 - (nowMs - oldest), 500, 60_000);
            return false;
        }
    }

    private void PruneRateLocked(long nowMs)
    {
        while (_actTimestamps.Count > 0 && nowMs - _actTimestamps.Peek() > 60_000)
        {
            _actTimestamps.Dequeue();
        }
    }

    public void Dispose() => InputGate.Dispose();
}
