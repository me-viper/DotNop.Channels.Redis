# DotNop.Channels.Redis

[![CI](https://github.com/me-viper/DotNop.Channels.Redis/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/me-viper/DotNop.Channels.Redis/actions/workflows/ci.yml)

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
  channel, including ones created later. Writes are checked against completion atomically, so no writer can add items
  after the channel is completed, and only the first `TryComplete` across all writers returns `true`. The error is
  stored as its message, so completing with an exception whose message is empty counts as a normal completion.
- The channel uses two keys: the list `<name>` and the completion marker `{<name>}.eof`, kept in one Redis Cluster slot
  by the hash tag. A name that already has a hash tag keeps its own (`orders{eu}` → `orders{eu}.eof`). On a cluster,
  avoid names that contain `}` but no hash tag (such as `a{}b` or `a}b`): there the two keys land in different slots.
- `null` items are allowed if the serializer supports them.
- Many readers and writers may share one `IConnectionMultiplexer`; disposing one removes only its own subscription.

### Serializers

- `Utf8JsonRedisValueSerializer<T>` — `System.Text.Json`, UTF-8 bytes.
- `JsonRedisValueSerializer<T>` — `System.Text.Json`, string.
- Implement `IRedisValueSerializer<T>` for a custom format.

### Metrics

The `DotNop.Channels.Redis` meter publishes:

| Instrument                                  | Type      | Unit        | Description                                  |
|---------------------------------------------|-----------|-------------|----------------------------------------------|
| `dotnop.channels.redis.written.messages`    | Counter   | `{message}` | Messages written.                            |
| `dotnop.channels.redis.read.messages`       | Counter   | `{message}` | Messages read.                               |
| `dotnop.channels.redis.write.wait.duration` | Histogram | `s`         | Time a writer waited for room.               |
| `dotnop.channels.redis.read.wait.duration`  | Histogram | `s`         | Time a reader waited for a message.          |
| `dotnop.channels.redis.queue.length`        | Gauge     | `{message}` | Messages in each channel with a live reader. |

All instruments carry a `channel` tag; the gauge also carries `db.namespace` (the Redis database index). The gauge
reports each channel and database once, however many readers it has, and stops reporting a reader once it is disposed.
It never queries Redis during collection: it reports the last known length and refreshes it in the background, so a
value can lag by one collection interval, and a channel is omitted while Redis is unreachable.

## Building

```shell
dotnet test    # requires Redis on 127.0.0.1:6379 (or set REDIS_CONNECTION); tests are skipped without it
dotnet pack -c Release   # package goes to artifacts/package/release
```

Versions come from Nerdbank.GitVersioning (`version.json`). Stable versions are built from `release/v{version}`
branches.

## License

[MIT](LICENSE)
