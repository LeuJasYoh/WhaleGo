using System.Text;
using System.Text.Json;

namespace WhaleGo.Gui.Validation;

/// <summary>
/// Builds the copy-paste AstrBot plugin config snippet (docs/GUI.md section 7.3). The plugin schema key is
/// url (NOT server_url) and token is substituted exactly once at copy time so the user needs one paste.
/// </summary>
public static class PluginConfigSnippet
{
    public const string TokenPlaceholder = "<这里替换为复制的 token>";
    public const int DefaultTimeoutSeconds = 45;
    public const string DockerHealthCommandTokenPlaceholder = "<token>";

    public static readonly IReadOnlyList<string> AllTools =
    [
        "go_health", "go_apps", "go_windows", "go_observe", "go_act", "go_launch", "go_stop",
    ];

    public static string BuildUrl(int port) =>
        "http://host.docker.internal:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mcp";

    public static IReadOnlyDictionary<string, object?> BuildValues(int port, string? tokenPlaintext)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = BuildUrl(port),
            ["token"] = string.IsNullOrEmpty(tokenPlaintext) ? TokenPlaceholder : tokenPlaintext,
            ["timeout"] = DefaultTimeoutSeconds,
            ["readonly"] = false,
            ["admin_only"] = true,
            ["allow_screenshot"] = true,
            ["allowed_tools"] = AllTools,
        };
        return values;
    }

    public static string BuildJson(int port, string? tokenPlaintext)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(BuildValues(port, tokenPlaintext), options);
    }

    /// <summary>Command the user can paste inside the container to verify reachability (never auto-executed).</summary>
    public static string BuildDockerHealthCommand(int port, string? tokenPlaintext)
    {
        var token = string.IsNullOrEmpty(tokenPlaintext) ? DockerHealthCommandTokenPlaceholder : tokenPlaintext;
        return "docker exec AstrBot curl -fsS -H \"Authorization: Bearer " + token + "\" http://host.docker.internal:" +
               port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/healthz";
    }

    /// <summary>Firewall rule command (copy-only; the GUI never elevates).</summary>
    public static string BuildFirewallCommand(int port, string remoteIp = "172.16.0.0/12")
    {
        var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rules = new[]
        {
            "New-NetFirewallRule -DisplayName \"WhaleGo-MCP\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + portText + " -RemoteAddress " + remoteIp,
        };
        return string.Join(Environment.NewLine, rules);
    }

    public static string BuildNetshFirewallCommand(int port) =>
        "netsh advfirewall firewall add rule name=\"WhaleGo-MCP\" dir=in action=allow protocol=TCP localport=" +
        port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " remoteip=172.16.0.0/12";

    public static string BuildSelfCheckReportText(IEnumerable<(string Name, string Status, string Detail)> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WhaleGo 自检报告");
        foreach (var (name, status, detail) in items)
        {
            builder.Append("- [").Append(status).Append("] ").Append(name).Append(": ").AppendLine(detail);
        }

        return builder.ToString().TrimEnd();
    }
}
