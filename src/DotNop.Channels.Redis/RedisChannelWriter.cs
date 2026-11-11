using System.Diagnostics;
using System.Threading.Channels;

using DotNext.Threading;

using DotNop.Channels.Redis.Telemetry;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

[PublicAPI]
public sealed class RedisChannelWriter<T> : ChannelWriter<T>, IDisposable
{
    private readonly string _channel;

    private readonly IDatabase _db;

    private readonly IRedisValueSerializer<T> _serializer;

    private readonly AsyncAutoResetEvent _writerSync = new(false);

    private readonly RedisChannelOptions _opts;

    private readonly ISubscriber _sub;

    private Exception? _doneWriting;

    public RedisChannelWriter(string channel, IDatabase db, IRedisValueSerializer<T> serializer) : this(channel, db, serializer, null)
    {
    }

    public RedisChannelWriter(string channel, IDatabase db, IRedisValueSerializer<T> serializer, RedisChannelOptions? opts)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(serializer);

        _channel = channel;
        _db = db;
        _serializer = serializer;
        _opts = opts ?? new RedisChannelOptions(int.MaxValue);

        if (_opts.Capacity <= 0)
            throw new InvalidOperationException("Bounded channel capacity should be greater than 1");

        _sub = _db.Multiplexer.GetSubscriber();

        _sub.Subscribe(new RedisChannel(_channel, RedisChannel.PatternMode.Literal))
            .OnMessage(p =>
                {
                    if (string.Equals(p.Message, RedisChannelUtils.MessageRead, StringComparison.Ordinal))
                        _writerSync.Set();

                    if (string.Equals(p.Message, RedisChannelUtils.CloseChannel, StringComparison.Ordinal))
                    {
                        TryComplete(false);
                        _writerSync.Set();
                    }
                }
                );

        var eof = _db.StringGet(RedisChannelUtils.EofChannel(_channel));

        if (!eof.IsNull)
            _doneWriting = eof.IsNullOrEmpty ? RedisChannelUtils.DoneWritingException : new ChannelClosedException(eof);
    }

    private bool CheckCompleted()
    {
        if (_doneWriting == null)
            return false;

        if (_doneWriting == RedisChannelUtils.DoneWritingException)
            return true;

        throw _doneWriting;
    }

    private bool FullCapacity()
    {
        if (!_opts.HasCapacity)
            return false;

        return _db.ListLength(_channel) >= _opts.Capacity;
    }

    private void Notify()
    {
        _sub.Publish(new RedisChannel(_channel, RedisChannel.PatternMode.Literal), RedisChannelUtils.MessageWritten);

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
    public override bool TryWrite(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (CheckCompleted())
            return false;

        if (FullCapacity())
        {
            switch (_opts.FullMode)
            {
                case BoundedChannelFullMode.DropWrite:
                    return true;
                case BoundedChannelFullMode.Wait:
                    return false;
            }
        }

        var dropNewest = $"""
                redis.call('LTRIM', @key, 0, {_opts.Capacity - 2})
                return redis.call('RPUSH', @key, @v)
            """;

        var dropOldest = $"""
                local llen = redis.call('RPUSH', @key, @v)
                if llen > {_opts.Capacity} then redis.call('LTRIM', @key, {-_opts.Capacity}, -1) end
                return llen
            """;

        long len;
        var msg = _serializer.WriteRedisValue(item);

        switch (_opts.FullMode)
        {
            case BoundedChannelFullMode.DropNewest:
                var r1 = _db.ScriptEvaluate(LuaScript.Prepare(dropNewest), new { key = (RedisKey)_channel, v = msg });
                len = r1.IsNull ? 0 : (long)r1;
                break;
            case BoundedChannelFullMode.DropOldest:
                var r2 = _db.ScriptEvaluate(LuaScript.Prepare(dropOldest), new { key = (RedisKey)_channel, v = msg });
                len = r2.IsNull ? 0 : (long)r2;
                break;
            default:
                len = _db.ListRightPush(_channel, msg);
                break;
        }

        Metrics.MessageWritten(_channel);
        Notify();

        return len > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        if (CheckCompleted())
            return false;

        var sw = new Stopwatch();

        if (_opts.FullMode == BoundedChannelFullMode.Wait)
        {
            while (FullCapacity())
            {
                sw.Start();

                if (cancellationToken.IsCancellationRequested)
                    return await ValueTask.FromCanceled<bool>(cancellationToken).ConfigureAwait(false);

                if (CheckCompleted())
                    return false;

                if (_opts.ChannelSlidingExpiration == TimeSpan.Zero)
                    await _writerSync.WaitAsync(cancellationToken).ConfigureAwait(false);
                else
                    await _writerSync.WaitAsync(_opts.ChannelSlidingExpiration, cancellationToken).ConfigureAwait(false);
            }
        }

        if (sw.IsRunning)
            Metrics.WriteWaited(_channel, sw.Elapsed);

        return true;
    }

    private bool TryComplete(bool initiator, string? error = null)
    {
        if (_doneWriting != null)
            return true;

        var errMsg = error ?? string.Empty;

        if (!initiator)
        {
            var err = _db.StringGet(RedisChannelUtils.EofChannel(_channel));

            if (!err.IsNull)
                errMsg = err;
        }
        else
        {
            if (!_db.StringSet(RedisChannelUtils.EofChannel(_channel), errMsg))
                return false;

            _sub.Publish(new RedisChannel(_channel, RedisChannel.PatternMode.Literal), RedisChannelUtils.CloseChannel);

            if (_opts.ExpireAfterCompletion != TimeSpan.Zero)
            {
                _db.KeyExpire(_channel, _opts.ExpireAfterCompletion, CommandFlags.FireAndForget);
                _db.KeyExpire(RedisChannelUtils.EofChannel(_channel), _opts.ExpireAfterCompletion, CommandFlags.FireAndForget);
            }
            else
            {
                if (_opts.ChannelSlidingExpiration != TimeSpan.Zero)
                {
                    _db.KeyExpire(_channel, _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
                    _db.KeyExpire(RedisChannelUtils.EofChannel(_channel), _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(errMsg))
            _doneWriting = RedisChannelUtils.DoneWritingException;
        else
        {
            var ex = new ChannelClosedException(errMsg);
            _doneWriting = ex;
        }

        return true;
    }

    /// <inheritdoc />
    public override bool TryComplete(Exception? error = null) => TryComplete(true, error?.Message);
}