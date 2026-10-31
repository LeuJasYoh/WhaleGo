namespace WhaleGo.Contracts.Model;

/// <summary>Local (camelCase) configuration keys. Wire JSON is always snake_case.</summary>
public static class ConfigKeys
{
    public const string ListenAddress = "listen.address";
    public const string ListenPort = "listen.port";
    public const string ListenHealthzRequireToken = "listen.healthzRequireToken";
    public const string AuthTokens = "auth.tokens";
    public const string PolicyReadonly = "policy.readonly";
    public const string PolicyActRatePerMinute = "policy.actRatePerMinute";
    public const string PolicyControllerWaitMs = "policy.controllerWaitMs";
    public const string PolicyStateTtlSeconds = "policy.stateTtlSeconds";
    public const string PolicyFrameTtlSeconds = "policy.frameTtlSeconds";
    public const string LimitsMaxObserveElements = "limits.maxObserveElements";
    public const string LimitsMaxObserveChars = "limits.maxObserveChars";
    public const string LimitsMaxImageWidth = "limits.maxImageWidth";
    public const string LimitsMaxImageBytes = "limits.maxImageBytes";
    public const string LimitsToolTimeoutSeconds = "limits.toolTimeoutSeconds";
    public const string ScreenshotEnabled = "screenshot.enabled";
    public const string ScreenshotMode = "screenshot.mode";
    public const string ScreenshotDefaultInclude = "screenshot.defaultInclude";
    public const string PrivacyMaskPasswordFields = "privacy.maskPasswordFields";
    public const string PrivacySensitiveWindows = "privacy.sensitiveWindows";
    public const string AuditEnabled = "audit.enabled";
    public const string AuditDir = "audit.dir";
    public const string AuditRequireDurableForActions = "audit.requireDurableForActions";
    public const string HotkeysEnabled = "hotkeys.enabled";
    public const string HotkeysEmergencyStop = "hotkeys.emergencyStop";
    public const string HotkeysOpenSettings = "hotkeys.openSettings";
    public const string UiNotificationsLevel = "ui.notificationsLevel";
    public const string UiTheme = "ui.theme";
    public const string LoggingLevel = "logging.level";
    public const string LoggingDir = "logging.dir";
    public const string LaunchAllowlist = "launch.allowlist";
}

/// <summary>Config value sources shown as badges in the settings window.</summary>
public static class ConfigSourceKinds
{
    public const string Default = "default";
    public const string File = "file";
    public const string Environment = "environment";
    public const string CommandLine = "commandLine";
    public const string Unavailable = "unavailable";
}

/// <summary>Documented defaults (DESIGN section 5.8); the executor and GUI both reference these.</summary>
public static class ConfigDefaults
{
    public const string Version = "0.1.0";
    public const string ListenAddress = "0.0.0.0";
    public const int ListenPort = 8765;
    public const bool ListenHealthzRequireToken = false;
    public const bool PolicyReadonly = false;
    public const int PolicyActRatePerMinute = 120;
    public const int PolicyControllerWaitMs = 2000;
    public const int PolicyStateTtlSeconds = 120;
    public const int PolicyFrameTtlSeconds = 60;
    public const int LimitsMaxObserveElements = 300;
    public const int LimitsMaxObserveChars = 20000;
    public const int LimitsMaxImageWidth = 1024;
    public const long LimitsMaxImageBytes = 200000;
    public const int LimitsToolTimeoutSeconds = 40;
    public const bool ScreenshotEnabled = true;
    public const string ScreenshotMode = "printwindow";
    public const bool ScreenshotDefaultInclude = false;
    public const bool PrivacyMaskPasswordFields = true;
    public const string AuditDir = "%LOCALAPPDATA%\\WhaleGo\\logs";
    public const bool AuditEnabled = true;
    public const bool AuditRequireDurableForActions = true;
    public const bool HotkeysEnabled = true;
    public const string HotkeysEmergencyStop = "Ctrl+Alt+Shift+F12";
    public const bool UiNotificationsLevelImportant = true;
    public const string UiTheme = "system";
    public const string LoggingLevel = "information";

    public static readonly IReadOnlyList<string> SensitiveWindows = ["*password*", "*bank*"];

    public static readonly IReadOnlyDictionary<string, object?> All = new Dictionary<string, object?>
    {
        [ConfigKeys.ListenAddress] = ListenAddress,
        [ConfigKeys.ListenPort] = ListenPort,
        [ConfigKeys.ListenHealthzRequireToken] = ListenHealthzRequireToken,
        [ConfigKeys.PolicyReadonly] = PolicyReadonly,
        [ConfigKeys.PolicyActRatePerMinute] = PolicyActRatePerMinute,
        [ConfigKeys.PolicyControllerWaitMs] = PolicyControllerWaitMs,
        [ConfigKeys.PolicyStateTtlSeconds] = PolicyStateTtlSeconds,
        [ConfigKeys.PolicyFrameTtlSeconds] = PolicyFrameTtlSeconds,
        [ConfigKeys.LimitsMaxObserveElements] = LimitsMaxObserveElements,
        [ConfigKeys.LimitsMaxObserveChars] = LimitsMaxObserveChars,
        [ConfigKeys.LimitsMaxImageWidth] = LimitsMaxImageWidth,
        [ConfigKeys.LimitsMaxImageBytes] = LimitsMaxImageBytes,
        [ConfigKeys.LimitsToolTimeoutSeconds] = LimitsToolTimeoutSeconds,
        [ConfigKeys.ScreenshotEnabled] = ScreenshotEnabled,
        [ConfigKeys.ScreenshotMode] = ScreenshotMode,
        [ConfigKeys.ScreenshotDefaultInclude] = ScreenshotDefaultInclude,
        [ConfigKeys.PrivacyMaskPasswordFields] = PrivacyMaskPasswordFields,
        [ConfigKeys.PrivacySensitiveWindows] = SensitiveWindows,
        [ConfigKeys.AuditEnabled] = AuditEnabled,
        [ConfigKeys.AuditDir] = AuditDir,
        [ConfigKeys.AuditRequireDurableForActions] = AuditRequireDurableForActions,
        [ConfigKeys.HotkeysEnabled] = HotkeysEnabled,
        [ConfigKeys.HotkeysEmergencyStop] = HotkeysEmergencyStop,
        [ConfigKeys.UiNotificationsLevel] = "important",
        [ConfigKeys.UiTheme] = UiTheme,
        [ConfigKeys.LoggingLevel] = LoggingLevel,
    };
}

/// <summary>Launch allowlist entry: the ONLY way the model can start a program (no paths/arguments).</summary>
public sealed record LaunchAllowlistEntry(string Name, string Path);
