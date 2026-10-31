using System.Diagnostics.CodeAnalysis;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform;

/// <summary>Status of one stateId in the handle store (diagnostics; <see cref="StateHandleStore.GetStatus"/>).</summary>
public enum StateHandleStatus
{
    Unknown = 0,
    Live = 1,
    Expired = 2,
}

/// <summary>
/// stateId → platform element handles cache (ARCHITECTURE §3.6). Owned by the observation platform (T2) and consumed by
/// the element action dispatcher (T3) and the Application layer (T4).
/// <para><b>Stable public surface:</b></para>
/// <list type="bullet">
/// <item><c>Register(stateId, WindowInfo, IReadOnlyList&lt;ElementHandle&gt;)</c> — called by UiaObservationEngine after every observe.</item>
/// <item><c>TryResolve(stateId, elementIndex, out ElementHandle?, out WindowInfo?)</c> — false for unknown state, expired entry,
/// or element index not present in that state.</item>
/// <item><c>TryGetWindow(stateId, out WindowInfo?)</c> — captured window facts (pid/class/bounds/dpi) for a second identity check;
/// HWND is obtained with <see cref="Windows.WindowIdCodec"/>.</item>
/// <item><c>Contains / GetStatus / Count</c> and <c>Invalidate(stateId) / InvalidateWindow(windowId) / TrimExpired(nowMs, ttl) / Clear()</c>.</item>
/// </list>
/// <para>Thread-safe. Default entry TTL is 10 minutes (longer than policy.stateTtlSeconds on purpose: freshness is enforced by
/// Contracts.Domain.FreshnessRules in the application layer, this store only bounds handle lifetime). At most 16 entries are
/// kept, evicting the least-recently-used stateId. This store is a handle cache, not an authority: callers MUST still validate
/// state freshness and live window identity before dispatching any action.</para>
/// </summary>
public sealed class StateHandleStore
{
    public const int DefaultMaxEntries = 16;

    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IPlatformClock _clock;
    private readonly TimeSpan _entryTtl;
    private readonly int _maxEntries;

    /// <summary>Uses <see cref="PlatformClock.Instance"/>, 10 min TTL and 16 entries.</summary>
    public StateHandleStore()
        : this(PlatformClock.Instance, DefaultEntryTtl, DefaultMaxEntries)
    {
    }

    public StateHandleStore(IPlatformClock clock, TimeSpan? entryTtl = null, int maxEntries = DefaultMaxEntries)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _entryTtl = entryTtl ?? DefaultEntryTtl;
        _maxEntries = Math.Max(1, maxEntries);
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public TimeSpan EntryTtl => _entryTtl;

    /// <summary>Register (or replace) the platform handles for one observation state.</summary>
    public void Register(string stateId, WindowInfo window, IReadOnlyList<ElementHandle> elements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(elements);

        var byIndex = new Dictionary<int, ElementHandle>(elements.Count);
        foreach (var element in elements)
        {
            if (element is not null)
            {
                byIndex[element.Index] = element;
            }
        }

        var now = _clock.NowMs;
        lock (_sync)
        {
            _entries[stateId] = new Entry(window, byIndex, now, now);
            PurgeExpiredLocked(now);
            while (_entries.Count > _maxEntries)
            {
                EvictLeastRecentlyUsedLocked();
            }
        }
    }

    /// <summary>Resolve the platform handle + captured window facts for (stateId, elementIndex).</summary>
    public bool TryResolve(
        string stateId,
        int elementIndex,
        [NotNullWhen(true)] out ElementHandle? element,
        [NotNullWhen(true)] out WindowInfo? window)
    {
        element = null;
        window = null;
        if (string.IsNullOrEmpty(stateId))
        {
            return false;
        }

        var now = _clock.NowMs;
        lock (_sync)
        {
            if (!_entries.TryGetValue(stateId, out var entry))
            {
                return false;
            }

            if (IsExpiredLocked(entry, now))
            {
                _entries.Remove(stateId);
                return false;
            }

            entry.LastAccessMs = now;
            if (!entry.Elements.TryGetValue(elementIndex, out var handle))
            {
                return false;
            }

            element = handle;
            window = entry.Window;
            return true;
        }
    }

