using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Abstractions;

/// <summary>Read-only diagnostics query surface shared by the settings window and tray.</summary>
public interface IStatusQuery
{
    StatusSnapshot GetStatus();
    IReadOnlyList<RecentRequest> GetRecentRequests(int limit);
    IReadOnlyDictionary<string, long> GetToolCounters();
}

/// <summary>
/// The ONLY kernel port the GUI may use (GUI references Contracts only).
/// Resume/ResetStop are local-machine-only operations; the GUI must apply a second confirmation before calling them.
/// </summary>
public interface IExecutorControl : IStatusQuery
{
    void Pause(string reason);
    void Resume();
    void Stop(string reason);
    void ResetStop();
    ConfigPatch GetConfig();
    IReadOnlyDictionary<string, string> GetConfigSources();
    void SetConfig(ConfigPatch patch);
    IReadOnlyList<TokenDescriptor> GetTokens();
    string RotateToken(string name);
    string RevealTokenForCopy();
    SelfCheckReport RunSelfCheck();
    IReadOnlyList<string> ReadAuditTail(int lines);
    void OpenDataFolder();
    void RequestRestart();
}

/// <summary>DWM dark title bar helper, implemented by Platform, consumed by the GUI (GUI never P/Invokes).</summary>
public interface IWindowTheme
{
    bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark);
}

/// <summary>Global hotkey registration, implemented by Platform, consumed by the GUI/host (GUI never P/Invokes).</summary>
public interface IHotkeyRegistrar
{
    bool TryRegister(string id, string gesture, Action onPressed);
    void Unregister(string id);
}

public interface IConfigStore
{
    ConfigPatch Current { get; }
    IReadOnlyDictionary<string, string> Sources { get; }
    void Save(ConfigPatch patch);
    string? SourceOf(string key);
}

public interface ITokenService
{
    string Generate();
    string Rotate();
    bool Verify(string presented);
}

public interface ISelfCheck
{
    SelfCheckReport Run();
}

/// <summary>Audit sink. Action records MUST be durable before the tool returns (fail-closed); observe records may be async.</summary>
public interface IAuditSink
{
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken ct);
}
