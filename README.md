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
| `Capacity`                 | Maximum number of items; `-1` or omitted means unbounded.          |
| `FullMode`                 | Behavior of the writer when the channel is full.                   |
| `ExpireAfterCompletion`    | How long the Redis keys are kept after the writer completes.       |
| `ChannelSlidingExpiration` | Sliding expiration of the Redis keys, refreshed on read and write. |

`new RedisChannelOptions(capacity)` throws `ArgumentOutOfRangeException` for `0` or values below `-1`. A writer created
without options is unbounded.

### Full modes

For a bounded channel the capacity check and the push run as one Redis script, so several writers (in one or many
processes) never push past capacity.

| `FullMode`   | `TryWrite` on a full channel                             |
|--------------|----------------------------------------------------------|
| `Wait`       | Returns `false`; `WriteAsync` waits until there is room. |
| `DropWrite`  | Drops the new item and returns `true`.                   |
| `DropNewest` | Removes the newest stored item, then writes.             |
| `DropOldest` | Writes, then removes the oldest stored item.             |

### Behavior

The reader and writer follow the `System.Threading.Channels` contract:

- `ReadAllAsync` removes items as it yields them and keeps going until the channel is completed and empty.
- `TryRead`, `TryPeek` and `TryWrite` never throw on a closed channel; they return `false`.
- `WaitToReadAsync` returns `false` once the channel is completed and empty; `WaitToWriteAsync` returns `false` once it
  is completed. If the channel was completed with an error, both throw a `ChannelClosedException` carrying that error
  message, and so do `ReadAsync` and `WriteAsync`.
- `Reader.Completion` finishes when the channel is completed and drained (or its keys expired through
  `ChannelSlidingExpiration`), and is faulted when the channel was completed with an error.
- Completion is shared through Redis: completing any writer closes the channel for all readers and writers of that
  channel, including ones created later.
- `null` items are allowed if the serializer supports them.
- Many readers and writers may share one `IConnectionMultiplexer`; disposing one removes only its own subscription.

### Serializers

- `Utf8JsonRedisValueSerializer<T>` — `System.Text.Json`, UTF-8 bytes.
- `JsonRedisValueSerializer<T>` — `System.Text.Json`, string.
- Implement `IRedisValueSerializer<T>` for a custom format.

### Metrics

The `DotNop.Channels.Redis` meter publishes:

| Instrument                           | Type      | Description                                  |
|--------------------------------------|-----------|----------------------------------------------|
| `dotnop.channels.redis.write.count`  | Counter   | Messages written.                            |
| `dotnop.channels.redis.read.count`   | Counter   | Messages read.                               |
| `dotnop.channels.redis.write.waited` | Histogram | Seconds a writer waited for room.            |
| `dotnop.channels.redis.read.waited`  | Histogram | Seconds a reader waited for a message.       |
| `dotnop.channels.redis.queue.count`  | Gauge     | Messages in each channel with a live reader. |

All instruments carry a `channel` tag. The gauge reports each channel once, however many readers it has, and stops
reporting a reader once it is disposed.

## Building

```shell
dotnet test    # requires Redis on localhost:6379
dotnet pack -c Release   # package goes to artifacts/package/release
```

Versions come from Nerdbank.GitVersioning (`version.json`). Stable versions are built from `release/v{version}`
branches.

## License

[MIT](LICENSE)
