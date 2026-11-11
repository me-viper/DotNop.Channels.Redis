namespace DotNop.Channels.Redis;

[PublicAPI]
public record RedisChannelReaderOptions
{
    public TimeSpan ChannelSlidingExpiration { get; init; } = TimeSpan.Zero;
}