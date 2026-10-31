using System.Globalization;
using System.Runtime.InteropServices;

namespace WhaleGo.Platform.Input;

/// <summary>Integrity comparison of the target process against the WhaleGo process.</summary>
public enum UipiVerdict
{
    /// <summary>Target integrity is the same or lower: native input is allowed by UIPI.</summary>
    SameOrLower = 0,

    /// <summary>Target integrity is strictly higher: every send would be blocked by UIPI.</summary>
    Higher = 1,

    /// <summary>The target token could not be read (access denied/protected). Callers must treat it as blocked.</summary>
    Unknown = 2,
}

/// <summary>
/// UIPI pre-check for one target process (docs/DESIGN.md section 5.5: "UIPI 预检 → refused, not_sent, 零发送").
/// Higher integrity or unreadable token = refused. No native input is ever attempted after a refusal.
/// </summary>
public sealed class UipiGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenIntegrityLevel = 25;
    private const int ErrorAccessDenied = 5;

    private readonly object _ownTokenLock = new();
    private uint? _ownIntegrityRid;

    /// <summary>Returns the integrity verdict; never throws for normal Win32 failures.</summary>
    public UipiVerdict Check(int targetPid) => Check(targetPid, out _);

    /// <summary>Returns the integrity verdict and a short internal detail (diagnostics only).</summary>
    public UipiVerdict Check(int targetPid, out string? detail)
    {
        detail = null;
        if (targetPid <= 0)
        {
            detail = "invalid_pid";
            return UipiVerdict.Unknown;
        }

        if (targetPid == Environment.ProcessId)
        {
            return UipiVerdict.SameOrLower;
        }

        var ownRid = TryGetOwnIntegrityRid();
        if (ownRid is null)
        {
            detail = "own_integrity_unavailable";
            return UipiVerdict.Unknown;
        }

        var targetRid = TryGetProcessIntegrityRid(targetPid, out var errorCode);
        if (targetRid is null)
        {
            detail = errorCode == ErrorAccessDenied ? "target_access_denied" : "target_integrity_unavailable";
            return UipiVerdict.Unknown;
        }

        if (targetRid.Value > ownRid.Value)
        {
            detail = string.Create(CultureInfo.InvariantCulture, $"target_rid=0x{targetRid.Value:X4};own_rid=0x{ownRid.Value:X4}");
            return UipiVerdict.Higher;
        }

        return UipiVerdict.SameOrLower;
    }

    /// <summary>Conservative policy: everything except SameOrLower is treated as blocked.</summary>
    public static bool IsBlocked(UipiVerdict verdict) => verdict != UipiVerdict.SameOrLower;

    /// <summary>Convenience: true when native input to this pid must be refused.</summary>
    public bool IsTargetBlocked(int targetPid, out string? detail) => IsBlocked(Check(targetPid, out detail));

    private uint? TryGetOwnIntegrityRid()
    {
        lock (_ownTokenLock)
        {
            if (_ownIntegrityRid is not null)
            {
                return _ownIntegrityRid;
            }

            var rid = TryReadIntegrityRid(GetCurrentProcess(), out _);
            _ownIntegrityRid = rid;
            return rid;
        }
    }

    private static uint? TryGetProcessIntegrityRid(int pid, out int errorCode)
    {
        errorCode = 0;
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, (uint)pid);
        if (processHandle == 0)
        {
            errorCode = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            return TryReadIntegrityRid(processHandle, out errorCode);
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }

    private static uint? TryReadIntegrityRid(nint processHandle, out int errorCode)
    {
        errorCode = 0;
        if (processHandle == 0 || !OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            errorCode = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenIntegrityLevel, 0, 0, out var neededSize);
            if (neededSize <= 0)
            {
                errorCode = Marshal.GetLastWin32Error();
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)neededSize);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, neededSize, out _))
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return null;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                if (label.Label.Sid == 0)
                {
                    errorCode = 0;
                    return null;
                }

                var subAuthorityCount = GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCount == 0)
                {
                    return null;
                }

                var count = Marshal.ReadByte(subAuthorityCount);
                if (count == 0)
                {
                    return null;
                }

                var ridPtr = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                return ridPtr == 0 ? null : (uint)Marshal.ReadInt32(ridPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint tokenHandle, uint tokenInformationClass, nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint GetSidSubAuthorityCount(nint sid);
}