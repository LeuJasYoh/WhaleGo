using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

public sealed record ErrorInfo(RetryHint Retry, DispatchStatus DispatchStatus, string Message, bool PluginSide = false);

/// <summary>Code -> retry/dispatch/message table. Single source: docs/PROTOCOL.md section 8 (locked by contract tests).</summary>
public static class ErrorCatalog
{
    private static readonly Dictionary<string, ErrorInfo> Map = new(StringComparer.Ordinal)
    {
        [ErrorCodes.InvalidArgument] = new(RetryHint.Never, DispatchStatus.NotSent, "参数不合法，请检查后重试。"),
        [ErrorCodes.UnsupportedProtocol] = new(RetryHint.Never, DispatchStatus.NotSent, "协议版本不兼容，请升级 WhaleGo 执行器或插件。"),
        [ErrorCodes.UnsupportedAction] = new(RetryHint.Never, DispatchStatus.NotSent, "v1 不支持该动作（组合键/右键/双击/拖拽/粘贴等）；仅支持单击、set_value、type、单键、scroll。"),
        [ErrorCodes.SessionNotFound] = new(RetryHint.Never, DispatchStatus.NotSent, "会话不存在，请重新调用。"),
        [ErrorCodes.SessionExpired] = new(RetryHint.Never, DispatchStatus.NotSent, "会话已过期，请重新调用。"),
        [ErrorCodes.SessionStopped] = new(RetryHint.Never, DispatchStatus.NotSent, "已停止，需在 Windows 本机托盘/设置窗口恢复后才能操作；不要重试。"),
        [ErrorCodes.Paused] = new(RetryHint.Never, DispatchStatus.NotSent, "已在本机暂停，恢复后才能操作；不要重试。"),
        [ErrorCodes.SessionLocked] = new(RetryHint.Never, DispatchStatus.NotSent, "桌面已锁屏，解锁后才能操作。"),
        [ErrorCodes.ControllerBusy] = new(RetryHint.Retry, DispatchStatus.NotSent, "其它会话正在操作桌面，请按 retry_after_ms 等待后重试。"),
        [ErrorCodes.ReadonlyMode] = new(RetryHint.Never, DispatchStatus.NotSent, "当前为只读模式，动作被禁用。"),
        [ErrorCodes.RateLimited] = new(RetryHint.Retry, DispatchStatus.NotSent, "操作过于频繁，请按 retry_after_ms 等待后重试。"),
        [ErrorCodes.AuditUnavailable] = new(RetryHint.Retry, DispatchStatus.NotSent, "审计写入失败，动作未执行（fail-closed）；请检查磁盘/审计目录后重试。"),
        [ErrorCodes.StaleState] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "观察状态已过期，请先重新调用 go_observe。"),
        [ErrorCodes.FrameNotActionable] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "截图不可用于坐标动作（最小化/黑帧/未知宿主/敏感窗口），请重新观察。"),
        [ErrorCodes.ElementUnavailable] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "目标元素已消失或不可用，请重新观察。"),
        [ErrorCodes.ElementActionUnavailable] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "目标元素不支持该动作，请重新观察后选择其它元素。"),
        [ErrorCodes.WindowNotFound] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "目标窗口不存在，请重新选择。"),
        [ErrorCodes.WindowChanged] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "目标窗口身份已变化，请重新观察。"),
        [ErrorCodes.AppNotFound] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "应用不存在，请先调用 go_apps 确认。"),
        [ErrorCodes.AmbiguousApp] = new(RetryHint.Never, DispatchStatus.NotSent, "该应用有多个窗口，请改用 window_id 指定。"),
        [ErrorCodes.LaunchFailed] = new(RetryHint.Never, DispatchStatus.NotSent, "启动失败，请检查 allowlist 配置与应用路径。"),
        [ErrorCodes.LaunchTimeout] = new(RetryHint.Never, DispatchStatus.NotSent, "等待窗口出现超时：进程可能已启动，禁止重试，请用 go_apps 确认。"),
        [ErrorCodes.ForegroundRequired] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "目标需要前台才能操作但无法安全激活；请重新观察或改用元素操作。"),
        [ErrorCodes.UipiBlocked] = new(RetryHint.Never, DispatchStatus.NotSent, "目标窗口权限更高（可能以管理员运行），无法操作。"),
        [ErrorCodes.ActionTimeout] = new(RetryHint.Retry, DispatchStatus.NotSent, "动作超时且未发送，请按提示重试。"),
        [ErrorCodes.DispatchUncertain] = new(RetryHint.Reobserve, DispatchStatus.PossiblySent, "动作可能已发送，禁止重试；请先重新观察。"),
        [ErrorCodes.Internal] = new(RetryHint.Never, DispatchStatus.NotSent, "内部错误，请重新观察后再试。"),
        [ErrorCodes.ExecutorUnreachable] = new(RetryHint.Retry, DispatchStatus.NotSent, "执行器不可达（未启动/防火墙/地址错误），请检查 Windows 端。", true),
        [ErrorCodes.ExecutorTimeout] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "执行器调用超时，请先重新观察。", true),
        [ErrorCodes.AuthFailed] = new(RetryHint.Never, DispatchStatus.NotSent, "认证失败：token 错误或已被轮换，请检查插件配置。", true),
        [ErrorCodes.ProtocolMismatch] = new(RetryHint.Never, DispatchStatus.NotSent, "协议版本不兼容，请升级两端。", true),
        [ErrorCodes.ConnectionLost] = new(RetryHint.Reobserve, DispatchStatus.NotSent, "连接中断；动作类可能已发送，禁止重试，请先重新观察。", true),
    };

    public static IReadOnlyDictionary<string, ErrorInfo> All => Map;

    public static bool TryGet(string? code, out ErrorInfo info)
    {
        if (code is not null && Map.TryGetValue(code, out var found))
        {
            info = found;
            return true;
        }
        info = Map[ErrorCodes.Internal];
        return false;
    }

    public static ErrorInfo Get(string code) => TryGet(code, out var info) ? info : Map[ErrorCodes.Internal];
}
