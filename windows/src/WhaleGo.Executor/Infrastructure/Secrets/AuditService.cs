using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Infrastructure.Secrets;

/// <summary>Audit write failure (fail-closed for write actions).</summary>
public sealed class AuditUnavailableException : Exception
{
    public AuditUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Audit orchestration: action records are written durably before the tool returns; observe/control records are queued
/// asynchronously with a drop counter surfaced through go_health.audit_dropped.
/// </summary>
public sealed class AuditService : IAsyncDisposable
{
    private readonly ConfigService _config;
    private readonly IAuditSink _sink;
    private readonly IPlatformClock _clock;
    private readonly ILogger<AuditService>? _logger;
    private readonly Channel<AuditEvent> _queue = Channel.CreateUnbounded<AuditEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;
    private long _dropped;

    public AuditService(ConfigService config, IAuditSink sink, IPlatformClock clock, ILogger<AuditService>? logger = null)
    {
        _config = config;
        _sink = sink;
        _clock = clock;
        _logger = logger;
        _consumer = Task.Run(ConsumeAsync);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public string AuditDirectory => ResolveAuditDirectory();

    public long NowMs => _clock.NowMs;

    /// <summary>Fail-closed check before a write action is dispatched; throws AuditUnavailableException.</summary>
    public async ValueTask PreflightActionAsync(string tool, string? sessionId, CancellationToken cancellationToken)
    {
        var config = _config.Config;
        if (!config.AuditEnabled || !config.AuditRequireDurableForActions)
        {
            return;
        }

        try
        {
            if (_sink is JsonlAuditSink jsonl)
            {
                await jsonl.EnsureWritableAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sink.WriteAsync(new AuditEvent(_clock.NowMs, AuditEvent.KindControl, SessionId: sessionId, Tool: tool, Event: "audit_preflight"), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "audit preflight failed");
            throw new AuditUnavailableException("审计写入不可用", ex);
        }
    }

    /// <summary>Durable action record; throws AuditUnavailableException when it cannot be persisted.</summary>
    public async ValueTask WriteActionAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (!_config.Config.AuditEnabled)
        {
            return;
        }

        try
        {
            await _sink.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "durable audit write failed");
            throw new AuditUnavailableException("审计写入不可用", ex);
        }
    }

    public void RecordObserve(AuditEvent auditEvent) => Enqueue(auditEvent);

    public void RecordControl(AuditEvent auditEvent) => Enqueue(auditEvent);

    public IReadOnlyList<string> ReadTail(int lines) =>
        _sink is JsonlAuditSink jsonl ? jsonl.ReadTail(lines) : [];

    private void Enqueue(AuditEvent auditEvent)
    {
        if (!_config.Config.AuditEnabled)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(auditEvent))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var auditEvent in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await _sink.WriteAsync(auditEvent, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _dropped);
                    _logger?.LogDebug(ex, "async audit write dropped");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private string ResolveAuditDirectory() =>
        _sink is JsonlAuditSink jsonl ? jsonl.AuditDirectory : _config.EffectiveAuditDir;

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // ignore during shutdown
        }

        _shutdown.Dispose();
    }
}
