using WhaleGo.Platform.Input;

namespace WhaleGo.Platform.Tests.Input;

public sealed class InputLaneTests
{
    [Fact]
    public async Task RunAsync_SerializesConcurrentWork()
    {
        var lane = new InputLane();
        var active = 0;
        var maxActive = 0;
        var completed = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => lane.RunAsync(async _ =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref completed);
            return true;
        }, CancellationToken.None));

        await Task.WhenAll(tasks);
        Assert.Equal(8, completed);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task RunAsync_NestedCallRunsInlineWithoutDeadlock()
    {
        var lane = new InputLane();
        var innerRan = false;
        var result = await lane.RunAsync(async _ =>
        {
            var inner = await lane.RunAsync(_ =>
            {
                innerRan = true;
                return Task.FromResult(42);
            }, CancellationToken.None);
            return inner;
        }, CancellationToken.None);

        Assert.True(innerRan);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_CanceledTokenThrowsBeforeRunningWork()
    {
        var lane = new InputLane();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lane.RunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(1);
        }, cts.Token));

        Assert.False(ran);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
