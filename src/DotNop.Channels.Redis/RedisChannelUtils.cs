using StackExchange.Redis;

namespace DotNop.Channels.Redis;

internal static class RedisChannelUtils
{
    public const string MessageRead = "r";

    public const string MessageWritten = "w";

    public const string CloseChannel = "x";

    public static readonly Exception DoneWritingException = new(nameof(DoneWritingException));

    public static RedisKey EofChannel(string channel) => new($"{channel}.eof");

    public static RedisChannel PubSubChannel(string channel) => new(channel, RedisChannel.PatternMode.Literal);
}