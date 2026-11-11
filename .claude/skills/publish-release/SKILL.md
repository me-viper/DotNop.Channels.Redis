---
name: publish-release
description: Release DotNop.Channels.Redis to nuget.org — a pre-release from main, a stable release (cuts a vX.Y branch), or a patch release on an existing release branch — by updating CHANGELOG.md, creating and pushing an NBGV version tag that triggers the CI publish job, and creating the GitHub release. Use when the user asks to release, publish, ship or tag a version of the package.
argument-hint: "[preview | stable | patch vX.Y]"
---

# Publish a release

Releases are published by the `publish` job in `.github/workflows/ci.yml`, triggered by pushing a `v*` tag. Versions
come from Nerdbank.GitVersioning (`version.json` + git height) — never edit version numbers by hand. `RELEASING.md` is
the human-readable version of this procedure; keep the two consistent.

Pushing a tag publishes a package that **cannot be deleted** from nuget.org. Never push without the user's explicit
confirmation of the exact version in this conversation.

## 1. Pick the release kind

From the argument, or ask if missing:

| Kind         | Branch tagged                   | Example version  |
|--------------|---------------------------------|------------------|
| `preview`    | `main`                          | `1.0.12-preview` |
| `stable`     | new `vX.Y` branch cut from main | `1.0.13`         |
| `patch vX.Y` | existing `vX.Y` release branch  | `1.0.14`         |

Release candidates (`prepare-release rc`) are not used: pre-releases are `-preview` builds from `main`.

## 2. Preflight — stop and report if any check fails

Run `dotnet tool restore` first, then:

1. `git status --porcelain` is empty (no uncommitted changes).
2. `git fetch origin --tags` then the branch to tag is not behind `origin` (`git rev-list --count HEAD..origin/<branch>`
   is `0`). If it is ahead, the unpushed commits go out with the release — list them and mention it.
3. The commit to tag has no existing `v*` tag (`git tag --points-at HEAD`).
4. CI is green for that commit if it was already pushed:
   `gh run list --workflow ci.yml --commit <sha> --json conclusion,status,headBranch`. If it has not been pushed yet, the
   tag build itself runs the tests; say so.
5. Tests pass locally against Redis: `dotnet test --solution DotNop.Channels.Redis.slnx -c Release`. If the summary shows
   all tests **skipped**, Redis is not running — that is not a pass; tell the user and rely on CI instead only if they
   agree.

## 3. Prepare

- **preview**: `git switch main`. Nothing else.
- **stable**: on `main`, first check `src/DotNop.Channels.Redis/PublicAPI.Unshipped.txt`. If it has entries besides
  `#nullable enable`, offer to move them into `PublicAPI.Shipped.txt` (sorted, keeping the `#nullable enable` header in
  both) and commit that as `chore: ship public API` before continuing. Note the `main` SHA (`git rev-parse HEAD`) for
  undoing, then run `dotnet nbgv prepare-release`. This creates the `vX.Y` branch
  with the prerelease suffix removed (one commit), and on `main` commits a version bump to `X.(Y+1)-preview`
  (`firstUnstableTag` in `version.json`; pass `--nextVersion <ver>` for another one) and then merges `vX.Y` back.
  It also rewrites `version.json`, dropping properties equal to NBGV defaults — harmless. Show both `version.json`
  changes, then `git switch vX.Y`.
- **patch**: `git switch vX.Y`. If the user named commits to include, `git cherry-pick` them and report conflicts
  instead of resolving them silently.

## 4. Update CHANGELOG.md

`CHANGELOG.md` at the repo root follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). If it does not exist,
create it with the standard header (`# Changelog` plus one line linking Keep a Changelog and stating that versions
follow NBGV/SemVer); do not backfill older releases.

1. Collect changes since the previous release of the same kind reachable from the branch being tagged — for a preview
   any `v*` tag (`git describe --tags --abbrev=0 --match "v*"`), for stable and patch the previous **stable** tag
   (`--exclude "v*-*"`), so a stable entry covers everything since the last stable, not just since the last preview
   (none → the whole history; for the first stable release write it as the initial release, summarizing the
   features). Then `git log <prev-tag>..HEAD --no-merges --format="%h %s%n%b"`.
