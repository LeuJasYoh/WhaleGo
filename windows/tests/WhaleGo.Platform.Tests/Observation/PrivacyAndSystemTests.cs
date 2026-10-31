using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Privacy;
using WhaleGo.Platform.SystemInfo;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class RedactorTests
{
    [Fact]
    public void MasksPasswordValues()
    {
        var redactor = new Redactor();
        Assert.Equal(PrivacyRules.MaskedValue, redactor.RedactValue("hunter2", isPassword: true));
        Assert.Equal("plain", redactor.RedactValue("plain", isPassword: false));
        // Contracts.Domain.PrivacyRules.MaskValue masks regardless of the underlying value (null included).
        Assert.Equal(PrivacyRules.MaskedValue, redactor.RedactValue(null, isPassword: true));

        var noMask = new Redactor(maskPasswordFields: false);
        Assert.False(noMask.MaskPasswordFields);
        Assert.Equal("hunter2", noMask.RedactValue("hunter2", isPassword: true));
    }

    [Fact]
    public void MatchesSensitiveWindowsByTitleAndProcessGlob()
    {
        var redactor = new Redactor(["*password*", "*bank*"]);
        Assert.True(redactor.IsSensitiveWindow("My Bank Login", null));
        Assert.True(redactor.IsSensitiveWindow(null, "BankHelper.exe"));
        Assert.True(redactor.IsSensitiveWindow("Password manager", "chrome"));
        Assert.False(redactor.IsSensitiveWindow("Notepad", "notepad"));
        Assert.False(redactor.IsSensitiveWindow(null, null));

        var empty = new Redactor([]);
        Assert.False(empty.IsSensitiveWindow("My Bank Login", null));
    }

    [Fact]
    public void SelfProcessDetectionMatchesContractsRules()
    {
        var redactor = new Redactor();
        Assert.True(redactor.IsSelfProcess("WhaleGo"));
        Assert.True(redactor.IsSelfProcess("WhaleGo.Executor"));
        Assert.True(redactor.IsSelfProcess("WhaleGo.Gui"));
        Assert.False(redactor.IsSelfProcess("notepad"));
        Assert.False(redactor.IsSelfProcess(null));
        Assert.True(PrivacyRules.IsSelfProcess("WhaleGo.Executor.Headless"));
    }
}

public sealed class SystemProbeTests
{
    [Fact]
    public void ProbesEnvironmentFactsWithoutThrowing()
    {
        var probe = new SystemProbe();
        var facts = probe.GetFacts();

        Assert.Equal(Environment.ProcessId, facts.ProcessId);
        Assert.StartsWith("Windows", facts.WindowsVersion);
        Assert.True(facts.UptimeMs > 0);
        Assert.True(facts.WindowsSessionId >= 0, $"session={facts.WindowsSessionId}");
        Assert.Contains(facts.Integrity, new[] { "untrusted", "low", "medium", "high", "system", "protected", "unknown" });
        Assert.Contains(facts.DpiAwareness, new[] { "per_monitor_v2", "per_monitor", "system", "unaware", "unknown" });
        Assert.False(string.IsNullOrWhiteSpace(facts.WindowsVersion));
        _ = facts.IsLocked; // exercised for stability; lock state depends on the interactive desktop
        _ = facts.IsInteractive;
        _ = facts.IsRemoteSession;
    }

    [Fact]
    public void HelpersReturnClosedVocabulary()
    {
        Assert.Equal("per_monitor_v2", SystemProbe.DescribeDpiAwarenessContext(new IntPtr(-4)));
        Assert.Equal("per_monitor", SystemProbe.DescribeDpiAwarenessContext(new IntPtr(-3)));
        Assert.Equal("system", SystemProbe.DescribeDpiAwarenessContext(new IntPtr(-2)));
        Assert.Equal("unaware", SystemProbe.DescribeDpiAwarenessContext(new IntPtr(-1)));
        Assert.Equal("unknown", SystemProbe.DescribeDpiAwarenessContext(IntPtr.Zero));
        Assert.Contains(SystemProbe.ReadIntegrityLevel(), new[] { "untrusted", "low", "medium", "high", "system", "protected", "unknown" });
        Assert.True(SystemProbe.GetSystemDpi() >= 96);
    }
}
