using System.Text.Json;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

public sealed class JsonRedisValueSerializer<T>(JsonSerializerOptions? options = null) : IRedisValueSerializer<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    public T? ReadRedisValue(RedisValue value)
    {
        if (value.IsNull || !value.HasValue)
            return default;

        return JsonSerializer.Deserialize<T>(value.ToString(), _options);
    }

    public RedisValue WriteRedisValue(T item)
    {
        var value = JsonSerializer.Serialize(item, _options);
        return (RedisValue)value;
    }
}