    /// <summary>Captured window facts for a stateId (for freshness/identity checks before acting).</summary>
    public bool TryGetWindow(string stateId, [NotNullWhen(true)] out WindowInfo? window)
    {
        window = null;
        if (string.IsNullOrEmpty(stateId))
        {
            return false;
        }

        var now = _clock.NowMs;
        lock (_sync)
        {
            if (!_entries.TryGetValue(stateId, out var entry))
            {
                return false;
            }

            if (IsExpiredLocked(entry, now))
            {
                _entries.Remove(stateId);
                return false;
            }

            entry.LastAccessMs = now;
            window = entry.Window;
            return true;
        }
    }

    public bool Contains(string stateId) => TryGetWindow(stateId, out _);

    /// <summary>Unknown when absent; Expired when the TTL lapsed (the entry is dropped); Live otherwise.</summary>
    public StateHandleStatus GetStatus(string stateId)
    {
        if (string.IsNullOrEmpty(stateId))
        {
            return StateHandleStatus.Unknown;
        }

        var now = _clock.NowMs;
        lock (_sync)
        {
            if (!_entries.TryGetValue(stateId, out var entry))
            {
                return StateHandleStatus.Unknown;
            }

            if (IsExpiredLocked(entry, now))
            {
                _entries.Remove(stateId);
                return StateHandleStatus.Expired;
            }

            return StateHandleStatus.Live;
        }
    }

    public void Invalidate(string stateId)
    {
        if (string.IsNullOrEmpty(stateId))
        {
            return;
        }

        lock (_sync)
        {
            _entries.Remove(stateId);
        }
    }

    public void InvalidateWindow(string windowId)
    {
        if (string.IsNullOrEmpty(windowId))
        {
            return;
        }

        lock (_sync)
        {
            var keys = _entries
                .Where(kv => string.Equals(kv.Value.Window.WindowId, windowId, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in keys)
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Remove entries whose capture time is older than <paramref name="ttl"/> relative to <paramref name="nowMs"/>.</summary>
    public int TrimExpired(long nowMs, TimeSpan ttl)
    {
        var ttlMs = ttl <= TimeSpan.Zero ? 0 : (long)ttl.TotalMilliseconds;
        lock (_sync)
        {
            var expired = _entries
                .Where(kv => ttlMs <= 0 || nowMs - kv.Value.CapturedAtMs > ttlMs)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }

            return expired.Count;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private bool IsExpiredLocked(Entry entry, long nowMs) =>
        _entryTtl > TimeSpan.Zero && nowMs - entry.CapturedAtMs > (long)_entryTtl.TotalMilliseconds;

    private void PurgeExpiredLocked(long nowMs)
    {
        var expired = _entries
            .Where(kv => IsExpiredLocked(kv.Value, nowMs))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }
    }

    private void EvictLeastRecentlyUsedLocked()
    {
        string? oldestKey = null;
        var oldestAccess = long.MaxValue;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastAccessMs < oldestAccess)
            {
                oldestAccess = entry.LastAccessMs;
                oldestKey = key;
            }
        }

        if (oldestKey is not null)
        {
            _entries.Remove(oldestKey);
        }
    }

    private sealed class Entry
    {
        public Entry(WindowInfo window, Dictionary<int, ElementHandle> elements, long capturedAtMs, long lastAccessMs)
        {
            Window = window;
            Elements = elements;
            CapturedAtMs = capturedAtMs;
            LastAccessMs = lastAccessMs;
        }

        public WindowInfo Window { get; }

        public Dictionary<int, ElementHandle> Elements { get; }

        public long CapturedAtMs { get; }

        public long LastAccessMs { get; set; }
    }
}
