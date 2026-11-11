using System.Text.Json;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

public class Utf8JsonRedisValueSerializer<T>(JsonSerializerOptions? options = null) : IRedisValueSerializer<T>
{
    public T? ReadRedisValue(RedisValue value) => JsonSerializer.Deserialize<T>(((ReadOnlyMemory<byte>)value).Span, options);

    public RedisValue WriteRedisValue(T item) => JsonSerializer.SerializeToUtf8Bytes(item, options);
}