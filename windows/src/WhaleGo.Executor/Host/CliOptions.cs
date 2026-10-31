using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WhaleGo.Executor.Host;

/// <summary>Command line: --headless --self-check [--json] --config &lt;path&gt; --no-hotkey --diagnostics-dump --port &lt;int&gt;.</summary>
public sealed class CliOptions
{
    public bool Headless { get; private set; }

    public bool NoHotkey { get; private set; }

    public bool SelfCheck { get; private set; }

    public bool Json { get; private set; }

    public bool DiagnosticsDump { get; private set; }

    public bool ShowHelp { get; private set; }

    public string? ConfigPath { get; private set; }

    public int? Port { get; private set; }

    public List<string> Warnings { get; } = [];

    public const string Usage = """
        WhaleGo.Executor — MCP 执行器（Windows 桌面控制）

        用法: WhaleGo.Executor.exe [选项]
          --headless            无托盘/无 WPF/无全局热键（CI/E2E），日志走 stdout
          --self-check          运行自检后退出（不启动 MCP 监听）
          --json                配合 --self-check：输出 JSON 报告
          --config <path>       使用指定配置文件（数据目录默认取该文件所在目录）
          --no-hotkey           不注册全局热键
          --diagnostics-dump    打印 go_health 等价 JSON + 最近请求后退出
          --port <int>          覆盖 listen.port（测试用）
          --help                显示本帮助

        环境变量:
          WHALEGO_DATA_DIR      覆盖运行时数据目录（secrets.json/control-state.json/logs）
          WHALEGO_HEADLESS=1    等价 --headless
          WHALEGO_*             配置覆盖（双下划线表示层级，如 WHALEGO_UI__THEME=dark）
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--headless":
                    options.Headless = true;
                    break;
                case "--no-hotkey":
                    options.NoHotkey = true;
                    break;
                case "--self-check":
                    options.SelfCheck = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--diagnostics-dump":
                    options.DiagnosticsDump = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;
                case "--config":
                    options.ConfigPath = ReadValue(args, ref index, "--config");
                    break;
                case "--port":
                    var portText = ReadValue(args, ref index, "--port");
                    if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
                    {
                        throw new ArgumentException("--port 需要 0..65535 的整数");
                    }
                    options.Port = port;
                    break;
                default:
                    if (argument.StartsWith("--config=", StringComparison.Ordinal))
                    {
                        options.ConfigPath = argument["--config=".Length..];
                    }
                    else if (argument.StartsWith("--port=", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(argument["--port=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inlinePort) || inlinePort is < 0 or > 65535)
                        {
                            throw new ArgumentException("--port 需要 0..65535 的整数");
                        }
                        options.Port = inlinePort;
                    }
                    else
                    {
                        options.Warnings.Add("忽略未知参数：" + argument);
                    }
                    break;
            }
        }

        var headlessEnvironment = Environment.GetEnvironmentVariable("WHALEGO_HEADLESS");
        if (!string.IsNullOrWhiteSpace(headlessEnvironment) &&
            (headlessEnvironment == "1" || headlessEnvironment.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            options.Headless = true;
        }

        return options;
    }

    public ExecutorHostOptions ToHostOptions(bool startMcp)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("WHALEGO_DATA_DIR");
        // --self-check / --diagnostics-dump never build the GUI or register global hotkeys.
        return new ExecutorHostOptions
        {
            Headless = startMcp ? Headless : true,
            NoHotkey = startMcp ? NoHotkey : true,
            StartMcp = startMcp,
            ConfigPath = string.IsNullOrWhiteSpace(ConfigPath) ? null : Path.GetFullPath(ConfigPath),
            DataDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory,
            ListenPort = Port,
            QuietLogging = false,
        };
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(name + " 需要一个值");
        }

        index++;
        return args[index];
    }
}