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

## Changelog

Every release adds a section to [`CHANGELOG.md`](CHANGELOG.md) ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
format) on the branch being tagged, committed as `docs: update changelog for v<version>` right before tagging. Write
it from the conventional commits since the previous tag (`feat` → Added, `fix` → Fixed, breaking changes called out
under Changed; skip `test`/`ci`/`build`/`chore` and internal-only `docs`).

The changelog commit raises the git height, so the version grows by one: commit it, then check
`dotnet nbgv get-version -v NuGetPackageVersion` and, if the heading is off, fix it with `git commit --amend` (amending
does not change the height). For stable and patch releases cherry-pick the commit onto `main` as well.

## Pre-release from `main`

`main` carries the prerelease suffix from `version.json` (e.g. `1.0-preview`).

```shell
git switch main
git pull
dotnet nbgv get-version -v NuGetPackageVersion   # current version, e.g. 1.0.11-preview
# add the 1.0.12-preview section to CHANGELOG.md, then:
git commit -am "docs: update changelog for v1.0.12-preview"
dotnet nbgv get-version -v NuGetPackageVersion   # check what will be published, e.g. 1.0.12-preview
dotnet nbgv tag                                  # creates tag v1.0.12-preview on HEAD
git push origin main v1.0.12-preview
```

## Stable release

1. Create a release branch. NBGV removes the prerelease suffix on the branch and bumps `main` to the next version:

   ```shell
   git switch main
   dotnet nbgv prepare-release
   # v1.0 branch -> "version": "1.0"
   # main        -> "version": "1.1-preview" (firstUnstableTag in version.json), then v1.0 is merged back into main
   ```

   `--nextVersion 2.0` changes the version `main` moves to. Release candidates (`prepare-release rc`) are not used;
   pre-releases are the `-preview` builds from `main`.

   On the release branch `dotnet nbgv get-version` shows a `-g<sha>` suffix because branch builds are not public
   releases; the tag build drops it.

   The changelog section for a stable release covers everything since the previous stable release, not only since the
   last preview.

2. Update the changelog on the release branch, tag it and push everything:

   ```shell
   git switch v1.0
   # add the 1.0.13 section to CHANGELOG.md, then:
   git commit -am "docs: update changelog for v1.0.13"
   git switch main && git cherry-pick v1.0 && git switch v1.0
   dotnet nbgv tag                                  # e.g. v1.0.13
   git push origin main v1.0 v1.0.13
   ```

3. After the first stable release of a major version, set the package validation baseline in
   `src/DotNop.Channels.Redis.csproj` so later builds fail on breaking API changes:

   ```xml
   <PackageValidationBaselineVersion>1.0.13</PackageValidationBaselineVersion>
   ```

   Validation downloads the baseline from nuget.org, so do this only once the version can be restored (indexing can
   take 10+ minutes); committing it earlier breaks restore on `main`.

## Patch release

Fix on (or cherry-pick to) the release branch, then tag it again:

```shell
git switch v1.0
git cherry-pick <commit>
# add the 1.0.14 section to CHANGELOG.md, then:
git commit -am "docs: update changelog for v1.0.14"
git switch main && git cherry-pick v1.0 && git switch v1.0
dotnet nbgv tag                                  # e.g. v1.0.14
git push origin main v1.0 v1.0.14
```

## After pushing a tag

- Watch the `CI` workflow run for the tag in GitHub Actions. If the `nuget` environment has reviewers, approve the
  `publish` job.
- nuget.org validates and indexes the package; it can take several minutes to appear in search and restore.
- The push uses `--skip-duplicate`, so re-running the workflow for an already published version is harmless.
- Once the `publish` job succeeded, create the GitHub release from the tag, with the changelog section and a link to
  the nuget.org package as notes (`--prerelease` for suffixed versions):

  ```shell
  gh release create v1.0.13 --verify-tag --title v1.0.13 --notes-file notes.md
  ```

## Mistakes

- A published version cannot be deleted from nuget.org, only unlisted (package page → Manage → Listing). Fix forward by
  releasing a new version.
- A tag pushed by mistake before the publish job ran can be removed with `git push origin :refs/tags/<tag>`. Cancel
  the workflow run first.
