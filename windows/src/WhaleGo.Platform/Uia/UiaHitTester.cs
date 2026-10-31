using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Input;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// Resolves the child HWND + client coordinates for a screen point inside a top-level window even when another
/// window currently occludes the target (the UIA window-domain walk is the secondary path required by PROTOCOL §6).
/// </summary>
public interface IWindowPointResolver
{
    bool TryResolveChildHwnd(WindowInfo window, int screenX, int screenY, out nint childHwnd, out int clientX, out int clientY);
}

/// <summary>
/// Coordinate hit-test (docs/PROTOCOL.md section 6): UIA ElementFromPoint (primary), window-domain element walk
/// (secondary, robust against occlusion), then host classification used only to choose the dispatch path.
/// </summary>
public sealed class UiaHitTester : IUiHitTester, IWindowPointResolver
{
    private const int MaxDomainElements = 2000;

    private readonly UiaEngine _engine;

    public UiaHitTester(UiaEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public HitTestResult HitTest(WindowInfo window, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            return HitTestCore(window, screenX, screenY);
        }
        catch (Exception)
        {
            return new HitTestResult(false, false, HostClass.Unknown, null, null, null);
        }
    }

    /// <summary>Resolves the deepest child HWND at a screen point inside the target window tree.</summary>
    public bool TryResolveChildHwnd(WindowInfo window, int screenX, int screenY, out nint childHwnd, out int clientX, out int clientY)
    {
        ArgumentNullException.ThrowIfNull(window);
        childHwnd = 0;
        clientX = 0;
        clientY = 0;
        try
        {
            var hwnd = ResolveHwnd(window);
            if (hwnd == 0)
            {
                return false;
            }

            // Fast path: nothing overlaps the target at this point.
            var nativeHit = NativeInput.WindowFromPoint(new NativeInput.Point(screenX, screenY));
            if (nativeHit != 0 && (nativeHit == hwnd || NativeInput.IsChild(hwnd, nativeHit)))
            {
                childHwnd = nativeHit;
            }
            else
            {
                childHwnd = ResolveChildFromDomain(hwnd, screenX, screenY);
                if (childHwnd == 0)
                {
                    return false;
                }
            }

            var client = NativeInput.ScreenToClientPoint(childHwnd, screenX, screenY);
            if (client.x is < 0 or > 32767 || client.y is < 0 or > 32767)
            {
                return false;
            }

            clientX = client.x;
            clientY = client.y;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Host classification matrix (v1): WinForms/WPF/WinUI/Notepad/dialogs are deliverable, Chromium/Electron
    /// top-level windows are droppable (coordinate clicks must use the foreground path), everything else unknown.
    /// </summary>
    public static HostClass ClassifyHost(string? className, string? processName, FrameworkType framework)
    {
        var cls = className ?? string.Empty;
        var proc = processName ?? string.Empty;

        if (cls.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "chrome", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "msedge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "chromium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proc, "electron", StringComparison.OrdinalIgnoreCase))
        {
            return HostClass.Droppable;
        }

        if (framework is FrameworkType.WinForms or FrameworkType.Wpf or FrameworkType.Xaml)
        {
            return HostClass.Deliverable;
        }

        if (string.Equals(proc, "notepad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "Notepad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "#32770", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
            cls is "Edit" or "Button" or "ComboBox" or "ListBox" or "Static" or "ScrollBar" or
                "SysListView32" or "SysTreeView32" or "SysTabControl32" or "msctls_trackbar32" or
                "msctls_updown32" or "msctls_progress32" or "ToolbarWindow32" or "rebar_window")
        {
            return HostClass.Deliverable;
        }

        return HostClass.Unknown;
    }

    private HitTestResult HitTestCore(WindowInfo window, int screenX, int screenY)
    {
        var hwnd = ResolveHwnd(window);
        if (hwnd == 0)
        {
            // An unparseable window_id has no verifiable window domain: fail closed.
            return new HitTestResult(false, false, HostClass.Unknown, null, null, null);
        }

        var withinWindow = window.Bounds.Contains(screenX, screenY);
        if (!withinWindow && hwnd != 0)
        {
            var nativeBounds = NativeInput.GetWindowScreenRect(hwnd);
            withinWindow = nativeBounds.Width > 0 && nativeBounds.Height > 0 && nativeBounds.Contains(screenX, screenY);
        }

        var clientRect = hwnd != 0 ? NativeInput.GetClientScreenRect(hwnd) : default;
        var withinClient = clientRect.Width > 0 && clientRect.Height > 0 && clientRect.Contains(screenX, screenY);

        if (!withinWindow)
        {
            return new HitTestResult(false, false, HostClass.Unknown, null, null, null);
        }

        // Primary: UIA ElementFromPoint. It returns the topmost window's element, which may belong to an occluder;
        // in that case fall back to the window-domain walk (secondary) instead of refusing the point.
        AutomationElement? pointElement = _engine.TryFromPoint(screenX, screenY);
        AutomationElement? windowElement = hwnd != 0 ? _engine.TryGetRoot(hwnd) : null;
        if (pointElement is not null && hwnd != 0 && !BelongsToWindow(pointElement, hwnd))
        {
            pointElement = null;
        }

        pointElement ??= FindElementInDomain(windowElement, screenX, screenY);
        var topClass = SafeRead(() => windowElement?.ClassName, window.ClassName) ?? window.ClassName;
        var processName = TryGetProcessName(window.Pid);
        var framework = windowElement is null
            ? FrameworkType.Unknown
            : SafeRead(() => windowElement.FrameworkType, FrameworkType.Unknown);

        var hostClass = ClassifyHost(topClass, processName, framework);
        var controlType = pointElement is null ? null : SafeRead<string?>(() => pointElement.ControlType.ToString(), null);
        var automationId = pointElement is null ? null : SafeRead<string?>(() => pointElement.AutomationId, null);
        var className = pointElement is not null
            ? SafeRead<string?>(() => pointElement.ClassName, null)
            : NativeInput.GetWindowClassName(hwnd);

        return new HitTestResult(true, withinClient, hostClass, controlType, automationId, className);
    }

    private nint ResolveChildFromDomain(nint rootHwnd, int screenX, int screenY)
    {
        var root = _engine.TryGetRoot(rootHwnd);
        if (root is null)
        {
            return rootHwnd;
        }

        nint best = rootHwnd;
        long bestArea = long.MaxValue;
        try
        {
            var elements = root.FindAllDescendants();
            var scanned = 0;
            foreach (var element in elements)
            {
                if (++scanned > MaxDomainElements)
                {
                    break;
                }

                var rect = SafeRead(() => element.BoundingRectangle, Rectangle.Empty);
                if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(screenX, screenY))
                {
                    continue;
                }

                var handle = SafeRead(() => element.Properties.NativeWindowHandle.ValueOrDefault, nint.Zero);
                if (handle == 0)
                {
                    continue;
                }

                long area = (long)rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = handle;
                }
            }
        }
        catch (Exception)
        {
            return rootHwnd;
        }

        return best;
    }

    private AutomationElement? FindElementInDomain(AutomationElement? windowElement, int screenX, int screenY)
    {
        if (windowElement is null)
        {
            return null;
        }

        AutomationElement? best = null;
        long bestArea = long.MaxValue;
        try
        {
            var elements = windowElement.FindAllDescendants();
            var scanned = 0;
            foreach (var element in elements)
            {
                if (++scanned > MaxDomainElements)
                {
                    break;
                }

                var rect = SafeRead(() => element.BoundingRectangle, Rectangle.Empty);
                if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(screenX, screenY))
                {
                    continue;
                }

                long area = (long)rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = element;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return best ?? windowElement;
    }

    private static bool BelongsToWindow(AutomationElement element, nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var current = element;
        for (var i = 0; i < 64 && current is not null; i++)
        {
            var handle = SafeRead(() => current.Properties.NativeWindowHandle.ValueOrDefault, nint.Zero);
            if (handle == hwnd)
            {
                return true;
            }

            current = SafeRead<AutomationElement?>(() => current.Parent, null);
        }

        return false;
    }

    private static nint ResolveHwnd(WindowInfo window) =>
        WindowIdCodec.TryParse(window.WindowId, out var hwnd) ? hwnd : 0;

    private static string? TryGetProcessName(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}