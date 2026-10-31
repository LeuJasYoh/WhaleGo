using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WhaleGo.Contracts;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Application;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;
using WhaleGo.Executor.Infrastructure.Secrets;
using WhaleGo.Gui;
using Microsoft.Extensions.DependencyInjection;

namespace WhaleGo.Executor.Host;

public sealed record DiagnosticsDumpReport(
    HealthReport Health,
    IReadOnlyList<RecentRequest> RecentRequests,
    SelfCheckReport SelfCheck);

/// <summary>CLI entry points behind Program.Main (self-check / diagnostics-dump / tray+headless server).</summary>
public static class CliRunners
{
    /// <summary>
    /// ASCII-safe dump JSON: --diagnostics-dump may be captured through Windows console code pages (CP936/CP1252),
    /// so non-ASCII text is \uXXXX-escaped while staying byte-identical in any decoder.
    /// </summary>
    private static readonly JsonSerializerOptions DumpJsonOptions = new(WireJson.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    private static readonly JsonSerializerOptions SelfCheckJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int RunSelfCheck(CliOptions cli)
    {
        ExecutorHostInstance? host = null;
        try
        {
            host = ExecutorHost.StartAsync(cli.ToHostOptions(startMcp: false)).GetAwaiter().GetResult();
            CrashLog.Configure(host.Paths.LogsRoot);
            var selfCheck = host.Services.GetRequiredService<ISelfCheck>();
            var report = selfCheck.Run();
            if (cli.Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(report, SelfCheckJsonOptions));
            }
            else
            {
                Console.Out.WriteLine("WhaleGo 自检" + (report.Ok ? "通过" : "失败"));
                foreach (var item in report.Items)
                {
                    var symbol = item.Status switch
                    {
                        SelfCheckStatus.Ok => "[OK]  ",
                        SelfCheckStatus.Warn => "[WARN]",
                        _ => "[FAIL]",
                    };
                    Console.Out.WriteLine(symbol + " " + item.Id + " " + item.Name + "：" + item.Detail +
                                          (string.IsNullOrWhiteSpace(item.Advice) ? string.Empty : " | 建议：" + item.Advice));
                }
            }

            Console.Out.Flush();
            return report.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        finally
        {
            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static int RunDiagnosticsDump(CliOptions cli)
    {
        ExecutorHostInstance? host = null;
        try
        {
            host = ExecutorHost.StartAsync(cli.ToHostOptions(startMcp: false)).GetAwaiter().GetResult();
            CrashLog.Configure(host.Paths.LogsRoot);
            var health = host.Services.GetRequiredService<HealthService>();
            var status = host.Services.GetRequiredService<StatusService>();
            var selfCheck = host.Services.GetRequiredService<ISelfCheck>();
            var report = new DiagnosticsDumpReport(
                Health: health.GetHealth("default", null),
                RecentRequests: status.GetRecentRequests(50),
                SelfCheck: selfCheck.Run());
            Console.Out.WriteLine(JsonSerializer.Serialize(report, DumpJsonOptions));
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        finally
        {
            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static int RunServer(CliOptions cli, IReadOnlyList<string> originalArgs)
    {
        var instanceSuffix = ResolveInstanceSuffix(cli);
        var acquisition = SingleInstanceGuard.Acquire(TimeSpan.FromSeconds(3), instanceSuffix);
        if (acquisition.PrimaryRunning)
        {
            if (cli.Headless)
            {
                Console.Error.WriteLine("WhaleGo 已在运行（同一交互会话只允许一个实例）。");
                return 2;
            }

            SingleInstanceGuard.SignalPrimaryShowSettings(instanceSuffix);
            return 0;
        }

        var guard = acquisition.Guard!;
        ExecutorHostInstance? host = null;
        GuiShell? gui = null;
        var restartRequested = false;
        try
        {
            foreach (var warning in cli.Warnings)
            {
                Console.Error.WriteLine("WARN: " + warning);
            }

            host = ExecutorHost.StartAsync(cli.ToHostOptions(startMcp: true)).GetAwaiter().GetResult();
            CrashLog.Configure(host.Paths.LogsRoot);
            gui = WireGuiAndHotkey(host, cli, guard);
            var runtime = (ExecutorRuntimeState)host.Services.GetRequiredService(typeof(ExecutorRuntimeState));

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                runtime.Shutdown.Set();
            };

            var instance = host;
            var tokenState = instance.Services.GetRequiredService<TokenService>();
            if (tokenState.TokenWasCreated)
            {
                Console.WriteLine("TOKEN_CREATED name=" + tokenState.CreatedTokenName + " file=" + instance.SecretsPath);
            }

            Console.WriteLine("LISTEN " + (instance.BaseUrl ?? "(disabled)"));
            Console.WriteLine("URL " + (instance.McpEndpoint ?? "(disabled)"));
            Console.WriteLine("DATA_DIR " + instance.DataDirectory);
            Console.Out.Flush();

            runtime.Shutdown.Wait();
            restartRequested = runtime.RestartRequested;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                gui?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("WARN: GUI 关闭失败：" + ex.Message);
            }

            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            guard.Dispose();
            if (restartRequested)
            {
                RestartWithoutHeadless(originalArgs);
            }
        }
    }

    /// <summary>Per-data-root instance suffix: empty for production defaults, deterministic hex for temp roots.</summary>
    public static string ResolveInstanceSuffix(CliOptions cli)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("WHALEGO_DATA_DIR");
        var hasOverride = !string.IsNullOrWhiteSpace(dataDirectory) || !string.IsNullOrWhiteSpace(cli.ConfigPath);
        if (!hasOverride)
        {
            return string.Empty;
        }

        var paths = ExecutorPaths.Resolve(cli.ConfigPath, dataDirectory);
        return SingleInstanceGuard.InstanceSuffix(paths.DataRoot);
    }

    private static GuiShell? WireGuiAndHotkey(ExecutorHostInstance host, CliOptions cli, SingleInstanceGuard guard)
    {
        var runtime = (ExecutorRuntimeState)host.Services.GetRequiredService(typeof(ExecutorRuntimeState));
        var configService = (ConfigService)host.Services.GetRequiredService(typeof(ConfigService));
        var control = host.Control;
        var hotkeys = (IHotkeyRegistrar)host.Services.GetRequiredService(typeof(IHotkeyRegistrar));
        GuiShell? gui = null;

        if (!cli.Headless)
        {
            try
            {
                var theme = (IWindowTheme)host.Services.GetRequiredService(typeof(IWindowTheme));
                var options = new GuiOptions(
                    Headless: cli.Headless,
                    NoHotkey: cli.NoHotkey,
                    Version: ProtocolInfo.ExecutorVersion,
                    ProtocolVersion: ProtocolInfo.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ExecutablePath: Environment.ProcessPath ?? string.Empty,
                    ConfigPath: host.ConfigPath,
                    LogsDirectory: host.Paths.LogsRoot);
                gui = new GuiShell(control, theme, hotkeys, options);
                gui.ExitRequested += (_, _) => runtime.Shutdown.Set();
                gui.RefreshStatus();
            }
            catch (Exception ex)
            {
                // GUI failure must not kill the kernel: continue headless-equivalent (fail-safe).
                runtime.AddWarning("GUI 初始化失败：" + ex.Message);
                gui = null;
            }
        }

        var config = configService.Config;
        runtime.HotkeyEnabled = !cli.Headless && !cli.NoHotkey && config.HotkeysEnabled;
        runtime.HotkeyGesture = config.HotkeysEmergencyStop;
        if (runtime.HotkeyEnabled && !string.IsNullOrWhiteSpace(config.HotkeysEmergencyStop))
        {
            try
            {
                var registered = hotkeys.TryRegister("emergency_stop", config.HotkeysEmergencyStop!, () =>
                {
                    try
                    {
                        control.Stop("hotkey");
                    }
                    catch (Exception ex)
                    {
                        runtime.AddWarning("热键停止失败：" + ex.Message);
                    }

                    try
                    {
                        gui?.ShowBalloon("WhaleGo", "已紧急停止（模型无法恢复，请到 Windows 托盘/设置窗口恢复）。", true);
                    }
                    catch (Exception)
                    {
                        // balloon is best effort
                    }
                });
                runtime.HotkeyRegistered = registered;
                if (!registered)
                {
                    runtime.HotkeyError = "热键注册失败（可能被其它程序占用）";
                    runtime.AddWarning(runtime.HotkeyError);
                }
            }
            catch (Exception ex)
            {
                runtime.HotkeyError = ex.Message;
                runtime.AddWarning("热键注册异常：" + ex.Message);
            }
        }

        if (!cli.Headless)
        {
            guard.StartShowSettingsListener(() =>
            {
                try
                {
                    gui?.ShowSettings();
                }
                catch (Exception ex)
                {
                    runtime.AddWarning("打开设置窗口失败：" + ex.Message);
                }
            });
        }

        return gui;
    }

    private static void RestartWithoutHeadless(IReadOnlyList<string> originalArgs)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            var arguments = originalArgs.Where(argument =>
                !string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(argument, "--self-check", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(argument, "--diagnostics-dump", StringComparison.OrdinalIgnoreCase)).ToList();
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                Arguments = string.Join(' ', arguments.Select(Quote)),
            };
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            Console.Error.WriteLine("WARN: 重启失败：" + ex.Message);
        }
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;
}