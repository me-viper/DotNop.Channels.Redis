using System.Diagnostics;
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

    private readonly ChannelMessageQueue _subscription;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile Exception? _doneWriting;

    public override bool CanCount => true;

    public override bool CanPeek => true;

    public override int Count => (int)_db.ListLength(_channel);

    public override Task Completion => _completion.Task;

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
        _subscription = _sub.Subscribe(RedisChannelUtils.PubSubChannel(_channel));
        _subscription.OnMessage(p =>
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

        Metrics.RegisterQueue(this, _channel, () => _db.ListLength(_channel));

        CheckIfClosed();
    }

    private void CheckIfClosed()
    {
        var doneWriting = RedisChannelUtils.ToCompletion(_db.StringGet(RedisChannelUtils.CompletionKey(_channel)));

        if (doneWriting == null)
            return;

        _doneWriting = doneWriting;

        if (_db.ListLength(_channel) == 0)
            CompleteReader();
    }

    /// <summary>Completes <see cref="Completion"/> once the channel is closed and drained.</summary>
    private void CompleteReader()
    {
        var doneWriting = _doneWriting;

        if (doneWriting == null)
            return;

        if (doneWriting == RedisChannelUtils.DoneWritingException)
            _completion.TrySetResult();
        else
            _completion.TrySetException(doneWriting);
    }

    private void Notify()
    {
        _sub.Publish(RedisChannelUtils.PubSubChannel(_channel), RedisChannelUtils.MessageRead);

        if (_opts.ChannelSlidingExpiration != TimeSpan.Zero)
        {
            _db.KeyExpire(_channel, _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
            _db.KeyExpire(RedisChannelUtils.CompletionKey(_channel), _opts.ChannelSlidingExpiration, CommandFlags.FireAndForget);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Metrics.UnregisterQueue(this);
        _subscription.Unsubscribe();
    }

    /// <inheritdoc />
    public override bool TryPeek([MaybeNullWhen(false)] out T item)
    {
        var msg = _db.ListGetByIndex(_channel, 0);

        if (msg.IsNull)
        {
            item = default;
            CompleteReader();
            return false;
        }

        item = _serializer.ReadRedisValue(msg)!;
        return true;
    }

    /// <inheritdoc />
    public override bool TryRead([MaybeNullWhen(false)] out T item)
    {
        var msg = _db.ListLeftPop(_channel);

        if (msg.IsNull)
        {
            item = default;
            CompleteReader();
            return false;
        }

        item = _serializer.ReadRedisValue(msg)!;

        Metrics.MessageRead(_channel);
        Notify();

        return true;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        var sw = new Stopwatch();

        while (await _db.ListLengthAsync(_channel).ConfigureAwait(false) == 0)
        {
            sw.Start();

            var doneWriting = _doneWriting;

            if (doneWriting != null)
            {
                CompleteReader();

                if (doneWriting == RedisChannelUtils.DoneWritingException)
                    return false;

                throw doneWriting;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var signaled = true;

            if (_opts.ChannelSlidingExpiration == TimeSpan.Zero)
                await _readerSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                signaled = await _readerSync.WaitAsync(_opts.ChannelSlidingExpiration, cancellationToken).ConfigureAwait(false);

            if (!signaled && !await _db.KeyExistsAsync(_channel).ConfigureAwait(false))
            {
                // Channel expired: nobody will ever write to it again.
                _completion.TrySetResult();
                return false;
            }
        }

        if (sw.IsRunning)
            Metrics.ReadWaited(_channel, sw.Elapsed);

        return true;
    }
}