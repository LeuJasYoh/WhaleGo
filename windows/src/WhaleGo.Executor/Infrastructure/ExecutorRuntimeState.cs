using System;
using System.Collections.Generic;

namespace WhaleGo.Executor.Infrastructure;

/// <summary>Process-wide runtime facts shared by the host, control surface and self-check.</summary>
public sealed class ExecutorRuntimeState
{
    private readonly object _lock = new();
    private readonly List<string> _warnings = new();

    public long StartedAtMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool McpListening { get; set; }

    public string? ListenRequested { get; set; }

    public string? ListenActual { get; set; }

    public int ActualPort { get; set; }

    public bool Headless { get; set; } = true;

    public bool NoHotkey { get; set; } = true;

    public bool HotkeyEnabled { get; set; }

    public bool HotkeyRegistered { get; set; }

    public string? HotkeyGesture { get; set; }

    public string? HotkeyError { get; set; }

    public string LastMcpProtocolVersion { get; set; } = string.Empty;

    public bool RestartRequested { get; set; }

    public string? FaultedReason { get; set; }

    public ManualResetEventSlim Shutdown { get; } = new(false);

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_lock)
            {
                return _warnings.ToArray();
            }
        }
    }

    public void AddWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        lock (_lock)
        {
            if (_warnings.Count < 20 && !_warnings.Contains(warning, StringComparer.Ordinal))
            {
                _warnings.Add(warning);
            }
        }
    }

    public void ClearWarnings()
    {
        lock (_lock)
        {
            _warnings.Clear();
        }
    }
}
