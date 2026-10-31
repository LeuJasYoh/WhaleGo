using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Windows;

namespace WhaleGo.Platform.Privacy;

/// <summary>
/// Privacy day-1 rules (docs/PROTOCOL.md §11): password value masking, sensitive-window matching
/// (title + process name against privacy.sensitiveWindows globs) and WhaleGo self-process exclusion.
/// The matcher lives in Contracts.Domain.PrivacyRules (pure) — this type only holds configuration and the
/// window/process glue.
/// </summary>
public sealed class Redactor
{
    private readonly string[] _patterns;

    public Redactor(IEnumerable<string>? sensitiveWindowPatterns = null, bool maskPasswordFields = true)
    {
        _patterns = (sensitiveWindowPatterns ?? ConfigDefaults.SensitiveWindows)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        MaskPasswordFields = maskPasswordFields;
    }

    public IReadOnlyList<string> SensitiveWindowPatterns => _patterns;

    public bool MaskPasswordFields { get; }

    /// <summary>Mask a value when the element is a password field and masking is enabled (PrivacyRules.MaskValue).</summary>
    public string? RedactValue(string? value, bool isPassword) => PrivacyRules.MaskValue(value, isPassword, MaskPasswordFields);

    public bool IsPasswordMasked(bool isPassword) => isPassword && MaskPasswordFields;

    /// <summary>Sensitive window match against title and process name using the configured glob list.</summary>
    public bool IsSensitiveWindow(string? title, string? processName) =>
        PrivacyRules.IsSensitiveWindow(title, _patterns) || PrivacyRules.IsSensitiveWindow(processName, _patterns);

    public bool IsSensitiveWindow(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var processName = Win32.TryGetProcessName(window.Pid, out var name) ? name : null;
        return IsSensitiveWindow(window.Title, processName);
    }

    /// <summary>WhaleGo's own processes are never enumerated/observed/captured (fixtures are targetable).</summary>
    public bool IsSelfProcess(string? processName) => PrivacyRules.IsSelfProcess(processName);
}
