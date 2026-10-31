using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Apps;

/// <summary>
/// Launch allowlist injected by the composition root (go_launch only accepts names from this list;
/// docs/PROTOCOL.md section 3.8). The Executor/ConfigStore owns loading; Platform only executes.
/// </summary>
public sealed class LaunchAllowlistOptions
{
    public LaunchAllowlistOptions(IReadOnlyList<LaunchAllowlistEntry>? entries = null) =>
        Entries = entries ?? Array.Empty<LaunchAllowlistEntry>();

    public IReadOnlyList<LaunchAllowlistEntry> Entries { get; }

    public static LaunchAllowlistOptions Empty { get; } = new();
}
