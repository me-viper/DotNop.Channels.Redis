using System.Threading.Channels;

namespace DotNop.Channels.Redis;

[PublicAPI]
public record RedisChannelOptions : RedisChannelReaderOptions
{
    public RedisChannelOptions() : this(-1)
    {
    }

    public RedisChannelOptions(int capacity)
    {
        HasCapacity = capacity > 0;
        Capacity = capacity == -1 ? int.MaxValue : capacity;
    }

    internal bool HasCapacity { get; }

    public int Capacity { get; }

    public BoundedChannelFullMode FullMode { get; init; }

    public TimeSpan ExpireAfterCompletion { get; init; } = TimeSpan.Zero;
}