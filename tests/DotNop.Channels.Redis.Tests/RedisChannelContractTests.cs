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
        _cnn = await TestRedis.ConnectAsync();
        await Db.KeyDeleteAsync([Name, RedisChannelUtils.CompletionKey(Name)]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cnn == null!)
            return;

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
        Assert.True(w.TryWrite("a"));
        Assert.True(w.TryWrite("b"));
        Assert.True(w.TryWrite("c"));

        using var r = R();
        Assert.True(r.TryRead(out var item));
        Assert.Equal("c", item);
        Assert.False(r.TryRead(out _));
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

        Assert.False(await r.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.True(r.Completion.IsCompleted);
    }

    [Fact(Timeout = 10_000)]
    public async Task Completion_FaultsWhenCompletedWithError()
    {
        using var w = W();
        using var r = R();
        w.Complete(new InvalidOperationException("boom"));

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

    [Fact(Timeout = 10_000)]
    public async Task TryReadAndTryPeek_DoNotThrowWhenCompletedWithError()
    {
        using var w = W();
        using var r = R();
        w.Complete(new InvalidOperationException("boom"));

        // Make sure the reader has observed the error before probing the Try* methods.
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await r.WaitToReadAsync(TestContext.Current.CancellationToken)
            );

        Assert.Null(Record.Exception(() => Assert.False(r.TryRead(out _))));
        Assert.Null(Record.Exception(() => Assert.False(r.TryPeek(out _))));
    }

    /// <summary>Completes the channel the way another process would: in Redis only, with no close notification.</summary>
    private void CompleteRemotely(string error = "") =>
        Assert.True(Db.StringSet(RedisChannelUtils.CompletionKey(Name), error));

    [Theory(Timeout = 10_000)]
    [InlineData(-1, BoundedChannelFullMode.Wait)]
    [InlineData(5, BoundedChannelFullMode.Wait)]
    [InlineData(5, BoundedChannelFullMode.DropWrite)]
    [InlineData(5, BoundedChannelFullMode.DropNewest)]
    [InlineData(5, BoundedChannelFullMode.DropOldest)]
    public async Task TryWrite_FailsOnceCompletedInRedis(int capacity, BoundedChannelFullMode mode)
    {
        using var w = W(new RedisChannelOptions(capacity) { FullMode = mode });

        Assert.True(w.TryWrite("a"));
        CompleteRemotely();

        Assert.False(w.TryWrite("b"));
        Assert.Equal(1, Db.ListLength(Name));

        // The failed write adopted the completion state.
        Assert.False(await w.WaitToWriteAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 10_000)]
    public async Task WriteAsync_ThrowsOnceCompletedInRedisWithError()
    {
        using var w = W();
        CompleteRemotely("boom");

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await w.WriteAsync("a", TestContext.Current.CancellationToken)
            );

        Assert.Equal("boom", ex.Message);
        Assert.Equal(0, Db.ListLength(Name));
    }

    [Fact(Timeout = 10_000)]
    public async Task TryComplete_FailsOnceCompletedInRedis()
    {
        using var w = W();
        CompleteRemotely("first");

        Assert.False(w.TryComplete(new InvalidOperationException("second")));
        Assert.Equal("first", Db.StringGet(RedisChannelUtils.CompletionKey(Name)).ToString());

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await w.WaitToWriteAsync(TestContext.Current.CancellationToken)
            );

        Assert.Equal("first", ex.Message);
    }

    [Fact]
    public void TryComplete_SecondCallOnSameWriterFails()
    {
        using var w = W();

        Assert.True(w.TryComplete());
        Assert.False(w.TryComplete(new InvalidOperationException("late")));
        Assert.Equal(string.Empty, Db.StringGet(RedisChannelUtils.CompletionKey(Name)).ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task TryComplete_WithEmptyErrorMessage_CompletesNormally()
    {
        using var w = W();
        using var r = R();

        // An empty message cannot be told apart from success in Redis.
        Assert.True(w.TryComplete(new InvalidOperationException(string.Empty)));

        Assert.False(await r.WaitToReadAsync(TestContext.Current.CancellationToken));
        await r.Completion;
    }

    [Theory]
    [InlineData("orders", "{orders}.eof")]
    [InlineData("orders{eu}", "orders{eu}.eof")]
    [InlineData("{eu}orders", "{eu}orders.eof")]
    [InlineData("a{b", "{a{b}.eof")]
    public void CompletionKey_SharesHashSlotWithListKey(string channel, string expected)
    {
        var key = RedisChannelUtils.CompletionKey(channel);

        Assert.Equal(expected, key.ToString());
        Assert.Equal(TestRedis.KeySlot(channel), TestRedis.KeySlot(key.ToString()));
    }

    [Theory]
    [InlineData("a{}b")]
    [InlineData("a}b")]
    public void CompletionKey_NamesWithStrayBraces_LandInAnotherSlot(string channel) =>

        // Documented limitation: a name hashed whole that contains '}' cannot be wrapped in a tag.
        Assert.NotEqual(TestRedis.KeySlot(channel), TestRedis.KeySlot(RedisChannelUtils.CompletionKey(channel).ToString()));

    [Theory]
    [InlineData("foo", 12182)]
    [InlineData("bar", 5061)]
    [InlineData("123456789", 0x31C3)]
    public void KeySlot_MatchesRedisCluster(string key, int slot) => Assert.Equal(slot, TestRedis.KeySlot(key));

    [Theory]
    [InlineData("{user1000}.following", "user1000")]
    [InlineData("foo{bar}{zap}", "bar")]
    [InlineData("foo{}{bar}", "foo{}{bar}")]
    public void KeySlot_HashesByTag(string key, string hashedPart) =>
        Assert.Equal(TestRedis.KeySlot(hashedPart), TestRedis.KeySlot(key));
}