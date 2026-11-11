using System.Threading.Channels;

using DotNext.Threading;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Tests;

record M
{
    public string? A { get; init; }
    public int B { get; init; }
}

public sealed class RedisChannelTests(ITestOutputHelper output) : IAsyncLifetime
{
    private IConnectionMultiplexer _cnn = null!;

    // Unique per test (xunit creates a class instance per test), so parallel runs never share Redis keys.
    private string ChannelName { get; } = $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync()
    {
        _cnn = await TestRedis.ConnectAsync();
        await _cnn.GetDatabase().KeyDeleteAsync([ChannelName, RedisChannelUtils.CompletionKey(ChannelName)]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cnn == null!)
            return;

        await _cnn.GetDatabase().KeyDeleteAsync([ChannelName, RedisChannelUtils.CompletionKey(ChannelName)]);
        await _cnn.DisposeAsync();
    }

    private RedisChannelWriter<M> Writer(RedisChannelOptions? opts = null) =>
        new(ChannelName, _cnn.GetDatabase(), new JsonRedisValueSerializer<M>(), opts);

    private RedisChannelReader<M> Reader(RedisChannelReaderOptions? opts = null) =>
        new(ChannelName, _cnn.GetDatabase(), new JsonRedisValueSerializer<M>(), opts);

    private RedisChannel<M> Channel(RedisChannelOptions? opts, bool utf8 = true) =>
        new(
            ChannelName,
            _cnn.GetDatabase(),
            utf8 ? new Utf8JsonRedisValueSerializer<M>() : new JsonRedisValueSerializer<M>(),
            opts
            );

