using WhaleGo.Contracts.Abstractions;

namespace WhaleGo.Platform;

/// <summary>Default wall clock for platform/application services (tests inject their own).</summary>
public sealed class PlatformClock : IPlatformClock
{
    public static readonly PlatformClock Instance = new();
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
