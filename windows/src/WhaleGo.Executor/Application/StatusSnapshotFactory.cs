using WhaleGo.Contracts;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Application;

/// <summary>Builds the GUI-facing StatusSnapshot from the same state that feeds go_health (single mapping point).</summary>
public static class StatusSnapshotFactory
{
    public static StatusSnapshot Build(
        SessionManager sessions,
        Infrastructure.Config.ConfigService config,
        Infrastructure.ExecutorRuntimeState runtime,
        Infrastructure.StatusService status,
        Infrastructure.Secrets.AuditService audit,
        Contracts.Abstractions.ISystemProbe systemProbe,
        long uptimeMs)
    {
        var current = config.Config;
        var paused = sessions.Paused;
        var stopped = sessions.Stopped;
        var anySessionStopped = false;
        foreach (var session in sessions.SnapshotSessions)
        {
            if (session.SessionStopped)
            {
                anySessionStopped = true;
                break;
            }
        }

        var controller = sessions.GetControllerStatus(null);
        var locked = false;
        try
        {
            locked = systemProbe.GetFacts().IsLocked;
        }
        catch (System.Exception)
        {
            // health/diagnostics must not fail because a probe is unavailable
        }

        var state = stopped ? ControlState.Stopped
            : paused ? ControlState.Paused
            : runtime.FaultedReason is not null ? ControlState.Faulted
            : ControlState.Running;

        var controllerInfo = controller is null
            ? null
            : new ControllerStatus(controller.Held, controller.Mine, controller.ExpiresInMs ?? 0);

        return new StatusSnapshot(
            State: state,
            AcceptingActions: !paused && !stopped && !anySessionStopped,
            Locked: locked,
            UptimeMs: uptimeMs,
            Version: ProtocolInfo.ExecutorVersion,
            ProtocolVersion: ProtocolInfo.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            McpProtocolVersion: runtime.LastMcpProtocolVersion,
            Paused: paused,
            PauseReason: sessions.PauseReason,
            PausedSinceMs: sessions.PausedSinceMs,
            Stopped: stopped,
            SessionStopped: anySessionStopped,
            Readonly: current.Readonly,
            ScreenshotEnabled: current.ScreenshotEnabled,
            ScreenshotDefaultInclude: current.ScreenshotDefaultInclude,
            AuditEnabled: current.AuditEnabled,
            AuditDurable: current.AuditRequireDurableForActions,
            ListenRequested: runtime.ListenRequested ?? string.Empty,
            ListenActual: runtime.ListenActual ?? string.Empty,
            Sessions: sessions.Count,
            Controller: controllerInfo,
            Warnings: runtime.Warnings);
    }
}