using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure.Config;

/// <summary>Startup configuration validation (blocks startup with a clear error rather than limping).</summary>
public static class ConfigValidation
{
    private static readonly string[] ScreenshotModes = ["printwindow", "wgc"];
    private static readonly string[] Themes = ["system", "light", "dark"];

    public static IReadOnlyList<string> Validate(ExecutorConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(config.ListenAddress))
        {
            errors.Add("listen.address 不能为空");
        }

        if (config.ListenPort is < 1 or > 65535)
        {
            errors.Add("listen.port 必须在 1..65535");
        }

        if (config.ActRatePerMinute is < 1 or > 100000)
        {
            errors.Add("policy.actRatePerMinute 必须在 1..100000");
        }

        if (config.ControllerWaitMs is < 0 or > 10000)
        {
            errors.Add("policy.controllerWaitMs 必须在 0..10000");
        }

        if (config.StateTtlSeconds is < 5 or > 3600)
        {
            errors.Add("policy.stateTtlSeconds 必须在 5..3600");
        }

        if (config.FrameTtlSeconds is < 1 or > 3600)
        {
            errors.Add("policy.frameTtlSeconds 必须在 1..3600");
        }

        if (config.MaxObserveElements is < 1 or > 2000)
        {
            errors.Add("limits.maxObserveElements 必须在 1..2000");
        }

        if (config.MaxObserveChars < 200)
        {
            errors.Add("limits.maxObserveChars 不能小于 200");
        }

        if (config.MaxImageWidth is < 64 or > 4096)
        {
            errors.Add("limits.maxImageWidth 必须在 64..4096");
        }

        if (config.MaxImageBytes is < 10000 or > 5242880)
        {
            errors.Add("limits.maxImageBytes 必须在 10000..5242880");
        }

        if (config.ToolTimeoutSeconds is < 5 or > 300)
        {
            errors.Add("limits.toolTimeoutSeconds 必须在 5..300");
        }

        if (!ScreenshotModes.Contains(config.ScreenshotMode, StringComparer.Ordinal))
        {
            errors.Add("screenshot.mode 只能是 printwindow|wgc");
        }

        if (!Themes.Contains(config.UiTheme, StringComparer.Ordinal))
        {
            errors.Add("ui.theme 只能是 system|light|dark");
        }

        if (config.HotkeysEnabled && string.IsNullOrWhiteSpace(config.HotkeysEmergencyStop))
        {
            errors.Add("hotkeys.emergencyStop 不能为空");
        }

        return errors;
    }

    /// <summary>Validate persisted config file paths without requiring the files to exist yet.</summary>
    public static IReadOnlyList<string> ValidatePaths(ExecutorPaths paths)
    {
        var errors = new List<string>();
        if (!Path.IsPathRooted(paths.ConfigPath))
        {
            errors.Add("config path must be absolute: " + paths.ConfigPath);
        }

        return errors;
    }
}
