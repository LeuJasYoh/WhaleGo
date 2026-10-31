using System;
using System.IO;

namespace WhaleGo.Executor.Infrastructure;

/// <summary>
/// Resolved host filesystem layout: config under %APPDATA%\WhaleGo, runtime data under %LOCALAPPDATA%\WhaleGo.
/// Logs (executor log + audit JSONL + crash logs) live next to the executable in a normal run
/// (<c>AppContext.BaseDirectory\logs</c>) so they ship with the app; tests/E2E that isolate via
/// WHALEGO_DATA_DIR or --config keep logs under the isolated data root instead.
/// </summary>
public sealed record ExecutorPaths(
    string ConfigRoot,
    string ConfigPath,
    string DataRoot,
    string SecretsPath,
    string ControlStatePath,
    string UiStatePath,
    string LogsRoot,
    string DefaultAuditDir)
{
    public static ExecutorPaths Resolve(string? configPath, string? dataDirectoryOverride)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var dataRootOverride = !string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? Path.GetFullPath(dataDirectoryOverride)
            : null;

        string configFile;
        string configRoot;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            configFile = Path.GetFullPath(configPath);
            configRoot = Path.GetDirectoryName(configFile) ?? localAppData;
        }
        else if (dataRootOverride is not null)
        {
            // Test isolation: WHALEGO_DATA_DIR alone keeps the config file inside the isolated root too.
            configRoot = dataRootOverride;
            configFile = Path.Combine(configRoot, "config.json");
        }
        else
        {
            configRoot = Path.Combine(appData, "WhaleGo");
            configFile = Path.Combine(configRoot, "config.json");
        }

        var dataRoot = dataRootOverride
            ?? (!string.IsNullOrWhiteSpace(configPath) ? configRoot : Path.Combine(localAppData, "WhaleGo"));

        // Normal portable run: logs next to WhaleGo.Executor.exe (ship-along folder).
        // Isolated runs (--config or WHALEGO_DATA_DIR) keep logs inside the isolated root so tests never
        // write into the build output and cleanup stays trivial.
        var isolated = dataRootOverride is not null || !string.IsNullOrWhiteSpace(configPath);
        var logsRoot = isolated
            ? Path.Combine(dataRoot, "logs")
            : Path.Combine(AppContext.BaseDirectory, "logs");

        return new ExecutorPaths(
            ConfigRoot: configRoot,
            ConfigPath: configFile,
            DataRoot: dataRoot,
            SecretsPath: Path.Combine(dataRoot, "secrets.json"),
            ControlStatePath: Path.Combine(dataRoot, "control-state.json"),
            UiStatePath: Path.Combine(dataRoot, "ui-state.json"),
            LogsRoot: logsRoot,
            DefaultAuditDir: logsRoot);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    /// <summary>
    /// Resolve a configured directory: expands <c>%EXEDIR%</c> (the executable folder) and %ENV% macros,
    /// relative values under the data root; null/empty/unknown-macro falls back.
    /// </summary>
    public static string ResolveDirectory(string? raw, string fallback, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var expanded = raw.Trim().Replace("%EXEDIR%", AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        expanded = Environment.ExpandEnvironmentVariables(expanded);
        if (expanded.Length == 0 || expanded.Contains('%'))
        {
            return fallback;
        }

        return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : Path.Combine(dataRoot, expanded);
    }
}
