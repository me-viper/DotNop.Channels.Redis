---
name: publish-release
description: Release DotNop.Channels.Redis to nuget.org — a pre-release from main, a stable release (cuts a vX.Y branch), or a patch release on an existing release branch — by creating and pushing an NBGV version tag that triggers the CI publish job. Use when the user asks to release, publish, ship or tag a version of the package.
argument-hint: "[preview | stable [rc] | patch vX.Y]"
---

# Publish a release

Releases are published by the `publish` job in `.github/workflows/ci.yml`, triggered by pushing a `v*` tag. Versions
come from Nerdbank.GitVersioning (`version.json` + git height) — never edit version numbers by hand. `RELEASING.md` is
the human-readable version of this procedure; keep the two consistent.

Pushing a tag publishes a package that **cannot be deleted** from nuget.org. Never push without the user's explicit
confirmation of the exact version in this conversation.

## 1. Pick the release kind

From the argument, or ask if missing:

| Kind          | Branch tagged                   | Example version          |
|---------------|---------------------------------|--------------------------|
| `preview`     | `main`                          | `1.0.12-preview`         |
| `stable [rc]` | new `vX.Y` branch cut from main | `1.0.13` / `1.0.13-rc`   |
| `patch vX.Y`  | existing `vX.Y` release branch  | `1.0.14`                 |

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
  both) and commit that as `chore: ship public API` before continuing. Then run `dotnet nbgv prepare-release` (or `dotnet nbgv prepare-release rc`). This creates the
  `vX.Y` branch with the prerelease suffix removed and commits a version bump on `main` (to `X.(Y+1)-alpha` by default;
  pass `--nextVersion <ver>` if the user wants another one). Show both `version.json` changes, then `git switch vX.Y`.
- **patch**: `git switch vX.Y`. If the user named commits to include, `git cherry-pick` them and report conflicts
  instead of resolving them silently.

Then show `dotnet nbgv get-version -v NuGetPackageVersion` — the tag build publishes exactly this version.

## 4. Confirm, tag, push

1. Show the user: version, branch, commit (`git log -1 --oneline`), and the exact refs that will be pushed. Ask for
   confirmation (AskUserQuestion). Stop here if they decline; for stable, tell them how to undo `prepare-release`
   locally (`git branch -D vX.Y` and `git reset --hard HEAD~1` on `main`) instead of doing it unasked.
2. `dotnet nbgv tag` — it prints the tag name (`v<version>`).
3. Push in one command, branches before the tag:
   - preview: `git push origin main <tag>`
   - stable: `git push origin main vX.Y <tag>`
   - patch: `git push origin vX.Y <tag>`

## 5. Follow up

1. Find the run: `gh run list --workflow ci.yml --branch <tag> --limit 1`, then watch it with
   `gh run watch <id> --exit-status` (run in background; it may take several minutes).
2. If the `publish` job waits on the `nuget` environment, tell the user to approve it in GitHub.
3. On failure, show the failing step's log (`gh run view <id> --log-failed`). Common causes: Trusted Publishing policy
   on nuget.org missing or not matching (owner `me-viper`, repo `DotNop.Channels.Redis`, workflow `ci.yml`, environment
   `nuget`); a version that already exists (`--skip-duplicate` makes that a no-op, not an error).
4. On success, give the package URL `https://www.nuget.org/packages/DotNop.Channels.Redis/<version>` and note indexing
   can take several minutes.
5. After the first stable release of a major version, offer to set `<PackageValidationBaselineVersion>` in
   `src/DotNop.Channels.Redis/DotNop.Channels.Redis.csproj` to that version (commit on `main`).

Never delete a pushed tag or unlist a package on your own; if something went wrong, explain the options from the
"Mistakes" section of `RELEASING.md` and let the user decide.
