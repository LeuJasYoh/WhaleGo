using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WhaleGo.Platform.Tests.TestInfrastructure;

/// <summary>
/// Test-host DPI awareness: the platform assumes physical pixels (executor manifest is PerMonitorV2). xunit's testhost is
/// normally DPI-unaware, which makes UIA rectangles virtualized; set PerMonitorV2 once at assembly load (best effort).
/// T3's defensive EnsureProcessDpiAware() is a no-op when this already ran.
/// </summary>
internal static class TestHostDpi
{
    private const long PerMonitorAwareV2 = -4;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new IntPtr(PerMonitorAwareV2));
        }
        catch
        {
            // Older Windows or already-set awareness: tests still run (coordinates may be virtualized at high DPI).
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);
}
