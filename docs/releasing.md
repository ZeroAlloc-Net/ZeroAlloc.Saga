# Releasing

This repo uses release-please with a **single component** at the repository root. One version
covers all six published packages, and the publish job packs every project marked
`<IsPackable>true</IsPackable>` at that version.

This matches 24 of the 28 ZeroAlloc repos, including `ZeroAlloc.Resilience`, which has the same
sibling-generator layout as this one.

## Why not one component per package

It used to declare one component per `src/` package. That caused two separate defects.

### 1. Commits that released nothing

release-please attributes each commit to a package **by the path of the files it changed**. A
commit touching no file under any package path mapped to nothing: every component reported
`Considering: 0 commits`, no release PR opened, and the workflow still went green. **A green merge
was not a release.**

- **#95**, **#71** — generator-only changes. `src/ZeroAlloc.Saga.Generator` was not a declared
  package even though the generator it builds ships inside `ZeroAlloc.Saga` as
  `analyzers/dotnet/cs`.
- **#131** — the fix for #127. It raised the `ZeroAlloc.Mediator` floor in root
  `Directory.Packages.props` and migrated tests and samples, touching nothing under `src/`.
  Nothing released, so `ZeroAlloc.Saga` on NuGet kept resolving the broken Mediator 5.0.0 while
  the issue sat closed. It took #132 and a second release to reach anyone.

Central package management made this permanent rather than occasional: **every dependency bump
lives in a root file no component path can see.**

### 2. Siblings published wrong dependency versions

Worse, and invisible until you read a published nuspec. The old publish job packed each released
package with `-p:PackageVersion=$version`. That is a **global** MSBuild property, so it stamped the
sibling's own version onto every project in the graph — including the `ZeroAlloc.Saga`
ProjectReference.

The result, live on NuGet before this change:

| package | version | declared dependency |
| --- | --- | --- |
| `ZeroAlloc.Saga.EfCore` | 1.3.0 | `ZeroAlloc.Saga >= 1.3.0` |
| `ZeroAlloc.Saga.Redis` | 1.2.0 | `ZeroAlloc.Saga >= 1.2.0` |
| `ZeroAlloc.Saga.Resilience` | 1.2.1 | `ZeroAlloc.Saga >= 1.2.1` |
| `ZeroAlloc.Saga.Outbox.Redis` | 1.2.0 | `ZeroAlloc.Saga >= 1.2.0` |

Each names its **own** version number, not the version it was built against. NuGet resolves a
minimum to the lowest match, so `install-package ZeroAlloc.Saga.EfCore` pulled `ZeroAlloc.Saga`
1.3.0 — from before 2.0.0, and long before the #127 fix. It installed at all only because a 1.3.0
happened to exist.

A single version makes this correct by construction: `Saga.EfCore` at X depends on
`ZeroAlloc.Saga` X, because they are the same X.

## The version floor

Collapsing set the version to `2.0.1`, the highest any package had reached, so nothing moved
backwards on NuGet. `ZeroAlloc.Saga.EfCore` jumped from 1.3.0 and the Redis packages from 1.2.0;
those lines are closed.

Per-package `CHANGELOG.md` files under `src/` are frozen records of that era. The live changelog is
the one at the repository root.

## Cost

Lockstep versioning: a fix in any package bumps all six. That is what the other 24 repos live with,
and it buys correct inter-package dependencies plus the guarantee that every commit can release.

## Verify against NuGet, not against a green tick

Producing no packages is now a hard error rather than a silent success, but the final check still
belongs to you:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.saga/index.json
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.saga/<version>/zeroalloc.saga.nuspec \
  | grep '<dependency'
```

A floating range such as `5.1.*` is resolved at pack time and baked into the nuspec as a minimum,
for example `>= 5.1.0`. NuGet resolves a minimum to the **lowest** matching version, so the floor
is what consumers actually get.
