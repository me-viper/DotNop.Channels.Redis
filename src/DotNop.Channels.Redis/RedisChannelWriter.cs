using System.Diagnostics;
using System.Threading.Channels;

using DotNext.Threading;

using DotNop.Channels.Redis.Telemetry;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

[PublicAPI]
public sealed class RedisChannelWriter<T> : ChannelWriter<T>, IDisposable
{
    /// <summary>Returned by the push scripts when the channel is already completed.</summary>
    private const long PushClosed = -1;

    /// <summary>Pushes the item unless the channel is completed.</summary>
    private static readonly LuaScript PushUnbounded = LuaScript.Prepare(
        """
        if redis.call('EXISTS', @done) == 1 then return -1 end
        return redis.call('RPUSH', @key, @v)
        """
        );

    /// <summary>Pushes the item only if the list has room; returns 0 when full.</summary>
    private static readonly LuaScript PushIfNotFull = LuaScript.Prepare(
        """
        if redis.call('EXISTS', @done) == 1 then return -1 end
        if redis.call('LLEN', @key) >= tonumber(@cap) then return 0 end
        return redis.call('RPUSH', @key, @v)
        """
        );

    /// <summary>Removes the newest items to make room, then pushes.</summary>
    private static readonly LuaScript PushDropNewest = LuaScript.Prepare(
        """
        if redis.call('EXISTS', @done) == 1 then return -1 end
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
        if redis.call('EXISTS', @done) == 1 then return -1 end
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
                    SyncCompletion();
                    _writerSync.Set();
                }
            }
            );

        _doneWriting = RedisChannelUtils.ToCompletion(_db.StringGet(RedisChannelUtils.CompletionKey(_channel)));
    }

    private async Task<bool> FullCapacityAsync()
    {
        if (!_opts.HasCapacity)
            return false;

        return await _db.ListLengthAsync(_channel).ConfigureAwait(false) >= _opts.Capacity;
    }

    /// <summary>Runs a push script; returns the list length, 0 when full or <see cref="PushClosed" />.</summary>
    private long Push(LuaScript script, RedisValue msg)
    {
        var result = _db.ScriptEvaluate(
            script,
            new
            {
                key = (RedisKey)_channel,
                done = RedisChannelUtils.CompletionKey(_channel),
                v = msg,
                cap = _opts.Capacity,
            }
            );

        return result.IsNull ? 0 : (long)result;
    }

    /// <summary>Adopts the completion state stored in Redis by another writer, if any.</summary>
    private void SyncCompletion()
    {
        if (_doneWriting != null)
            return;

        var doneWriting = RedisChannelUtils.ToCompletion(_db.StringGet(RedisChannelUtils.CompletionKey(_channel)));

        if (doneWriting != null)
            _doneWriting = doneWriting;
    }

    private void Notify()
    {
        _sub.Publish(RedisChannelUtils.PubSubChannel(_channel), RedisChannelUtils.MessageWritten);

        if (_opts.ChannelSlidingExpiration != TimeSpan.Zero)
        {
            _db.KeyExpire(_channel, _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
            _db.KeyExpire(RedisChannelUtils.CompletionKey(_channel), _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
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

        var script = !_opts.HasCapacity
            ? PushUnbounded
            : _opts.FullMode switch
            {
                BoundedChannelFullMode.Wait or BoundedChannelFullMode.DropWrite => PushIfNotFull,
                BoundedChannelFullMode.DropNewest => PushDropNewest,
                BoundedChannelFullMode.DropOldest => PushDropOldest,
                _ => throw new InvalidOperationException($"Unsupported full mode: {_opts.FullMode}"),
            };

        var pushed = Push(script, msg);

        if (pushed == PushClosed)
        {
            // Completed by another writer whose close notification has not arrived yet.
            SyncCompletion();
            return false;
        }

        // Channel is full: Wait reports failure, DropWrite drops the item but reports success.
        if (pushed == 0)
            return _opts.FullMode == BoundedChannelFullMode.DropWrite;

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

    /// <inheritdoc />
    public override bool TryComplete(Exception? error = null)
    {
        if (_doneWriting != null)
            return false;

        var errMsg = error?.Message ?? string.Empty;

        var expiration = _opts.ExpireAfterCompletion != TimeSpan.Zero
            ? _opts.ExpireAfterCompletion
            : _opts.ChannelSlidingExpiration;

        // Only the first completer across all instances wins.
        if (!_db.StringSet(
            RedisChannelUtils.CompletionKey(_channel),
            errMsg,
            expiration == TimeSpan.Zero ? null : expiration,
            When.NotExists
            ))
        {
            SyncCompletion();
            return false;
        }

        _doneWriting = RedisChannelUtils.ToCompletion(errMsg);

        _sub.Publish(RedisChannelUtils.PubSubChannel(_channel), RedisChannelUtils.CloseChannel);

        if (expiration != TimeSpan.Zero)
            _db.KeyExpire(_channel, expiration, CommandFlags.FireAndForget);

        return true;
    }
}