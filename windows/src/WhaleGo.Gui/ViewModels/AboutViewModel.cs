using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels;

/// <summary>
/// About / limitations page: versions, manual-start statement, theme explanation, known limitations and the privacy
/// note (docs/GUI.md 6.1 / 9.7 / 11.4). hotkeys.openSettings stays disabled — stated explicitly.
/// </summary>
public sealed class AboutViewModel : PageBaseViewModel
{
    private string _mcpProtocolVersion = "未知";
    private string _hotkeyText = ConfigDefaults.HotkeysEmergencyStop;
    private string _hotkeyStatusText = "未注册";
    private string _actionMessage = string.Empty;

    public AboutViewModel(GuiContext context)
        : base("关于 / 限制", "关于/限制", "版本、主题、启动方式与已知限制", context)
    {
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenLogsCommand = new RelayCommand(() => ActionMessage = ShellLauncher.TryOpenFolder(LogsDirectory, Context.Logger) ? "已打开日志目录" : "打开日志目录失败");
        CopyPathsCommand = new RelayCommand(CopyPaths);
    }

    public string ProductName => "WhaleGo";

    public string Version => Context.Options.Version;

    public string GuiVersion => typeof(AboutViewModel).Assembly.GetName().Version?.ToString() ?? Context.Options.Version;

    public string ProtocolVersion => Context.Options.ProtocolVersion;

    public string McpProtocolVersion => _mcpProtocolVersion;

    public string ExecutablePath => Context.Options.ExecutablePath;

    public string ConfigPath => Context.Options.ConfigPath;

    public string LogsDirectory => Context.LogsDirectory ?? "%LOCALAPPDATA%\\WhaleGo\\logs";

    public string AuditDirectory => Context.AuditDirectory ?? LogsDirectory;

    public static string StartupText => "启动方式：手动启动（不自启）";

    public static string StartupExplanation =>
        "WhaleGo 不写启动文件夹、不建计划任务、也没有自启开关。重启电脑后必须手动双击 WhaleGo.Executor.exe，" +
        "在此之前模型无法操作桌面（fail-safe 设计）。";

    public static string ThemeExplanation =>
        "浅色/深色两套主题 + 跟随系统：切换即时生效并写入 ui.theme，无需重启；高对比度模式下自动改用系统配色；" +
        "Windows 10 不支持 DWM 暗色标题栏时回退为浅色标题栏 + 深色内容。";

    public string HotkeyText => _hotkeyText;

    public string HotkeyStatusText => _hotkeyStatusText;

    public static string HotkeyExplanation =>
        "全局紧急停止热键（只停不恢复、不弹确认）。恢复只能在本机托盘/设置页二次确认。hotkeys.openSettings 保持不启用（未拍板）。";

    public static IReadOnlyList<string> Limitations { get; } =
    [
        "UIPI / 高完整性窗口：WhaleGo 以用户中完整性运行，对管理员权限窗口的动作会被拒绝（UIPI_BLOCKED）；不建议以管理员身份运行 WhaleGo。",
        "锁屏 / UAC 安全桌面：锁屏或安全桌面下无法操作（SESSION_LOCKED），系统热键也收不到。",
        "Chromium / GPU 渲染窗口：坐标动作可能降级，优先用元素（UIA）路径；WGC 截图是 P2 占位。",
        "多屏 / 负坐标 / 混合 DPI：已按 PerMonitorV2 处理，但极端布局下 UI 自动化可能取不到正确坐标。",
        "系统渲染的组件不跟随应用主题：托盘右键菜单、文件选择框、气泡通知由 Windows 渲染。",
        "无开机自启：重启电脑后需要手动启动 WhaleGo（本版本明确不做自启）。",
        "不做 WebUI / WebView：设置界面全部为原生 WPF。",
    ];

    public static string Disclaimer =>
        "WhaleGo 让远程模型在获得授权后操作本机桌面，存在误操作与隐私暴露风险。请仅在自己可控的机器上运行，" +
        "保持 token 私密、开启审计与只读兜底；截图含用户隐私，未经明确要求不得转发。";

    public static string PrivacyNote =>
        "GUI 不常驻显示 token 明文；复制到剪贴板的 token 60 秒后条件清空；粘贴的插件配置含 token，请勿外传。" +
        "诊断页只展示请求元数据（不含参数、输入文本、截图字节与完整会话 id）。";

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand CopyPathsCommand { get; }

    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (SetProperty(ref _actionMessage, value))
            {
                OnPropertyChanged(nameof(HasActionMessage));
            }
        }
    }

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessage);

    public override async Task RefreshAsync()
    {
        var status = await Context.Control.GetStatusAsync().ConfigureAwait(true);
        if (status.Success && status.Value is not null)
        {
            _mcpProtocolVersion = string.IsNullOrWhiteSpace(status.Value.McpProtocolVersion) ? "未知" : status.Value.McpProtocolVersion;
            OnPropertyChanged(nameof(McpProtocolVersion));
        }

        var config = await Context.Control.GetConfigAsync().ConfigureAwait(true);
        if (config.Success && config.Value is not null)
        {
            if (config.Value.Values.TryGetValue(ConfigKeys.HotkeysEmergencyStop, out var hotkey))
            {
                _hotkeyText = ConfigValue.AsString(hotkey) ?? ConfigDefaults.HotkeysEmergencyStop;
            }

            if (config.Value.Values.TryGetValue(ConfigKeys.AuditDir, out var auditDir))
            {
                Context.AuditDirectory = ConfigValue.AsString(auditDir);
                OnPropertyChanged(nameof(AuditDirectory));
            }

            OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public void SetHotkeyStatus(string text)
    {
        _hotkeyStatusText = text;
        OnPropertyChanged(nameof(HotkeyStatusText));
    }

    private void OpenDataFolder()
    {
        _ = OpenDataFolderAsync();
    }

    private async Task OpenDataFolderAsync()
    {
        var result = await Context.Control.OpenDataFolderAsync().ConfigureAwait(true);
        ActionMessage = result.Success ? "已打开数据目录" : "打开数据目录失败：" + (result.ErrorMessage ?? "未知错误");
    }

    private void CopyPaths()
    {
        var text = string.Join(Environment.NewLine, new[]
        {
            "程序：" + ExecutablePath,
            "配置：" + ConfigPath,
            "日志：" + LogsDirectory,
            "审计：" + AuditDirectory,
        });
        Context.Clipboard.SetText(text);
        ActionMessage = "已复制路径信息";
    }
}
