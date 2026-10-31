using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;

namespace WhaleGo.Executor.Application;

/// <summary>IExecutorControl implementation: the only kernel port the GUI/tray may use (local recovery only).</summary>
public sealed class ControlService : IExecutorControl
{
    private readonly ConfigService _config;
    private readonly SessionManager _sessions;
    private readonly StatusService _status;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;
    private readonly SelfCheckService _selfCheck;
    private readonly ExecutorRuntimeState _runtime;
    private readonly ExecutorPaths _paths;
    private readonly ISystemProbe _systemProbe;
    private readonly IPlatformClock _clock;

    public ControlService(
        ConfigService config,
        SessionManager sessions,
        StatusService status,
        TokenService tokens,
        AuditService audit,
        SelfCheckService selfCheck,
        ExecutorRuntimeState runtime,
        ExecutorPaths paths,
        ISystemProbe systemProbe,
        IPlatformClock clock)
    {
        _config = config;
        _sessions = sessions;
        _status = status;
        _tokens = tokens;
        _audit = audit;
        _selfCheck = selfCheck;
        _runtime = runtime;
        _paths = paths;
        _systemProbe = systemProbe;
        _clock = clock;
    }

    public StatusSnapshot GetStatus() =>
        StatusSnapshotFactory.Build(_sessions, _config, _runtime, _status, _audit, _systemProbe, UptimeMs());

    public IReadOnlyList<RecentRequest> GetRecentRequests(int limit) => _status.GetRecentRequests(limit);

    public IReadOnlyDictionary<string, long> GetToolCounters() => _status.GetToolCounters();

    public void Pause(string reason)
    {
        _sessions.Pause(reason);
        RecordControl("pause", reason);
    }

    public void Resume()
    {
        _sessions.Resume();
        RecordControl("resume", "local");
    }

    public void Stop(string reason)
    {
        _sessions.Stop(reason);
        RecordControl("stop", reason);
    }

    public void ResetStop()
    {
        _sessions.ResetStop();
        RecordControl("reset_stop", "local_confirm");
    }

    public ConfigPatch GetConfig() => _config.Current;

    public IReadOnlyDictionary<string, string> GetConfigSources() => _config.Sources;

    public void SetConfig(ConfigPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        _config.Save(patch);
        if (patch.Values.ContainsKey(ConfigKeys.ListenPort) || patch.Values.ContainsKey(ConfigKeys.ListenAddress))
        {
            _runtime.AddWarning("监听配置已修改，重启后生效");
        }

        RecordControl("config", "local");
    }

    public IReadOnlyList<TokenDescriptor> GetTokens() => _tokens.GetTokens();

    public string RotateToken(string name)
    {
        var token = _tokens.Rotate(name);
        RecordControl("token_rotate", name);
        return token;
    }

    public string RevealTokenForCopy() =>
        _tokens.TryGetPlaintextForCopy() ?? throw new InvalidOperationException("token 不可读，请先轮换 token。");

    public SelfCheckReport RunSelfCheck() => _selfCheck.Run();

    public IReadOnlyList<string> ReadAuditTail(int lines) => _audit.ReadTail(lines);

    public void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _paths.DataRoot + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best effort: the menu action must never crash the kernel.
        }
    }

    public void RequestRestart()
    {
        _runtime.RestartRequested = true;
        _runtime.Shutdown.Set();
    }

    private void RecordControl(string op, string? reason) =>
        _audit.RecordControl(new AuditEvent(
            TsMs: _clock.NowMs,
            Kind: AuditEvent.KindControl,
            Tool: "control",
            Op: op,
            Reason: reason,
            Actor: "local",
            Event: "control"));

    private long UptimeMs() => Math.Max(0, _clock.NowMs - _runtime.StartedAtMs);
}
