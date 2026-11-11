using StackExchange.Redis;

namespace DotNop.Channels.Redis;

public interface IRedisValueSerializer<T>
{
    T? ReadRedisValue(RedisValue value);

    RedisValue WriteRedisValue(T item);
}