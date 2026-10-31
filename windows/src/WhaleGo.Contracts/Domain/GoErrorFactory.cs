using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

/// <summary>Builds model-facing GoError objects and enforces the retry/dispatch invariants.</summary>
public static class GoErrorFactory
{
    public static GoError FromCode(
        string code,
        IReadOnlyDictionary<string, object?>? details = null,
        IReadOnlyDictionary<string, object?>? diagnostic = null,
        ActionReceiptWire? receipt = null,
        long? retryAfterMs = null,
        string? message = null,
        DispatchStatus? dispatchStatus = null)
    {
        var known = ErrorCatalog.TryGet(code, out var info);
        if (!known)
        {
            code = ErrorCodes.Internal;
        }

        var dispatch = dispatchStatus ?? receipt?.DispatchStatus ?? info.DispatchStatus;
        var retry = info.Retry;
        if (dispatch == DispatchStatus.PossiblySent)
        {
            retry = RetryHint.Reobserve;
        }

        var actionSent = receipt?.ActionSent ?? (dispatch != DispatchStatus.NotSent);
        var effectiveRetryAfter = retry == RetryHint.Retry ? retryAfterMs : null;
        return new GoError(
            ProtocolInfo.ErrorSchemaVersion,
            code,
            message ?? info.Message,
            retry,
            actionSent,
            dispatch,
            details,
            diagnostic,
            receipt,
            effectiveRetryAfter);
    }

    /// <summary>Action-type failures always carry the full receipt (not_sent / possibly_sent).</summary>
    public static GoError FromOutcome(ActionReceipt receipt, string code, string? message = null, long? retryAfterMs = null)
    {
        var wire = ReceiptFactory.ToWire(receipt);
        return FromCode(code, receipt: wire, message: message, retryAfterMs: retryAfterMs);
    }

    /// <summary>True when the error means "nothing was sent" and the caller may retry/reobserve per its hint.</summary>
    public static bool IsNotSent(GoError error) => error.DispatchStatus == DispatchStatus.NotSent && !error.ActionSent;
}
