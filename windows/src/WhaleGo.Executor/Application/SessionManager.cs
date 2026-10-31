using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Application;

/// <summary>Outcome of one global controller-lease acquisition (TTL 60s, wait budget policy.controllerWaitMs).</summary>
public sealed record ControllerLeaseResult(
    bool Acquired,
    string? BlockCode,
    bool HeldByOther,
    long RetryAfterMs,
    long WaitMs);

/// <summary>
/// Owns business sessions, the global input lease, the three control states (readonly is config, paused is memory,
/// global stop persists) and per-session rate limits. Recovery is local-only.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    public const long ControllerLeaseTtlMs = 60_000;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, GoSession> _sessions = new(StringComparer.Ordinal);
    private readonly IPlatformClock _clock;
    private readonly ConfigService _config;
    private readonly ControlStateStore _controlState;
    private readonly ILogger<SessionManager>? _logger;
    private readonly int _epoch = 1;
    private readonly Stopwatch _processWatch = Stopwatch.StartNew();

    private bool _paused;
    private string? _pauseReason;
    private long _pausedSinceMs;
    private bool _stopped;
    private string? _stopReason;
    private long _stoppedSinceMs;
    private string? _leaseHolder;
    private long _leaseExpiresAtMs;
    private long _lastCleanupMs;

    public SessionManager(
        IPlatformClock clock,
        ConfigService config,
        ControlStateStore controlState,
        ILogger<SessionManager>? logger = null)
    {
        _clock = clock;
        _config = config;
        _controlState = controlState;
        _logger = logger;
    }

    public int Epoch => _epoch;

    public long NowMs => _clock.NowMs;

    public bool Paused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    public string? PauseReason
    {
        get
        {
            lock (_lock)
            {
                return _pauseReason;
            }
        }
    }

    public long? PausedSinceMs
    {
        get
        {
            lock (_lock)
            {
                return _paused ? _pausedSinceMs : null;
            }
        }
    }

    public bool Stopped
    {
        get
        {
            lock (_lock)
            {
                return _stopped;
            }
        }
    }

    public string? StopReason
    {
        get
        {
            lock (_lock)
            {
                return _stopReason;
            }
        }
    }

    public long? StoppedSinceMs
    {
        get
        {
            lock (_lock)
            {
                return _stopped ? _stoppedSinceMs : null;
            }
        }
    }

    public int Count => _sessions.Count;

    public IReadOnlyCollection<GoSession> SnapshotSessions => _sessions.Values.ToList();

    public GoSession GetOrCreate(string sessionId)
    {
        var id = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        var now = _clock.NowMs;
        CleanupIdle(now);
        var session = _sessions.GetOrAdd(id, key => new GoSession(key, _epoch, now));
        session.Touch(now);
        return session;
    }

    public bool TryGet(string sessionId, out GoSession session) => _sessions.TryGetValue(sessionId, out session!);

    public void RemoveSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        lock (_lock)
        {
            if (string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal))
            {
                _leaseHolder = null;
                _leaseExpiresAtMs = 0;
            }
        }

        session.Dispose();
    }

    /// <summary>True when the session may perform actions (paused/stopped are global, session_stopped is per session).</summary>
    public bool AcceptingActions(GoSession session)
    {
        lock (_lock)
        {
            return !_paused && !_stopped && !session.SessionStopped;
        }
    }

    /// <summary>First control-state error code blocking actions for this session, or null.</summary>
    public string? ActionBlockCode(GoSession session)
    {
        lock (_lock)
        {
            if (_paused)
            {
                return ErrorCodes.Paused;
            }

            if (_stopped || session.SessionStopped)
            {
                return ErrorCodes.SessionStopped;
            }

            return null;
        }
    }

    public void Pause(string? reason)
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _pauseReason = string.IsNullOrWhiteSpace(reason) ? "user_button" : reason;
            _pausedSinceMs = _clock.NowMs;
            ReleaseLeaseLocked();
        }

        InvalidateAllSessions();
        _logger?.LogInformation("control: paused");
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _pauseReason = null;
            _pausedSinceMs = 0;
        }

        _logger?.LogInformation("control: resumed");
    }

    /// <summary>Global kill switch; persisted so a restart stays stopped until local ResetStop.</summary>
    public void Stop(string? reason)
    {
        var now = _clock.NowMs;
        lock (_lock)
        {
            _stopped = true;
            _stopReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason;
            _stoppedSinceMs = now;
            ReleaseLeaseLocked();
        }

        InvalidateAllSessions();
        _controlState.Save(new ControlStateRecord(true, _stopReason, now));
        _logger?.LogWarning("control: global stop engaged");
    }

    public void ResetStop()
    {
        lock (_lock)
        {
            if (!_stopped)
            {
                return;
            }

            _stopped = false;
            _stopReason = null;
            _stoppedSinceMs = 0;
        }

        _controlState.Save(ControlStateRecord.Running);
        _logger?.LogInformation("control: global stop reset locally");
    }

    /// <summary>Model go_stop semantics: stop only this session; observations keep working.</summary>
    public void StopSession(string sessionId, string? reason)
    {
        var session = GetOrCreate(sessionId);
        session.MarkStopped(reason, _clock.NowMs);
        lock (_lock)
        {
            if (string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal))
            {
                ReleaseLeaseLocked();
            }
        }

        _logger?.LogInformation("control: session stopped");
    }

    /// <summary>Startup restore of the persisted global stop (after the kernel is ready).</summary>
    public void RestoreGlobalStop(string? reason, long sinceMs)
    {
        lock (_lock)
        {
            _stopped = true;
            _stopReason = string.IsNullOrWhiteSpace(reason) ? "persisted" : reason;
            _stoppedSinceMs = sinceMs > 0 ? sinceMs : _clock.NowMs;
            ReleaseLeaseLocked();
        }

        InvalidateAllSessions();
    }

    public bool CheckActRate(GoSession session, out long retryAfterMs)
    {
        var limit = _config.Config.ActRatePerMinute;
        return session.TryCheckActRate(_clock.NowMs, limit, out retryAfterMs);
    }

    public async Task<ControllerLeaseResult> AcquireControllerLeaseAsync(string sessionId, int waitMs, CancellationToken cancellationToken)
    {
        var budget = Math.Clamp(waitMs, 0, 10_000);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining;
            lock (_lock)
            {
                if (_paused)
                {
                    return new ControllerLeaseResult(false, ErrorCodes.Paused, false, 0, stopwatch.ElapsedMilliseconds);
                }

                if (_stopped)
                {
                    return new ControllerLeaseResult(false, ErrorCodes.SessionStopped, false, 0, stopwatch.ElapsedMilliseconds);
                }

                var now = _clock.NowMs;
                if (_leaseHolder is null || string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal) || now >= _leaseExpiresAtMs)
                {
                    _leaseHolder = sessionId;
                    _leaseExpiresAtMs = now + ControllerLeaseTtlMs;
                    return new ControllerLeaseResult(true, null, false, 0, stopwatch.ElapsedMilliseconds);
                }

                remaining = Math.Max(1, _leaseExpiresAtMs - now);
            }

            if (stopwatch.ElapsedMilliseconds >= budget)
            {
                long retryAfter;
                lock (_lock)
                {
                    retryAfter = Math.Clamp(_leaseExpiresAtMs - _clock.NowMs, 1, 5000);
                }
                return new ControllerLeaseResult(false, null, true, retryAfter, stopwatch.ElapsedMilliseconds);
            }

            var delayMs = (int)Math.Clamp(Math.Min(remaining, 20), 5, 30);
            if (stopwatch.ElapsedMilliseconds + delayMs > budget)
            {
                delayMs = Math.Max(1, (int)(budget - stopwatch.ElapsedMilliseconds));
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    public void ReleaseControllerLease(string sessionId)
    {
        lock (_lock)
        {
            if (string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal))
            {
                ReleaseLeaseLocked();
            }
        }
    }

    public ControllerStatus? GetControllerStatus(string? sessionId)
    {
        lock (_lock)
        {
            var now = _clock.NowMs;
            if (_leaseHolder is null || now >= _leaseExpiresAtMs)
            {
                return null;
            }

            return new ControllerStatus(
                Held: true,
                Mine: sessionId is not null && string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal),
                ExpiresInMs: Math.Max(0, _leaseExpiresAtMs - now));
        }
    }

    public bool IsLeaseHolder(string sessionId)
    {
        lock (_lock)
        {
            return string.Equals(_leaseHolder, sessionId, StringComparison.Ordinal) && _clock.NowMs < _leaseExpiresAtMs;
        }
    }

    public int InvalidateAllSessions()
    {
        var count = 0;
        foreach (var session in _sessions.Values)
        {
            session.InvalidateAfterAct();
            count++;
        }
        return count;
    }

    private void ReleaseLeaseLocked()
    {
        _leaseHolder = null;
        _leaseExpiresAtMs = 0;
    }

    private void CleanupIdle(long nowMs)
    {
        if (nowMs - _lastCleanupMs < 60_000)
        {
            return;
        }

        _lastCleanupMs = nowMs;
        var idleCutoff = nowMs - (long)SessionIdleTimeout.TotalMilliseconds;
        foreach (var pair in _sessions)
        {
            if (pair.Value.LastUsedMs < idleCutoff && !pair.Value.SessionStopped)
            {
                RemoveSession(pair.Key);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        ReturnLeaseToken();
        return ValueTask.CompletedTask;
    }

    private void ReturnLeaseToken()
    {
        lock (_lock)
        {
            ReleaseLeaseLocked();
        }
    }

    /// <summary>Test/diagnostic helper: real time is used only for the lease wait budget.</summary>
    public long ProcessUptimeMs => _processWatch.ElapsedMilliseconds;
}