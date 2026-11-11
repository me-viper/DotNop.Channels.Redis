using StackExchange.Redis;

namespace DotNop.Channels.Redis;

internal static class RedisChannelUtils
{
    public const string MessageRead = "r";

    public const string MessageWritten = "w";

    public const string CloseChannel = "x";

    public static Exception DoneWritingException = new(nameof(DoneWritingException));

    public static Task NeverEndingTask { get; } = new TaskCompletionSource().Task;

    public static RedisKey EofChannel(string channel) => new($"{channel}.eof");
}