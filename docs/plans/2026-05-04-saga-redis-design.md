# `ZeroAlloc.Saga.Redis` — Second durable backend

**Date:** 2026-05-04
**Status:** Design — pending user approval before implementation
**Roadmap entry:** v1.3 in README.md

## Goal

Add Redis as a durable backend for `ZeroAlloc.Saga`, mirroring the shape of `ZeroAlloc.Saga.EfCore` 1.0. One fluent call (`WithRedisStore()`) swaps the InMemory default for a Redis-backed `ISagaStore<TSaga, TKey>`. The runtime contract is unchanged — saga handlers see the same `LoadOrCreateAsync` / `SaveAsync` / `RemoveAsync` API; OCC retries via the existing scope-per-attempt loop; tests stay backend-agnostic.

## Architecture

### Storage model

One Redis key per saga instance:

```
saga:{SagaTypeName}:{CorrelationKey}
```

Value = byte-encoded saga state via `ISagaPersistableState` + `SagaStateWriter` (already in Saga 1.2). State is opaque bytes — Redis treats it as `RedisValue` of the byte-string variant.

Concurrency token: stored alongside the state in a Redis Hash with two fields:
- `state` — bytes
- `version` — Guid string (rotated on every save)

So actual key is a Hash:
```
HMSET saga:OrderFulfillmentSaga:42 state <bytes> version <guid>
```

This mirrors the EfCore single-row-per-saga + RowVersion-as-Guid pattern.

### OCC via WATCH/MULTI/EXEC

`SaveAsync` flow:
1. `WATCH saga:{type}:{key}`
2. `HGET saga:{type}:{key} version` → compare to expected (the version observed at `LoadOrCreateAsync`).
3. If mismatch, `UNWATCH`, throw `RedisSagaConcurrencyException`.
4. If match, `MULTI` → `HSET state=<new bytes> version=<new guid>` → `EXEC`.
5. If `EXEC` returns null (transaction aborted because watched key changed concurrently), throw `RedisSagaConcurrencyException`.

`LoadOrCreateAsync`:
- `HMGET saga:{type}:{key} state version`
- If both null, return `new TSaga()` and remember "no version observed" so the eventual `SaveAsync` does an INSERT (uses `HSETNX` instead of `HSET`-with-version-check).
- Otherwise deserialize state via `SagaStateReader` and remember the observed version.

`RemoveAsync`:
- `WATCH` + `HGET version` + `MULTI` + `DEL` + `EXEC`. Same OCC pattern.

The `version` stored on the saga itself (in-memory, not on the Saga base class) is tracked by the `RedisSagaStore` instance via a per-key dictionary keyed by `(SagaType, CorrelationKey)`. Cleared on save/remove.

### Connection management

User registers `IConnectionMultiplexer` themselves (per StackExchange.Redis convention) — typically as a Singleton via `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(...))`.

`WithRedisStore()` adds:
```csharp
services.AddScoped<IDatabase>(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());
// SagaStoreRegistrar.Apply<TSaga,TKey> uses RedisSagaStore<TSaga,TKey> as the impl
```

`RedisSagaStore<TSaga, TKey>` takes `IDatabase` (Scoped resolution; each per-attempt scope gets its own conceptual `IDatabase` reference, though they share the underlying multiplexer). The scope-per-attempt retry loop already in place from the recent merge handles the retry semantics correctly — each attempt creates a fresh scope, resolves a fresh `IDatabase`, fresh per-key version state, fresh OCC attempt.

### Builder shape

`ZeroAlloc.Saga.Redis` ships:
- `RedisSagaStore<TSaga, TKey>` — implements `ISagaStore<TSaga, TKey>`.
- `SagaRedisBuilderExtensions.WithRedisStore(this ISagaBuilder, Action<RedisSagaStoreOptions>?)` — registers the store, flips `ISagaBuilderMutable.IsRedisBackend` (parallel to `IsEfCoreBackend`), and installs a `SagaStoreRegistrar` delegate so generator-emitted `With{Saga}Saga()` resolves the Redis store.
- `RedisSagaStoreOptions` — exposes `KeyPrefix` (default `"saga"`), `MaxRetryAttempts`, `RetryBaseDelay`, `UseExponentialBackoff` (parallel to `EfCoreSagaStoreOptions`; reuses `SagaRetryOptions`).
- `RedisSagaConcurrencyException` — sentinel thrown on OCC mismatch. Caught by the generator-emitted handler's retry loop.

