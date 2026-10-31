using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Windows;

/// <summary>Result of window/app target resolution; failures are explicit, never silent.</summary>
public sealed record WindowResolutionResult(
    WindowInfo? Window,
    WindowResolution Resolution,
    string? ErrorCode,
    string? ErrorDetail,
    IReadOnlyList<WindowInfo> Candidates,
    bool MainFallback)
{
    public bool IsResolved => Window is not null;

    public static WindowResolutionResult Exact(WindowInfo window) =>
        new(window, WindowResolution.Exact, null, null, Array.Empty<WindowInfo>(), false);

    public static WindowResolutionResult FallbackTo(WindowInfo window) =>
        new(window, WhaleGo.Contracts.Model.WindowResolution.Fallback, null, null, Array.Empty<WindowInfo>(), true);

    public static WindowResolutionResult Unavailable(string errorCode, string detail, IReadOnlyList<WindowInfo>? candidates = null) =>
        new(null, WhaleGo.Contracts.Model.WindowResolution.Unavailable, errorCode, detail, candidates ?? Array.Empty<WindowInfo>(), false);
}

/// <summary>
/// Target resolution rules (docs/PROTOCOL.md §3.4/§3.5/§3.6, D16):
/// <list type="bullet">
/// <item>window_id → that window only; missing/invalid → WINDOW_NOT_FOUND.</item>
/// <item>app_id → unique window required; 0 windows → WINDOW_NOT_FOUND; &gt;1 → AMBIGUOUS_APP with the candidate
/// window list. Never picks silently among equally valid windows.</item>
/// <item>No target → foreground window as an explicit fallback (Resolution=Fallback, MainFallback=true, the
/// engine adds the main_fallback advisory).</item>
/// <item>Main-window fallback (go_launch app activation) is opt-in only via <see cref="ResolveMainWindow"/>;
/// it also reports Resolution=Fallback/MainFallback=true.</item>
/// </list>
/// </summary>
public sealed class WindowResolver
{
    private readonly IWindowCatalog _catalog;

    public WindowResolver(IWindowCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Resolve an observe target. Exactly one of windowId/appId should be supplied by the tool layer.</summary>
    public WindowResolutionResult Resolve(string? windowId, string? appId)
    {
        var hasWindowId = !string.IsNullOrWhiteSpace(windowId);
        var hasAppId = !string.IsNullOrWhiteSpace(appId);

        if (hasWindowId && hasAppId)
        {
            return WindowResolutionResult.Unavailable(ErrorCodes.InvalidArgument, "window_id and app_id are mutually exclusive.");
        }

        if (hasWindowId)
        {
            var window = _catalog.TryGetWindow(windowId!);
            return window is null
                ? WindowResolutionResult.Unavailable(ErrorCodes.WindowNotFound, $"No such window: {windowId}.")
                : WindowResolutionResult.Exact(window);
        }

        if (hasAppId)
        {
            return ResolveIfUnique(appId!);
        }

        return ResolveForeground();
    }

    /// <summary>Resolve app_id only when it has exactly one live window (AMBIGUOUS_APP otherwise).</summary>
    public WindowResolutionResult ResolveIfUnique(string appId)
    {
        var windows = _catalog.ListWindows(appId);
        if (windows.Count == 0)
        {
            return WindowResolutionResult.Unavailable(ErrorCodes.WindowNotFound, $"No live window for app_id '{appId}'.");
        }

        if (windows.Count > 1)
        {
            var ids = string.Join(", ", windows.Select(w => w.WindowId));
            return WindowResolutionResult.Unavailable(
                ErrorCodes.AmbiguousApp,
                $"app_id '{appId}' has {windows.Count} windows; retry with window_id (one of: {ids}).",
                windows);
        }

        return WindowResolutionResult.Exact(windows[0]);
    }

    /// <summary>Explicit fallback: the current foreground window (never silent; caller must surface main_fallback).</summary>
    public WindowResolutionResult ResolveForeground()
    {
        var windows = _catalog.ListWindows();
        var foreground = windows.FirstOrDefault(w => w.IsForeground) ?? windows.FirstOrDefault();
        return foreground is null
            ? WindowResolutionResult.Unavailable(ErrorCodes.WindowNotFound, "No foreground window is available.")
            : WindowResolutionResult.FallbackTo(foreground);
    }

    /// <summary>
    /// Main-window fallback for app activation (go_launch): a unique window is Exact; multiple windows fall back to
    /// the foreground one (or the first listed) and are explicitly marked Fallback/MainFallback=true.
    /// </summary>
    public WindowResolutionResult ResolveMainWindow(string appId)
    {
        var windows = _catalog.ListWindows(appId);
        if (windows.Count == 0)
        {
            return WindowResolutionResult.Unavailable(ErrorCodes.WindowNotFound, $"No live window for app_id '{appId}'.");
        }

        if (windows.Count == 1)
        {
            return WindowResolutionResult.Exact(windows[0]);
        }

        var chosen = windows.FirstOrDefault(w => w.IsForeground) ?? windows[0];
        return WindowResolutionResult.FallbackTo(chosen) with { Candidates = windows };
    }
}
