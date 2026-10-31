using System.Globalization;

namespace WhaleGo.Platform.Windows;

/// <summary>
/// window_id wire format (docs/PROTOCOL.md §3.5): "w-" + HWND formatted as 12 uppercase hex digits,
/// e.g. w-00000000006AF2. The id is an opaque locator, never an identity trust source: callers must
/// re-validate pid/class through <see cref="WindowCatalog"/>.
/// </summary>
public static class WindowIdCodec
{
    public const string Prefix = "w-";
    public const int HexDigits = 12;

    public static string Format(IntPtr hwnd) => Prefix + unchecked((ulong)hwnd.ToInt64()).ToString("X12", CultureInfo.InvariantCulture);

    public static string Format(int hwndValue) => Format(new IntPtr(hwndValue));

    public static bool TryParse(string? windowId, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrEmpty(windowId) || !windowId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = windowId[Prefix.Length..];
        if (hex.Length != HexDigits ||
            !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        hwnd = new IntPtr(unchecked((long)value));
        return hwnd != IntPtr.Zero;
    }

    public static IntPtr Parse(string windowId) =>
        TryParse(windowId, out var hwnd) ? hwnd : throw new FormatException($"Invalid window_id: '{windowId}'.");
}