### Generator change for retry-loop catch

Current handler catch:
```csharp
catch (Exception ex) when (attempts < _retry.MaxRetryAttempts && IsEfCoreConflict(ex))
```
where `IsEfCoreConflict` matches `DbUpdateException` / `DbUpdateConcurrencyException` by FQN.

Need to extend `IsEfCoreConflict` (rename to `IsBackendConflict`) to also match `ZeroAlloc.Saga.Redis.RedisSagaConcurrencyException`. The match remains string-name to avoid the generator output referencing the Redis package.

```csharp
private static bool IsBackendConflict(Exception ex)
{
    var n = ex.GetType().FullName;
    return n == "Microsoft.EntityFrameworkCore.DbUpdateException"
        || n == "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"
        || n == "ZeroAlloc.Saga.Redis.RedisSagaConcurrencyException";
}
```

This is a **non-breaking generator change** — pure addition. Existing emitted code keeps the EfCore checks; new emissions also check Redis. Since the handler is emitted per-consumer-assembly, recompiling against the new generator picks up the new check transparently.

### Builder ordering

`WithRedisStore()` and `WithEfCoreStore()` are mutually exclusive: calling both throws `InvalidOperationException`. `IsRedisBackend` and `IsEfCoreBackend` flags on `ISagaBuilderMutable` (the latter already exists) are checked at composition time.

`WithOutbox()` composition:
- With `WithEfCoreStore()`: atomic via shared `DbContext` (already shipping).
- With `WithRedisStore()`: **NOT atomic** in v1. The outbox enqueue (currently `EfCoreOutboxStore` writing to a SQL table) cannot participate in Redis's MULTI/EXEC. We document this explicitly: "the outbox bridge requires `WithEfCoreStore()`; Redis users should rely on at-least-once semantics + idempotent receivers (ZASAGA015)." A future Redis-native outbox store (separate package) could close the gap.

We could go further and emit a new diagnostic — `ZASAGA019` — when both `WithRedisStore()` AND `WithOutbox()` are detected in the same compilation. Worth it? Probably yes; misuse here loses atomicity silently.

### Compatibility with `WithResilience()`

Pure DI decorator at the dispatcher level — works with any backend. No change.

### Compatibility with scope-per-attempt retry

The handler retry loop creates a fresh `IServiceScope` per attempt. With Redis:
- Fresh `IDatabase` per scope (still backed by the same `IConnectionMultiplexer` Singleton).
- Fresh `RedisSagaStore` instance per scope.
- Per-instance version-tracking dictionary starts empty per attempt.
- WATCH/MULTI/EXEC on the same key from a fresh attempt sees committed state from any prior winning attempt → if the value changed, fresh `LoadOrCreate` reflects it.

This is exactly the "exactly-once dispatch under OCC retry" behavior we just shipped for EfCore — it works the same way for Redis.

## AOT compatibility

`StackExchange.Redis` 2.8+ supports AOT (per their release notes; `<IsAotCompatible>true</IsAotCompatible>` works). `RedisSagaStore` itself uses no reflection (just `IDatabase` calls). We'll set `<IsAotCompatible>true</IsAotCompatible>` + `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` and address any analyzer hits as they come up — same approach as `Saga.EfCore`.

