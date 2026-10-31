using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using WhaleGo.Contracts.Abstractions;

namespace WhaleGo.Platform.System;

/// <summary>
/// Global hotkey registrar (Contracts port, docs/GUI.md section 9.2 / D22).
/// Owns one dedicated message-only window on a dedicated background thread; GUI/Executor never P/Invoke.
/// RegisterHotKey is thread-affine, so every register/unregister is marshalled onto that window thread.
/// Registration failures (conflict/invalid gesture) return false, never throw and never auto-swap keys.
/// </summary>
public sealed class HotkeyRegistrar : IHotkeyRegistrar, IDisposable
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmHotkey = 0x0312;
    private const uint WmAppWake = 0x8000;

    private static readonly nint HwndMessage = new(-3);
    private static readonly WndProcDelegate WindowProc = StaticWindowProc;
    private static readonly ConcurrentDictionary<nint, HotkeyRegistrar> Instances = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LoopWork> _loopWork = new();
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _thread;
    private nint _hwnd;
    private bool _available;
    private int _nextId;
    private bool _disposed;

    public HotkeyRegistrar()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "WhaleGo-HotkeyMessageLoop",
        };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    public bool TryRegister(string id, string gesture, Action onPressed)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(onPressed);

        if (!TryParseGesture(gesture, out var modifiers, out var virtualKey))
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed || !_available || _hwnd == 0)
            {
                return false;
            }
        }

        if (IsRegistered(id))
        {
            Unregister(id);
        }

        var hotkeyId = Interlocked.Increment(ref _nextId);
        var registered = false;
        if (!RunOnLoopThread(() => registered = RegisterHotKey(_hwnd, hotkeyId, modifiers | ModNoRepeat, virtualKey)))
        {
            return false;
        }

        if (!registered)
        {
            return false;
        }

        lock (_lock)
        {
            _registrations[id] = new Registration(hotkeyId, onPressed);
        }

        return true;
    }

    public void Unregister(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        Registration? existing = null;
        lock (_lock)
        {
            if (_registrations.TryGetValue(id, out var found))
            {
                _registrations.Remove(id);
                existing = found;
            }
        }

        if (existing is null)
        {
            return;
        }

        _ = RunOnLoopThread(() => UnregisterHotKey(_hwnd, existing.HotkeyId));
    }

    public void Dispose()
    {
        Registration[] pending;
        nint hwnd;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _registrations.Values.ToArray();
            _registrations.Clear();
            hwnd = _hwnd;
        }

        if (pending.Length > 0)
        {
            _ = RunOnLoopThread(() =>
            {
                foreach (var registration in pending)
                {
                    _ = UnregisterHotKey(_hwnd, registration.HotkeyId);
                }
            });
        }

        if (hwnd != 0)
        {
            _ = PostMessageW(hwnd, WmClose, 0, 0);
        }

        _ = _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    /// <summary>Parses gestures like "Ctrl+Alt+Shift+F12" into Win32 modifiers + virtual key.</summary>
    public static bool TryParseGesture(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var mods = 0u;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var isLast = i == tokens.Length - 1;
            switch (token.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    mods |= ModControl;
                    break;
                case "ALT":
                    mods |= ModAlt;
                    break;
                case "SHIFT":
                    mods |= ModShift;
                    break;
                case "WIN":
                case "META":
                    mods |= ModWin;
                    break;
                default:
                    if (!isLast || !TryParseVirtualKey(token, out var vk))
                    {
                        return false;
                    }

                    virtualKey = vk;
                    break;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        modifiers = mods;
        return true;
    }

    private bool IsRegistered(string id)
    {
        lock (_lock)
        {
            return _registrations.ContainsKey(id);
        }
    }

    private bool RunOnLoopThread(Action action)
    {
        if (Thread.CurrentThread == _thread)
        {
            action();
            return true;
        }

        var hwnd = _hwnd;
        if (hwnd == 0)
        {
            return false;
        }

        using var done = new ManualResetEventSlim(initialState: false);
        _loopWork.Enqueue(new LoopWork(action, done));
        if (!PostMessageW(hwnd, WmAppWake, 0, 0))
        {
            return false;
        }

        return done.Wait(TimeSpan.FromSeconds(2));
    }

    private void DrainLoopWork()
    {
        while (_loopWork.TryDequeue(out var work))
        {
            try
            {
                work.Action();
            }
            catch (Exception)
            {
                // A registration action must never kill the message loop.
            }
            finally
            {
                work.Done.Set();
            }
        }
    }

    private static bool TryParseVirtualKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }

            return false;
        }

        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f') &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        virtualKey = token.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "PRINTSCREEN" => 0x2C,
            "PAUSE" => 0x13,
            _ => 0,
        };
        return virtualKey != 0;
    }

    private void ThreadMain()
    {
        try
        {
            var instance = GetModuleHandleW(null);
            var className = string.Create(CultureInfo.InvariantCulture, $"WhaleGoHotkeyWindow-{Environment.ProcessId}");
            var windowClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = WindowProc,
                hInstance = instance,
                lpszClassName = className,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                _ready.Set();
                return;
            }

            var hwnd = CreateWindowExW(0, className, "WhaleGo.HotkeyWindow", 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
            if (hwnd == 0)
            {
                _ = UnregisterClassW(className, instance);
                _ready.Set();
                return;
            }

            Instances[hwnd] = this;
            lock (_lock)
            {
                _hwnd = hwnd;
                _available = true;
            }

            _ready.Set();

            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessageW(ref message);
            }

            Instances.TryRemove(hwnd, out _);
            lock (_lock)
            {
                _hwnd = 0;
                _available = false;
            }

            _ = DestroyWindow(hwnd);
            _ = UnregisterClassW(className, instance);
        }
        catch (Exception)
        {
            _ready.Set();
        }
    }

    private static nint StaticWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmHotkey:
                if (Instances.TryGetValue(hwnd, out var hotkeyOwner))
                {
                    hotkeyOwner.DispatchHotkey((int)wParam);
                }

                return 0;
            case WmAppWake:
                if (Instances.TryGetValue(hwnd, out var workOwner))
                {
                    workOwner.DrainLoopWork();
                }

                return 0;
            case WmClose:
                PostQuitMessage(0);
                return 0;
            case WmDestroy:
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void DispatchHotkey(int hotkeyId)
    {
        Action? callback = null;
        lock (_lock)
        {
            foreach (var registration in _registrations.Values)
            {
                if (registration.HotkeyId == hotkeyId)
                {
                    callback = registration.Callback;
                    break;
                }
            }
        }

        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception)
        {
            // Hotkey handlers must never kill the message loop.
        }
    }

    private sealed record Registration(int HotkeyId, Action Callback);

    private sealed record LoopWork(Action Action, ManualResetEventSlim Done);

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}