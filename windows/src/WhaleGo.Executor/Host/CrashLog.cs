using System;
using System.IO;
using System.Threading.Tasks;

namespace WhaleGo.Executor.Host;

/// <summary>Crash evidence in %LOCALAPPDATA%\WhaleGo\logs (never tokens/plaintext); fail-closed means no auto-recovers.</summary>
public static class CrashLog
{
    private static readonly object Lock = new();
    private static string? _directory;

    public static void Configure(string directory) => _directory = directory;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Write("unhandled", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Write("unobserved-task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private static void Write(string kind, Exception? exception)
    {
        try
        {
            var directory = _directory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhaleGo", "logs");
            }

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId + ".log");
            var text = DateTimeOffset.Now.ToString("o") + " [" + kind + "] " + (exception?.ToString() ?? "unknown") + Environment.NewLine;
            lock (Lock)
            {
                File.AppendAllText(file, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // crashing while writing the crash log must not crash further
        }
    }
}
