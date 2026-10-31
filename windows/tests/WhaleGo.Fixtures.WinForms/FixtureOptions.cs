using System.Globalization;

namespace WhaleGo.Fixtures.WinForms;

/// <summary>Command line options of the WhaleGo WinForms fixture.</summary>
internal sealed class FixtureOptions
{
    public string? TitleSuffix { get; private init; }

    public string? ReadyFile { get; private init; }

    public int? AutoCloseMs { get; private init; }

    public string? ClickLog { get; private init; }

    public bool ShowHelp { get; private init; }

    public static FixtureOptions Parse(string[] args)
    {
        string? title = null;
        string? readyFile = null;
        string? clickLog = null;
        int? autoCloseMs = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "-?":
                    showHelp = true;
                    break;
                case "--title":
                    title = NextValue(args, ref i, arg);
                    break;
                case "--ready-file":
                    readyFile = NextValue(args, ref i, arg);
                    break;
                case "--click-log":
                    clickLog = NextValue(args, ref i, arg);
                    break;
                case "--auto-close-ms":
                    var raw = NextValue(args, ref i, arg);
                    autoCloseMs = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0
                        ? ms
                        : throw new ArgumentException($"--auto-close-ms 需要非负整数，收到: {raw}");
                    break;
                default:
                    throw new ArgumentException($"未知参数: {arg}");
            }
        }

        return new FixtureOptions
        {
            TitleSuffix = title,
            ReadyFile = readyFile,
            ClickLog = clickLog,
            AutoCloseMs = autoCloseMs,
            ShowHelp = showHelp,
        };
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} 缺少参数值");
        }

        index++;
        return args[index];
    }
}
