namespace DotNop.Channels.Redis.Telemetry;

/// <summary>
/// Caches a channel's length for the queue gauge so metric collection never blocks on Redis.
/// Each observation reports the cached value and starts a background refresh for the next one.
/// </summary>
internal sealed class QueueProbe : IDisposable
{
    private const long Unknown = -1;

    private readonly Func<Task<long>> _fetch;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Captured up front: <see cref="CancellationTokenSource.Token"/> throws once disposed.</summary>
    private readonly CancellationToken _token;

    private long _value = Unknown;

    private int _refreshing;

    public string Channel { get; }

    public int Database { get; }

    public QueueProbe(string channel, int database, Func<Task<long>> fetch)
    {
        Channel = channel;
        Database = database;
        _fetch = fetch;
        _token = _cts.Token;
    }

    public bool TryGetValue(out long value)
    {
        value = Volatile.Read(ref _value);
        return value != Unknown;
    }

    /// <summary>Records a length the owner observed anyway, sparing a round trip.</summary>
    public void Set(long value) => Volatile.Write(ref _value, value);

    /// <summary>Starts a background refresh unless one is already running.</summary>
    public void Refresh()
    {
        if (_token.IsCancellationRequested || Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;

        _ = RefreshAsync(_token);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var value = await _fetch().WaitAsync(cancellationToken).ConfigureAwait(false);
            Set(value);
        }
#pragma warning disable CA1031 // Fire-and-forget: any failure just means the length is unknown.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Redis unavailable, connection disposed or probe disposed: stop reporting a stale value.
            Set(Unknown);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}