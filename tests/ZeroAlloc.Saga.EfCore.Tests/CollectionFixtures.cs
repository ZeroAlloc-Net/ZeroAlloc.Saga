using Xunit;

namespace ZeroAlloc.Saga.EfCore.Tests;

/// <summary>
/// xUnit collection that serializes tests touching the process-wide
/// <see cref="SagaStoreRegistrar"/> static state. Tests that call
/// <c>WithEfCoreStore&lt;TContext&gt;()</c> mutate that static and would
/// race each other under xUnit's default per-class parallelism.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EfCoreStaticStateCollection
{
    public const string Name = "EfCoreStaticState";
}
