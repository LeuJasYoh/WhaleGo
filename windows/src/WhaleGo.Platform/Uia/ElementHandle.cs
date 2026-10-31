using FlaUI.Core.AutomationElements;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// Platform-internal handle for one observed element: the live FlaUI element plus the identity facts that were true
/// when it was observed. Kept public only so sibling files inside WhaleGo.Platform can pass it around — it must NOT be
/// exposed through the Executor/GUI or the wire. Resolution/lifetime is owned by <see cref="WhaleGo.Platform.StateHandleStore"/>.
/// </summary>
public sealed class ElementHandle
{
    public ElementHandle(
        AutomationElement element,
        int index,
        string kind,
        string? title,
        string? automationId,
        string? className,
        string windowId,
        int windowPid,
        IntPtr hwnd,
        bool isPassword,
        long capturedAtMs)
    {
        Element = element ?? throw new ArgumentNullException(nameof(element));
        Index = index;
        Kind = kind;
        Title = title;
        AutomationId = automationId;
        ClassName = className;
        WindowId = windowId;
        WindowPid = windowPid;
        Hwnd = hwnd;
        IsPassword = isPassword;
        CapturedAtMs = capturedAtMs;
    }

    /// <summary>Live UIA element (UIA3). Use with try/catch — it can throw ElementNotAvailableException when stale.</summary>
    public AutomationElement Element { get; }

    public int Index { get; }

    public string Kind { get; }

    public string? Title { get; }

    public string? AutomationId { get; }

    public string? ClassName { get; }

    public string WindowId { get; }

    public int WindowPid { get; }

    /// <summary>Top-level window HWND that owns this element (from the WindowInfo locator; re-validate liveness).</summary>
    public IntPtr Hwnd { get; }

    public bool IsPassword { get; }

    public long CapturedAtMs { get; }
}
