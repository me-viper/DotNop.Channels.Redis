using System.Diagnostics.Metrics;

using DotNop.Channels.Redis.Telemetry;

using StackExchange.Redis;

namespace DotNop.Channels.Redis.Tests;

public sealed class MetricsTests : IAsyncLifetime
{
    private IConnectionMultiplexer _cnn = null!;

    private string ChannelName { get; } = $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => _cnn = await TestRedis.ConnectAsync();

    public async ValueTask DisposeAsync()
    {
        if (_cnn == null!)
            return;

        await _cnn.GetDatabase().KeyDeleteAsync(ChannelName);
        await _cnn.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task QueueLength_ReportsCachedLengthAndStopsAfterDispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _cnn.GetDatabase();
        await db.ListRightPushAsync(ChannelName, ["a", "b", "c"]);

        long? observed = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Metrics.MeterName && instrument.Name == "dotnop.channels.redis.queue.length")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "channel", Value: string channel } && channel == ChannelName)
                        observed = value;
                }
            }
            );
        listener.Start();

        var reader = new RedisChannelReader<string>(ChannelName, db, new JsonRedisValueSerializer<string>());

        try
        {
            var reported = await TestRedis.WaitUntilAsync(
                () =>
                {
                    listener.RecordObservableInstruments();
                    return observed == 3;
                },
                TimeSpan.FromSeconds(5),
                ct
                );

            Assert.True(reported, $"Expected queue length 3, observed {observed?.ToString() ?? "nothing"}");
        }
        finally
        {
            reader.Dispose();
        }

        observed = null;
        listener.RecordObservableInstruments();
        Assert.Null(observed);
    }
}