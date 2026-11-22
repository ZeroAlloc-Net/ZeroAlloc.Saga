# AGENTS.md: ZeroAlloc.Saga

Source-generated sagas for .NET. A `[Saga]` partial class declares steps and compensation, and a Roslyn
generator emits the state machine, notification handlers and command dispatch. Stores: in-memory, EF Core,
ZeroAlloc.ORM and Redis, each with an optional outbox bridge, plus a resilience bridge. It ships as NuGet
packages from one release-please component, so every package carries the same version.

## Layout

- `src/ZeroAlloc.Saga.Generator` targets `netstandard2.0` and is not packable. Its DLL is bundled into the
  `ZeroAlloc.Saga` nupkg as `analyzers/dotnet/cs` by the `IncludeGeneratorInPackage` target, so a generator
  change releases through `ZeroAlloc.Saga`.
- `ZeroAlloc.Saga` depends on `ZeroAlloc.Mediator.Generator` as a package so consumers get `IMediator` from
  one reference. `src/Directory.Build.targets` strips it from every `src` library's own compilation.
- Mediator floors differ on purpose: runtime 5.1.x, generator 6.7.x, both in `Directory.Packages.props`
  with the reasons in comments. `ZeroAlloc.Saga.Generator.Tests` overrides the runtime to 6.7.*.
- Packable libraries and the test projects target `net10.0;net11.0`.
- `tests/ZeroAlloc.Saga.MultiAssembly.*` check sagas split across assemblies.
- `samples/AotSmoke*` and `samples/ResilienceSample` are the AOT smokes that CI publishes, not shipped code.
- Generator snapshots are the `*.verified.cs` files under `tests/ZeroAlloc.Saga.Generator.Tests/Snapshots`.
  Edit them only by regenerating: run the snapshot tests with `ZA_SNAPSHOT_UPDATE=1`, then review the diff.

## Build and test

SDK: `global.json` pins 11.0.100 with `rollForward: latestMinor`. Runtimes: test projects target `net10.0` and
`net11.0`, so both runtimes must be installed. The Redis, Outbox.Redis, Orm and Outbox.Orm
test projects start Testcontainers, so they need a running Docker daemon.

Job `build` in `.github/workflows/build.yml`:

```bash
dotnet restore ZeroAlloc.Saga.slnx
dotnet build ZeroAlloc.Saga.slnx --configuration Release --no-restore
dotnet test ZeroAlloc.Saga.slnx --configuration Release --no-build --verbosity normal --collect "XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
```

The same job then runs the `ci-baseline` coverage gate over `TestResults/**/coverage.cobertura.xml`; the
commands above do not reproduce it. Keep the `codecov.yml` project status as it is.

Job `api-compat` in `build.yml` calls the shared `api-compat.yml`; intentional breaks live in
`apicompat-suppressions.xml`.

Jobs in `.github/workflows/aot-smoke.yml`:

```bash
dotnet publish samples/AotSmoke/AotSmoke.csproj -c Release -o aot-out                 # aot-smoke
timeout --kill-after=10s 120s ./aot-out/AotSmoke
dotnet build samples/AotSmokeEfCore/AotSmokeEfCore.csproj -c Release                   # aot-smoke-efcore
timeout --kill-after=10s 180s dotnet run --project samples/AotSmokeEfCore/AotSmokeEfCore.csproj -c Release --no-build
dotnet publish samples/AotSmokeOutbox/AotSmokeOutbox.csproj -c Release -o aot-out-outbox   # aot-smoke-outbox
timeout --kill-after=10s 120s ./aot-out-outbox/AotSmokeOutbox
dotnet publish samples/ResilienceSample/ResilienceSample.csproj -c Release -o aot-out-resilience   # aot-smoke-resilience
timeout --kill-after=10s 120s ./aot-out-resilience/ResilienceSample
dotnet publish samples/AotSmokeOrm/AotSmokeOrm.csproj -c Release -o aot-out-orm        # aot-smoke-orm
timeout --kill-after=10s 120s ./aot-out-orm/AotSmokeOrm
dotnet publish samples/AotSmokeRedis/AotSmokeRedis.csproj -c Release -o aot-out-redis  # aot-smoke-redis
```

Job `smoke` in `.github/workflows/benchmarks.yml` (one operation per benchmark; it also fails when the log
shows no `executed benchmarks: N` with N above 0):

```bash
dotnet build benchmarks/ZeroAlloc.Saga.Benchmarks -c Release
dotnet run --project benchmarks/ZeroAlloc.Saga.Benchmarks -c Release --no-build -- --filter '*' --iterationCount 1 --warmupCount 1 --invocationCount 1 --unrollFactor 1
```

