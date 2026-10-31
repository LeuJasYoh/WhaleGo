using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Tests;

public sealed class ReceiptFactoryTests
{
    private static readonly ActionPlan ElementClick = new("click", TargetKind.Element, 3, "st-8f21cd04");
    private static readonly ActionPlan SetValue = new("set_value", TargetKind.Element, 4, "st-8f21cd04", SetValue: "hello");

    [Fact]
    public void AcceptedClickIsUnverifiedAndNotRetryable()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.Accepted("uia", false, VerificationInfo.Unverified));
        Assert.Equal(DispatchStatus.Accepted, receipt.DispatchStatus);
        Assert.True(receipt.ActionSent);
        Assert.False(receipt.RetryAction);
        Assert.Equal("unverified", receipt.Verification.Status);
        Assert.Equal("click_no_readback", receipt.Verification.Reason);
        Assert.False(receipt.StateAvailable);
    }

    [Fact]
    public void PossiblySentIsNeverRetryAction()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.PossiblySent("send_input", true, "timeout_after_dispatch"));
        Assert.Equal(DispatchStatus.PossiblySent, receipt.DispatchStatus);
        Assert.True(receipt.ActionSent);
        Assert.False(receipt.RetryAction);
        Assert.Equal("unverified", receipt.Verification.Status);
        Assert.Equal("timeout_after_dispatch", receipt.Verification.Reason);
    }

    [Fact]
    public void SentWithFailureCodeIsTreatedAsPossiblySent()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            new PlatformActionOutcome(true, false, "send_input", false, ErrorCodes.Internal, "boom"));
        Assert.Equal(DispatchStatus.PossiblySent, receipt.DispatchStatus);
        Assert.False(receipt.RetryAction);
    }

    [Fact]
    public void NotSentRetryableFailureAllowsRetryAction()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.NotSent(ErrorCodes.ControllerBusy));
        Assert.Equal(DispatchStatus.NotSent, receipt.DispatchStatus);
        Assert.False(receipt.ActionSent);
        Assert.True(receipt.RetryAction);
    }

    [Fact]
    public void NotSentStaleStateIsNotRetryAction()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.NotSent(ErrorCodes.StaleState));
        Assert.Equal(DispatchStatus.NotSent, receipt.DispatchStatus);
        Assert.False(receipt.RetryAction);
    }

    [Fact]
    public void SetValueVerifiedReadback()
    {
        var receipt = ReceiptFactory.From("wg-1", SetValue,
            new PlatformActionOutcome(true, false, "uia", false, null, null, "verified", "readback_equals"));
        Assert.Equal(DispatchStatus.Accepted, receipt.DispatchStatus);
        Assert.Equal("verified", receipt.Verification.Status);
        Assert.Equal("readback_equals", receipt.Verification.Reason);
    }

    [Fact]
    public void SetValueMismatchedKeepsAcceptedButNotRetryable()
    {
        var receipt = ReceiptFactory.From("wg-1", SetValue,
            new PlatformActionOutcome(true, false, "uia", false, null, null, "mismatched", "readback_differs"));
        Assert.Equal(DispatchStatus.Accepted, receipt.DispatchStatus);
        Assert.False(receipt.RetryAction);
        Assert.Equal("mismatched", receipt.Verification.Status);
    }

    [Fact]
    public void WireReceiptUsesSnakeCaseStrings()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.Accepted("uia", false, VerificationInfo.Unverified));
        var wire = ReceiptFactory.ToWire(receipt);
        Assert.Equal("element", wire.TargetKind);
        Assert.Equal(DispatchStatus.Accepted, wire.DispatchStatus);
        var json = WireJson.Serialize(wire);
        Assert.Contains("\"dispatch_status\":\"accepted\"", json);
        Assert.Contains("\"target_kind\":\"element\"", json);
        Assert.Contains("\"state_available\":false", json);
    }

    [Fact]
    public void ApiErrorCarriesFullReceiptAndRetryReobserve()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick,
            PlatformActionOutcome.PossiblySent("send_input", true, "timeout_after_dispatch"));
        var error = GoErrorFactory.FromOutcome(receipt, ErrorCodes.DispatchUncertain);
        Assert.Equal(RetryHint.Reobserve, error.Retry);
        Assert.Equal(DispatchStatus.PossiblySent, error.DispatchStatus);
        Assert.True(error.ActionSent);
        Assert.NotNull(error.ActionReceipt);
        Assert.False(error.ActionReceipt!.RetryAction);
    }

    [Fact]
    public void NotSentErrorIsNotActionSent()
    {
        var receipt = ReceiptFactory.From("wg-1", ElementClick, PlatformActionOutcome.NotSent(ErrorCodes.StaleState));
        var error = GoErrorFactory.FromCode(ErrorCodes.StaleState, receipt: ReceiptFactory.ToWire(receipt));
        Assert.False(error.ActionSent);
        Assert.Equal(RetryHint.Reobserve, error.Retry);
    }

    [Fact]
    public void PossiblySentAlwaysReobserveEvenIfCatalogSaysOtherwise()
    {
        foreach (var code in ErrorCodes.ExecutorCodes)
        {
            var receipt = ReceiptFactory.From("wg-1", ElementClick,
                PlatformActionOutcome.PossiblySent("send_input", true, "timeout_after_dispatch"));
            var error = GoErrorFactory.FromCode(code, receipt: ReceiptFactory.ToWire(receipt));
            Assert.Equal(DispatchStatus.PossiblySent, error.DispatchStatus);
            Assert.Equal(RetryHint.Reobserve, error.Retry);
            Assert.False(error.ActionReceipt!.RetryAction);
        }
    }
}
