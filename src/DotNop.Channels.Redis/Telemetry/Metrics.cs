using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Telemetry;

internal static class Metrics
{
    public static readonly Meter Meter = new("DotNop.Channels.Redis", "1.0");

    private static readonly Counter<long> Sent = Meter.CreateCounter<long>(
        "dotnop.channels.redis.write.count",
        unit: "{message}",
        description: "Number of messages written into channel"
        );

    private static readonly Histogram<double> WriteWaitedMeter = Meter.CreateHistogram<double>(
        "dotnop.channels.redis.write.waited",
        unit: "{seconds}",
        description: "Number of seconds waited to write into channel"
        );

    private static readonly Counter<long> Read = Meter.CreateCounter<long>(
        "dotnop.channels.redis.read.count",
        unit: "{message}"
        );

    private static readonly Histogram<double> ReadWaitedMeter = Meter.CreateHistogram<double>(
        "dotnop.channels.redis.read.waited",
        unit: "{seconds}",
        description: "Number of seconds waited to read from channel"
        );

    /// <summary>Live readers, keyed by instance; several readers of one channel are reported once.</summary>
    private static readonly ConcurrentDictionary<object, (string Channel, Func<long> Count)> Queues = new();

    [UsedImplicitly]
    private static readonly ObservableGauge<long> QueueCount = Meter.CreateObservableGauge(
        "dotnop.channels.redis.queue.count",
        ObserveQueues,
        unit: "{message}",
        description: "Number of messages in channel"
        );

    public static void MessageWritten(string channel) => Sent.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void MessageRead(string channel) => Read.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void WriteWaited(string channel, TimeSpan ts) => WriteWaitedMeter.Record(
        ts.TotalSeconds,
        new KeyValuePair<string, object?>("channel", channel)
        );

    public static void ReadWaited(string channel, TimeSpan ts) => ReadWaitedMeter.Record(
        ts.TotalSeconds,
        new KeyValuePair<string, object?>("channel", channel)
        );

    public static void RegisterQueue(object owner, string channel, Func<long> count) => Queues[owner] = (channel, count);

    public static void UnregisterQueue(object owner) => Queues.TryRemove(owner, out _);

    private static IEnumerable<Measurement<long>> ObserveQueues()
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (channel, count) in Queues.Values)
        {
            if (!channels.Add(channel))
                continue;

            long value;

            try
            {
                value = count();
            }
            catch (Exception p) when (p is RedisException or TimeoutException)
            {
                // Redis unavailable: skip this channel rather than failing the whole collection.
                continue;
            }

            yield return new Measurement<long>(value, new KeyValuePair<string, object?>("channel", channel));
        }
    }
}