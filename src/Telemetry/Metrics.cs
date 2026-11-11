using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;

namespace DotNop.Channels.Redis.Telemetry;

internal static class Metrics
{
    public const string MeterName = "DotNop.Channels.Redis";

    public static readonly Meter Meter = new(MeterName, GetVersion());

    private static readonly Counter<long> Written = Meter.CreateCounter<long>(
        "dotnop.channels.redis.written.messages",
        unit: "{message}",
        description: "Number of messages written into channel"
        );

    private static readonly Histogram<double> WriteWaitDuration = Meter.CreateHistogram<double>(
        "dotnop.channels.redis.write.wait.duration",
        unit: "s",
        description: "Time a writer waited for room in channel"
        );

    private static readonly Counter<long> Read = Meter.CreateCounter<long>(
        "dotnop.channels.redis.read.messages",
        unit: "{message}",
        description: "Number of messages read from channel"
        );

    private static readonly Histogram<double> ReadWaitDuration = Meter.CreateHistogram<double>(
        "dotnop.channels.redis.read.wait.duration",
        unit: "s",
        description: "Time a reader waited for a message in channel"
        );

    /// <summary>Probes of live readers; several readers of one channel are reported once.</summary>
    private static readonly ConcurrentDictionary<QueueProbe, byte> Queues = new();

    [UsedImplicitly]
    private static readonly ObservableGauge<long> QueueLength = Meter.CreateObservableGauge(
        "dotnop.channels.redis.queue.length",
        ObserveQueues,
        unit: "{message}",
        description: "Number of messages in channel"
        );

    public static void MessageWritten(string channel) => Written.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void MessageRead(string channel) => Read.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void WriteWaited(string channel, TimeSpan ts) => WriteWaitDuration.Record(
        ts.TotalSeconds,
        new KeyValuePair<string, object?>("channel", channel)
        );

    public static void ReadWaited(string channel, TimeSpan ts) => ReadWaitDuration.Record(
        ts.TotalSeconds,
        new KeyValuePair<string, object?>("channel", channel)
        );

    public static void RegisterQueue(QueueProbe probe)
    {
        Queues[probe] = 0;
        probe.Refresh();
    }

    public static void UnregisterQueue(QueueProbe probe) => Queues.TryRemove(probe, out _);

    private static IEnumerable<Measurement<long>> ObserveQueues()
    {
        var reported = new HashSet<(int Database, string Channel)>();

        foreach (var probe in Queues.Keys)
        {
            var key = (probe.Database, probe.Channel);

            if (reported.Contains(key))
                continue;

            // Never touch Redis here: report the cached length and refresh it for the next collection.
            probe.Refresh();

            if (!probe.TryGetValue(out var value))
                continue;

            reported.Add(key);

            yield return new Measurement<long>(
                value,
                new KeyValuePair<string, object?>("channel", probe.Channel),
                new KeyValuePair<string, object?>("db.namespace", probe.Database.ToString(CultureInfo.InvariantCulture))
                );
        }
    }

    private static string? GetVersion()
    {
        var version = typeof(Metrics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version == null)
            return null;

        // Drop build metadata (e.g. "+<commit>") so the instrumentation scope version stays a plain SemVer.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}