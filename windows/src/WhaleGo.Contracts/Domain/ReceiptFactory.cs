using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

/// <summary>Platform outcome -> receipt three-state rules (the single place where possibly_sent is decided).</summary>
public static class ReceiptFactory
{
    public static ActionReceipt From(string sessionId, ActionPlan plan, PlatformActionOutcome outcome)
    {
        DispatchStatus status;
        if (outcome.Sent && (outcome.Uncertain || outcome.FailureCode is not null))
        {
            status = DispatchStatus.PossiblySent;
        }
        else if (outcome.Sent)
        {
            status = DispatchStatus.Accepted;
        }
        else
        {
            status = DispatchStatus.NotSent;
        }

        var retryAction = status == DispatchStatus.NotSent
            && outcome.FailureCode is not null
            && ErrorCatalog.TryGet(outcome.FailureCode, out var info)
            && info.Retry == RetryHint.Retry;

        var verification = VerificationFor(plan, outcome, status);
        return new ActionReceipt(
            sessionId,
            plan.Op,
            status != DispatchStatus.NotSent,
            status,
            retryAction,
            plan.Kind,
            outcome.Escalated,
            verification,
            StateAvailable: false,
            new DispatchInfo(outcome.Method, outcome.Escalated));
    }

    public static ActionReceiptWire ToWire(ActionReceipt receipt) => new(
        receipt.ActionSent,
        receipt.DispatchStatus,
        receipt.RetryAction,
        receipt.TargetKind == TargetKind.Element ? "element" : "coordinate",
        receipt.Escalated,
        receipt.Verification,
        receipt.StateAvailable);

    private static VerificationInfo VerificationFor(ActionPlan plan, PlatformActionOutcome outcome, DispatchStatus status)
    {
        if (status == DispatchStatus.PossiblySent)
        {
            return new VerificationInfo("unverified", outcome.VerificationReason ?? "timeout_after_dispatch");
        }

        if (status == DispatchStatus.NotSent)
        {
            return new VerificationInfo("unverified", outcome.VerificationReason ?? "not_sent");
        }

        var platformStatus = outcome.VerificationStatus;
        var readbackOp = plan.Op is "set_value" or "type";
        if (readbackOp && platformStatus is "verified" or "mismatched")
        {
            return new VerificationInfo(platformStatus, outcome.VerificationReason);
        }

        return new VerificationInfo("unverified", outcome.VerificationReason ?? plan.Op switch
        {
            "click" => "click_no_readback",
            "key" => "key_no_readback",
            "scroll" => "scroll_no_readback",
            _ => "no_readback",
        });
    }
}
