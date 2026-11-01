using System.IO;
using Microsoft.Win32;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Apps;

/// <summary>
/// Resolves a model-provided app name to a launch target without any allowlist:
/// absolute .exe/.lnk path, system execution aliases (calc/notepad/...), desktop/Start-Menu shortcuts
/// and App Paths registry entries. Shortcut/app-path enumeration is cached for one minute.
/// </summary>
public sealed class InstalledAppCatalog : IInstalledAppCatalog
{
    private const long CacheMs = 60_000;
    private const int MaxCandidates = 10;

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = "notepad.exe",
            ["记事本"] = "notepad.exe",
            ["calc"] = "calc.exe",
            ["计算器"] = "calc.exe",
            ["mspaint"] = "mspaint.exe",
            ["paint"] = "mspaint.exe",
            ["画图"] = "mspaint.exe",
            ["explorer"] = "explorer.exe",
            ["资源管理器"] = "explorer.exe",
            ["文件资源管理器"] = "explorer.exe",
            ["cmd"] = "cmd.exe",
            ["命令提示符"] = "cmd.exe",
            ["powershell"] = "powershell.exe",
            ["Windows PowerShell"] = "powershell.exe",
            ["taskmgr"] = "taskmgr.exe",
            ["任务管理器"] = "taskmgr.exe",
            ["control"] = "control.exe",
            ["控制面板"] = "control.exe",
            ["regedit"] = "regedit.exe",
            ["注册表编辑器"] = "regedit.exe",
            ["snippingtool"] = "snippingtool.exe",
            ["截图工具"] = "snippingtool.exe",
            ["magnify"] = "magnify.exe",
            ["放大镜"] = "magnify.exe",
            ["osk"] = "osk.exe",
            ["屏幕键盘"] = "osk.exe",
            ["charmap"] = "charmap.exe",
            ["字符映射表"] = "charmap.exe",
            ["msinfo32"] = "msinfo32.exe",
            ["系统信息"] = "msinfo32.exe",
            ["winver"] = "winver.exe",
            ["dxdiag"] = "dxdiag.exe",
        };

    private readonly object _gate = new();
    private IReadOnlyList<InstalledAppInfo> _cache = [];
    private long _cachedAtMs;

    public InstalledAppResolveResult Resolve(string name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return InstalledAppResolveResult.Missing();
        }

        var fromPath = TryAbsolutePath(text);
        if (fromPath is not null)
        {
            return InstalledAppResolveResult.Found(fromPath);
        }

        var alias = TryAlias(text);
        if (alias is not null)
        {
            return InstalledAppResolveResult.Found(alias);
        }

        var catalog = GetCatalog();
        var exact = Distinct(catalog.Where(app => MatchesExact(app, text))).ToList();
        if (exact.Count == 1)
        {
            return InstalledAppResolveResult.Found(exact[0]);
        }

        if (exact.Count > 1)
        {
            return InstalledAppResolveResult.Ambiguous(exact.Take(MaxCandidates).ToList());
        }

        if (text.Length >= 2)
        {
            var fuzzy = Distinct(catalog.Where(app => MatchesContains(app, text))).Take(MaxCandidates).ToList();
            if (fuzzy.Count == 1)
            {
                return InstalledAppResolveResult.Found(fuzzy[0]);
            }

            if (fuzzy.Count > 1)
            {
                return InstalledAppResolveResult.Ambiguous(fuzzy);
            }
        }

        return InstalledAppResolveResult.Missing();
    }

    private static InstalledAppInfo? TryAbsolutePath(string text)
    {
        if (!Path.IsPathFullyQualified(text) || !File.Exists(text))
        {
            return null;
        }

        var extension = Path.GetExtension(text);
        var stem = Path.GetFileNameWithoutExtension(text);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledAppInfo(stem, text, LaunchTargetKind.Executable, "path");
        }

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledAppInfo(stem, text, LaunchTargetKind.Shortcut, "path");
        }

        return null;
    }

    private static InstalledAppInfo? TryAlias(string text)
    {
        foreach (var pair in Aliases)
        {
            if (string.Equals(pair.Key, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key + ".exe", text, StringComparison.OrdinalIgnoreCase))
            {
                return new InstalledAppInfo(pair.Key, pair.Value, LaunchTargetKind.Alias, "alias");
            }
        }

        return null;
    }

    private IReadOnlyList<InstalledAppInfo> GetCatalog()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_cache.Count > 0 && now - _cachedAtMs < CacheMs)
            {
                return _cache;
            }

            var apps = new List<InstalledAppInfo>();
            foreach (var pair in Aliases)
            {
                apps.Add(new InstalledAppInfo(pair.Key, pair.Value, LaunchTargetKind.Alias, "alias"));
            }

            apps.AddRange(EnumerateShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "start-menu"));
            apps.AddRange(EnumerateShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "common-start-menu"));
            apps.AddRange(EnumerateShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "desktop"));
            apps.AddRange(EnumerateShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "common-desktop"));
            apps.AddRange(EnumerateAppPaths(Registry.CurrentUser));
            apps.AddRange(EnumerateAppPaths(Registry.LocalMachine));

            _cache = Distinct(apps).ToList();
            _cachedAtMs = now;
            return _cache;
        }
    }

    private static IEnumerable<InstalledAppInfo> EnumerateShortcuts(string? directory, string source)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        // Walk manually with per-directory exception handling: the Start Menu contains protected
        // junctions ("程序" etc.), so a single Directory.GetFiles(AllDirectories) can abort the whole scan.
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                files.AddRange(Directory.GetFiles(current, "*.lnk"));
                foreach (var child in Directory.GetDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (Exception)
            {
                // Skip unreadable directories; keep the rest of the catalog.
            }
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length > 0)
            {
                yield return new InstalledAppInfo(name, file, LaunchTargetKind.Shortcut, source);
            }
        }
    }

    private static IEnumerable<InstalledAppInfo> EnumerateAppPaths(RegistryKey root)
    {
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
        if (key is null)
        {
            yield break;
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(subKeyName);
            var path = subKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(path))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(subKeyName);
            if (name.Length > 0)
            {
                yield return new InstalledAppInfo(name, path, LaunchTargetKind.Executable, "app-paths");
            }
        }
    }

    private static bool MatchesExact(InstalledAppInfo app, string name)
    {
        if (string.Equals(app.DisplayName, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(app.LaunchPath);
        var stem = Path.GetFileNameWithoutExtension(app.LaunchPath);
        return string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stem, name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesContains(InstalledAppInfo app, string name) =>
        app.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(app.LaunchPath).Contains(name, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<InstalledAppInfo> Distinct(IEnumerable<InstalledAppInfo> apps) =>
        apps.GroupBy(app => app.LaunchPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
}
