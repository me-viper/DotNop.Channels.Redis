using System.Text;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Tests;

internal static class TestRedis
{
    /// <summary>Set once a connection attempt failed, so the remaining tests skip without retrying.</summary>
    private static volatile string? _unavailable;

    /// <summary>Connection string from <c>REDIS_CONNECTION</c>, or the local default.</summary>
    /// <remarks>
    /// <c>127.0.0.1</c> rather than <c>localhost</c>: on Windows the latter tries IPv6 first and costs ~2s per connection.
    /// </remarks>
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("REDIS_CONNECTION") is { Length: > 0 } p ? p : "127.0.0.1:6379";

    /// <summary>Connects to the test Redis, skipping the test when it is not reachable.</summary>
    public static async Task<IConnectionMultiplexer> ConnectAsync()
    {
        if (_unavailable is { } reason)
            Assert.Skip(reason);

        try
        {
            return await ConnectionMultiplexer.ConnectAsync(ConnectionString).ConfigureAwait(false);
        }
        catch (RedisConnectionException ex)
        {
            _unavailable = $"Redis is not reachable at '{ConnectionString}': {ex.Message}";
            Assert.Skip(_unavailable);
            throw;
        }
    }

    /// <summary>
    /// Redis Cluster hash slot of <paramref name="key" />, computed locally:
    /// <see cref="IConnectionMultiplexer.HashSlot" /> returns -1 on a non-cluster server.
    /// </summary>
    public static int KeySlot(string key)
    {
        var open = key.IndexOf('{', StringComparison.Ordinal);

        if (open >= 0)
        {
            var close = key.IndexOf('}', open + 1);

            if (close > open + 1)
                key = key[(open + 1)..close];
        }

        return Crc16(Encoding.UTF8.GetBytes(key)) % 16384;
    }

    /// <summary>CRC16-CCITT (XMODEM), as used by Redis Cluster.</summary>
    private static int Crc16(byte[] data)
    {
        var crc = 0;

        foreach (var b in data)
        {
            crc ^= b << 8;

            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }

        return crc;
    }

    /// <summary>Polls <paramref name="condition" /> until it holds or <paramref name="timeout" /> passes.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}