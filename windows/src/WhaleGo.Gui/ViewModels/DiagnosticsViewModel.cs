using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels;

public sealed record DetailRow(string Label, string Value);

public sealed class RecentRequestRow
{
    public RecentRequestRow(RecentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Time = FormatTime(request.TsMs);
        Tool = request.Tool;
        SessionTag = DiagnosticsMapper.MaskSessionTag(request.SessionTag);
        ElapsedMs = request.ElapsedMs;
        Result = string.IsNullOrEmpty(request.ErrorCode) ? request.Result : "ERROR[" + request.ErrorCode + "]";
        Note = DiagnosticsMapper.BuildRequestNote(request);
    }

    public string Time { get; }

    public string Tool { get; }

    public string SessionTag { get; }

    public int ElapsedMs { get; }

    public string Result { get; }

    public string Note { get; }

    private static string FormatTime(long tsMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "—";
        }
    }
}

public sealed class ToolCounterRow
{
    public ToolCounterRow(string tool, long total, double? ratePerMinute, string note)
    {
        Tool = tool;
        Total = total;
        RatePerMinute = ratePerMinute;
        Note = note;
    }

    public string Tool { get; }

    public long Total { get; }

    public double? RatePerMinute { get; }

    public string Note { get; }

    public string RateText => RatePerMinute is null
        ? "—"
        : RatePerMinute.Value.ToString("0.0", CultureInfo.InvariantCulture) + "/min";
}

public sealed class SelfCheckItemRow
{
    public SelfCheckItemRow(SelfCheckItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Name = item.Name;
        Status = item.Status;
        Badge = item.Status switch
        {
            SelfCheckStatus.Ok => "✅ 通过",
            SelfCheckStatus.Warn => "⚠️ 警告",
            _ => "❌ 失败",
        };
        Severity = item.Status switch
        {
            SelfCheckStatus.Ok => UiSeverity.Info,
            SelfCheckStatus.Warn => UiSeverity.Warning,
            _ => UiSeverity.Danger,
        };
        Detail = item.Detail;
        Advice = item.Advice ?? string.Empty;
        Id = item.Id;
    }

    public string Id { get; }

    public string Name { get; }

    public SelfCheckStatus Status { get; }

    public string Badge { get; }

    public UiSeverity Severity { get; }

    public string Detail { get; }

    public string Advice { get; }

    public bool HasAdvice => !string.IsNullOrEmpty(Advice);
}

/// <summary>Pure mapping helpers for the diagnostics tables (unit tested; no WPF / no kernel).</summary>
public static class DiagnosticsMapper
{
    /// <summary>Never show a full session id: only the trailing 6 characters (docs/GUI.md 8.2).</summary>
    public static string MaskSessionTag(string? sessionTag)
    {
        if (string.IsNullOrWhiteSpace(sessionTag))
        {
            return "—";
        }

        var value = sessionTag.Trim();
        return value.Length <= 6 ? value : "…" + value[^6..];
    }

