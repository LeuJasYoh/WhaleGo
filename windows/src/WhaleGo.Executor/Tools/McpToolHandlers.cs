using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Application;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Tools;

/// <summary>
/// MCP handler surface: tools/list comes straight from the contract file, call_tool dispatches to application services.
/// Internal args (session_id/request_id/scope) are stripped before domain validation and never surface to the model.
/// </summary>
public sealed class McpToolHandlers
{
    private readonly ContractToolCatalog _catalog;
    private readonly IServiceProvider _services;
    private readonly ExecutorRuntimeState _runtime;
    private readonly StatusService _status;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpToolHandlers>? _logger;

    public McpToolHandlers(
        ContractToolCatalog catalog,
        IServiceProvider services,
        ExecutorRuntimeState runtime,
        StatusService status,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpToolHandlers>? logger = null)
    {
        _catalog = catalog;
        _services = services;
        _runtime = runtime;
        _status = status;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public ListToolsResult ListTools() => new() { Tools = _catalog.Tools.ToList() };

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        var telemetry = new Telemetry();
        string? sessionId = null;
        string result = "ok";
        string? errorCode = null;
        try
        {
            var negotiated = context.Server?.NegotiatedProtocolVersion;
            if (!string.IsNullOrWhiteSpace(negotiated))
            {
                _runtime.LastMcpProtocolVersion = negotiated;
            }

            var arguments = ToolArguments.Parse(context.Params?.Arguments);
            SessionResolver.ValidateInternalOrThrow(arguments.InternalSessionId);
            sessionId = ResolveSession(arguments, context.Server?.SessionId);
            telemetry.SessionTag = SessionResolver.Mask(sessionId);
            var resultValue = await DispatchAsync(name, arguments, context, sessionId, telemetry, cancellationToken).ConfigureAwait(false);
            return resultValue;
        }
        catch (WhaleGoToolException exception)
        {
            errorCode = exception.Error.Code;
            result = "error";
            return ToolResults.Error(exception.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            errorCode = ErrorCodes.Internal;
            result = "error";
            _logger?.LogError(exception, "tool call failed: {Tool}", name);
            return ToolResults.InternalError(exception);
        }
        finally
        {
            _status.IncrementTool(name);
            _status.RecordRequest(new RecentRequest(
                TsMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tool: name,
                SessionTag: telemetry.SessionTag ?? (sessionId is null ? null : SessionResolver.Mask(sessionId)),
                Op: telemetry.Op,
                Result: result,
                ElapsedMs: (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                ErrorCode: errorCode,
                Elements: telemetry.Elements,
                HasScreenshot: telemetry.HasScreenshot,
                LeaseWaitMs: telemetry.LeaseWaitMs));
        }
    }

    private async ValueTask<CallToolResult> DispatchAsync(
        string name,
        ToolArguments arguments,
        RequestContext<CallToolRequestParams> context,
        string sessionId,
        Telemetry telemetry,
        CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "go_health":
                arguments.EnsureOnly();
                return Health(arguments, context, sessionId);

            case "go_apps":
                arguments.EnsureOnly("include_windows");
                return Apps(arguments);

            case "go_windows":
                arguments.EnsureOnly("app_id");
                return Windows(arguments);

            case "go_observe":
                arguments.EnsureOnly("window_id", "app_id", "include_screenshot", "settle_ms", "max_elements", "max_value_chars");
                return await ObserveAsync(arguments, sessionId, telemetry, cancellationToken).ConfigureAwait(false);

            case "go_act":
                arguments.EnsureOnly("op", "target", "args");
                return await ActAsync(arguments, sessionId, telemetry, cancellationToken).ConfigureAwait(false);

            case "go_launch":
                arguments.EnsureOnly("app", "app_id", "wait_ms");
                return await LaunchAsync(arguments, sessionId, telemetry, cancellationToken).ConfigureAwait(false);

            case "go_stop":
                arguments.EnsureOnly("reason");
                return Stop(arguments, sessionId);

            default:
                throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "未知工具：" + name + "。");
        }
    }

    private CallToolResult Health(ToolArguments arguments, RequestContext<CallToolRequestParams> context, string sessionId)
    {
        var health = _services.GetRequiredService<HealthService>();
        var report = health.GetHealth(sessionId, context.Server?.NegotiatedProtocolVersion);
        var okLine = "OK: WhaleGo " + ProtocolInfo.ExecutorVersion + " · protocol v" + ProtocolInfo.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " · accepting_actions=" + (report.AcceptingActions ? "true" : "false");
        return ToolResults.QueryOk(okLine, report);
    }

    private CallToolResult Apps(ToolArguments arguments)
    {
        var includeWindows = arguments.GetBool("include_windows") ?? false;
        var windows = _services.GetRequiredService<WindowService>();
        var report = windows.ListApps(includeWindows);
        var foreground = report.Apps.FirstOrDefault(app => app.Foreground);
        var okLine = "OK: 应用 " + report.Apps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个" +
                     (foreground is null ? string.Empty : "（前台 " + foreground.Name + "）");
        return ToolResults.QueryOk(okLine, report);
    }

    private CallToolResult Windows(ToolArguments arguments)
    {
        var appId = arguments.GetString("app_id");
        var windows = _services.GetRequiredService<WindowService>();
        var report = windows.ListWindows(appId);
        var foreground = report.Windows.FirstOrDefault(window => window.Foreground);
        var okLine = "OK: 窗口 " + report.Windows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个" +
                     (foreground is null ? string.Empty : "（前台 " + foreground.WindowId + "）");
        return ToolResults.QueryOk(okLine, report);
    }

