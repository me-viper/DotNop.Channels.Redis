using System.Threading.Channels;

using DotNext.Threading;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Tests;

record M
{
    public string? A { get; init; }
    public int B { get; init; }
}

public class RedisChannelTests(ITestOutputHelper output) : IAsyncLifetime
{
    private IConnectionMultiplexer _cnn = null!;

    // Unique per test (xunit creates a class instance per test), so parallel runs never share Redis keys.
    private string ChannelName { get; } = $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync()
    {
        _cnn = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        _cnn.GetDatabase().KeyDelete(ChannelName);
        _cnn.GetDatabase().KeyDelete(RedisChannelUtils.CompletionKey(ChannelName));
    }

    public ValueTask DisposeAsync()
    {
        _cnn.GetDatabase().KeyDelete(ChannelName);
        _cnn.GetDatabase().KeyDelete(RedisChannelUtils.CompletionKey(ChannelName));

        return ValueTask.CompletedTask;
    }

    [Fact(Timeout = 15_000)]

    //[Fact]
    public async Task Channel()
    {
        var wm = new M { A = "asd", B = 123213 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            null
            );

        using var goAhead = new AsyncAutoResetEvent(false);

        var writerTask = Task.Run(
            async () =>
            {
                try
                {
                    // ReSharper disable once AccessToDisposedClosure
                    var w = channel.Writer;
                    var res = w.TryWrite(wm);
                    Assert.True(res);
                    output.WriteLine("Write #1");

                    goAhead.Set();
                    output.WriteLine("Signal #1");

                    await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                    res = w.TryWrite(wm);
                    Assert.True(res);
                    output.WriteLine("Write #2");

                    res = w.TryComplete();
                    Assert.True(res);
                    output.WriteLine("Complete");

                    await Assert.ThrowsAsync<ChannelClosedException>(async () => await w.WriteAsync(wm, TestContext.Current.CancellationToken));
                }
                catch (Exception ex)
                {
                    Assert.Fail(ex.Message);
                }
            },
            TestContext.Current.CancellationToken
            );

        var readerTask = Task.Run(
            async () =>
            {
                try
                {
                    await goAhead.WaitAsync(TestContext.Current.CancellationToken);
                    output.WriteLine("Signaled #1");

                    // ReSharper disable once AccessToDisposedClosure
                    var r = channel.Reader;

                    var res = r.TryRead(out var rm);
                    Assert.True(res);
                    Assert.Equal(wm, rm);
                    output.WriteLine("Read #1");

                    var r1 = await r.WaitToReadAsync(TestContext.Current.CancellationToken);
                    Assert.True(r1);
                    output.WriteLine("Wait to read #1");

                    rm = await r.ReadAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(wm, rm);
                    output.WriteLine("Read #2");

                    var r2 = await r.WaitToReadAsync(TestContext.Current.CancellationToken);
                    Assert.False(r2);
                    output.WriteLine("Wait to read #2");

                    await Assert.ThrowsAsync<ChannelClosedException>(async () => await r.ReadAsync(TestContext.Current.CancellationToken));
                }
                catch (Exception ex)
                {
                    Assert.Fail(ex.Message);
                }
            },
            TestContext.Current.CancellationToken
            );

        await Task.WhenAll(writerTask, readerTask).WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Read()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var w = channel.Writer;
        var r = channel.Reader;

        var r1 = r.TryPeek(out _);
        Assert.False(r1);

        var r2 = w.TryWrite(wm);
        Assert.True(r2);

        Assert.True(w.TryComplete());

        var r3 = r.TryPeek(out var m1);
        Assert.True(r3);
        Assert.Equal(1, m1!.B);

        var r4 = r.TryRead(out var m2);
        Assert.True(r4);
        Assert.Equal(1, m2!.B);

        var r5 = r.TryRead(out _);
        Assert.False(r5);
    }

    [Fact]
    public void BoundedWriterDropOldest()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        channel.Writer.TryWrite(wm with { B = 2 });
        channel.Writer.TryWrite(wm with { B = 3 });
        channel.Writer.TryWrite(wm with { B = 4 });
        channel.Writer.TryWrite(wm with { B = 5 });

        var res = channel.Reader.TryRead(out var i1);
        Assert.True(res);
        Assert.Equal(4, i1!.B);

