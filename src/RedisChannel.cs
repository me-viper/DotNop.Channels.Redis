using System.Threading.Channels;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

[PublicAPI]
public sealed class RedisChannel<T> : Channel<T>, IDisposable
{
    private readonly string _channel;

    private readonly IDatabase _db;

    public RedisChannel(string channel, IDatabase db, IRedisValueSerializer<T> serializer, RedisChannelOptions? opts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(serializer);

        _channel = channel;
        _db = db;

        Reader = new RedisChannelReader<T>(channel, db, serializer, opts);
        Writer = new RedisChannelWriter<T>(channel, db, serializer, opts);
    }

    public void Dispose()
    {
        ((IDisposable)Reader).Dispose();
        ((IDisposable)Writer).Dispose();
    }

    public bool Exists() => _db.KeyExists(_channel);

    public void Reset()
    {
        _db.KeyDelete(_channel);
        _db.KeyDelete(RedisChannelUtils.CompletionKey(_channel));
    }
}