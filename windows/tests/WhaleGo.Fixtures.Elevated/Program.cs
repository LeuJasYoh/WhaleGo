using System.Runtime.InteropServices;

namespace WhaleGo.Fixtures.Elevated;

/// <summary>Entry point of the elevated (high integrity) WhaleGo fixture. Manual P1 UIPI case only.</summary>
internal static class Program
{
    private const int AttachParentProcess = -1;

    private const string Usage = """
        WhaleGo Fixtures.Elevated - elevated (high-integrity) UIPI test fixture. NOT part of CI.

        Starting this executable triggers a UAC prompt (requestedExecutionLevel=requireAdministrator).

        Usage: WhaleGo.Fixtures.Elevated.exe [options]

          --title <suffix>      Window title suffix -> "WhaleGo Fixture Elevated - <suffix>"
          --ready-file <path>   Write a ready marker file after the window is shown
          --auto-close-ms <n>   Close the window automatically after n milliseconds
          --click-log <path>    Append one line per Increment click to this file
          --help, -h, -?        Print this help and exit 0

        Automation ids: btnIncrement, lblCounter, lblStatus (window: frmElevated)
        Exit codes: 0 normal / --help, 2 invalid arguments.
        """;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        _ = AttachConsole(AttachParentProcess);

        FixtureOptions options;
        try
        {
            options = FixtureOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.Elevated: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        // Suppressed WFO0003 in the csproj: the manifest also declares PerMonitorV2 on purpose.
        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new ElevatedFixtureForm(options);
        Application.Run(form);
        return 0;
    }
}
