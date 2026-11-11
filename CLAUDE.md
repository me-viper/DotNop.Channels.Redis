# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`DotNop.Channels.Redis` is a NuGet library: a `System.Threading.Channels` `Channel<T>` backed by a Redis list, so the
reader and writer can live in different processes. Targets `net10.0` and `net11.0`.

## Commands

```powershell
dotnet build DotNop.Channels.Redis.slnx
dotnet test --solution DotNop.Channels.Redis.slnx
dotnet test --project tests/DotNop.Channels.Redis.Tests -f net10.0 --filter-method "*ReadAllAsync_ConsumesItems"
dotnet pack src/DotNop.Channels.Redis/DotNop.Channels.Redis.csproj -c Release -o artifacts/packages
jb cleanupcode DotNop.Channels.Redis.slnx --include="<file1>;<file2>"   # lint; `lint` profile from .sln.DotSettings
dotnet tool restore; dotnet nbgv get-version                          # version computed from git
```

- Tests use Microsoft.Testing.Platform (`global.json`) with xUnit v3, so filters are `--filter-method` / `--filter-class`,
  not `--filter`.
- Tests need a real Redis at `127.0.0.1:6379` (override with `REDIS_CONNECTION`). Without it every test is **skipped**,
  not failed — a green run with all tests skipped proves nothing. CI runs a `redis:8-alpine` service.
- `WaitMode_ManyWritersNeverExceedCapacity` is `Explicit` (it starves the thread pool on small CI runners); run it with
  `--explicit on` after changing the writer's Lua scripts or capacity logic.
- `Release` builds use `TreatWarningsAsErrors`, and code style is enforced in build (`EnforceCodeStyleInBuild`).

## Architecture

There is no in-process buffer: every operation goes straight to Redis, and instances coordinate only through Redis.

- **Keys.** A channel `<name>` uses the list `<name>` (items, `RPUSH` to write, pop from the head to read) and the
  completion marker from `RedisChannelUtils.CompletionKey` — `{<name>}.eof`, or `<name>.eof` when the name already has
  a hash tag. Both keys must share a Redis Cluster slot because Lua scripts touch both; keep that invariant when adding
  keys.
- **Completion is shared state.** Completing is `SET <eof> <error message> NX`: the first `TryComplete` across all
  processes wins; an empty value means normal completion, a non-empty one becomes a `ChannelClosedException`
  (`RedisChannelUtils.ToCompletion`). Each reader/writer caches it in `_doneWriting` and re-syncs from Redis.
- **Writes are atomic Lua scripts** (`RedisChannelWriter`): one script per `BoundedChannelFullMode`, each first checks
  the completion key, so capacity checks and the no-writes-after-completion rule hold across many writers.
- **Wake-ups go through pub/sub** on a channel with the same name: writers publish `w` after a write, readers publish
  `r` after a read, completion publishes `x` (`RedisChannelUtils` constants). Waiters block on a DotNext
  `AsyncAutoResetEvent` set by the subscription handler, then re-check Redis — pub/sub is only a hint, Redis state is
  the truth. With `ChannelSlidingExpiration` set, waits also time out so expired keys are noticed.
- `RedisChannel<T>` just pairs a `RedisChannelReader<T>` and `RedisChannelWriter<T>`; both are usable standalone.
- **Telemetry** (`Telemetry/`): `Metrics` owns the `DotNop.Channels.Redis` meter. The queue-length gauge reads cached
  values from a `QueueProbe` per live reader (refreshed in the background, deduplicated per channel + database) and must
  never call Redis during collection. Instrument names follow OpenTelemetry conventions and are documented in
  `README.md` — keep them in sync.

## Conventions

- **Public API is tracked** by `Microsoft.CodeAnalysis.PublicApiAnalyzers`: any public surface change must be added to
  `src/DotNop.Channels.Redis/PublicAPI.Unshipped.txt` (RS0016/RS0017 warnings, errors in `Release`). Entries move to `PublicAPI.Shipped.txt`
  when released.
- `README.md` is packed into the NuGet package and is the user-facing spec of behavior (options, full modes, completion
  semantics, metrics). Update it together with behavior changes; keep maintainer-only content out of it.
- Package versions are central (`Directory.Packages.props`); shared MSBuild settings live in `Directory.Build.props`.
- Never set versions in project files — Nerdbank.GitVersioning computes them from `version.json` and git height.
  Releasing (tag push → nuget.org via Trusted Publishing) is described in `RELEASING.md` and automated by the
  `/publish-release` skill (`.claude/skills/publish-release`); keep the two in sync.
- Tests use a unique channel name per test (`test_{Guid}`) and delete both keys in setup/teardown; Redis-dependent
  tests connect through `TestRedis.ConnectAsync()` so they skip cleanly when Redis is absent.
