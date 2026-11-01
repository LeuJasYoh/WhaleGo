namespace WhaleGo.Contracts.Model;

/// <summary>How the platform starts a resolved launch target.</summary>
public enum LaunchTargetKind
{
    /// <summary>Direct executable path (Process.Start, no shell).</summary>
    Executable = 0,

    /// <summary>Desktop/Start Menu .lnk shortcut (ShellExecute resolves the target).</summary>
    Shortcut = 1,

    /// <summary>Windows app execution alias such as notepad.exe/calc.exe (ShellExecute).</summary>
    Alias = 2,
}

/// <summary>One concrete way to start an app: display name for humans plus the path/alias to execute.</summary>
public sealed record LaunchTarget(string DisplayName, string LaunchPath, LaunchTargetKind Kind);

/// <summary>A locally installed app discovered from the desktop, Start Menu, App Paths or system aliases.</summary>
public sealed record InstalledAppInfo(
    string DisplayName,
    string LaunchPath,
    LaunchTargetKind Kind,
    string? Source = null);

/// <summary>Result of resolving a model-provided app name against the local catalog.</summary>
public sealed record InstalledAppResolveResult(
    InstalledAppInfo? App,
    IReadOnlyList<InstalledAppInfo> Candidates,
    string? ErrorCode)
{
    public bool Ok => App is not null;

    public static InstalledAppResolveResult Found(InstalledAppInfo app) => new(app, [], null);

    public static InstalledAppResolveResult Missing() => new(null, [], "app_not_found");

    public static InstalledAppResolveResult Ambiguous(IReadOnlyList<InstalledAppInfo> candidates) =>
        new(null, candidates, "ambiguous_app");
}
