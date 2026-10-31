using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WhaleGo.Executor.Infrastructure.Logging;

/// <summary>Minimal dependency-free rotating-by-day file logger (no token/params are ever passed by callers).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string directory, LogLevel minimumLevel)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        _loggers.Clear();
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level(level)}] {category}: {message}";
            if (exception is not null)
            {
                line += " | " + exception.GetType().Name + ": " + exception.Message;
            }

            var file = Path.Combine(_directory, "executor-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never kill the kernel.
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
