using System.Diagnostics.Metrics;

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
}