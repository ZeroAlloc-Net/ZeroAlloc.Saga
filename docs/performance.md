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

Measured 2026-11-21 on a Linux virtual machine under light load, out of process in three
launches:

```
BenchmarkDotNet v0.15.8, Linux Debian GNU/Linux 13 (trixie)
Intel Core Ultra 7 255H 3.69GHz, 1 CPU, 12 logical and 12 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
```

> Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published.

`Ratio` is each row's mean time relative to the duplicate-event path in the same run, with the
lowest and highest of the three launches in brackets.

| Benchmark | What one operation does | Ratio | Allocated |
|---|---|---:|---:|
| `DuplicateEvent` | A forward step whose trigger the saga already consumed; logs at Debug and returns | 1.00 | 360 B |
| `OrphanCompensation` | A compensation event with no saga to compensate; logs at Warning and returns | 0.99 (0.98–1.01) | 360 B |
| `FullLifecycle` | A whole saga: create, two saves, completion and removal, three dispatches | 4.78 (4.58–4.89) | 1,456 B |

The 360 B on the two early-return paths is what every handler attempt costs: the per-attempt DI
scope and the store and dispatcher it resolves. The key lock allocates nothing when no other
handler holds the same key and a pooled lock entry is available for reuse; before 5.0 it cost 40 B per
attempt (#250). The first acquire allocates a lock entry, and so does any acquire that finds the pool
empty, for example when concurrent distinct keys use up the pooled entries. The pool keeps at most 64
released entries. Both paths also log, so a log call
that allocated while its level is disabled would show up in these two rows; see #248.

There is no published head-to-head against another saga library yet.
