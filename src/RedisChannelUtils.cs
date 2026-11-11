using System.Threading.Channels;

using StackExchange.Redis;

namespace DotNop.Channels.Redis;

internal static class RedisChannelUtils
{
    public const string MessageRead = "r";

    public const string MessageWritten = "w";

    public const string CloseChannel = "x";

    public static readonly Exception DoneWritingException = new(nameof(DoneWritingException));

    /// <summary>
    /// Key that marks the channel as completed; its value is the completion error message (empty on success).
    /// </summary>
    /// <remarks>
    /// The key must be in the same Redis Cluster slot as the list key <c>channel</c>, so Lua scripts may touch both:
    /// a channel name that already has a hash tag keeps it (<c>orders{eu}.eof</c>), any other name becomes the tag
    /// (<c>{orders}.eof</c>).
    /// </remarks>
    public static RedisKey CompletionKey(string channel) =>
        new(HasHashTag(channel) ? $"{channel}.eof" : $"{{{channel}}}.eof");

    /// <summary>Whether Redis Cluster hashes <paramref name="key" /> by a <c>{tag}</c> rather than by the whole key.</summary>
    private static bool HasHashTag(string key)
    {
        var open = key.IndexOf('{', StringComparison.Ordinal);

        if (open < 0)
            return false;

        var close = key.IndexOf('}', open + 1);
        return close > open + 1;
    }

    public static RedisChannel PubSubChannel(string channel) => new(channel, RedisChannel.PatternMode.Literal);

    /// <summary>Converts a completion key value to the local completion state; <see langword="null" /> when open.</summary>
    public static Exception? ToCompletion(RedisValue value)
    {
        if (value.IsNull)
            return null;

        return value.IsNullOrEmpty ? DoneWritingException : new ChannelClosedException(value);
    }
}