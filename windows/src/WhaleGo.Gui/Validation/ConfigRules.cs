using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Gui.Validation;

/// <summary>
/// GUI-side validation mirror of docs/GUI.md section 6.2. The kernel remains authoritative (it re-validates on
/// SetConfig); this layer exists so the settings window can refuse to save and point at the exact field.
/// All validators return null when valid, otherwise a short Chinese error message.
/// </summary>
public static class ConfigRules
{
    /// <summary>Keys whose change requires an executor restart (docs/GUI.md 6.2).</summary>
    public static readonly IReadOnlyList<string> RestartRequiredKeys =
    [
        ConfigKeys.ListenAddress,
        ConfigKeys.ListenPort,
        ConfigKeys.ScreenshotMode,
        ConfigKeys.AuditDir,
        ConfigKeys.LoggingDir,
    ];

    public static readonly IReadOnlyList<string> ScreenshotModes = ["printwindow", "wgc"];

    public static readonly IReadOnlyList<string> NotificationsLevels = ["off", "important", "all"];

    public static readonly IReadOnlyList<string> LoggingLevels = ["trace", "debug", "information", "warning", "error"];

    public static readonly IReadOnlyList<string> ThemeValues = ["system", "light", "dark"];

    private static readonly Regex AllowlistNamePattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenNamePattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Integer ranges from docs/GUI.md 6.2.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> IntRanges = new Dictionary<string, (int, int)>
    {
        [ConfigKeys.ListenPort] = (1, 65535),
        [ConfigKeys.PolicyActRatePerMinute] = (1, 600),
        [ConfigKeys.PolicyControllerWaitMs] = (0, 10000),
        [ConfigKeys.PolicyStateTtlSeconds] = (10, 600),
        [ConfigKeys.PolicyFrameTtlSeconds] = (10, 300),
        [ConfigKeys.LimitsMaxObserveElements] = (50, 2000),
        [ConfigKeys.LimitsMaxObserveChars] = (2000, 200000),
        [ConfigKeys.LimitsMaxImageWidth] = (320, 4096),
        [ConfigKeys.LimitsMaxImageBytes] = (20000, 2000000),
        [ConfigKeys.LimitsToolTimeoutSeconds] = (5, 40),
    };

    public static bool RequiresRestart(string key) => RestartRequiredKeys.Contains(key);

    public static bool IsEnvironmentOrCommandLine(string? sourceKind) =>
        string.Equals(sourceKind, ConfigSourceKinds.Environment, StringComparison.Ordinal) ||
        string.Equals(sourceKind, ConfigSourceKinds.CommandLine, StringComparison.Ordinal);

    /// <summary>Derives the documented environment override name, e.g. listen.port =&gt; WHALEGO_LISTEN__PORT.</summary>
    public static string EnvironmentVariableName(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        var group = dot < 0 ? key : key[..dot];
        var leaf = dot < 0 ? string.Empty : key[(dot + 1)..].Replace('.', '_');
        return "WHALEGO_" + group.ToUpperInvariant() + (leaf.Length == 0 ? string.Empty : "__" + leaf.ToUpperInvariant());
    }

    public static string? ValidateAddress(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "监听地址不能为空";
        }

