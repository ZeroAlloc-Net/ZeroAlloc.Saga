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
dotnet run --project benchmarks/ZeroAlloc.Saga.Benchmarks -c Release
```

There is no published head-to-head against another saga library yet.