    public static string BuildRequestNote(RecentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parts = new List<string>();
        if (request.Elements is not null)
        {
            parts.Add("elements=" + request.Elements.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (request.HasScreenshot)
        {
            parts.Add("含截图");
        }

        if (request.LeaseWaitMs is > 0)
        {
            parts.Add("等租约 " + request.LeaseWaitMs.Value.ToString(CultureInfo.InvariantCulture) + "ms");
        }

        if (!string.IsNullOrEmpty(request.Op))
        {
            parts.Add(request.Op);
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    public static string BuildSelfCheckReport(IReadOnlyList<SelfCheckItemRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var lines = new List<string> { "WhaleGo 自检报告", "生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), string.Empty };
        foreach (var item in items)
        {
            lines.Add("[" + item.Badge + "] " + item.Name + "：" + item.Detail);
            if (item.HasAdvice)
            {
                lines.Add("    建议：" + item.Advice);
            }
        }

        lines.Add(string.Empty);
        lines.Add("说明：报告不含 token / 会话 id / 窗口标题。");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Diagnostics page: status details, controller lease, recent requests (metadata only), tool counters with rate,
/// audit tail, one-click self-check and folder shortcuts (docs/GUI.md section 8).
/// </summary>
public sealed class DiagnosticsViewModel : PageBaseViewModel
{
    private readonly Dictionary<string, long> _lastCounters = new(StringComparer.Ordinal);
    private long _lastCountersAtMs;
    private StatusSnapshot? _snapshot;
    private bool _isBusy;
    private bool _isLoadingAudit;
    private string _auditTail = string.Empty;
    private string _selfCheckSummary = "尚未运行自检";
    private string _hotkeyStatusText = "未注册";
    private string? _lastQuickAction;

    public DiagnosticsViewModel(GuiContext context)
        : base("诊断与日志", "诊断与日志", "现在能不能被操作、谁在操作、连不上卡在哪", context)
    {
        StatusDetails = [];
        RecentRequests = [];
        ToolCounters = [];
        SelfCheckItems = [];
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsBusy);
        LoadAuditCommand = new AsyncRelayCommand(LoadAuditAsync, () => !IsLoadingAudit);
        RunSelfCheckCommand = new AsyncRelayCommand(RunSelfCheckAsync, () => !IsBusy);
        CopyReportCommand = new RelayCommand(CopyReport, () => SelfCheckItems.Count > 0);
        CopyAuditCommand = new RelayCommand(CopyAudit, () => !string.IsNullOrEmpty(AuditTail));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenAuditCommand = new RelayCommand(OpenAudit);
        RequestRestartCommand = new AsyncRelayCommand(RequestRestartAsync, () => !IsBusy);
        RefreshAllCommand.ExecutionFailed += OnCommandFailed;
        RunSelfCheckCommand.ExecutionFailed += OnCommandFailed;
        LoadAuditCommand.ExecutionFailed += OnCommandFailed;
    }

    public ObservableCollection<DetailRow> StatusDetails { get; }

    public ObservableCollection<RecentRequestRow> RecentRequests { get; }

    public ObservableCollection<ToolCounterRow> ToolCounters { get; }

    public ObservableCollection<SelfCheckItemRow> SelfCheckItems { get; }

    public AsyncRelayCommand RefreshAllCommand { get; }

    public AsyncRelayCommand LoadAuditCommand { get; }

    public AsyncRelayCommand RunSelfCheckCommand { get; }

    public RelayCommand CopyReportCommand { get; }

    public RelayCommand CopyAuditCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand OpenAuditCommand { get; }

    public AsyncRelayCommand RequestRestartCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshAllCommand.RaiseCanExecuteChanged();
                RunSelfCheckCommand.RaiseCanExecuteChanged();
                RequestRestartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingAudit
    {
        get => _isLoadingAudit;
        private set
        {
            if (SetProperty(ref _isLoadingAudit, value))
            {
                LoadAuditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AuditTail
    {
        get => _auditTail;
        private set
        {
            if (SetProperty(ref _auditTail, value))
            {
                CopyAuditCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasAuditTail));
            }
        }
    }

    public bool HasAuditTail => !string.IsNullOrEmpty(AuditTail);

    public string SelfCheckSummary
    {
        get => _selfCheckSummary;
        private set => SetProperty(ref _selfCheckSummary, value);
    }

    public string HotkeyStatusText => _hotkeyStatusText;

    public string? LastQuickAction
    {
        get => _lastQuickAction;
        private set
        {
            if (SetProperty(ref _lastQuickAction, value))
            {
                OnPropertyChanged(nameof(HasLastQuickAction));
            }
        }
    }

    public bool HasLastQuickAction => !string.IsNullOrEmpty(LastQuickAction);

    public string LogsDirectory => Context.LogsDirectory ?? "%LOCALAPPDATA%\\WhaleGo\\logs";

    public string AuditDirectory => Context.AuditDirectory ?? "%LOCALAPPDATA%\\WhaleGo\\logs";

    public override async Task RefreshAsync()
    {
        if (!IsBusy)
        {
            await RefreshAllAsync().ConfigureAwait(true);
        }
    }

    public void SetHotkeyStatus(string text)
    {
        _hotkeyStatusText = text;
        OnPropertyChanged(nameof(HotkeyStatusText));
    }

    public override void OnStatusUpdated(StatusSnapshot snapshot)
    {
        _snapshot = snapshot;
        ApplySnapshot(snapshot);
    }

    public async Task RefreshAllAsync()
    {
        IsBusy = true;
        try
        {
            var status = await Context.Control.GetStatusAsync().ConfigureAwait(true);
            if (status.Success && status.Value is not null)
            {
                _snapshot = status.Value;
                ApplySnapshot(status.Value);
            }

            var recent = await Context.Control.GetRecentRequestsAsync(50).ConfigureAwait(true);
            if (recent.Success && recent.Value is not null)
            {
                RecentRequests.Clear();
                foreach (var request in recent.Value.Take(50))
                {
                    RecentRequests.Add(new RecentRequestRow(request));
                }
            }

            var counters = await Context.Control.GetToolCountersAsync().ConfigureAwait(true);
            if (counters.Success && counters.Value is not null)
            {
                ApplyCounters(counters.Value);
            }

            var config = await Context.Control.GetConfigAsync().ConfigureAwait(true);
            if (config.Success && config.Value is not null)
            {
                var auditDir = ConfigRules.IsEnvironmentOrCommandLine(null)
                    ? null
                    : ConfigValue.AsString(config.Value.Values.TryGetValue(ConfigKeys.AuditDir, out var rawAudit) ? rawAudit : null);
                if (!string.IsNullOrWhiteSpace(auditDir))
                {
                    Context.AuditDirectory = auditDir;
                }

                OnPropertyChanged(nameof(AuditDirectory));
                OnPropertyChanged(nameof(LogsDirectory));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySnapshot(StatusSnapshot snapshot)
    {
        StatusDetails.Clear();
        StatusDetails.Add(new DetailRow("运行状态", StatusText.Label(snapshot) + " · " + StatusText.Detail(snapshot)));
        StatusDetails.Add(new DetailRow("实际监听", StatusText.ListenLine(snapshot)));
        StatusDetails.Add(new DetailRow("版本 / 协议", StatusText.VersionLine(snapshot, Context.Options)));
        StatusDetails.Add(new DetailRow("会话 / 租约", StatusText.SessionsLine(snapshot)));
        StatusDetails.Add(new DetailRow("只读 / 截图 / 审计", StatusText.PolicyLine(snapshot)));
        StatusDetails.Add(new DetailRow("运行时长", FormatUptime(snapshot.UptimeMs)));
        StatusDetails.Add(new DetailRow("热键", _hotkeyStatusText));
        if (snapshot.Warnings.Count > 0)
        {
            StatusDetails.Add(new DetailRow("警告", string.Join("；", snapshot.Warnings)));
        }
    }

    private void ApplyCounters(IReadOnlyDictionary<string, long> counters)
    {
        var now = Context.NowMs();
        var elapsedMs = _lastCountersAtMs == 0 ? 0 : now - _lastCountersAtMs;
        ToolCounters.Clear();
        foreach (var pair in counters.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            double? rate = null;
            if (elapsedMs > 0 && _lastCounters.TryGetValue(pair.Key, out var previous))
            {
                var delta = Math.Max(0, pair.Value - previous);
                rate = delta / (elapsedMs / 60000.0);
            }

            ToolCounters.Add(new ToolCounterRow(pair.Key, pair.Value, rate, "进程启动至今，不落盘"));
        }

        _lastCounters.Clear();
        foreach (var pair in counters)
        {
            _lastCounters[pair.Key] = pair.Value;
        }

        _lastCountersAtMs = now;
    }

    public async Task LoadAuditAsync()
    {
        IsLoadingAudit = true;
        try
        {
            var tail = await Context.Control.ReadAuditTailAsync(200).ConfigureAwait(true);
            if (!tail.Success || tail.Value is null)
            {
                AuditTail = "读取审计失败：" + (tail.ErrorMessage ?? "未知错误");
                return;
            }

            AuditTail = tail.Value.Count == 0
                ? "今日暂无审计记录。"
                : string.Join(Environment.NewLine, tail.Value);
            LastQuickAction = "已加载审计末尾 200 行（只读）";
        }
        finally
        {
            IsLoadingAudit = false;
        }
    }

    public async Task RunSelfCheckAsync()
    {
        IsBusy = true;
        try
        {
            SelfCheckItems.Clear();
            var report = await Context.Control.RunSelfCheckAsync().ConfigureAwait(true);
            if (!report.Success || report.Value is null)
            {
                SelfCheckSummary = "自检失败：" + (report.ErrorMessage ?? "未知错误");
                return;
            }

            foreach (var item in report.Value.Items)
            {
                SelfCheckItems.Add(new SelfCheckItemRow(item));
            }

            var themeItem = Context.ThemeSelfCheck?.Invoke();
            if (themeItem is not null)
            {
                SelfCheckItems.Add(new SelfCheckItemRow(themeItem));
            }

            var failed = report.Value.Items.Count(static item => item.Status == SelfCheckStatus.Fail);
            var warned = report.Value.Items.Count(static item => item.Status == SelfCheckStatus.Warn);
            SelfCheckSummary = failed > 0
                ? "自检完成：❌ " + failed.ToString(CultureInfo.InvariantCulture) + " 项失败，请按建议处理"
                : warned > 0
                    ? "自检完成：⚠️ " + warned.ToString(CultureInfo.InvariantCulture) + " 项警告"
                    : "自检完成：全部通过";
            CopyReportCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyReport()
    {
        var text = DiagnosticsMapper.BuildSelfCheckReport(SelfCheckItems);
        Context.Clipboard.SetText(text);
        LastQuickAction = "已复制自检报告（纯文本，不含 token）";
    }

    private void CopyAudit()
    {
        Context.Clipboard.SetText(AuditTail);
        LastQuickAction = "已复制审计末尾内容";
    }

    private void OpenLogs()
    {
        LastQuickAction = ShellLauncher.TryOpenFolder(LogsDirectory, Context.Logger) ? "已打开日志目录" : "打开日志目录失败（路径不存在？）";
    }

    private void OpenAudit()
    {
        LastQuickAction = ShellLauncher.TryOpenFolder(AuditDirectory, Context.Logger) ? "已打开审计目录" : "打开审计目录失败（路径不存在？）";
    }

    private async Task RequestRestartAsync()
    {
        if (!Context.Dialogs.Confirm(
                "重启执行器",
                "将请求执行器立即重启以应用需要重启的配置。重启期间模型暂时不可用。确定继续吗？",
                "立即重启"))
        {
            return;
        }

        var result = await Context.Control.RequestRestartAsync().ConfigureAwait(true);
        LastQuickAction = result.Success ? "已请求重启执行器" : "重启请求失败：" + (result.ErrorMessage ?? "未知错误");
    }

    private static string FormatUptime(long uptimeMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, uptimeMs));
        return span.TotalHours >= 1
            ? span.ToString(@"h\.mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void OnCommandFailed(object? sender, Exception exception)
    {
        Context.Logger.Error("diagnostics command failed", exception);
        LastQuickAction = "操作失败：" + exception.Message;
        IsBusy = false;
    }
}