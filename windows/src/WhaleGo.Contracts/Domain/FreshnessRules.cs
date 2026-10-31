using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

public enum FreshnessVerdict
{
    Fresh = 0,
    Expired = 1,
    WindowMissing = 2,
    WindowChanged = 3,
    NotActionable = 4,
}

public sealed record FreshnessResult(FreshnessVerdict Verdict, string? ErrorCode)
{
    public bool IsFresh => Verdict == FreshnessVerdict.Fresh;

    public static readonly FreshnessResult Fresh = new(FreshnessVerdict.Fresh, null);
}

/// <summary>Pure freshness rules (docs/PROTOCOL.md section 5.2): fail-closed, never auto re-observes.</summary>
public static class FreshnessRules
{
    public static FreshnessResult EvaluateState(
        ObservationSnapshot? snapshot,
        string? requestedStateId,
        WindowIdentity? currentWindow,
        long nowMs,
        int stateTtlSeconds)
    {
        if (snapshot is null || string.IsNullOrEmpty(requestedStateId) ||
            !string.Equals(snapshot.StateId, requestedStateId, StringComparison.Ordinal))
        {
            return new FreshnessResult(FreshnessVerdict.Expired, ErrorCodes.StaleState);
        }

        if (currentWindow is null)
        {
            return new FreshnessResult(FreshnessVerdict.WindowMissing, ErrorCodes.WindowNotFound);
        }

        if (!SameIdentity(WindowIdentity.From(snapshot.Window), currentWindow))
        {
            return new FreshnessResult(FreshnessVerdict.WindowChanged, ErrorCodes.WindowChanged);
        }

        if (IsExpired(snapshot.CapturedAtMs, nowMs, stateTtlSeconds))
        {
            return new FreshnessResult(FreshnessVerdict.Expired, ErrorCodes.StaleState);
        }

        return FreshnessResult.Fresh;
    }

    public static FreshnessResult EvaluateFrame(
        FrameBinding? frame,
        WindowIdentity? currentWindow,
        long nowMs,
        int frameTtlSeconds)
    {
        if (frame is null)
        {
            return new FreshnessResult(FreshnessVerdict.Expired, ErrorCodes.StaleState);
        }

        if (currentWindow is null)
        {
            return new FreshnessResult(FreshnessVerdict.WindowMissing, ErrorCodes.WindowNotFound);
        }

        if (!string.Equals(frame.WindowId, currentWindow.WindowId, StringComparison.Ordinal) ||
            frame.OwnerPid != currentWindow.OwnerPid ||
            !string.Equals(frame.ClassName, currentWindow.ClassName, StringComparison.Ordinal))
        {
            return new FreshnessResult(FreshnessVerdict.WindowChanged, ErrorCodes.WindowChanged);
        }

        if (IsExpired(frame.CapturedAtMs, nowMs, frameTtlSeconds))
        {
            return new FreshnessResult(FreshnessVerdict.Expired, ErrorCodes.StaleState);
        }

        if (!frame.Actionable)
        {
            return new FreshnessResult(FreshnessVerdict.NotActionable, ErrorCodes.FrameNotActionable);
        }

        if (frame.WindowBounds != currentWindow.Bounds || frame.Dpi != currentWindow.Dpi)
        {
            return new FreshnessResult(FreshnessVerdict.Expired, ErrorCodes.StaleState);
        }

        return FreshnessResult.Fresh;
    }

    public static bool SameIdentity(WindowIdentity left, WindowIdentity right) =>
        string.Equals(left.WindowId, right.WindowId, StringComparison.Ordinal) &&
        left.OwnerPid == right.OwnerPid &&
        string.Equals(left.ClassName, right.ClassName, StringComparison.Ordinal);

    public static bool IsExpired(long capturedAtMs, long nowMs, int ttlSeconds) =>
        ttlSeconds > 0 && nowMs - capturedAtMs > ttlSeconds * 1000L;
}
