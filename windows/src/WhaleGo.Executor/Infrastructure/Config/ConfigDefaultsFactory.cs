using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>Executor-side defaults on top of the Contracts defaults (allowlist paths, logging dir).</summary>
public static class ConfigDefaultsFactory
{
    public static IReadOnlyDictionary<string, object?> Create()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ConfigDefaults.All)
        {
            values[pair.Key] = pair.Value;
        }

        values[ConfigKeys.LoggingDir] = ConfigDefaults.AuditDir;
        values[ConfigKeys.HotkeysOpenSettings] = null;
        values[ConfigKeys.LaunchAllowlist] = DefaultAllowlist().ToList();
        values[ConfigKeys.PrivacySensitiveWindows] = ConfigDefaults.SensitiveWindows.ToList();
        return values;
    }

    public static IReadOnlyList<LaunchAllowlistEntry> DefaultAllowlist()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = @"C:\Windows";
        }

        return
        [
            new LaunchAllowlistEntry("notepad", Path.Combine(systemRoot, "System32", "notepad.exe")),
            new LaunchAllowlistEntry("calc", Path.Combine(systemRoot, "System32", "calc.exe")),
            new LaunchAllowlistEntry("mspaint", Path.Combine(systemRoot, "System32", "mspaint.exe")),
            new LaunchAllowlistEntry("explorer", Path.Combine(systemRoot, "explorer.exe")),
        ];
    }
}