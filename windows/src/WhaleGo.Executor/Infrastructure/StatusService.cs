using System;
using System.Collections.Generic;
using System.Linq;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure;

/// <summary>Recent-request ring buffer and tool counters for the diagnostics page (never parameters/titles/tokens).</summary>
public sealed class StatusService
{
    private const int RecentCapacity = 50;
    private readonly object _lock = new();
    private readonly LinkedList<RecentRequest> _recent = new();
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);

    public void RecordRequest(RecentRequest request)
    {
        lock (_lock)
        {
            _recent.AddFirst(request);
            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveLast();
            }
        }
    }

    public void IncrementTool(string tool)
    {
        lock (_lock)
        {
            _counters[tool] = _counters.TryGetValue(tool, out var count) ? count + 1 : 1;
        }
    }

    public IReadOnlyList<RecentRequest> GetRecentRequests(int limit)
    {
        var take = Math.Clamp(limit, 0, RecentCapacity);
        lock (_lock)
        {
            return _recent.Take(take).ToList();
        }
    }

    public IReadOnlyDictionary<string, long> GetToolCounters()
    {
        lock (_lock)
        {
            return new Dictionary<string, long>(_counters, StringComparer.Ordinal);
        }
    }
}
