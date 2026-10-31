using System;
using WhaleGo.Executor.Host;

namespace WhaleGo.Executor;

public static class Program
{
    /// <summary>Composition root: CLI → (self-check | diagnostics-dump | single instance + kernel + GUI/hotkey).</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        CrashLog.Install();
        // Machine-readable CLI output (--self-check --json / --diagnostics-dump) must be UTF-8 even when redirected.
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or PlatformNotSupportedException)
        {
            // keep the default console encoding when it cannot be changed (e.g. detached WinExe)
        }
        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(CliOptions.Usage);
            return 1;
        }

        if (cli.ShowHelp)
        {
            Console.WriteLine(CliOptions.Usage);
            return 0;
        }

        if (cli.SelfCheck)
        {
            return CliRunners.RunSelfCheck(cli);
        }

        if (cli.DiagnosticsDump)
        {
            return CliRunners.RunDiagnosticsDump(cli);
        }

        return CliRunners.RunServer(cli, args);
    }
}