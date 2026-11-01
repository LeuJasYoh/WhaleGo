using System;
using System.Collections.Generic;
using System.Linq;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>Executor-side defaults on top of the Contracts defaults (logging dir).</summary>
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
        values[ConfigKeys.PrivacySensitiveWindows] = ConfigDefaults.SensitiveWindows.ToList();
        return values;
    }
}
