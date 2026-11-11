using System.Threading.Channels;

namespace DotNop.Channels.Redis;

[PublicAPI]
public record RedisChannelOptions : RedisChannelReaderOptions
{
    /// <summary>Options of an unbounded channel.</summary>
    public RedisChannelOptions() : this(-1)
    {
    }

    /// <param name="capacity">Maximum number of items in the channel, or <c>-1</c> for an unbounded channel.</param>
    public RedisChannelOptions(int capacity)
    {
        if (capacity is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity should be positive or -1 (unbounded).");

        HasCapacity = capacity > 0;
        Capacity = capacity == -1 ? int.MaxValue : capacity;
    }

    internal bool HasCapacity { get; }

    public int Capacity { get; }

    public BoundedChannelFullMode FullMode { get; init; }

    public TimeSpan ExpireAfterCompletion { get; init; } = TimeSpan.Zero;
}