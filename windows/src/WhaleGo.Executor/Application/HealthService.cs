using System;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;

namespace WhaleGo.Executor.Application;

/// <summary>Builds the single go_health response (whalego-health-v1) from live state.</summary>
public sealed class HealthService
{
    private static readonly string[] ActionCapabilities = ["click", "set_value", "type", "key", "scroll"];
    private static readonly string[] TargetCapabilities = ["element", "coordinate"];
    private static readonly string[] LaunchCapabilities = ["allowlist", "activate"];

    private readonly ConfigService _config;
    private readonly SessionManager _sessions;
    private readonly AuditService _audit;
    private readonly ISystemProbe _systemProbe;
    private readonly ExecutorRuntimeState _runtime;
    private readonly IPlatformClock _clock;

    public HealthService(
        ConfigService config,
        SessionManager sessions,
        AuditService audit,
        ISystemProbe systemProbe,
        ExecutorRuntimeState runtime,
        IPlatformClock clock)
    {
        _config = config;
        _sessions = sessions;
        _audit = audit;
        _systemProbe = systemProbe;
        _runtime = runtime;
        _clock = clock;
    }

    public HealthReport GetHealth(string sessionId, string? mcpProtocolVersion)
    {
        var config = _config.Config;
        var session = _sessions.GetOrCreate(sessionId);
        var facts = ReadFacts();
        var paused = _sessions.Paused;
        var stopped = _sessions.Stopped;
        var sessionStopped = session.SessionStopped || stopped;
        var controller = _sessions.GetControllerStatus(sessionId);

        var observation = new List<string> { "uia_tree" };
        if (config.ScreenshotEnabled)
        {
            observation.Add("screenshot_printwindow");
        }

        var now = _clock.NowMs;
        var uptimeMs = Math.Max(0, now - _runtime.StartedAtMs);
        return new HealthReport(
            SchemaVersion: ProtocolInfo.HealthSchemaVersion,
            Ok: true,
            Version: ProtocolInfo.ExecutorVersion,
            ProtocolVersion: ProtocolInfo.ProtocolVersion,
            McpProtocolVersion: string.IsNullOrWhiteSpace(mcpProtocolVersion)
                ? _runtime.LastMcpProtocolVersion
                : mcpProtocolVersion,
            Os: new HealthOsInfo(
                WindowsVersion: facts.WindowsVersion,
                WindowsSessionId: facts.WindowsSessionId,
                Interactive: facts.IsInteractive,
                Locked: facts.IsLocked,
                Integrity: facts.Integrity,
                DpiAwareness: facts.DpiAwareness),
            Capabilities: new HealthCapabilities(
                Observation: observation,
                Actions: ActionCapabilities,
                Targets: TargetCapabilities,
                Launch: LaunchCapabilities,
                LaunchAllowlist: config.LaunchAllowlistNames),
            Paused: paused,
            PauseReason: paused ? _sessions.PauseReason : null,
            PausedSinceMs: paused ? _sessions.PausedSinceMs : null,
            Stopped: stopped,
            SessionStopped: sessionStopped,
            AcceptingActions: !paused && !stopped && !session.SessionStopped,
            UptimeS: uptimeMs / 1000,
            Sessions: _sessions.Count,
            Controller: controller is null
                ? null
                : new HealthControllerInfo(controller.Held, controller.Mine, controller.ExpiresInMs ?? 0),
            Limits: new HealthLimitsInfo(
                MaxObserveElements: config.MaxObserveElements,
                MaxObserveChars: config.MaxObserveChars,
                MaxImageBytes: config.MaxImageBytes,
                ActRatePerMinute: config.ActRatePerMinute,
                StateTtlSeconds: config.StateTtlSeconds,
                FrameTtlSeconds: config.FrameTtlSeconds,
                ToolTimeoutSeconds: config.ToolTimeoutSeconds),
            AuditDropped: _audit.DroppedCount);
    }

    private SystemFacts ReadFacts()
    {
        try
        {
            return _systemProbe.GetFacts();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SystemFacts(
                WindowsVersion: Environment.OSVersion.Version.ToString(),
                WindowsSessionId: 0,
                IsInteractive: false,
                IsLocked: false,
                Integrity: "unknown",
                DpiAwareness: "unknown",
                ProcessId: Environment.ProcessId,
                IsRemoteSession: false,
                UptimeMs: _runtime.StartedAtMs);
        }
    }
}