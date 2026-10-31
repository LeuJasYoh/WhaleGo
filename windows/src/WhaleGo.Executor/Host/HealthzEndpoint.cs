using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Domain;
using WhaleGo.Executor.Application;
using WhaleGo.Executor.Infrastructure;

namespace WhaleGo.Executor.Host;

/// <summary>/healthz payload (PROTOCOL §1): exactly {ok,version,protocol_version,uptime_s,paused,stopped,accepting_actions}.</summary>
public sealed record HealthzPayload(
    bool Ok,
    string Version,
    int ProtocolVersion,
    long UptimeS,
    bool Paused,
    bool Stopped,
    bool AcceptingActions);

public static class HealthzEndpoint
{
    public static Task HandleAsync(HttpContext context)
    {
        var sessions = (SessionManager)(context.RequestServices.GetService(typeof(SessionManager))
            ?? throw new InvalidOperationException("SessionManager not registered"));
        var runtime = (ExecutorRuntimeState)(context.RequestServices.GetService(typeof(ExecutorRuntimeState))
            ?? throw new InvalidOperationException("ExecutorRuntimeState not registered"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new HealthzPayload(
            Ok: true,
            Version: ProtocolInfo.ExecutorVersion,
            ProtocolVersion: ProtocolInfo.ProtocolVersion,
            UptimeS: Math.Max(0, now - runtime.StartedAtMs) / 1000,
            Paused: sessions.Paused,
            Stopped: sessions.Stopped,
            AcceptingActions: !sessions.Paused && !sessions.Stopped);
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(WireJson.Serialize(payload) + "\n");
    }
}