Fast inner loop, verified without Docker; no CI job runs these exact commands:

```bash
dotnet test tests/ZeroAlloc.Saga.Tests -c Release -f net10.0
dotnet test tests/ZeroAlloc.Saga.Tests -c Release -f net10.0 --filter "FullyQualifiedName~SagaStateAllocationTests"
dotnet test tests/ZeroAlloc.Saga.Generator.Tests -c Release -f net10.0
```

Local pack: no version fails with `ZAVERSION001`, and a `--no-build` pack of an assembly built without it
fails with `ZAVERSION002`. Pass the version to every build step:

```bash
SHA=$(git rev-parse --short HEAD)
dotnet pack src/ZeroAlloc.Saga -c Release -p:Version=9.9.9-local.$SHA -o ./packout
```

CI job `sdk10-consumer` (in `build.yml`) needs a 10.0.1xx SDK; `tests/ZeroAlloc.Saga.Sdk10Smoke/global.json` pins
10.0.100 with `rollForward: latestPatch`, so a newer band cannot stand in for it. It builds and runs a
consumer of the packed package with Roslyn 5.0:

```bash
dotnet pack src/ZeroAlloc.Saga -c Release -o artifacts -p:Version=9.9.9-local.sha$(git rev-parse --short HEAD)
cd tests/ZeroAlloc.Saga.Sdk10Smoke/Consumer
dotnet run -c Release -p:ZeroAllocSagaVersion=9.9.9-local.sha$(git rev-parse --short HEAD)
```

CI job `roslyn-floor` calls the shared `roslyn-floor.yml` from ZeroAlloc-Net/.github: it packs the
solution and fails when a packed DLL references Roslyn above 5.0.0.

## Conventions and gotchas
- The saga generator references `Microsoft.CodeAnalysis.CSharp` and `.Workspaces` 5.0.0, the compiler of SDK
  band 10.0.1xx, the oldest supported SDK, set in `Directory.Packages.props`. A compiler older than the
  Roslyn the DLL references skips the generator with `CS9057`, and nothing is generated. The shared
  Renovate preset holds it below 5.1. If a newer Roslyn API is needed, change the code; raise the floor
  only together with the minimum SDK. The generator test projects use 5.0.0 too. The saga's `IMediator`
  comes from the `ZeroAlloc.Mediator.Generator` package dependency, so the `sdk10-consumer` job passes
  only when that floor is a Mediator.Generator built against Roslyn 5.0; raise the floor when one ships.

- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on, and `RS0016`/`RS0017` are errors: every public
  API change needs an entry in that project's `PublicAPI.Unshipped.txt`. Move entries to `Shipped` only through
  the release PR, which `release-tracking` checks. The same holds for generator diagnostics in
  `AnalyzerReleases.Unshipped.md`. Diagnostic ids are `ZASAGA001` to `ZASAGA099` and are documented in
  `docs/diagnostics.md`.
- The allocation budgets in `docs/performance.md` are asserted by `SagaStateAllocationTests`: 0 B for the state
  writer, reader primitives and generated `Restore()`, and 48 B for `Snapshot()` of a 21-byte state, which is
  the returned `byte[]`. Change a figure in the test and the doc together.
- `PublishAot=true` is set inside each AOT sample csproj, never on the command line: the command-line form
  reaches the `netstandard2.0` generator through the project references and fails with `NETSDK1207`.
- Two AOT smokes are partial by design. `AotSmokeEfCore` is built and run on the JIT, because EF Core's
  dynamic LINQ is not NativeAOT-safe yet. `AotSmokeRedis` is published but not run, because it needs a Redis
  server. Do not turn either into a full publish-and-run without that blocker resolved.
- The benchmark project targets only `net10.0`, not both LTS frameworks as the org rule says.
- Release-please has a single root component, with `tests` and `samples` excluded. Any other path counts as
  releasable, including `Directory.Packages.props`. See `docs/releasing.md` before touching the component layout.

<!-- org-core:start sha256=8faf417fa86f11bd8ad9bc98dfa4683fba44e440291e84c6cb9b0ec3ca0c0e7e (generated from docker-nuc scripts/agents-md/org-core-zeroalloc.md; do not edit here) -->
## Org rules

These rules hold in every ZeroAlloc-Net repo. Each library makes two promises: it works under
NativeAOT and trimming, and its hot path allocates nothing. The 2026-10-11 sweep found 16 of 28 repos
allocating on the hot path, most with docs claiming 0 B.

### Hot path

The operation a consumer runs repeatedly: `Send`, a cache hit, `Validate`, `Map`, `Serialize`, a
resilience call, a state transition, an append, a worker poll, including the idle poll. Startup and
registration are not hot path. For a source generator, the hot path is the code it emits: judge the
snapshots and the generated output.

