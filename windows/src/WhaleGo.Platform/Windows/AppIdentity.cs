using System.Security.Cryptography;
using System.Text;

namespace WhaleGo.Platform.Windows;

/// <summary>
/// Stable application identity (docs/PROTOCOL.md §3.4): classic apps use
/// win-exe:&lt;sha1(normalized absolute exe path, lowercase)[:12]&gt;; packaged apps use win-aumid:&lt;aumid&gt;.
/// Title is never part of identity.
/// </summary>
public static class AppIdentity
{
    public const string ExePrefix = "win-exe:";
    public const string AumidPrefix = "win-aumid:";
    public const int HashPrefixLength = 12;

    /// <summary>Normalizes an exe path for hashing: absolute, forward slashes, invariant lowercase.</summary>
    public static string NormalizeExePath(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return Path.GetFullPath(exePath).Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>win-exe:&lt;sha1(normalized absolute exe path lowercase)[:12]&gt;.</summary>
    public static string ForExePath(string exePath) =>
        ExePrefix + Sha1Hex(NormalizeExePath(exePath))[..HashPrefixLength];

    /// <summary>win-aumid:&lt;aumid&gt; for packaged applications.</summary>
    public static string ForAumid(string aumid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aumid);
        return AumidPrefix + aumid;
    }

    /// <summary>Fallback identity when neither exe path nor AUMID is readable (protected processes).</summary>
    public static string ForUnknownProcess(int pid) => ExePrefix + Sha1Hex("unknown:" + pid)[..HashPrefixLength];

    public static bool IsExeId(string? appId) => appId is not null && appId.StartsWith(ExePrefix, StringComparison.Ordinal);

    public static bool IsAumidId(string? appId) => appId is not null && appId.StartsWith(AumidPrefix, StringComparison.Ordinal);

    internal static string Sha1Hex(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }
}
