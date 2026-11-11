# DotNop.Channels.Redis

`System.Threading.Channels` implementation backed by Redis. `RedisChannel<T>` is a `Channel<T>` whose reader and writer
can live in different processes and share messages through a Redis key.

## Installation

```shell
dotnet add package DotNop.Channels.Redis
```

## Usage

```csharp
using System.Threading.Channels;

using DotNop.Channels.Redis;

using StackExchange.Redis;

await using var cnn = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var db = cnn.GetDatabase();

using var channel = new RedisChannel<Message>(
    "my-channel",
    db,
    new Utf8JsonRedisValueSerializer<Message>(),
    new RedisChannelOptions(capacity: 100) { FullMode = BoundedChannelFullMode.Wait }
    );

await channel.Writer.WriteAsync(new Message("hello"), cancellationToken);
channel.Writer.Complete();

await foreach (var msg in channel.Reader.ReadAllAsync(cancellationToken))
{
    Console.WriteLine(msg.Text);
}

record Message(string Text);
```

`RedisChannelReader<T>` and `RedisChannelWriter<T>` can also be used on their own when producer and consumer are in
different processes.

### Options

| Option                     | Description                                                        |
|----------------------------|--------------------------------------------------------------------|
| `Capacity`                 | Maximum number of items in the channel (unbounded when omitted).   |
| `FullMode`                 | Behavior of the writer when the channel is full.                   |
| `ExpireAfterCompletion`    | How long the Redis keys are kept after the writer completes.       |
| `ChannelSlidingExpiration` | Sliding expiration of the Redis keys, refreshed on read and write. |

### Serializers

- `Utf8JsonRedisValueSerializer<T>` — `System.Text.Json`, UTF-8 bytes.
- `JsonRedisValueSerializer<T>` — `System.Text.Json`, string.
- Implement `IRedisValueSerializer<T>` for a custom format.

### Metrics

The `DotNop.Channels.Redis` meter publishes `dotnop.channels.redis.{write,read}.count`,
`dotnop.channels.redis.{write,read}.waited` and `dotnop.channels.redis.queue.count`.

## Building

```shell
dotnet test    # requires Redis on localhost:6379
dotnet pack -c Release   # package goes to artifacts/package/release
```

Versions come from Nerdbank.GitVersioning (`version.json`). Stable versions are built from `release/v{version}`
branches.

## License

MIT