2. Map conventional commits to sections, rewritten as short user-facing sentences (not raw commit subjects):
   `feat` → **Added**, `fix` → **Fixed**, `perf`/`refactor` with visible effect → **Changed**, removals →
   **Removed**, and anything marked `!` or `BREAKING CHANGE:` → **Changed** prefixed with `**Breaking:**`. Skip
   `test`, `ci`, `build`, `chore` and `docs` unless the change matters to package users (e.g. a documented behavior
   change). Omit empty sections; if nothing is user-facing, write a single `### Changed` line saying so.
3. Add the section under the header, newest first, as `## [<version>] - <yyyy-MM-dd>`, and a link reference at the
   bottom: `[<version>]: https://github.com/me-viper/DotNop.Channels.Redis/releases/tag/v<version>`. Move any
   `## [Unreleased]` content into it.
4. Commit on the branch being tagged as `docs: update changelog for v<version>`. The commit raises the git height, so
   the version usually grows by one: predict it as the current patch + 1, commit, then re-run
   `dotnet nbgv get-version -v NuGetPackageVersion`. On a `vX.Y` branch it prints a `-g<sha>` suffix (branch builds
   are not public; the tag drops it) — compare only the number. If it differs, fix the heading and link and
   `git commit --amend --no-edit` (amending does not change the height) until they match.
5. stable / patch: also `git cherry-pick` that commit onto `main` so its changelog stays complete (report conflicts
   instead of resolving them silently), then `git switch` back to the branch being tagged.

## 5. Confirm, tag, push

1. Show the user: version, branch, commit (`git log -1 --oneline`), the new changelog section, and the exact refs that
   will be pushed; mention that a GitHub release will be created after the package is published. Ask for confirmation
   (AskUserQuestion). Stop here if they decline and tell them how to undo locally instead of doing it unasked: drop the
   changelog commit(s) (`git reset --hard HEAD~1` on each branch that got one), and for stable also undo
   `prepare-release` (`git branch -D vX.Y` and `git reset --hard <main SHA noted before prepare-release>` on `main`,
   which removes the bump, the merge and the cherry-picked changelog at once).
2. `dotnet nbgv tag` — it prints the tag name (`v<version>`).
3. Push in one command, branches before the tag:
   - preview: `git push origin main <tag>`
   - stable: `git push origin main vX.Y <tag>`
   - patch: `git push origin main vX.Y <tag>`

## 6. Follow up

1. Find the run: `gh run list --workflow ci.yml --branch <tag> --limit 1`, then watch it with
   `gh run watch <id> --exit-status` (run in background; it may take several minutes).
2. If the `publish` job waits on the `nuget` environment, tell the user to approve it in GitHub.
3. On failure, show the failing step's log (`gh run view <id> --log-failed`). Common causes: Trusted Publishing policy
   on nuget.org missing or not matching (owner `me-viper`, repo `DotNop.Channels.Redis`, workflow `ci.yml`, environment
   `nuget`); a version that already exists (`--skip-duplicate` makes that a no-op, not an error).
4. On success, give the package URL `https://www.nuget.org/packages/DotNop.Channels.Redis/<version>` and note indexing
   can take several minutes.
## 7. Create the GitHub release

Only after the `publish` job succeeded, so the release never points to a package that is not on nuget.org. If it
failed, ask the user whether to create the release anyway.

1. Write the release notes to a file in the scratchpad: the body of the version's `CHANGELOG.md` section (without its
   `## [...]` heading), then a line `**NuGet:** https://www.nuget.org/packages/DotNop.Channels.Redis/<version>`.
2. Create the release, with `--prerelease` for a `-preview` version and `--latest` for a stable or patch one:
   `gh release create <tag> --verify-tag --title <tag> --notes-file <notes> --prerelease|--latest`.
3. Give the user the release URL that `gh` prints.

## 8. Wrap up

1. Switch the checkout back to `main` (stable and patch releases leave it on `vX.Y`).
2. After the first stable release of a major version, offer to set `<PackageValidationBaselineVersion>` in
   `src/DotNop.Channels.Redis/DotNop.Channels.Redis.csproj` to that version (commit on `main`). Package validation
   restores the baseline from nuget.org, so wait until the version is downloadable
   (`https://api.nuget.org/v3-flatcontainer/dotnop.channels.redis/<version>/dotnop.channels.redis.nuspec` returns 200;
   poll in the background, it can take 10+ minutes), then verify with a Release `dotnet pack` before committing —
   committing earlier breaks restore on `main`.

Never delete a pushed tag or unlist a package on your own; if something went wrong, explain the options from the
"Mistakes" section of `RELEASING.md` and let the user decide.