    [Fact(Timeout = 15_000)]
    public async Task Channel_WriterAndReaderRunConcurrently()
    {
        var wm = new M { A = "asd", B = 123213 };

        using var channel = Channel(null);
        using var goAhead = new AsyncAutoResetEvent(false);

        var writerTask = Task.Run(
            async () =>
            {
                // ReSharper disable once AccessToDisposedClosure
                var w = channel.Writer;
                Assert.True(w.TryWrite(wm));
                output.WriteLine("Write #1");

                // ReSharper disable once AccessToDisposedClosure
                goAhead.Set();

                // Give the reader time to block in WaitToReadAsync.
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

                Assert.True(w.TryWrite(wm));
                output.WriteLine("Write #2");

                Assert.True(w.TryComplete());
                output.WriteLine("Complete");

                await Assert.ThrowsAsync<ChannelClosedException>(async () => await w.WriteAsync(wm, TestContext.Current.CancellationToken));
            },
            TestContext.Current.CancellationToken
            );

        var readerTask = Task.Run(
            async () =>
            {
                // ReSharper disable once AccessToDisposedClosure
                await goAhead.WaitAsync(TestContext.Current.CancellationToken);

                // ReSharper disable once AccessToDisposedClosure
                var r = channel.Reader;

                Assert.True(r.TryRead(out var rm));
                Assert.Equal(wm, rm);
                output.WriteLine("Read #1");

                Assert.True(await r.WaitToReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(wm, await r.ReadAsync(TestContext.Current.CancellationToken));
                output.WriteLine("Read #2");

                Assert.False(await r.WaitToReadAsync(TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<ChannelClosedException>(async () => await r.ReadAsync(TestContext.Current.CancellationToken));
            },
            TestContext.Current.CancellationToken
            );

        await Task.WhenAll(writerTask, readerTask).WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read(bool utf8)
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = Channel(new(2) { FullMode = BoundedChannelFullMode.DropOldest }, utf8);

        var w = channel.Writer;
        var r = channel.Reader;

        Assert.False(r.TryPeek(out _));
        Assert.True(w.TryWrite(wm));
        Assert.True(w.TryComplete());

        Assert.True(r.TryPeek(out var m1));
        Assert.Equal(1, m1!.B);

        Assert.True(r.TryRead(out var m2));
        Assert.Equal(1, m2!.B);

        Assert.False(r.TryRead(out _));
    }

    [Fact]
    public void BoundedWriterDropOldest()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = Channel(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        for (var i = 2; i <= 5; i++)
            Assert.True(channel.Writer.TryWrite(wm with { B = i }));

        Assert.True(channel.Reader.TryRead(out var i1));
        Assert.Equal(4, i1!.B);

        Assert.True(channel.Reader.TryRead(out var i2));
        Assert.Equal(5, i2!.B);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void BoundedWriterDropWrite()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = Channel(new(2) { FullMode = BoundedChannelFullMode.DropWrite });

        // Writes to a full channel are dropped but still reported as successful.
        for (var i = 2; i <= 5; i++)
            Assert.True(channel.Writer.TryWrite(wm with { B = i }));

        Assert.True(channel.Reader.TryRead(out var i1));
        Assert.Equal("asd", i1!.A);
        Assert.Equal(2, i1.B);

        Assert.True(channel.Reader.TryRead(out var i2));
        Assert.Equal("asd", i2!.A);
        Assert.Equal(3, i2.B);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void BoundedWriterDropNewest()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = Channel(new(2) { FullMode = BoundedChannelFullMode.DropNewest });

        for (var i = 2; i <= 5; i++)
            Assert.True(channel.Writer.TryWrite(wm with { B = i }));

        Assert.True(channel.Reader.TryRead(out var i1));
        Assert.Equal("asd", i1!.A);
        Assert.Equal(2, i1.B);

        Assert.True(channel.Reader.TryRead(out var i2));
        Assert.Equal("asd", i2!.A);
        Assert.Equal(5, i2.B);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact(Timeout = 15_000)]
    public async Task BoundedWriterWait()
    {
        var wm = new M { A = "asd", B = 1 };
        using var goAhead = new AsyncAutoResetEvent(false);

        var writerTask = Task.Run(
            async () =>
            {
                using var w = Writer(new(2) { FullMode = BoundedChannelFullMode.Wait });

                await w.WriteAsync(wm with { B = 2 }, TestContext.Current.CancellationToken);
                await w.WriteAsync(wm with { B = 3 }, TestContext.Current.CancellationToken);
                Assert.False(w.TryWrite(wm with { B = 4 }));

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(1));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await w.WriteAsync(wm with { B = 4 }, cts.Token));
                }

                // ReSharper disable once AccessToDisposedClosure
                goAhead.Set();

                await w.WriteAsync(wm with { B = 5 }, TestContext.Current.CancellationToken);

                Assert.True(w.TryComplete());
            },
            TestContext.Current.CancellationToken
            );

        using var r = Reader();

        await goAhead.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, (await r.ReadAsync(TestContext.Current.CancellationToken)).B);
        Assert.Equal(3, (await r.ReadAsync(TestContext.Current.CancellationToken)).B);
        Assert.Equal(5, (await r.ReadAsync(TestContext.Current.CancellationToken)).B);

