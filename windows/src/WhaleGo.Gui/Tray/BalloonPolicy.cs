using System.Text.RegularExpressions;
using WhaleGo.Gui.Infrastructure;

namespace WhaleGo.Gui.Tray;

/// <summary>ui.notificationsLevel values (DESIGN section 5.8): off / important / all.</summary>
public enum BalloonLevel
{
    Off = 0,
    Important = 1,
    All = 2,
}

/// <summary>Balloon triggers from docs/GUI.md section 5.3.</summary>
public enum BalloonKind
{
    EmergencyStop = 0,
    ModelStop = 1,
    StartupFailure = 2,
    AuditFailure = 3,
    AuthFailure = 4,
    AbnormalExit = 5,
    Info = 6,
    GenericImportant = 7,
    GenericInfo = 8,
}

public static class BalloonLevels
{
    public static BalloonLevel ParseConfig(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => BalloonLevel.Off,
        "all" => BalloonLevel.All,
        _ => BalloonLevel.Important,
    };

    public static bool IsImportant(BalloonKind kind) => kind is
        BalloonKind.EmergencyStop or BalloonKind.ModelStop or
        BalloonKind.StartupFailure or BalloonKind.AuditFailure or BalloonKind.GenericImportant;
}

/// <summary>
/// "宁可少、不可吵": same-kind 30s cooldown, at most 3 balloons per minute globally, level filtering, and
/// sanitization (no token, no newlines, &lt;=140 chars). Clock injected for tests.
/// </summary>
public sealed class BalloonPolicy
{
    public const int SameKindCooldownMs = 30_000;
    public const int GlobalWindowMs = 60_000;
    public const int GlobalMaxPerWindow = 3;
    public const int MaxMessageChars = 140;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<BalloonKind, long> _lastShown = [];
    private readonly Queue<long> _shownAt = [];

    public BalloonPolicy(Func<long> nowMs)
    {
        ArgumentNullException.ThrowIfNull(nowMs);
        _nowMs = nowMs;
    }

    public bool Evaluate(BalloonKind kind, BalloonLevel level)
    {
        if (level == BalloonLevel.Off)
        {
            return false;
        }

        if (level == BalloonLevel.Important && !BalloonLevels.IsImportant(kind))
        {
            return false;
        }

        var now = _nowMs();
        if (_lastShown.TryGetValue(kind, out var last) && now - last < SameKindCooldownMs)
        {
            return false;
        }

        while (_shownAt.Count > 0 && now - _shownAt.Peek() >= GlobalWindowMs)
        {
            _shownAt.Dequeue();
        }

        return _shownAt.Count < GlobalMaxPerWindow;
    }

    public void RecordShown(BalloonKind kind)
    {
        var now = _nowMs();
        _lastShown[kind] = now;
        _shownAt.Enqueue(now);
    }

    public void Reset()
    {
        _lastShown.Clear();
        _shownAt.Clear();
    }

    /// <summary>Flattens to one line, redacts credentials and truncates to 140 chars.</summary>
    public static string Sanitize(string? message, int maxChars = MaxMessageChars)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var singleLine = Regex.Replace(message, @"\s+", " ").Trim();
        var redacted = TokenRedactor.Redact(singleLine);
        return redacted.Length <= maxChars ? redacted : redacted[..(maxChars - 1)] + "…";
    }
}
