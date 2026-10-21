# Performance

## Allocation budgets

Saga state crosses process boundaries through `ISagaPersistableState`: every `[Saga]` class gets
a generator-emitted `Snapshot()` and `Restore()` built on the `SagaStateWriter` and
`SagaStateReader` ref structs. Every durable store calls `Snapshot()` once per saga step. Each
figure below is an exact budget asserted by `SagaStateAllocationTests`:

| Call | Allocation |
|---|---:|
| `SagaStateWriter`, every supported shape, into your `IBufferWriter<byte>` | 0 B |
| `SagaStateReader`, primitives | 0 B |
| `SagaStateReader.ReadString` and `ReadBytes` | only the returned value, for example 40 B for `"order-42"` |
| Generated `Restore()` | 0 B |
| Generated `Snapshot()`, a 21-byte state | 48 B, the exact-size `byte[]` it returns |

`Snapshot()` writes into a scratch writer each thread keeps and reuses, so the returned array is
its only allocation. Before 4.1.3 the same call allocated 168 B
([#273](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/273)).

Supported state shapes: primitives, enums, `string`, `DateTime` / `DateTimeOffset` / `TimeSpan` /
`Guid`, `[TypedId]`-attributed types, the common `record struct Foo(TPrim Bar)` shape, `byte[]`,
and `Nullable<T>` wrappers thereof. A member of any other type is reported as `ZASAGA014`; exclude
transient or computed members with `[NotSagaState]`.

## Benchmarks

`benchmarks/ZeroAlloc.Saga.Benchmarks` measures the generated notification handlers on the
InMemory store with every log level disabled, including the key lock, the per-attempt DI scope and
the store and dispatcher each attempt resolves:

```bash
dotnet run --project benchmarks/ZeroAlloc.Saga.Benchmarks -c Release -- --filter "*" --launchCount 3
```

Measured 2026-10-21 on an otherwise idle machine, out of process in three launches, pinned to two
performance cores:

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9711)
12th Gen Intel Core i9-12900HK 2.50GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
```

> Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published.

`Ratio` is each row's mean time relative to the duplicate-event path in the same run, with the
lowest and highest of the three launches in brackets.

| Benchmark | What one operation does | Ratio | Allocated |
|---|---|---:|---:|
| `DuplicateEvent` | A forward step whose trigger the saga already consumed; logs at Debug and returns | 1.00 | 400 B |
| `OrphanCompensation` | A compensation event with no saga to compensate; logs at Warning and returns | 0.96 (0.95–0.98) | 400 B |
| `FullLifecycle` | A whole saga: create, two saves, completion and removal, three dispatches | 4.67 (4.63–4.76) | 1,576 B |

The 400 B on the two early-return paths is what every handler attempt costs: the key lock, the
per-attempt DI scope and the store and dispatcher it resolves. Both paths also log, so a log call
that allocated while its level is disabled would show up in these two rows; see #248.

There is no published head-to-head against another saga library yet.
