using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;

namespace WhaleGo.Executor.Application;

public sealed record LaunchOutcome(LaunchReport Report, string OkSummary, long LeaseWaitMs);

/// <summary>
/// go_launch: allowlist-only start or activate-existing. Write action → durable audit preflight is fail-closed
/// (AUDIT_UNAVAILABLE and nothing launched if the audit record cannot be persisted).
/// </summary>
public sealed class AppLaunchService
{
    private readonly SessionManager _sessions;
    private readonly WindowService _windows;
    private readonly IAppLauncher _launcher;
    private readonly ConfigService _config;
    private readonly AuditService _audit;
    private readonly ISystemProbe _systemProbe;
    private readonly IPlatformClock _clock;
    private readonly ILogger<AppLaunchService>? _logger;

    public AppLaunchService(
        SessionManager sessions,
        WindowService windows,
        IAppLauncher launcher,
        ConfigService config,
        AuditService audit,
        ISystemProbe systemProbe,
        IPlatformClock clock,
        ILogger<AppLaunchService>? logger = null)
    {
        _sessions = sessions;
        _windows = windows;
        _launcher = launcher;
        _config = config;
        _audit = audit;
        _systemProbe = systemProbe;
        _clock = clock;
        _logger = logger;
    }

    public async Task<LaunchOutcome> LaunchAsync(
        string sessionId,
        string? app,
        string? appId,
        int waitMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var hasApp = !string.IsNullOrWhiteSpace(app);
        var hasAppId = !string.IsNullOrWhiteSpace(appId);
        if (hasApp == hasAppId)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "app 与 app_id 必须二选一。");
        }

        if (waitMs is < 0 or > 10_000)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "wait_ms 必须在 0..10000。");
        }

        var config = _config.Config;
        var session = _sessions.GetOrCreate(sessionId);
        EnsureAllowed(session, config);

        LaunchEntryConfig? entry = null;
        if (hasApp)
        {
            var name = app!.Trim();
            entry = config.LaunchAllowlist.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null && !config.LaunchEnforceAllowlist && IsAbsoluteExePath(name))
            {
                // launch.enforceAllowlist=false (local GUI policy escape hatch): absolute .exe path allowed.
                // Still no PATH lookup, no relative paths, no arguments; audited as a write action.
                entry = new LaunchEntryConfig(name, name);
            }

            if (entry is null)
            {
                throw WhaleGoToolException.FromCode(
                    ErrorCodes.InvalidArgument,
                    message: config.LaunchEnforceAllowlist
                        ? $"只能启动 allowlist 内的应用（{string.Join("/", config.LaunchAllowlistNames)}）；关闭白名单可在 Windows 设置「执行策略」里操作。"
                        : "启动白名单已关闭：app 必须是白名单名称或存在的绝对 .exe 路径。");
            }
        }

        var stopped = Stopwatch.StartNew();
        long leaseWaitMs = 0;
        if (hasAppId)
        {
            var windows = _windows.GetAppWindows(appId!.Trim());
            if (windows.Count == 0)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.AppNotFound);
            }

            var lease = await _sessions.AcquireControllerLeaseAsync(sessionId, config.ControllerWaitMs, cancellationToken).ConfigureAwait(false);
            leaseWaitMs = lease.WaitMs;
            if (!lease.Acquired)
            {
                if (lease.BlockCode is not null)
                {
                    throw WhaleGoToolException.FromCode(lease.BlockCode);
                }

                throw WhaleGoToolException.FromCode(
                    ErrorCodes.ControllerBusy,
                    details: new Dictionary<string, object?>
                    {
                        ["controller"] = new Dictionary<string, object?> { ["held"] = true, ["mine"] = false },
                    },
                    retryAfterMs: lease.RetryAfterMs);
            }
        }

        try
        {
            await _audit.PreflightActionAsync("go_launch", sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditUnavailableException)
        {
            _sessions.ReleaseControllerLease(sessionId);
            throw WhaleGoToolException.FromCode(ErrorCodes.AuditUnavailable);
        }

        LaunchResult result;
        try
        {
            result = await _launcher.LaunchAsync(
                new LaunchRequest(
                    AppName: entry?.Name,
                    AppId: hasAppId ? appId!.Trim() : null,
                    WaitMs: waitMs,
                    AllowAbsolutePath: !config.LaunchEnforceAllowlist),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = PlatformErrors.TryGetCode(ex);
            _logger?.LogWarning(ex, "go_launch failed");
            WriteAudit(sessionId, hasApp ? app : null, appId, result: null, elapsedMs: stopped.ElapsedMilliseconds, errorCode: code ?? ErrorCodes.LaunchFailed);
            throw WhaleGoToolException.FromCode(code == ErrorCodes.LaunchTimeout ? ErrorCodes.LaunchTimeout : ErrorCodes.LaunchFailed);
        }

        // Mirrors WhaleGo.Platform.Apps.AppLauncher's documented mapping without taking a Platform dependency:
        // started-but-no-window => LAUNCH_TIMEOUT (never retry); nothing started => LAUNCH_FAILED; activation => success.
        if (IndicatesLaunchTimeout(result))
        {
            WriteAudit(sessionId, hasApp ? app : null, appId, result, elapsedMs: stopped.ElapsedMilliseconds, errorCode: ErrorCodes.LaunchTimeout);
            throw WhaleGoToolException.FromCode(ErrorCodes.LaunchTimeout);
        }

        if (IndicatesLaunchFailure(result) || (!result.Launched && !result.Activated))
        {
            WriteAudit(sessionId, hasApp ? app : null, appId, result: null, elapsedMs: stopped.ElapsedMilliseconds, errorCode: ErrorCodes.LaunchFailed);
            throw WhaleGoToolException.FromCode(ErrorCodes.LaunchFailed);
        }

        if (!result.Settled)
        {
            WriteAudit(sessionId, hasApp ? app : null, appId, result, elapsedMs: stopped.ElapsedMilliseconds, errorCode: ErrorCodes.LaunchTimeout);
            throw WhaleGoToolException.FromCode(ErrorCodes.LaunchTimeout);
        }

        WriteAudit(sessionId, hasApp ? app : null, appId, result, elapsedMs: stopped.ElapsedMilliseconds, errorCode: null);
        var report = new LaunchReport(
            SchemaVersion: ProtocolInfo.LaunchSchemaVersion,
            Launched: result.Launched,
            Activated: result.Activated,
            App: result.AppId is null && result.Pid is null ? null : new LaunchAppReport(result.AppId ?? string.Empty, result.Pid),
            Window: result.Window is null ? null : new LaunchWindowReport(result.Window.WindowId, result.Window.Title, result.Window.IsOnscreen),
            Settled: result.Settled);
        var okSummary = BuildOkSummary(result, hasApp ? entry!.Name : null);
        return new LaunchOutcome(report, okSummary, leaseWaitMs);
    }

    private void EnsureAllowed(GoSession session, ExecutorConfig config)
    {
        var block = _sessions.ActionBlockCode(session);
        if (block is not null)
        {
            throw WhaleGoToolException.FromCode(block);
        }

        if (config.Readonly)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.ReadonlyMode);
        }

        try
        {
            if (_systemProbe.GetFacts().IsLocked)
            {
                throw WhaleGoToolException.FromCode(ErrorCodes.SessionLocked);
            }
        }
        catch (WhaleGoToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // probe unavailable: fail-open only for the lock check (actions still guarded by audit/policy)
        }

        if (!_sessions.CheckActRate(session, out var retryAfterMs))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.RateLimited, retryAfterMs: retryAfterMs);
        }

        session.RecordAct(_clock.NowMs);
    }

    private static bool IsAbsoluteExePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IndicatesLaunchTimeout(LaunchResult result) =>
        result is { Launched: false, Activated: false, Pid: not null, Window: null };

    private static bool IndicatesLaunchFailure(LaunchResult result) =>
        result is { Launched: false, Activated: false, Pid: null, Window: null };

    private static string BuildOkSummary(LaunchResult result, string? allowlistName)
    {
        var target = allowlistName ?? result.AppId ?? "app";
        if (result.Launched)
        {
            var pid = result.Pid is null ? string.Empty : " pid=" + result.Pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var window = result.Window is null ? string.Empty : " window=" + result.Window.WindowId;
            return "OK: 已启动 " + target + pid + window + " settled=" + (result.Settled ? "true" : "false");
        }

        var activatedWindow = result.Window is null ? string.Empty : " window=" + result.Window.WindowId;
        return "OK: 已激活 " + target + activatedWindow + " settled=" + (result.Settled ? "true" : "false");
    }

    private void WriteAudit(string sessionId, string? app, string? appId, LaunchResult? result, long elapsedMs, string? errorCode)
    {
        var auditEvent = new AuditEvent(
            TsMs: _clock.NowMs,
            Kind: AuditEvent.KindAction,
            SessionId: sessionId,
            Tool: "go_launch",
            Op: "launch",
            AppId: result?.AppId ?? appId,
            ActionSent: true,
            ElapsedMs: elapsedMs,
            ErrorCode: errorCode,
            Event: app is not null ? "launch" : "activate",
            Reason: app);
        try
        {
            _audit.WriteActionAsync(auditEvent, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (AuditUnavailableException)
        {
            // Preflight already proved audit writability; a late failure is logged, never allowed to mask the launch outcome.
            _logger?.LogError("go_launch audit record could not be persisted after dispatch");
        }
    }
}