### Allocation traps the sweep found

| Pattern | Instead |
|---|---|
| `async ValueTask` that only awaits and returns | `return new ValueTask<T>(inner.XAsync(ct));` when the method only forwards a `Task<T>` (return a forwarded `ValueTask<T>` directly). The wrapper stores a synchronous throw in the returned task; the direct return lets it, and the cancellation status of the callee, reach the caller. Keep the `async` wrapper when callers depend on either |
| `Task.WhenAll` over tasks that already completed | skip the await only when every task has `IsCompletedSuccessfully` (`Status == TaskStatus.RanToCompletion` on `netstandard2.0`); otherwise `await Task.WhenAll`, which also reports faults of completed tasks |
| `TagList` or log `params object[]` built before `Enabled`/`IsEnabled` | check first, or `LoggerMessage.Define` |
| event args allocated before checking for subscribers | copy the handler to a local, check, then allocate |
| `new Serializer()` / `new Utf8JsonWriter()` per call | cache stateless instances, reuse writers with `Reset` |
| `GetString()` / `ToString()` round-trips in JSON converters | parse and format through spans |
| `ToArray()` / `ToList()` copies between layers | pass `ReadOnlyMemory<T>` or spans, copy once at most |
| LINQ in emitted code | emit `for` loops |
| `yield` iterator behind a public `GetEnumerator` | public struct enumerator, from the first release |
| `Enum.IsDefined(typeof(E), v)` with a boxed value, `decimal.GetBits(v)` to read the scale | `Enum.IsDefined<E>(v)` when `v` has type `E` (.NET 5 or later, so not on .NET Framework, `netstandard2.0`, `netstandard2.1` or .NET Core 3.1); `v.Scale` for a scale read only (.NET 7 or later, never for the coefficient or sign). Keep the old call on older targets |
| configuration parsed or read per call | parse once and cache |
| an `AsyncLocal` write per call | pass state explicitly |
| a capturing lambda or method-group delegate per call | cache the delegate in a field, or a static lambda |
| a per-key object that is never removed | remove on release; otherwise it is a leak |

Inherent allocations are fine when documented: the consumer's own result, an exception on a failure
path, a one-time cache fill.

### Tests and benchmarks

- Every hot path has an allocation gate test (`AllocationGate` from ZeroAlloc.TestHelpers) that measures
  the whole public call, including any state setup.
- Budgets are exact. If a path allocates, assert that number and comment what it is.
- Test the generated code, not a hand-written stand-in, and cover every shape: each attribute, nested
  collections, and the asynchronous case with a fake that returns pending tasks.
- Benchmarks run in CI through the shared `benchmark-smoke.yml` and target the library's LTS
  frameworks. The benchmark body allocates nothing besides the library itself. Run locally with
  `--inProcess`.
- Publish exact allocations and same-run ratios against a `Baseline`, never absolute timings. Run with
  `--launchCount 3`, pin CPUs with `taskset`, give the spread, and label every table.

### NativeAOT and trimming

- Packable runtime projects, and projects consumers publish, set `IsAotCompatible=true` on modern
  target frameworks. Analyzers and source generators target `netstandard2.0`.
- No reflection or runtime code generation on a supported path. An opt-in reflection fallback carries
  honest `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. Every suppression must be true;
  remove dead ones.
- The AOT smoke sample publishes with `TreatWarningsAsErrors=true`. If a third-party warning really
  blocks it, use a narrow, justified suppression for that warning. `WarningsNotAsErrors` selects codes,
  not assemblies, so it would also downgrade the same warning from generated code.
- The smoke runs the hot path of every package in the repo. A package without a smoke has unknown AOT
  status. Adapters run against real services in CI service containers; publish-only is a last resort.
  App and template smokes make authenticated requests.

### Docs and shipping

- Docs match measurements. After a hot-path change, re-measure and update `docs/performance.md` and the
  README. Never write "0 B" without a test that asserts it.
- New public API is `feat:`, even for a performance change. Public API changes are additive: never add
  an optional parameter to a shipped signature. A breaking end state goes in a `next-major` issue.
- A behaviour change gets a `## Behaviour change` section in the PR body.
- Conventional Commits with release-please. Subjects start lowercase, body lines stay within 100
  characters, and a body never contains nested parentheses: release-please drops the whole commit.
- Local packs use `9.9.9-local.<sha>`. After a release, verify the package on NuGet.
- Fix review findings in the PR; file an issue only for what cannot go in it.

Full rules, template wiring lessons and the reasons behind each rule: https://github.com/ZeroAlloc-Net/.github/blob/main/AGENTS.md
<!-- org-core:end -->
