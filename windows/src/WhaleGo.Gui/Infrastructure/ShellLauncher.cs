using System.Diagnostics;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>
/// Opens folders in Explorer. docs/GUI.md section 8.3 specifies explorer.exe for the logs/audit directories;
/// this is plain shell activation (no P/Invoke, no privilege elevation) and is the only place the GUI leaves
/// the in-process kernel port surface.
/// </summary>
public static class ShellLauncher
{
    public static bool TryOpenFolder(string? path, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Environment.ExpandEnvironmentVariables(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + fullPath + "\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn("failed to open folder '" + path + "': " + ex.Message);
            return false;
        }
    }
}
