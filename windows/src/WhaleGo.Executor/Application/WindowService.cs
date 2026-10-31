using System;
using System.Collections.Generic;
using System.Linq;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Application;

/// <summary>apps/windows queries plus window resolution for observe (never guesses; ambiguous app_id fails closed).</summary>
public sealed class WindowService
{
    private readonly IWindowCatalog _catalog;

    public WindowService(IWindowCatalog catalog)
    {
        _catalog = catalog;
    }

    public AppsReport ListApps(bool includeWindows)
    {
        var apps = _catalog.ListApps(new ListAppsOptions(includeWindows));
        var entries = apps
            .OrderByDescending(app => app.IsForeground)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(MapApp)
            .ToList();
        return new AppsReport(ProtocolInfo.AppsSchemaVersion, entries, 0);
    }

    public WindowsReport ListWindows(string? appId)
    {
        var windows = _catalog.ListWindows(string.IsNullOrWhiteSpace(appId) ? null : appId);
        var entries = windows
            .OrderByDescending(window => window.IsForeground)
            .ThenByDescending(window => window.IsOnscreen)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .Select(MapWindow)
            .ToList();
        return new WindowsReport(ProtocolInfo.WindowsSchemaVersion, entries);
    }

    /// <summary>Resolve go_observe target (window_id wins over app_id; ambiguous app_id → AMBIGUOUS_APP).</summary>
    public WindowInfo ResolveForObserve(string? windowId, string? appId)
    {
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            var window = _catalog.TryGetWindow(windowId.Trim());
            if (window is null)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.WindowNotFound);
            }

            return window;
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            var windows = _catalog.ListWindows(appId.Trim());
            if (windows.Count == 0)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.AppNotFound);
            }

            if (windows.Count > 1)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.AmbiguousApp);
            }

            return windows[0];
        }

        throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "需要 window_id 或 app_id。");
    }

    public WindowInfo? TryGetWindow(string? windowId) =>
        string.IsNullOrWhiteSpace(windowId) ? null : _catalog.TryGetWindow(windowId);

    public IReadOnlyList<WindowInfo> GetAppWindows(string appId) => _catalog.ListWindows(appId);

    public static AppEntry MapApp(AppInfo app)
    {
        var foreground = app.Windows.FirstOrDefault(window => window.IsForeground);
        var recommended = foreground
            ?? app.Windows.FirstOrDefault(window => window.IsOnscreen && !window.IsMinimized)
            ?? app.Windows.FirstOrDefault();
        return new AppEntry(
            AppId: app.AppId,
            Name: app.Name,
            Pid: app.Pid,
            Exe: app.ExecutablePath,
            Aumid: app.AumId,
            IsPackaged: app.IsPackaged,
            Foreground: app.IsForeground,
            WindowsCount: app.Windows.Count,
            RecommendedWindowId: recommended?.WindowId);
    }

    public static WindowEntry MapWindow(WindowInfo window) => new(
        WindowId: window.WindowId,
        AppId: window.AppId,
        Title: window.Title,
        ClassName: window.ClassName,
        Pid: window.Pid,
        Onscreen: window.IsOnscreen,
        Minimized: window.IsMinimized,
        Foreground: window.IsForeground,
        Enabled: window.IsEnabled,
        Bounds: [window.Bounds.Left, window.Bounds.Top, window.Bounds.Right, window.Bounds.Bottom],
        GeometrySource: window.GeometrySource,
        Dpi: window.Dpi,
        CapturedAtMs: window.CapturedAtMs,
        Kind: window.Kind,
        OwnerWindowId: window.OwnerWindowId,
        ParentWindowId: window.ParentWindowId,
        IsModal: window.IsModal);
}