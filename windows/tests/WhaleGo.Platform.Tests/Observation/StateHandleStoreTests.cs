using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Tests.TestInfrastructure;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class StateHandleStoreTests
{
    [Fact]
    public void RegisterResolveThenExpireByTtl()
    {
        var (handles, window) = GetRealHandles();
        var clock = new FakeClock();
        var store = new StateHandleStore(clock, TimeSpan.FromSeconds(5), maxEntries: 4);

        store.Register("st-00000001", window, handles);
        Assert.Equal(1, store.Count);
        Assert.Equal(StateHandleStatus.Live, store.GetStatus("st-00000001"));
        Assert.True(store.TryGetWindow("st-00000001", out var storedWindow));
        Assert.Equal(window.WindowId, storedWindow!.WindowId);

        var first = handles[0];
        Assert.True(store.TryResolve("st-00000001", first.Index, out var resolved, out var resolvedWindow));
        Assert.Same(first, resolved);
        Assert.Equal(window.WindowId, resolvedWindow!.WindowId);

        Assert.False(store.TryResolve("st-00000001", 9999, out _, out _));
        Assert.False(store.TryResolve("st-unknown", 0, out _, out _));
        Assert.Equal(StateHandleStatus.Unknown, store.GetStatus("st-unknown"));

        clock.NowMs += 6000;
        Assert.Equal(StateHandleStatus.Expired, store.GetStatus("st-00000001"));
        Assert.False(store.TryResolve("st-00000001", first.Index, out _, out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void InvalidateAndInvalidateWindow()
    {
        var (handles, window) = GetRealHandles();
        var clock = new FakeClock();
        var store = new StateHandleStore(clock);

        store.Register("st-00000001", window, handles);
        store.Register("st-00000002", window, handles);
        store.Invalidate("st-00000001");
        Assert.Equal(StateHandleStatus.Unknown, store.GetStatus("st-00000001"));
        Assert.Equal(StateHandleStatus.Live, store.GetStatus("st-00000002"));

        store.InvalidateWindow(window.WindowId);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void LruEvictionKeepsMostRecentEntries()
    {
        var (handles, window) = GetRealHandles();
        var clock = new FakeClock();
        var store = new StateHandleStore(clock, TimeSpan.FromMinutes(10), maxEntries: 2);

        store.Register("st-00000001", window, handles);
        clock.NowMs += 10;
        store.Register("st-00000002", window, handles);
        clock.NowMs += 10;
        store.Register("st-00000003", window, handles);

        Assert.Equal(2, store.Count);
        Assert.Equal(StateHandleStatus.Unknown, store.GetStatus("st-00000001"));
        Assert.Equal(StateHandleStatus.Live, store.GetStatus("st-00000002"));
        Assert.Equal(StateHandleStatus.Live, store.GetStatus("st-00000003"));
    }

    [Fact]
    public void TrimExpiredRemovesOldEntries()
    {
        var (handles, window) = GetRealHandles();
        var clock = new FakeClock();
        var store = new StateHandleStore(clock);
        store.Register("st-00000001", window, handles);
        clock.NowMs += 100;
        store.Register("st-00000002", window, handles);

        Assert.Equal(0, store.TrimExpired(clock.NowMs, TimeSpan.FromSeconds(10)));
        clock.NowMs += 5000;
        Assert.Equal(2, store.TrimExpired(clock.NowMs, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ObservationRegistersResolvableHandles()
    {
        using var harness = PlatformHarness.Start();
        var snapshot = harness.Observe();
        Assert.Matches("^st-[0-9a-f]{8}$", snapshot.StateId);
        Assert.Equal(string.Empty, snapshot.SessionId);
        Assert.NotEmpty(snapshot.Elements);

        Assert.True(harness.Store.TryGetWindow(snapshot.StateId, out var storedWindow));
        Assert.Equal(snapshot.Window.WindowId, storedWindow!.WindowId);
        Assert.Equal(StateHandleStatus.Live, harness.Store.GetStatus(snapshot.StateId));

        foreach (var element in snapshot.Elements)
        {
            Assert.True(
                harness.Store.TryResolve(snapshot.StateId, element.Index, out var handle, out var resolvedWindow),
                $"state {snapshot.StateId} index {element.Index} must resolve");
            Assert.NotNull(handle);
            Assert.Equal(element.Index, handle!.Index);
            Assert.Equal(snapshot.Window.WindowId, handle.WindowId);
            Assert.Equal(resolvedWindow!.WindowId, handle.WindowId);
        }

        harness.Store.Invalidate(snapshot.StateId);
        Assert.Equal(StateHandleStatus.Unknown, harness.Store.GetStatus(snapshot.StateId));
        Assert.False(harness.Store.TryResolve(snapshot.StateId, snapshot.Elements[0].Index, out _, out _));
    }

    private static (IReadOnlyList<ElementHandle> Handles, WindowInfo Window) GetRealHandles()
    {
        using var harness = PlatformHarness.Start();
        var snapshot = harness.Observe();
        var handles = new List<ElementHandle>();
        foreach (var element in snapshot.Elements)
        {
            if (harness.Store.TryResolve(snapshot.StateId, element.Index, out var handle, out _))
            {
                handles.Add(handle!);
            }
        }

        Assert.NotEmpty(handles);
        return (handles, snapshot.Window);
    }

    private sealed class FakeClock : IPlatformClock
    {
        public long NowMs { get; set; } = 1_000_000;
    }
}
