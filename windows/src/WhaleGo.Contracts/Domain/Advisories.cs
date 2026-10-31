namespace WhaleGo.Contracts.Domain;

/// <summary>Closed advisory set for v1. New values require a protocol version bump.</summary>
public static class Advisories
{
    public const string TreeBudgetExceeded = "tree_budget_exceeded";
    public const string ScreenshotFailed = "screenshot_failed";
    public const string CaptureDegraded = "capture_degraded";
    public const string NotSettled = "not_settled";
    public const string MainFallback = "main_fallback";
    public const string UiaUnavailable = "uia_unavailable";
    public const string ScreenshotDisabled = "screenshot_disabled";
    public const string ScreenshotSuppressed = "screenshot_suppressed";

    public static readonly IReadOnlyList<string> All =
    [
        TreeBudgetExceeded, ScreenshotFailed, CaptureDegraded, NotSettled, MainFallback, UiaUnavailable,
        ScreenshotDisabled, ScreenshotSuppressed,
    ];

    /// <summary>Frame signature closed set.</summary>
    public static readonly IReadOnlyList<string> FrameSignatures = ["nonblank_ok", "blank", "black", "failed"];
}