    private async ValueTask<CallToolResult> ObserveAsync(
        ToolArguments arguments,
        string sessionId,
        Telemetry telemetry,
        CancellationToken cancellationToken)
    {
        var windowId = arguments.GetString("window_id");
        var appId = arguments.GetString("app_id");
        if (string.IsNullOrWhiteSpace(windowId) && string.IsNullOrWhiteSpace(appId))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "需要 window_id 或 app_id。");
        }

        var includeScreenshot = arguments.GetBool("include_screenshot") ?? false;
        var settleMs = arguments.GetInt("settle_ms") ?? 250;
        if (settleMs is < 0 or > 2000)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "settle_ms 必须在 0..2000。");
        }

        var maxElements = arguments.GetInt("max_elements") ?? ConfigDefaults.LimitsMaxObserveElements;
        if (maxElements is < 1 or > 2000)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "max_elements 必须在 1..2000。");
        }

        var maxValueChars = arguments.GetInt("max_value_chars") ?? 48;
        if (maxValueChars is < 1 or > 4000)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "max_value_chars 必须在 1..4000。");
        }

        var observation = _services.GetRequiredService<ObservationService>();
        var result = await observation.ObserveAsync(
            sessionId,
            new ObserveRequest(windowId, appId, includeScreenshot, settleMs, maxElements, maxValueChars),
            cancellationToken).ConfigureAwait(false);
        telemetry.Elements = result.Elements.Count;
        telemetry.HasScreenshot = result.PngBytes is { Length: > 0 };
        return ToolResults.ObserveOk(result.PngBytes, result.OkSummary, result.Report, result.TreeText);
    }

    private async ValueTask<CallToolResult> ActAsync(
        ToolArguments arguments,
        string sessionId,
        Telemetry telemetry,
        CancellationToken cancellationToken)
    {
        var op = arguments.GetString("op");
        if (string.IsNullOrWhiteSpace(op))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "缺少 op。");
        }

        var target = arguments.GetObject("target");
        if (target is null)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "缺少 target（所有动作必须带 target）。");
        }

        var actionArgs = arguments.GetObject("args");
        var actions = _services.GetRequiredService<ActionService>();
        var outcome = await actions.ActAsync(sessionId, arguments.InternalRequestId, op, target.Value, actionArgs, cancellationToken).ConfigureAwait(false);
        telemetry.Op = outcome.Report.Op;
        telemetry.LeaseWaitMs = outcome.LeaseWaitMs;
        return ToolResults.QueryOk(ToolResults.ActSuccessLine, outcome.Report);
    }

    private async ValueTask<CallToolResult> LaunchAsync(
        ToolArguments arguments,
        string sessionId,
        Telemetry telemetry,
        CancellationToken cancellationToken)
    {
        var app = arguments.GetString("app");
        var appId = arguments.GetString("app_id");
        if ((string.IsNullOrWhiteSpace(app)) == (string.IsNullOrWhiteSpace(appId)))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "app 与 app_id 必须二选一。");
        }

        var waitMs = arguments.GetInt("wait_ms") ?? 5000;
        if (waitMs is < 0 or > 10_000)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "wait_ms 必须在 0..10000。");
        }

        var launch = _services.GetRequiredService<AppLaunchService>();
        var outcome = await launch.LaunchAsync(sessionId, app, appId, waitMs, cancellationToken).ConfigureAwait(false);
        telemetry.Op = "launch";
        telemetry.LeaseWaitMs = outcome.LeaseWaitMs;
        return ToolResults.QueryOk(outcome.OkSummary, outcome.Report);
    }

    private CallToolResult Stop(ToolArguments arguments, string sessionId)
    {
        var scope = arguments.InternalScope;
        if (string.Equals(scope, "all", StringComparison.Ordinal))
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "scope=all 仅限本机管理员命令；模型只能停止本会话。");
        }

        var reason = arguments.GetString("reason");
        if (reason is not null && reason.Length > 200)
        {
            throw WhaleGoToolException.FromCode(ErrorCodes.InvalidArgument, message: "reason 超过 200 字符上限。");
        }

        var sessions = _services.GetRequiredService<SessionManager>();
        sessions.StopSession(sessionId, string.IsNullOrWhiteSpace(reason) ? "model_stop" : reason);
        var report = new StopReport(ProtocolInfo.StopSchemaVersion, Ok: true, Stopped: true, Scope: "session", SessionsAffected: 1);
        return ToolResults.QueryOk("OK: 已停止本会话（scope=session）；go_observe/go_health 仍可用，恢复仅限本机。", report);
    }

    private string ResolveSession(ToolArguments arguments, string? mcpSessionId)
    {
        var header = _httpContextAccessor.HttpContext?.Request.Headers[SessionResolver.SessionHeader].ToString();
        if (!string.IsNullOrWhiteSpace(header) && !SessionResolver.IsValidBusinessSessionId(header))
        {
            header = null;
        }

        return SessionResolver.Resolve(arguments.InternalSessionId, header, mcpSessionId);
    }

    private sealed class Telemetry
    {
        public string? SessionTag { get; set; }

        public string? Op { get; set; }

        public int? Elements { get; set; }

        public bool HasScreenshot { get; set; }

        public long? LeaseWaitMs { get; set; }
    }
}