        res = channel.Reader.TryRead(out var i2);
        Assert.True(res);
        Assert.Equal(5, i2!.B);
    }

    [Fact]
    public void BoundedWriterDropWrite()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropWrite }
            );

        channel.Writer.TryWrite(wm with { B = 2 });
        channel.Writer.TryWrite(wm with { B = 3 });
        channel.Writer.TryWrite(wm with { B = 4 });
        channel.Writer.TryWrite(wm with { B = 5 });

        var res = channel.Reader.TryRead(out var i1);
        Assert.True(res);
        Assert.Equal("asd", i1!.A);
        Assert.Equal(2, i1.B);

        res = channel.Reader.TryRead(out var i2);
        Assert.True(res);
        Assert.Equal("asd", i2!.A);
        Assert.Equal(3, i2.B);
    }

    [Fact]
    public void BoundedWriterDropNewest()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropNewest }
            );

        channel.Writer.TryWrite(wm with { B = 2 });
        channel.Writer.TryWrite(wm with { B = 3 });
        channel.Writer.TryWrite(wm with { B = 4 });
        channel.Writer.TryWrite(wm with { B = 5 });

        var res = channel.Reader.TryRead(out var i1);
        Assert.True(res);
        Assert.Equal("asd", i1!.A);
        Assert.Equal(2, i1.B);

        res = channel.Reader.TryRead(out var i2);
        Assert.True(res);
        Assert.Equal("asd", i2!.A);
        Assert.Equal(5, i2.B);
    }

    [Fact]
    public async Task BoundedWriterWait()
    {
        var wm = new M { A = "asd", B = 1 };
        using var goAhead = new AsyncAutoResetEvent(false);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var writerTask = Task.Run(
            async () =>
            {
                try
                {
                    using var w = new RedisChannelWriter<M>(
                        ChannelName,
                        _cnn.GetDatabase(),
                        new JsonRedisValueSerializer<M>(),
                        new(2) { FullMode = BoundedChannelFullMode.Wait }
                        );

                    await w.WriteAsync(wm with { B = 2 }, testCts.Token);
                    await w.WriteAsync(wm with { B = 3 }, testCts.Token);

                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await Assert.ThrowsAsync<OperationCanceledException>(async () => await w.WriteAsync(wm with { B = 4 }, cts.Token));

                    goAhead.Set();

                    await w.WriteAsync(wm with { B = 5 }, testCts.Token);

                    w.TryComplete();
                }
                catch (Exception ex)
                {
                    Assert.Fail(ex.ToString());
                    throw;
                }
            },
            testCts.Token
            );

        using var r = new RedisChannelReader<M>(ChannelName, _cnn.GetDatabase(), new JsonRedisValueSerializer<M>());

        await goAhead.WaitAsync(testCts.Token);

        var i1 = await r.ReadAsync(testCts.Token);
        Assert.Equal(2, i1.B);

        var i2 = await r.ReadAsync(testCts.Token);
        Assert.Equal(3, i2.B);

        var i3 = await r.ReadAsync(testCts.Token);
        Assert.Equal(5, i3.B);

        await writerTask.WaitAsync(testCts.Token);
    }

    [Fact]
    public async Task CloseChannelWithError()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        using var w2 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var r1 = w1.TryWrite(wm);
        Assert.True(r1);

        var r2 = w2.TryWrite(wm);
        Assert.True(r2);

        var r3 = w1.TryComplete(new InvalidOperationException("Close!"));
        Assert.True(r3);

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm, TestContext.Current.CancellationToken));

        Assert.Equal("Close!", ex.Message);
    }

    [Fact]
    public async Task CloseChannelWithSuccess()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        using var w2 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        using var r = new RedisChannelReader<M>(ChannelName, _cnn.GetDatabase(), new JsonRedisValueSerializer<M>());

        var r1 = w1.TryWrite(wm);
        Assert.True(r1);

        var r2 = w2.TryWrite(wm with { B = 2 });
        Assert.True(r2);

        var r3 = w1.TryComplete();
        Assert.True(r3);

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm with { B = 3 }, TestContext.Current.CancellationToken));

        var r4 = r.ReadAllAsync(TestContext.Current.CancellationToken)
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .ToArray();

        Assert.Collection(
            r4,
            p => Assert.Equal(1, p.B),
            p => Assert.Equal(2, p.B)
            );
    }

    [Fact]
    public void WriteFailsIfClosed()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var r1 = w1.TryWrite(wm);
        Assert.True(r1);

        var r2 = w1.TryComplete();
        Assert.True(r2);

        using var w2 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var r3 = w2.TryWrite(wm);
        Assert.False(r3);
    }

    [Fact]
    public async Task WriteFailsIfClosedWithError()
    {
        var wm = new M { A = "asd", B = 1 };

        using var w1 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var r1 = w1.TryWrite(wm);
        Assert.True(r1);

        var r2 = w1.TryComplete(new InvalidOperationException("Close!"));
        Assert.True(r2);

        using var w2 = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        Assert.False(w2.TryWrite(wm));

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () => await w2.WriteAsync(wm, TestContext.Current.CancellationToken)
            );

        Assert.Equal("Close!", ex.Message);
    }

    [Fact]
    public async Task Cleanup()
    {
        var wm = new M { A = "asd", B = 1 };

        var db = _cnn.GetDatabase();

        using var writer = new RedisChannelWriter<M>(
            ChannelName,
            db,
            new JsonRedisValueSerializer<M>(),
            new(2) { ChannelSlidingExpiration = TimeSpan.FromSeconds(1) }
            );

        var r1 = writer.TryWrite(wm);
        Assert.True(r1);

        var r2 = writer.TryComplete();
        Assert.True(r2);

        Assert.True(db.KeyExists(ChannelName));
        Assert.True(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(db.KeyExists(ChannelName));
        Assert.False(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));
    }

    [Fact]
    public async Task Cleanup2()
    {
        var wm = new M { A = "asd", B = 1 };

        var db = _cnn.GetDatabase();

        using var writer = new RedisChannelWriter<M>(
            ChannelName,
            db,
            new JsonRedisValueSerializer<M>(),
            new(2) { ExpireAfterCompletion = TimeSpan.FromSeconds(3) }
            );

        var r1 = writer.TryWrite(wm);
        Assert.True(r1);

        var r2 = writer.TryComplete();
        Assert.True(r2);

        Assert.True(db.KeyExists(ChannelName));
        Assert.True(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(db.KeyExists(ChannelName));
        Assert.False(db.KeyExists(RedisChannelUtils.CompletionKey(ChannelName)));
    }

    [Fact]
    public void CanReadIfClosed()
    {
        var wm = new M { A = "asd", B = 1 };

        using var writer = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var r1 = writer.TryWrite(wm);
        Assert.True(r1);

        var r2 = writer.TryComplete();
        Assert.True(r2);

        using var reader = new RedisChannelReader<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>()
            );

        var r3 = reader.TryRead(out _);
        Assert.True(r3);

        var r4 = reader.TryRead(out _);
        Assert.False(r4);
    }

    [Fact(Timeout = 15_000)]

    //[Fact]
    public async Task ReaderHandleExpiration()
    {
        var wm = new M { A = "asd", B = 1 };

        using var writer = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest, ChannelSlidingExpiration = TimeSpan.FromSeconds(3) }
            );

        using var reader = new RedisChannelReader<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new() { ChannelSlidingExpiration = TimeSpan.FromSeconds(3) }
            );

        Assert.True(writer.TryWrite(wm));
        Assert.True(reader.TryPeek(out _));

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.False(reader.TryRead(out _));

        var r1 = await reader.WaitToReadAsync(TestContext.Current.CancellationToken);
        Assert.False(r1);

        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]

    //[Fact]
    public async Task WriterHandleExpiration()
    {
        var wm = new M { A = "asd", B = 1 };

        using var writer = new RedisChannelWriter<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.Wait, ChannelSlidingExpiration = TimeSpan.FromSeconds(5) }
            );

        Assert.True(writer.TryWrite(wm));
        Assert.True(writer.TryWrite(wm));
        Assert.False(writer.TryWrite(wm));

        var r1 = await writer.WaitToWriteAsync(TestContext.Current.CancellationToken);
        Assert.True(r1);
    }

    [Fact]
    public void ReadUtf8()
    {
        var wm = new M { A = "asd", B = 1 };

        using var channel = new RedisChannel<M>(
            ChannelName,
            _cnn.GetDatabase(),
            new Utf8JsonRedisValueSerializer<M>(),
            new(2) { FullMode = BoundedChannelFullMode.DropOldest }
            );

        var w = channel.Writer;
        var r = channel.Reader;

        var r1 = r.TryPeek(out _);
        Assert.False(r1);

        var r2 = w.TryWrite(wm);
        Assert.True(r2);

        Assert.True(w.TryComplete());

        var r3 = r.TryPeek(out var m1);
        Assert.True(r3);
        Assert.Equal(1, m1!.B);

        var r4 = r.TryRead(out var m2);
        Assert.True(r4);
        Assert.Equal(1, m2!.B);

        var r5 = r.TryRead(out _);
        Assert.False(r5);
    }
}