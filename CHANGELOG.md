# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [SemVer](https://semver.org/) as computed
by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning).

## [1.0.26] - 2026-11-11

### Added

- `Channel<T>` implementation backed by a Redis list, so readers and writers can run in different processes.
- Bounded and unbounded channels with all `BoundedChannelFullMode` modes, enforced atomically across writers.
- Channel completion shared through Redis: the first `TryComplete` wins, and an error completion surfaces as
  `ChannelClosedException` to every reader and writer.
- Optional sliding expiration of channel keys.
- OpenTelemetry metrics on the `DotNop.Channels.Redis` meter, including a queue-length gauge.

[1.0.26]: https://github.com/me-viper/DotNop.Channels.Redis/releases/tag/v1.0.26