        return IPAddress.TryParse(text, out _) ? null : "请输入合法的 IPv4 地址（0.0.0.0 / 127.0.0.1 / 具体网卡地址）";
    }

    public static string? ValidatePortText(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "端口不能为空";
        }

        if (!int.TryParse(value, out var port))
        {
            return "端口必须是整数";
        }

        return port is < 1 or > 65535 ? "端口范围 1–65535" : null;
    }

    public static string? ValidateInt(string key, int? value)
    {
        if (value is null)
        {
            return "请输入整数";
        }

        if (!IntRanges.TryGetValue(key, out var range))
        {
            return null;
        }

        return value < range.Min || value > range.Max
            ? "范围 " + range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture) + "–" +
              range.Max.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    public static string? ValidateEnum(string key, string? value)
    {
        var options = key switch
        {
            ConfigKeys.ScreenshotMode => ScreenshotModes,
            ConfigKeys.UiNotificationsLevel => NotificationsLevels,
            ConfigKeys.LoggingLevel => LoggingLevels,
            ConfigKeys.UiTheme => ThemeValues,
            _ => null,
        };

        if (options is null)
        {
            return null;
        }

        return options.Contains(value ?? string.Empty, StringComparer.Ordinal)
            ? null
            : "必须是 " + string.Join(" / ", options) + " 之一";
    }

    /// <summary>RegisterHotKey syntax: Ctrl/Alt/Shift/Win modifiers plus a single A–Z, 0–9 or F1–F24 key.</summary>
    public static string? ValidateHotkeyGesture(string? gesture)
    {
        var text = (gesture ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "热键不能为空";
        }

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Length < 2)
        {
            return "至少需要一个修饰键 + 一个主键（例如 Ctrl+Alt+Shift+F12）";
        }

        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].Trim();
            if (modifier is not ("Ctrl" or "Alt" or "Shift" or "Win"))
            {
                return "修饰键只能是 Ctrl / Alt / Shift / Win";
            }

            if (!modifiers.Add(modifier))
            {
                return "修饰键不能重复";
            }
        }

        var key = parts[^1].Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return null;
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f') &&
            int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return null;
        }

        return "主键必须是单个字母/数字或 F1–F24";
    }

    public static string? ValidateGlob(string? value, int maxLength = 64)
    {
        var text = value ?? string.Empty;
        if (text.Length == 0)
        {
            return "匹配规则不能为空";
        }

        if (text.Length > maxLength)
        {
            return "每项最多 " + maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个字符";
        }

        return text.Contains(',', StringComparison.Ordinal) ? "请一行一个规则，不要用逗号拼接" : null;
    }

    public static string? ValidateAllowlistName(string? value, IEnumerable<string>? otherNames = null)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "名称不能为空";
        }

        if (!AllowlistNamePattern.IsMatch(text))
        {
            return "名称 1–32 字符，仅限字母/数字/下划线/连字符";
        }

        return otherNames is not null && otherNames.Contains(text, StringComparer.OrdinalIgnoreCase)
            ? "名称重复"
            : null;
    }

    public static string? ValidateAllowlistPath(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "路径不能为空";
        }

        if (!Path.IsPathRooted(text))
        {
            return "必须是绝对路径";
        }

        return File.Exists(text) ? null : "文件不存在";
    }

    public static string? ValidateTokenName(string? value) =>
        TokenNamePattern.IsMatch((value ?? string.Empty).Trim()) ? null : "名称 1–32 字符，仅限字母/数字/下划线/连字符";

    public static string? ValidateAuditDirectory(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "目录不能为空";
        }

        var expanded = Environment.ExpandEnvironmentVariables(text);
        if (!Path.IsPathRooted(expanded))
        {
            return "必须是绝对路径（支持 %LOCALAPPDATA% 等环境变量）";
        }

        return null;
    }

    /// <summary>Warnings that do not block saving but must be surfaced (docs/GUI.md 6.2 / 9.6).</summary>
    public static string? WarningForAddress(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text switch
        {
            "127.0.0.1" => "仅本机监听：host.docker.internal 走宿主虚拟网卡而非回环，容器将无法连接（只适合本机测试）",
            "0.0.0.0" => "监听所有网卡：局域网内任何能访问该端口的设备都可能尝试连接（仍需 token），建议用防火墙限定 Docker/WSL 网段",
            _ => null,
        };
    }

    public static string? WarningForPort(int? port) =>
        port is > 0 and < 1024 ? "小于 1024 的端口通常需要管理员权限，且更容易被系统占用" : null;

    public static bool IsLocalhostAddress(string? address) =>
        string.Equals((address ?? string.Empty).Trim(), "127.0.0.1", StringComparison.Ordinal);
}

/// <summary>Defaults used to pre-fill fields when the kernel config does not contain a key yet.</summary>
public static class GuiConfigDefaults
{
    public static readonly IReadOnlyList<LaunchAllowlistEntry> LaunchAllowlist =
    [
        new("notepad", @"C:\Windows\System32\notepad.exe"),
        new("calc", @"C:\Windows\System32\calc.exe"),
        new("mspaint", @"C:\Windows\System32\mspaint.exe"),
        new("explorer", @"C:\Windows\explorer.exe"),
    ];

    public static object? Get(string key) => key switch
    {
        ConfigKeys.LoggingDir => ConfigDefaults.AuditDir,
        ConfigKeys.LaunchAllowlist => LaunchAllowlist,
        ConfigKeys.PrivacySensitiveWindows => ConfigDefaults.SensitiveWindows,
        ConfigKeys.AuditDir => ConfigDefaults.AuditDir,
        _ => ConfigDefaults.All.TryGetValue(key, out var value) ? value : null,
    };
}
