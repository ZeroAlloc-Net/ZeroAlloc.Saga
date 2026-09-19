# Releasing

This repo uses release-please in **multi-component manifest mode**. `release-please-config.json`
declares six packages, one per `src/` directory:

```
src/ZeroAlloc.Saga
src/ZeroAlloc.Saga.EfCore
src/ZeroAlloc.Saga.Outbox
src/ZeroAlloc.Saga.Outbox.Redis
src/ZeroAlloc.Saga.Redis
src/ZeroAlloc.Saga.Resilience
```

## The trap: a change outside `src/` releases nothing

release-please attributes each commit to a package **by the path of the files it changed**. A
commit that touches no file under any of the six package paths maps to no component. Every
component reports `Considering: 0 commits`, no release PR is opened, and the workflow still
succeeds.

**A green merge is not a release.** This has bitten the repo repeatedly:

- #95 and #71 — generator-only changes. `src/ZeroAlloc.Saga.Generator` is not a declared
  package, so changes there match nothing. Tracked in #98.
- #131 — the fix for #127. It changed the `ZeroAlloc.Mediator` floor to 5.1.0 in
  `Directory.Packages.props` and migrated tests and samples. Nothing under `src/`, so nothing
  released — and `ZeroAlloc.Saga` on NuGet kept resolving the broken Mediator 5.0.0 while the
  issue sat closed.

Central package management makes this sharp: **every dependency bump lives in root
`Directory.Packages.props`, which no component path can see.** A dependency fix will merge green
and ship nothing unless you act.

## There is no config option for this

release-please offers only `exclude-paths` per package, plus a root `"."` package meaning "release
on any change". Adding `"."` here would create a seventh release artifact that maps to no real
NuGet package, so it is not an option. There is no `include-paths` or `always-update` equivalent.

## What to do instead

When a change outside `src/` needs to reach consumers — a dependency floor, a generator fix —
make it reach a component path one of two ways:

1. **Touch the packaging project.** Land a real change in the `.csproj` of each affected package,
   such as a comment recording the new dependency floor and why. This is what #131's follow-up
   did for the three packages that depend on `ZeroAlloc.Mediator`.
2. **Use a `Release-As:` footer** on a commit that touches the packaging package's path, to force
   a specific version.

## Always verify against NuGet

The publish job reporting success is not proof either. After a release merges, check the package
actually appeared and that its dependency ranges are what you expect:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.saga/index.json
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.saga/<version>/zeroalloc.saga.nuspec \
  | grep '<dependency'
```

Note that a floating range such as `5.1.*` is resolved at pack time and baked into the nuspec as a
minimum, for example `>= 5.1.0`. NuGet resolves a minimum to the **lowest** matching version, so
the floor is what consumers actually get.
