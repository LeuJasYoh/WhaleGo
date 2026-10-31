using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// Observation-engine configuration (populated from executor config by T4; defaults mirror Contracts.Model.ConfigDefaults).
/// Sensitive-window patterns and password masking live on <see cref="Privacy.Redactor"/> so the tree builder and capture
/// share one policy instance.
/// </summary>
public sealed record ObservationEngineOptions
{
    /// <summary>Fallback element cap when ObserveOptions.MaxElements is not positive (limits.maxObserveElements).</summary>
    public int DefaultMaxElements { get; set; } = ConfigDefaults.LimitsMaxObserveElements;

    /// <summary>Text-tree character budget passed to Contracts.Domain.ObservationShaper (limits.maxObserveChars).</summary>
    public int MaxObserveChars { get; set; } = ConfigDefaults.LimitsMaxObserveChars;

    /// <summary>Per-value cap when ObserveOptions.MaxValueChars is not positive.</summary>
    public int DefaultMaxValueChars { get; set; } = 48;

    /// <summary>Structural settle budget when ObserveOptions.SettleMs is not positive (0 = no waiting).</summary>
    public int DefaultSettleMs { get; set; } = 250;

    /// <summary>Executor screenshot.enabled switch (part of the three-way AND).</summary>
    public bool ScreenshotEnabled { get; set; } = ConfigDefaults.ScreenshotEnabled;

    /// <summary>Capture method; v1 only "printwindow".</summary>
    public string ScreenshotMode { get; set; } = ConfigDefaults.ScreenshotMode;

    public int MaxImageWidth { get; set; } = ConfigDefaults.LimitsMaxImageWidth;

    public long MaxImageBytes { get; set; } = ConfigDefaults.LimitsMaxImageBytes;

    /// <summary>Suppress capture for privacy.sensitiveWindows matches (day-1 rule; normally true).</summary>
    public bool SuppressSensitiveWindows { get; set; } = true;

    /// <summary>Interval used by the structural settle sampler.</summary>
    public int SettleSampleIntervalMs { get; set; } = 50;

    /// <summary>Hard UIA node cap for the tree walk (2000 per docs; request max_elements is applied on top by the shaper).</summary>
    public int MaxTreeNodes { get; set; } = UiaTreeBuilder.MaxNodesHardCap;
}
