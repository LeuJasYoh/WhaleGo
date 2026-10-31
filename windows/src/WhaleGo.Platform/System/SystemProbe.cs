using System.Runtime.InteropServices;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.SystemInfo;

/// <summary>
/// Environment facts for go_health/SESSION_LOCKED/self-check (docs/ARCHITECTURE §3.6, GUI §8.4). Clean-room P/Invoke only;
/// no side effects beyond opening the input desktop/token/process handle read-only.
/// <para>NOTE: the namespace is <c>WhaleGo.Platform.SystemInfo</c> on purpose — a namespace named
/// <c>WhaleGo.Platform.System</c> would shadow the global <c>System</c> namespace for every file nested under
/// <c>WhaleGo.Platform.*</c>. Do not rename it back.</para>
/// </summary>
public sealed class SystemProbe : ISystemProbe
{
    public const int SmRemoteSession = 0x1000;

    public SystemFacts GetFacts()
    {
        var sessionId = GetProcessSessionId(Environment.ProcessId);
        var isLocked = TryGetInputDesktopName(out var desktopName)
            ? !string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase)
            : true;
        var isInteractive = IsInteractiveSession(sessionId);

        return new SystemFacts(
            WindowsVersion: ReadWindowsVersion(),
            WindowsSessionId: sessionId,
            IsInteractive: isInteractive,
            IsLocked: isLocked,
            Integrity: ReadIntegrityLevel(),
            DpiAwareness: ReadDpiAwareness(),
            ProcessId: Environment.ProcessId,
            IsRemoteSession: Win32.IsRemoteSession(),
            UptimeMs: unchecked((long)Win32.GetTickCount64()));
    }

    /// <summary>Session id of a process (ProcessIdToSessionId); -1 when unavailable.</summary>
    public static int GetProcessSessionId(int pid)
    {
        try
        {
            return Win32.ProcessIdToSessionId(unchecked((uint)pid), out var sessionId) ? unchecked((int)sessionId) : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Interactive = current session is the active console session and the input desktop can be opened.</summary>
    public static bool IsInteractiveSession(int sessionId)
    {
        try
        {
            var activeConsoleSession = Win32.WTSGetActiveConsoleSessionId();
            if (activeConsoleSession == 0xFFFFFFFF || sessionId < 0 || unchecked((int)activeConsoleSession) != sessionId)
            {
                return false;
            }

            return TryGetInputDesktopName(out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the input desktop is openable and reports a desktop name (locked/secure desktops may deny it).</summary>
    public static bool TryGetInputDesktopName(out string desktopName) => Win32.TryOpenInputDesktop(out desktopName);

    /// <summary>Windows version string including build (e.g. "Windows 10.0.26100").</summary>
    public static string ReadWindowsVersion()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            return $"Windows {version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Token integrity level: untrusted|low|medium|high|system|protected|unknown.</summary>
    public static string ReadIntegrityLevel()
    {
        var process = Win32.GetCurrentProcess();
        if (!Win32.OpenProcessToken(process, Win32.TOKEN_QUERY, out var token))
        {
            return "unknown";
        }

        try
        {
            Win32.GetTokenInformation(token, Win32.TokenIntegrityLevel, IntPtr.Zero, 0, out var required);
            if (required <= 0)
            {
                return "unknown";
            }

            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (!Win32.GetTokenInformation(token, Win32.TokenIntegrityLevel, buffer, required, out _))
                {
                    return "unknown";
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }: first pointer-sized field is PSID.
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                {
                    return "unknown";
                }

                var subAuthorityCount = Marshal.ReadByte(sid, 1);
                if (subAuthorityCount < 1)
                {
                    return "unknown";
                }

                var rid = unchecked((uint)Marshal.ReadInt32(sid, 8 + ((subAuthorityCount - 1) * 4)));
                return rid switch
                {
                    0x0000 => "untrusted",
                    0x1000 => "low",
                    0x2000 => "medium",
                    0x3000 => "high",
                    0x4000 => "system",
                    0x5000 => "protected",
                    _ => "unknown",
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return "unknown";
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }

    /// <summary>Process DPI awareness: per_monitor_v2|per_monitor|system|unaware|unknown.</summary>
    public static string ReadDpiAwareness()
    {
        try
        {
            return DescribeDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Describe any DPI_AWARENESS_CONTEXT handle (pseudo handles -1..-4 are recognized).</summary>
    public static string DescribeDpiAwarenessContext(IntPtr context)
    {
        if (context == IntPtr.Zero)
        {
            return "unknown";
        }

        if (Win32.AreDpiAwarenessContextsEqual(context, new IntPtr(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)))
        {
            return "per_monitor_v2";
        }

        if (Win32.AreDpiAwarenessContextsEqual(context, new IntPtr(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE)))
        {
            return "per_monitor";
        }

        if (Win32.AreDpiAwarenessContextsEqual(context, new IntPtr(Win32.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE)))
        {
            return "system";
        }

        if (Win32.AreDpiAwarenessContextsEqual(context, new IntPtr(Win32.DPI_AWARENESS_CONTEXT_UNAWARE)))
        {
            return "unaware";
        }

        var awareness = Win32.GetAwarenessFromDpiAwarenessContext(context);
        return awareness switch
        {
            0 => "unaware",
            1 => "system",
            2 => "per_monitor",
            _ => "unknown",
        };
    }

    /// <summary>System DPI (GetDpiForSystem); 96 on failure.</summary>
    public static uint GetSystemDpi()
    {
        try
        {
            var dpi = Win32.GetDpiForSystem();
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96;
        }
    }
}
