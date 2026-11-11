using System.Diagnostics;
using System.Threading.Channels;

using DotNext.Threading;

using DotNop.Channels.Redis.Telemetry;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

[PublicAPI]
public sealed class RedisChannelWriter<T> : ChannelWriter<T>, IDisposable
{
    /// <summary>Pushes the item only if the list has room; returns 0 when full.</summary>
    private static readonly LuaScript PushIfNotFull = LuaScript.Prepare(
        """
        if redis.call('LLEN', @key) >= tonumber(@cap) then return 0 end
        return redis.call('RPUSH', @key, @v)
        """
        );

    /// <summary>Removes the newest items to make room, then pushes.</summary>
    private static readonly LuaScript PushDropNewest = LuaScript.Prepare(
        """
        local cap = tonumber(@cap)
        if redis.call('LLEN', @key) >= cap then
            if cap == 1 then redis.call('DEL', @key) else redis.call('LTRIM', @key, 0, cap - 2) end
        end
        return redis.call('RPUSH', @key, @v)
        """
        );

    /// <summary>Pushes, then removes the oldest items beyond capacity.</summary>
    private static readonly LuaScript PushDropOldest = LuaScript.Prepare(
        """
        local cap = tonumber(@cap)
        local llen = redis.call('RPUSH', @key, @v)
        if llen > cap then redis.call('LTRIM', @key, -cap, -1) end
        return llen
        """
        );

    private readonly string _channel;

    private readonly IDatabase _db;

    private readonly IRedisValueSerializer<T> _serializer;

    private readonly AsyncAutoResetEvent _writerSync = new(false);

    private readonly RedisChannelOptions _opts;

    private readonly ISubscriber _sub;

    private readonly ChannelMessageQueue _subscription;

    private volatile Exception? _doneWriting;

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
        _opts = opts ?? new RedisChannelOptions();

        _sub = _db.Multiplexer.GetSubscriber();
        _subscription = _sub.Subscribe(RedisChannelUtils.PubSubChannel(_channel));
        _subscription.OnMessage(p =>
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

    private async Task<bool> FullCapacityAsync()
    {
        if (!_opts.HasCapacity)
            return false;

        return await _db.ListLengthAsync(_channel).ConfigureAwait(false) >= _opts.Capacity;
    }

    private long Push(LuaScript script, RedisValue msg)
    {
        var result = _db.ScriptEvaluate(script, new { key = (RedisKey)_channel, v = msg, cap = _opts.Capacity });
        return result.IsNull ? 0 : (long)result;
    }

    private void Notify()
    {
        _sub.Publish(RedisChannelUtils.PubSubChannel(_channel), RedisChannelUtils.MessageWritten);

        if (_opts.ChannelSlidingExpiration != TimeSpan.Zero)
        {
            _db.KeyExpire(_channel, _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
            _db.KeyExpire(RedisChannelUtils.EofChannel(_channel), _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _subscription.Unsubscribe();

    /// <inheritdoc />
    public override bool TryWrite(T item)
    {
        if (_doneWriting != null)
            return false;

        var msg = _serializer.WriteRedisValue(item);

        if (!_opts.HasCapacity)
            _db.ListRightPush(_channel, msg);
        else
        {
            switch (_opts.FullMode)
            {
                case BoundedChannelFullMode.Wait:
                    if (Push(PushIfNotFull, msg) == 0)
                        return false;

                    break;
                case BoundedChannelFullMode.DropWrite:
                    // Channel is full: the item is dropped, but the write is reported as successful.
                    if (Push(PushIfNotFull, msg) == 0)
                        return true;

                    break;
                case BoundedChannelFullMode.DropNewest:
                    Push(PushDropNewest, msg);
                    break;
                case BoundedChannelFullMode.DropOldest:
                    Push(PushDropOldest, msg);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported full mode: {_opts.FullMode}");
            }
        }

        Metrics.MessageWritten(_channel);
        Notify();

        return true;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        var sw = new Stopwatch();

        while (true)
        {
            var doneWriting = _doneWriting;

            if (doneWriting != null)
            {
                if (doneWriting == RedisChannelUtils.DoneWritingException)
                    return false;

                throw doneWriting;
            }

            if (_opts.FullMode != BoundedChannelFullMode.Wait || !await FullCapacityAsync().ConfigureAwait(false))
                break;

            sw.Start();
            cancellationToken.ThrowIfCancellationRequested();

            if (_opts.ChannelSlidingExpiration == TimeSpan.Zero)
                await _writerSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await _writerSync.WaitAsync(_opts.ChannelSlidingExpiration, cancellationToken).ConfigureAwait(false);
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

            _sub.Publish(RedisChannelUtils.PubSubChannel(_channel), RedisChannelUtils.CloseChannel);

            var expiration = _opts.ExpireAfterCompletion != TimeSpan.Zero
                ? _opts.ExpireAfterCompletion
                : _opts.ChannelSlidingExpiration;

            if (expiration != TimeSpan.Zero)
            {
                _db.KeyExpire(_channel, expiration, CommandFlags.FireAndForget);
                _db.KeyExpire(RedisChannelUtils.EofChannel(_channel), expiration, CommandFlags.FireAndForget);
            }
        }

        _doneWriting = string.IsNullOrWhiteSpace(errMsg)
            ? RedisChannelUtils.DoneWritingException
            : new ChannelClosedException(errMsg);

        return true;
    }

    /// <inheritdoc />
    public override bool TryComplete(Exception? error = null) => TryComplete(true, error?.Message);
}