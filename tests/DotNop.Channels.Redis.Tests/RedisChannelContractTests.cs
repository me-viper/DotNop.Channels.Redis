using System.Threading.Channels;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Tests;

// Channel contract checks: consumption, completion, disposal and bounded modes.
public sealed class RedisChannelContractTests : IAsyncLifetime
{
    private IConnectionMultiplexer _cnn = null!;

    private string Name { get; } = $"test_{Guid.NewGuid():N}";

    private IDatabase Db => _cnn.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        _cnn = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        await Db.KeyDeleteAsync([Name, RedisChannelUtils.CompletionKey(Name)]);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.KeyDeleteAsync([Name, RedisChannelUtils.CompletionKey(Name)]);
        await _cnn.DisposeAsync();
    }

    private RedisChannelWriter<string> W(RedisChannelOptions? o = null) =>
        new(Name, Db, new JsonRedisValueSerializer<string>(), o);

    private RedisChannelReader<string> R() => new(Name, Db, new JsonRedisValueSerializer<string>());

    [Fact(Timeout = 10_000)]
    public async Task ReadAllAsync_ConsumesItems()
    {
        using var w = W();
        using var r = R();
        w.TryWrite("a");
        w.TryWrite("b");
        w.Complete();

        var items = new List<string>();
        await foreach (var p in r.ReadAllAsync(TestContext.Current.CancellationToken))
            items.Add(p);

        Assert.Equal(["a", "b"], items);
        Assert.Equal(0, r.Count);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReadAllAsync_ContinuesUntilCompleted()
    {
        using var w = W();
        using var r = R();
        w.TryWrite("a");

        var items = new List<string>();

        await foreach (var p in r.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            items.Add(p);

            if (items.Count == 1)
            {
                w.TryWrite("b");
                w.Complete();
            }
        }

        Assert.Equal(["a", "b"], items);
    }

    [Fact]
    public void DropNewest_Capacity1_IsBounded()
    {
        using var w = W(new RedisChannelOptions(1) { FullMode = BoundedChannelFullMode.DropNewest });
        w.TryWrite("a");
        w.TryWrite("b");
        w.TryWrite("c");

        Assert.Equal(1, Db.ListLength(Name));
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposingOneReader_DoesNotBreakAnother()
    {
        using var w = W();
        var r1 = R();
        using var r2 = R();
        r1.Dispose();

        var wait = r2.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        w.TryWrite("a");

        var done = await Task.WhenAny(wait, Task.Delay(3000, TestContext.Current.CancellationToken));
        Assert.Same(wait, done);
    }

    [Fact(Timeout = 10_000)]
    public async Task Completion_CompletesAfterDrain()
    {
        using var w = W();
        using var r = R();
        w.Complete();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(await r.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.True(r.Completion.IsCompleted);
    }

    [Fact(Timeout = 10_000)]
    public async Task Completion_FaultsWhenCompletedWithError()
    {
        using var w = W();
        using var r = R();
        w.Complete(new InvalidOperationException("boom"));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ChannelClosedException>(async () => await r.WaitToReadAsync(TestContext.Current.CancellationToken));
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(() => r.Completion);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void WaitMode_ManyWritersNeverExceedCapacity()
    {
        var writers = Enumerable.Range(0, 8)
            .Select(_ => W(new RedisChannelOptions(5) { FullMode = BoundedChannelFullMode.Wait }))
            .ToArray();

        try
        {
            Parallel.ForEach(
                writers,
                p =>
                {
                    for (var i = 0; i < 20; i++)
                        p.TryWrite("x");
                }
                );

            Assert.Equal(5, Db.ListLength(Name));
        }
        finally
        {
            foreach (var p in writers)
                p.Dispose();
        }
    }

    [Fact]
    public void NullItem_RoundTrips()
    {
        using var w = new RedisChannelWriter<string?>(Name, Db, new JsonRedisValueSerializer<string?>());
        using var r = new RedisChannelReader<string?>(Name, Db, new JsonRedisValueSerializer<string?>());

        Assert.True(w.TryWrite(null));
        Assert.True(r.TryRead(out var item));
        Assert.Null(item);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Options_RejectInvalidCapacity(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisChannelOptions(capacity));

    [Fact]
    public void Options_DefaultIsUnbounded()
    {
        var opts = new RedisChannelOptions();
        Assert.False(opts.HasCapacity);
    }

    [Fact]
    public void TryRead_DoesNotThrowWhenCompletedWithError()
    {
        using var w = W();
        using var r = R();
        w.Complete(new InvalidOperationException("boom"));
        Thread.Sleep(300);

        var ex = Record.Exception(() => r.TryRead(out _));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(-1, BoundedChannelFullMode.Wait)]
    [InlineData(5, BoundedChannelFullMode.Wait)]
    [InlineData(5, BoundedChannelFullMode.DropWrite)]
    [InlineData(5, BoundedChannelFullMode.DropNewest)]
    [InlineData(5, BoundedChannelFullMode.DropOldest)]
    public void TryWrite_FailsRightAfterAnotherWriterCompletes(int capacity, BoundedChannelFullMode mode)
    {
        var opts = new RedisChannelOptions(capacity) { FullMode = mode };
        using var w1 = W(opts);
        using var w2 = W(opts);

        Assert.True(w2.TryWrite("a"));
        Assert.True(w1.TryComplete());

        // No wait for the close notification: the push script checks the completion key itself.
        Assert.False(w2.TryWrite("b"));
        Assert.Equal(1, Db.ListLength(Name));
    }

    [Fact]
    public async Task TryComplete_OnlyFirstCompleterWins()
    {
        using var w1 = W();
        using var w2 = W();

        Assert.True(w1.TryComplete(new InvalidOperationException("first")));
        Assert.False(w2.TryComplete(new InvalidOperationException("second")));
        Assert.False(w1.TryComplete());

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await w2.WaitToWriteAsync(TestContext.Current.CancellationToken)
            );

        Assert.Equal("first", ex.Message);
    }

    [Theory]
    [InlineData("orders", "{orders}.eof")]
    [InlineData("orders{eu}", "orders{eu}.eof")]
    [InlineData("{eu}orders", "{eu}orders.eof")]
    public void CompletionKey_SharesHashSlotWithListKey(string channel, string expected)
    {
        var key = RedisChannelUtils.CompletionKey(channel);

        Assert.Equal(expected, key.ToString());
        Assert.Equal(_cnn.HashSlot(channel), _cnn.HashSlot(key));
    }
}