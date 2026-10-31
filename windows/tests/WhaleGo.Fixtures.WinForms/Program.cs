using System.Runtime.InteropServices;

namespace WhaleGo.Fixtures.WinForms;

/// <summary>
/// Entry point of the WhaleGo WinForms test fixture.
/// This process is a normal, visible, per-monitor-V2 DPI aware WinForms window used by the
/// observation / input test suites (UIA tree, patterns, screen capture, coordinate hit-test).
/// </summary>
internal static class Program
{
    private const int AttachParentProcess = -1;

    private const string Usage = """
        WhaleGo Fixtures.WinForms - UIA / input test fixture for the WhaleGo executor test suites.

        Usage: WhaleGo.Fixtures.WinForms.exe [options]

          --title <suffix>      Window title suffix -> "WhaleGo Fixture - <suffix>" (also shown in lblStatus)
          --ready-file <path>   Write a ready marker file after the window is shown and laid out
          --auto-close-ms <n>   Close the window automatically after n milliseconds (0 = next tick)
          --click-log <path>    Append one line per click on pnlClick / btnClickTarget to this file
          --help, -h, -?        Print this help and exit 0

        Automation ids (Control.Name / UIA):
          btnIncrement   Button  "Increment"   increments counter
          lblCounter     Label                 counter value
          lblStatus      Label                 "counter=0;check=off;text=;clicks=0" refreshed every 200 ms
          txtInput       Edit                  empty text box (type / set_value target)
          btnSetText     Button  "Set"         sets txtInput.Text to "set-by-button"
          txtSecret      Edit                  password box, text "secret123", UseSystemPasswordChar=true
          chkToggle      CheckBox "Toggle"
          lstItems       ListBox               items A, B, C
          cboChoice      ComboBox              items X, Y, Z
          pnlClick       Panel                 240x140 click target (counts Click events)
          btnClickTarget Button  "ClickTarget" counts its own invocations
          lblClickCount  Label                 "panel=<n>;target=<n>"
          btnDisabled    Button  "Disabled"    Enabled=false (unavailable element)

        Exit codes: 0 normal / --help, 2 invalid arguments.
        """;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        // A WinExe has no console of its own; attach to the launching console when there is one so
        // --help / argument errors are visible from PowerShell / CI. Redirected std handles still win.
        _ = AttachConsole(AttachParentProcess);

        FixtureOptions options;
        try
        {
            options = FixtureOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"WhaleGo.Fixtures.WinForms: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new FixtureForm(options);
        Application.Run(form);
        return 0;
    }
}