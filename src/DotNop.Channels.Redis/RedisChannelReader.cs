using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using DotNext.Threading;

using DotNop.Channels.Redis.Telemetry;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

[PublicAPI]
public sealed class RedisChannelReader<T> : ChannelReader<T>, IDisposable
{
    private readonly string _channel;

    private readonly IDatabase _db;

    private readonly IRedisValueSerializer<T> _serializer;

    private readonly AsyncAutoResetEvent _readerSync = new(false);

    private readonly RedisChannelReaderOptions _opts;

    private readonly ISubscriber _sub;

    private Exception? _doneWriting;

    public override bool CanCount => true;

    public override bool CanPeek => true;

    public override int Count => (int)_db.ListLength(_channel);

    public RedisChannelReader(string channel, IDatabase db, IRedisValueSerializer<T> serializer) : this(channel, db, serializer, null)
    {
    }

    public RedisChannelReader(string channel, IDatabase db, IRedisValueSerializer<T> serializer, RedisChannelReaderOptions? opts)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(serializer);

        _channel = channel;
        _db = db;
        _serializer = serializer;
        _opts = opts ?? new();

        _sub = _db.Multiplexer.GetSubscriber();
        _sub.Subscribe(new RedisChannel(_channel, RedisChannel.PatternMode.Literal))
            .OnMessage(p =>
                {
                    if (string.Equals(p.Message, RedisChannelUtils.MessageWritten, StringComparison.Ordinal))
                        _readerSync.Set();

                    if (string.Equals(p.Message, RedisChannelUtils.CloseChannel, StringComparison.Ordinal))
                    {
                        CheckIfClosed();
                        _readerSync.Set();
                    }
                }
                );

        Metrics.Meter.CreateObservableGauge(
            "dotnop.channels.redis.queue.count",
            () => new Measurement<long>(Count, new KeyValuePair<string, object?>("channel", _channel)),
            unit: "{message}",
            description: "Number of messages in channel"
            );

        CheckIfClosed();
    }

    private void CheckIfClosed()
    {
        var eof = _db.StringGet(RedisChannelUtils.EofChannel(_channel));

        if (!eof.IsNull)
            _doneWriting = eof.IsNullOrEmpty ? RedisChannelUtils.DoneWritingException : new ChannelClosedException(eof);
    }

    private bool IsCompleted()
    {
        if (_doneWriting == null)
            return false;

        if (_doneWriting == RedisChannelUtils.DoneWritingException)
            return true;

        throw _doneWriting;
    }

    private void Notify()
    {
        _sub.Publish(new RedisChannel(_channel, RedisChannel.PatternMode.Literal), RedisChannelUtils.MessageRead);

        if (_opts.ChannelSlidingExpiration != TimeSpan.Zero)
        {
            _db.KeyExpire(_channel, _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
            _db.KeyExpire(RedisChannelUtils.EofChannel(_channel), _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sub.Unsubscribe(new RedisChannel(_channel, RedisChannel.PatternMode.Literal));
    }

    /// <inheritdoc />
    public override bool TryPeek([MaybeNullWhen(false)] out T item)
    {
        var msg = _db.ListGetByIndex(_channel, 0);

        if (msg.IsNull)
        {
            item = default;
            IsCompleted();
            return false;
        }

        item = _serializer.ReadRedisValue(msg);
        return item != null;
    }


    /// <inheritdoc />
    public override bool TryRead([MaybeNullWhen(false)] out T item)
    {
        var msg = _db.ListLeftPop(_channel);

        if (msg.IsNull)
        {
            item = default;
            IsCompleted();
            return false;
        }

        item = _serializer.ReadRedisValue(msg);
        var result = item != null;

        Metrics.MessageRead(_channel);

        if (result)
            Notify();

        return result;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            yield break;

        foreach (var msg in await _db.ListRangeAsync(_channel).ConfigureAwait(false))
        {
            Metrics.MessageRead(_channel);
            Notify();

            if (cancellationToken.IsCancellationRequested)
                yield break;

            var item = _serializer.ReadRedisValue(msg);

            if (item != null)
                yield return item;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        var sw = new Stopwatch();

        while (await _db.ListLengthAsync(_channel).ConfigureAwait(false) == 0)
        {
            sw.Start();

            if (IsCompleted())
                return false;

            if (cancellationToken.IsCancellationRequested)
                return await ValueTask.FromCanceled<bool>(cancellationToken).ConfigureAwait(false);

            var signaled = true;

            if (_opts.ChannelSlidingExpiration == TimeSpan.Zero)
                await _readerSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                signaled = await _readerSync.WaitAsync(_opts.ChannelSlidingExpiration, cancellationToken).ConfigureAwait(false);

            if (!signaled && !_db.KeyExists(_channel))
                return false;
        }

        if (sw.IsRunning)
            Metrics.ReadWaited(_channel, sw.Elapsed);

        return true;
    }
}