A new sample `samples/AotSmokeRedis` would prove this — but Redis requires a running server, so this is a JIT-only smoke (mirrors `AotSmokeEfCore` which is JIT-only because of EF Core's experimental AOT story). Empirical AOT verification deferred.

## Tests

- **Unit tests** — `RedisSagaStore` against a mocked `IDatabase`. Probably 8-10 tests: insert path, update path, OCC mismatch detection, remove path, concurrent-write race simulation.
- **Integration tests** — `Testcontainers.Redis` spins up a real Redis instance for the duration of the test class. ~6-8 tests: full saga run, OCC retry recovery, version rotation observed.
- **E2E retry test** (parallel to the EfCore E2ETests `OCC_HandlerRetryLoop_RecoversFromTransientConflict`) — proves that the scope-per-attempt retry loop works end-to-end with the Redis backend.

Test container dependency means tests need Docker available. CI runners have Docker; the existing EfCore tests use SQLite in-memory which doesn't have this constraint, so adding Testcontainers is a one-time CI dep increase. Acceptable.

## Files

```
src/ZeroAlloc.Saga.Redis/
├── ZeroAlloc.Saga.Redis.csproj            # ref Saga, StackExchange.Redis
├── RedisSagaStore.cs                      # the ISagaStore<TSaga,TKey> impl
├── RedisSagaStoreOptions.cs               # KeyPrefix, retry knobs (extends SagaRetryOptions)
├── RedisSagaConcurrencyException.cs       # sentinel for OCC mismatch
├── SagaRedisBuilderExtensions.cs          # WithRedisStore() + IsRedisBackend mutability
├── SagaRedisDiagnosticIds.cs              # ZASAGA019 (Redis + Outbox warning) optional
├── PublicAPI.{Shipped,Unshipped}.txt
└── version.txt = 0.0.0

src/ZeroAlloc.Saga/ISagaBuilder.cs         # add IsRedisBackend bool
src/ZeroAlloc.Saga/ISagaBuilderMutable.cs  # setter
src/ZeroAlloc.Saga.Generator/HandlerEmitter.cs  # extend IsBackendConflict to match Redis exception FQN

tests/ZeroAlloc.Saga.Redis.Tests/
├── ZeroAlloc.Saga.Redis.Tests.csproj      # ref Testcontainers.Redis
├── RedisFixture.cs                        # Testcontainers Redis lifetime
├── RedisSagaStoreTests.cs                 # unit-ish (against the real container)
├── E2ETests.cs                            # full saga happy/compensation path
└── OccTests.cs                            # retry-loop recovery, exhaustion

samples/AotSmokeRedis/                     # optional; defer to follow-up PR if scope grows

docs/persistence-redis.md                  # mirrors persistence-efcore.md

release-please-config.json + manifest      # add Saga.Redis at 0.0.0
ZeroAlloc.Saga.slnx                        # add the new csprojs
README.md                                   # roadmap row swap (Redis is now this release; v1.3+ items shift)
```

## Out of scope (v1)

1. **Redis-native outbox store** — would close the `WithOutbox().WithRedisStore()` atomicity gap. Substantial separate effort; defer.
2. **Cluster mode** — single-node Redis only in v1. Cluster works for the dispatch path (single key per saga = single hash slot) but the operations need explicit `{}` hash-tag annotations to guarantee co-location. Out of v1.
3. **`SagaInstance` cross-saga listing** — `SCAN saga:*` works but is not an indexed query. v1 ships without an `IList<SagaInstance>` API; comes later if a host needs it.
4. **Telemetry / OTel spans** — separate `Saga.Telemetry` bridge in roadmap.

## Decision points (please confirm or course-correct)

1. **Storage shape: Redis Hash with `state` + `version` fields.** Alternative: Redis String (single binary blob, version prepended in a fixed-width header). Hash is more diagnosable (`HGETALL` shows fields) but uses ~30 bytes more per entry. Hash recommended; tradeoff is small.

2. **Generator change: extend `IsBackendConflict` to match Redis exception FQN.** Non-breaking. Alternative: virtualize the conflict check via a new `ISagaConflictDetector` abstraction. More flexible, more ceremony. Recommend the simple FQN-list extension.

3. **`WithOutbox().WithRedisStore()` diagnostic ZASAGA019.** Adds value for footgun prevention. Opt-in here; if you'd rather just document, skip the diagnostic.

4. **Testcontainers.Redis dependency.** Requires Docker on CI. The existing AotSmokeEfCore CI runner has Docker; we just add the package. Acceptable?

5. **Mutual exclusion with `WithEfCoreStore()`.** Calling both should throw. Alternative: last-one-wins (silent override). Throw recommended; misuse is otherwise hard to debug.

6. **Sample.** Skip `AotSmokeRedis` for v1, ship later if useful? Or include a JIT-only sample for documentation parity with `AotSmokeEfCore`?

## Effort estimate

- Generator change + RedisSagaStore + builder extension + options/exception types: ~half day.
- Testcontainers fixtures + 14-16 tests: ~half day.
- Docs + roadmap update: ~hour.
- Release-please + slnx wiring + Directory.Packages.props bumps: ~hour.

**Total: 1-1.5 days of focused work.** Less than the "full week+" earlier estimate because the EfCore backend's scaffolding (SagaStoreRegistrar, ISagaBuilder shape, scope-per-attempt retry) is already in place and we're following its template.

---

Confirm or course-correct on the 6 decision points above and I'll start implementation.
