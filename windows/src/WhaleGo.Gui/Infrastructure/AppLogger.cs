using System.IO;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>GUI-side logger. Writes to a file under the configured logs directory; never logs token plaintext.</summary>
public interface IAppLogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    private NullAppLogger()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Best-effort append-only file logger. Failures are swallowed on purpose: logging must never take the
/// executor down (the kernel keeps running even if the GUI log path is unwritable).
/// </summary>
public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly string? _path;
    private bool _disposed;

    public FileAppLogger(string? directory, string fileName = "gui.log")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, fileName);
        }
        catch
        {
            _path = null;
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = string.Concat(
            DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            " [",
            level,
            "] ",
            TokenRedactor.Redact(message));
        if (exception is not null)
        {
            line += Environment.NewLine + TokenRedactor.Redact(exception.ToString());
        }

        lock (_sync)
        {
            if (_disposed || _path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // intentionally ignored: never let logging break the UI/kernel
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }
}
