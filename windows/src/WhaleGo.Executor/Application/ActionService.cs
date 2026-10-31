using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;

namespace WhaleGo.Executor.Application;

public sealed record ActOutcome(ActReport Report, ActionReceipt Receipt, long LeaseWaitMs);

/// <summary>
/// Single action channel orchestration: validate → control/read-only/rate → freshness → controller lease →
/// audit preflight → dispatch → receipt → durable audit → success or single error channel.
/// </summary>
public sealed class ActionService
{
    private readonly SessionManager _sessions;
    private readonly WindowService _windows;
    private readonly IElementActionDispatcher _elementDispatcher;
    private readonly ICoordinateActionDispatcher _coordinateDispatcher;
    private readonly IInputLane _inputLane;
    private readonly ConfigService _config;
    private readonly AuditService _audit;
    private readonly ISystemProbe _systemProbe;
    private readonly IPlatformClock _clock;
    private readonly ILogger<ActionService>? _logger;

    public ActionService(
        SessionManager sessions,
        WindowService windows,
        IElementActionDispatcher elementDispatcher,
        ICoordinateActionDispatcher coordinateDispatcher,
        IInputLane inputLane,
        ConfigService config,
        AuditService audit,
        ISystemProbe systemProbe,
        IPlatformClock clock,
        ILogger<ActionService>? logger = null)
    {
        _sessions = sessions;
        _windows = windows;
        _elementDispatcher = elementDispatcher;
        _coordinateDispatcher = coordinateDispatcher;
        _inputLane = inputLane;
        _config = config;
        _audit = audit;
        _systemProbe = systemProbe;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ActOutcome> ActAsync(
        string sessionId,
        string? requestId,
        string? op,
        JsonElement target,
        JsonElement? args,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var validation = ActionValidator.Validate(op, target, args);
        if (!validation.Ok)
        {
            throw new WhaleGoToolException(validation.Error!);
        }

        var plan = validation.Plan!;
        var config = _config.Config;
        var session = _sessions.GetOrCreate(sessionId);
        EnsureActionAllowed(session, config);
        CheckRateOrThrow(session);

        var now = _clock.NowMs;
        var currentWindow = _windows.TryGetWindow(session.CurrentWindowId);
        var identity = currentWindow is null ? null : WindowIdentity.From(currentWindow);
        ResolveFreshTarget(session, plan, identity, config, now);

        var lease = await _sessions.AcquireControllerLeaseAsync(sessionId, config.ControllerWaitMs, cancellationToken).ConfigureAwait(false);
        if (!lease.Acquired)
        {
            if (lease.BlockCode is not null)
            {
                throw WhaleGoToolException.FromCode(lease.BlockCode);
            }

            throw WhaleGoToolException.FromCode(
                ErrorCodes.ControllerBusy,
                details: new Dictionary<string, object?>
                {
                    ["controller"] = new Dictionary<string, object?> { ["held"] = true, ["mine"] = false },
                },
                retryAfterMs: lease.RetryAfterMs);
        }

        try
        {
            await _audit.PreflightActionAsync("go_act", sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditUnavailableException)
        {
            _sessions.ReleaseControllerLease(sessionId);
            throw WhaleGoToolException.FromCode(ErrorCodes.AuditUnavailable);
        }

        var outcome = await DispatchAsync(session, plan, cancellationToken).ConfigureAwait(false);
        var receipt = ReceiptFactory.From(sessionId, plan, outcome);
        if (outcome.Sent)
        {
            session.InvalidateAfterAct();
        }

        var errorCode = outcome.Sent && outcome.Uncertain ? ErrorCodes.DispatchUncertain
            : outcome.Sent ? null
            : KnownFailureCode(outcome.FailureCode);
        var auditError = await WriteAuditAsync(session, plan, outcome, receipt, stopwatch.ElapsedMilliseconds, errorCode).ConfigureAwait(false);
        if (auditError is not null)
        {
            if (outcome.Sent)
            {
                _logger?.LogError("action dispatch succeeded but durable audit failed");
                throw new WhaleGoToolException(GoErrorFactory.FromCode(
                    ErrorCodes.AuditUnavailable,
                    receipt: ReceiptFactory.ToWire(receipt),
                    message: "审计写入失败；动作可能已发送，禁止重试，请先重新观察。",
                    dispatchStatus: DispatchStatus.PossiblySent));
            }

            throw WhaleGoToolException.FromCode(ErrorCodes.AuditUnavailable);
        }

        if (receipt.DispatchStatus == DispatchStatus.PossiblySent)
        {
            throw WhaleGoToolException.FromReceipt(receipt, ErrorCodes.DispatchUncertain);
        }

        if (receipt.DispatchStatus == DispatchStatus.NotSent)
        {
            throw WhaleGoToolException.FromReceipt(receipt, KnownFailureCode(outcome.FailureCode));
        }

        var report = new ActReport(
            SchemaVersion: ProtocolInfo.ActSchemaVersion,
            SessionId: sessionId,
            RequestId: NormalizeRequestId(requestId),
            Op: plan.Op,
            ActionReceipt: ReceiptFactory.ToWire(receipt));
        return new ActOutcome(report, receipt, lease.WaitMs);
    }

    private void EnsureActionAllowed(GoSession session, ExecutorConfig config)
    {
        var block = _sessions.ActionBlockCode(session);
        if (block is not null)
        {
            throw WhaleGoToolException.FromCode(block);
        }

        if (config.Readonly)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.ReadonlyMode);
        }

        if (IsLocked())
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.SessionLocked);
        }
    }

    private void CheckRateOrThrow(GoSession session)
    {
        if (!_sessions.CheckActRate(session, out var retryAfterMs))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.RateLimited, retryAfterMs: retryAfterMs);
        }

        session.RecordAct(_clock.NowMs);
    }

    private bool IsLocked()
    {
        try
        {
            return _systemProbe.GetFacts().IsLocked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void ResolveFreshTarget(GoSession session, ActionPlan plan, WindowIdentity? identity, ExecutorConfig config, long now)
    {
        if (plan.Kind == TargetKind.Element)
        {
            var freshness = FreshnessRules.EvaluateState(session.State, plan.StateId, identity, now, config.StateTtlSeconds);
            if (!freshness.IsFresh)
            {
                session.InvalidateState();
                throw WhaleGoToolException.FromCode(freshness.ErrorCode ?? ErrorCodes.StaleState);
            }

            var state = session.State!;
            if (plan.ElementIndex is null || !state.Elements.Any(element => element.Index == plan.ElementIndex.Value))
            {
                session.InvalidateState();
                throw WhaleGoToolException.FromCode(ErrorCodes.ElementUnavailable);
            }
        }
        else
        {
            var freshness = FreshnessRules.EvaluateFrame(session.Frame, identity, now, config.FrameTtlSeconds);
            if (!freshness.IsFresh)
            {
                session.InvalidateFrame();
                throw WhaleGoToolException.FromCode(freshness.ErrorCode ?? ErrorCodes.StaleState);
            }

            var frame = session.Frame!;
            if (plan.X is null || plan.Y is null || plan.X < 0 || plan.Y < 0 ||
                plan.X >= frame.ImageWidth || plan.Y >= frame.ImageHeight)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "坐标越界：必须在最近一次截图的图片像素范围内。");
            }
        }
    }

    private async Task<PlatformActionOutcome> DispatchAsync(GoSession session, ActionPlan plan, CancellationToken cancellationToken)
    {
        await session.InputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A pause/stop/readonly toggle may have landed while this action was queued: re-check before dispatch so
            // queued actions are cancelled (in-flight native calls are the only possibly_sent window).
            var blockCode = _sessions.ActionBlockCode(session);
            if (blockCode is not null)
            {
                return PlatformActionOutcome.NotSent(blockCode);
            }

            if (_config.Config.Readonly)
            {
                return PlatformActionOutcome.NotSent(ErrorCodes.ReadonlyMode);
            }

            if (plan.Kind == TargetKind.Element)
            {
                var stateId = plan.StateId!;
                var index = plan.ElementIndex!.Value;
                return await _inputLane.RunAsync(
                    ct => _elementDispatcher.ExecuteAsync(stateId, index, plan, ct),
                    cancellationToken).ConfigureAwait(false);
            }

            var frame = session.Frame!;
            var x = plan.X!.Value;
            var y = plan.Y!.Value;
            return await _inputLane.RunAsync(
                ct => _coordinateDispatcher.ExecuteAsync(frame, x, y, plan, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = PlatformErrors.TryGetCode(ex);
            _logger?.LogWarning(ex, "dispatch raised an exception");
            if (code is not null && ErrorCatalog.TryGet(code, out var info) && info.DispatchStatus != DispatchStatus.PossiblySent)
            {
                return PlatformActionOutcome.NotSent(code, ex.Message);
            }

            return PlatformActionOutcome.PossiblySent("unknown", false, "exception_after_dispatch");
        }
        finally
        {
            session.InputGate.Release();
        }
    }

    private async Task<string?> WriteAuditAsync(
        GoSession session,
        ActionPlan plan,
        PlatformActionOutcome outcome,
        ActionReceipt receipt,
        long elapsedMs,
        string? errorCode)
    {
        // Input text is never logged in plaintext: only its length + SHA-256 prefix (DESIGN §5.7 / PROTOCOL §11.1).
        var inputText = plan.SetValue ?? plan.TypeText;
        var auditEvent = new AuditEvent(
            TsMs: _clock.NowMs,
            Kind: AuditEvent.KindAction,
            SessionId: session.SessionId,
            Tool: "go_act",
            Op: plan.Op,
            WindowId: session.CurrentWindowId,
            TargetKind: plan.Kind == TargetKind.Element ? "element" : "coordinate",
            ActionSent: receipt.ActionSent,
            DispatchStatus: receipt.DispatchStatus,
            Method: outcome.Method,
            Promoted: outcome.Escalated,
            Verification: receipt.Verification.Status,
            ElapsedMs: elapsedMs,
            ErrorCode: errorCode,
            Event: "act",
            TextLength: inputText is null ? null : TextUtil.RuneCount(inputText),
            TextSha256Prefix: inputText is null ? null : TextUtil.ShortSha256(inputText));

        try
        {
            await _audit.WriteActionAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (AuditUnavailableException)
        {
            return ErrorCodes.AuditUnavailable;
        }
    }

    private static string KnownFailureCode(string? failureCode)
    {
        if (failureCode is not null && ErrorCatalog.TryGet(failureCode, out _))
        {
            return failureCode;
        }

        return ErrorCodes.Internal;
    }

    private static string NormalizeRequestId(string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && requestId.Length <= 64 &&
            requestId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
        {
            return requestId;
        }

        return "req-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }
}