        await writerTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloseChannelWithError()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });
        using var w2 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.True(w1.TryWrite(wm));
        Assert.True(w2.TryWrite(wm));
        Assert.True(w1.TryComplete(new InvalidOperationException("Close!")));

        // No wait for the close notification: the write itself detects completion in Redis.
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm, TestContext.Current.CancellationToken));

        Assert.Equal("Close!", ex.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task CloseChannelWithSuccess()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });
        using var w2 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });
        using var r = Reader();

        Assert.True(w1.TryWrite(wm));
        Assert.True(w2.TryWrite(wm with { B = 2 }));
        Assert.True(w1.TryComplete());

        await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm with { B = 3 }, TestContext.Current.CancellationToken));

        var items = new List<M>();

        await foreach (var p in r.ReadAllAsync(TestContext.Current.CancellationToken))
            items.Add(p);

        Assert.Collection(
            items,
            p => Assert.Equal(1, p.B),
            p => Assert.Equal(2, p.B)
            );
    }

    [Fact]
    public void WriteFailsIfClosed()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.True(w1.TryWrite(wm));
        Assert.True(w1.TryComplete());

        using var w2 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.False(w2.TryWrite(wm));
    }

    [Fact]
    public async Task WriteFailsIfClosedWithError()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.True(w1.TryWrite(wm));
        Assert.True(w1.TryComplete(new InvalidOperationException("Close!")));

        using var w2 = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.False(w2.TryWrite(wm));

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm, TestContext.Current.CancellationToken));

        Assert.Equal("Close!", ex.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task Cleanup_SlidingExpirationAppliesAfterCompletion()
    {
        var db = _cnn.GetDatabase();

        using var writer = Writer(new(2) { ChannelSlidingExpiration = TimeSpan.FromSeconds(1) });

        Assert.True(writer.TryWrite(new() { A = "asd", B = 1 }));
        Assert.True(writer.TryComplete());

        Assert.NotNull(db.KeyTimeToLive(ChannelName));
        Assert.NotNull(db.KeyTimeToLive(RedisChannelUtils.CompletionKey(ChannelName)));

        Assert.True(await TestRedis.WaitUntilAsync(() => !db.KeyExists(ChannelName), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));
    }

    [Fact(Timeout = 10_000)]
    public async Task Cleanup_ExpireAfterCompletion()
    {
        var db = _cnn.GetDatabase();

        using var writer = Writer(new(2) { ExpireAfterCompletion = TimeSpan.FromSeconds(1) });

        Assert.True(writer.TryWrite(new() { A = "asd", B = 1 }));

        // No expiration before completion.
        Assert.Null(db.KeyTimeToLive(ChannelName));

        Assert.True(writer.TryComplete());

        Assert.NotNull(db.KeyTimeToLive(ChannelName));
        Assert.NotNull(db.KeyTimeToLive(RedisChannelUtils.CompletionKey(ChannelName)));

        Assert.True(await TestRedis.WaitUntilAsync(() => !db.KeyExists(ChannelName), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));
    }

    [Fact]
    public void CanReadIfClosed()
    {
        using var writer = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest });

        Assert.True(writer.TryWrite(new() { A = "asd", B = 1 }));
        Assert.True(writer.TryComplete());

        using var reader = Reader();

        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));
    }

    [Fact(Timeout = 10_000)]
    public async Task CanDrainThenFailIfClosedWithError()
    {
        using var writer = Writer();

        Assert.True(writer.TryWrite(new() { A = "asd", B = 1 }));
        Assert.True(writer.TryComplete(new InvalidOperationException("boom")));

        using var reader = Reader();

        Assert.True(await reader.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, (await reader.ReadAsync(TestContext.Current.CancellationToken)).B);

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("boom", ex.Message);

        await Assert.ThrowsAsync<ChannelClosedException>(() => reader.Completion);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReaderHandleExpiration()
    {
        var expiration = TimeSpan.FromSeconds(1);

        using var writer = Writer(new(2) { FullMode = BoundedChannelFullMode.DropOldest, ChannelSlidingExpiration = expiration });
        using var reader = Reader(new() { ChannelSlidingExpiration = expiration });

        Assert.True(writer.TryWrite(new() { A = "asd", B = 1 }));
        Assert.True(reader.TryPeek(out _));

        var db = _cnn.GetDatabase();
        Assert.True(await TestRedis.WaitUntilAsync(() => !db.KeyExists(ChannelName), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.False(reader.TryRead(out _));
        Assert.False(await reader.WaitToReadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 10_000)]
    public async Task WriterHandleExpiration()
    {
        var wm = new M { A = "asd", B = 1 };

        using var writer = Writer(
            new(2) { FullMode = BoundedChannelFullMode.Wait, ChannelSlidingExpiration = TimeSpan.FromSeconds(1) }
            );

        Assert.True(writer.TryWrite(wm));
        Assert.True(writer.TryWrite(wm));
        Assert.False(writer.TryWrite(wm));

        // The full list expires, so there is room again.
        Assert.True(await writer.WaitToWriteAsync(TestContext.Current.CancellationToken));
    }
}