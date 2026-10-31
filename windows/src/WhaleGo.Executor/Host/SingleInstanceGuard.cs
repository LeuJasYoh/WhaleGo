using System;
using System.IO;
using System.Threading;

namespace WhaleGo.Executor.Host;

/// <summary>
/// Single instance via Local\WhaleGo.Executor.SingleInstance mutex (per interactive session) plus the
/// Local\WhaleGo.Executor.ShowSettings event used by a second (GUI) instance to surface the running window.
/// Headless conflicts exit with code 2 (scripts can detect them).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string BaseMutexName = @"Local\WhaleGo.Executor.SingleInstance";
    public const string BaseShowSettingsEventName = @"Local\WhaleGo.Executor.ShowSettings";

    private readonly string _mutexName;
    private readonly string _showSettingsEventName;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSettingsEvent;
    private Thread? _listener;
    private volatile bool _stopping;
    private bool _disposed;

    private SingleInstanceGuard(string mutexName, string showSettingsEventName, Mutex mutex, EventWaitHandle showSettingsEvent)
    {
        _mutexName = mutexName;
        _showSettingsEventName = showSettingsEventName;
        _mutex = mutex;
        _showSettingsEvent = showSettingsEvent;
    }

    /// <summary>
    /// Instance names are per data root: the default %LOCALAPPDATA%\WhaleGo root keeps the plain production names, while
    /// an overridden data root (WHALEGO_DATA_DIR or --config, e.g. E2E/CI) gets a deterministic suffix so it can run
    /// alongside a live production executor. Identical (data root, instance) pairs still conflict with exit code 2.
    /// </summary>
    public static string InstanceSuffix(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return string.Empty;
        }

        var normalized = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "." + Convert.ToHexString(hash).ToLowerInvariant()[..8];
    }

    public static string MutexNameFor(string suffix) => BaseMutexName + suffix;

    public static string ShowSettingsEventNameFor(string suffix) => BaseShowSettingsEventName + suffix;

    /// <summary>Try to become the single instance; otherwise the caller signals the primary instance / exits.</summary>
    public static SingleInstanceAcquisition Acquire(TimeSpan wait, string suffix = "")
    {
        var mutexName = MutexNameFor(suffix);
        var eventName = ShowSettingsEventNameFor(suffix);
        var mutex = new Mutex(true, mutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceAcquisition(new SingleInstanceGuard(mutexName, eventName, mutex, CreateEvent(eventName)), PrimaryRunning: false);
        }

        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (acquired)
        {
            return new SingleInstanceAcquisition(new SingleInstanceGuard(mutexName, eventName, mutex, CreateEvent(eventName)), PrimaryRunning: false);
        }

        mutex.Dispose();
        return new SingleInstanceAcquisition(null, PrimaryRunning: true);
    }

    public static bool SignalPrimaryShowSettings(string suffix = "")
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowSettingsEventNameFor(suffix));
            handle.Set();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    public void StartShowSettingsListener(Action onShowSettings)
    {
        ArgumentNullException.ThrowIfNull(onShowSettings);
        if (_listener is not null)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            while (!_stopping)
            {
                try
                {
                    if (_showSettingsEvent.WaitOne(250))
                    {
                        try
                        {
                            onShowSettings();
                        }
                        catch (Exception)
                        {
                            // GUI failures must never kill the kernel.
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "WhaleGo.ShowSettings",
        };
        _listener.Start();
    }

    private static EventWaitHandle CreateEvent(string eventName) =>
        new(false, EventResetMode.AutoReset, eventName);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        try
        {
            _showSettingsEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        _listener?.Join(TimeSpan.FromSeconds(1));
        _showSettingsEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned
        }

        _mutex.Dispose();
    }
}

public sealed record SingleInstanceAcquisition(SingleInstanceGuard? Guard, bool PrimaryRunning);