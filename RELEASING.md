# Releasing

Packages are published to [nuget.org](https://www.nuget.org/packages/DotNop.Channels.Redis) by the `publish` job in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml). It runs only when a `v*` tag is pushed and only after the build
and tests pass. Authentication uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(GitHub OIDC), so there is no API key to store or rotate.

Versions are computed by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) (NBGV) from
[`version.json`](version.json) and git history: `major.minor` comes from `version.json`, the patch number is the git
height (number of commits since `version.json` last changed). Never edit versions in project files.

The `nbgv` CLI is a local tool: run `dotnet tool restore` once, then use `dotnet nbgv ...`.

## One-time setup

- **nuget.org**: Account → Trusted Publishing → add a GitHub Actions policy with owner `me-viper`, repository
  `DotNop.Channels.Redis`, workflow file `ci.yml`, environment `nuget`.
- **GitHub** (optional): Settings → Environments → `nuget` → add required reviewers to approve every publish manually.
  The environment is created automatically on the first run if it does not exist.

## Pre-release from `main`

`main` carries the prerelease suffix from `version.json` (e.g. `1.0-preview`).

```shell
git switch main
git pull
dotnet nbgv get-version -v NuGetPackageVersion   # check what will be published, e.g. 1.0.12-preview
dotnet nbgv tag                                  # creates tag v1.0.12-preview on HEAD
git push origin v1.0.12-preview
```

## Stable release

1. Create a release branch. NBGV removes the prerelease suffix on the branch and bumps `main` to the next version:

   ```shell
   git switch main
   dotnet nbgv prepare-release
   # v1.0 branch -> "version": "1.0"
   # main        -> "version": "1.1-alpha"
   ```

   Options:
   - `dotnet nbgv prepare-release rc` makes the release branch `1.0-rc` for a release candidate first.
   - `--nextVersion 2.0` changes the version `main` moves to.
   - To keep `-preview` instead of `-alpha` on `main`, set `"release": { "firstUnstableTag": "preview" }` in
     `version.json`.

2. Tag the release branch and push everything:

   ```shell
   git switch v1.0
   dotnet nbgv tag                                  # e.g. v1.0.13
   git push origin main v1.0 v1.0.13
   ```

3. After the first stable release of a major version, set the package validation baseline in
   `src/DotNop.Channels.Redis/DotNop.Channels.Redis.csproj` so later builds fail on breaking API changes:

   ```xml
   <PackageValidationBaselineVersion>1.0.13</PackageValidationBaselineVersion>
   ```

## Patch release

Fix on (or cherry-pick to) the release branch, then tag it again:

```shell
git switch v1.0
git cherry-pick <commit>
dotnet nbgv tag                                  # e.g. v1.0.14
git push origin v1.0 v1.0.14
```

## After pushing a tag

- Watch the `CI` workflow run for the tag in GitHub Actions. If the `nuget` environment has reviewers, approve the
  `publish` job.
- nuget.org validates and indexes the package; it can take several minutes to appear in search and restore.
- The push uses `--skip-duplicate`, so re-running the workflow for an already published version is harmless.

## Mistakes

- A published version cannot be deleted from nuget.org, only unlisted (package page → Manage → Listing). Fix forward by
  releasing a new version.
- A tag pushed by mistake before the publish job ran can be removed with `git push origin :refs/tags/<tag>`. Cancel
  the workflow run first.
