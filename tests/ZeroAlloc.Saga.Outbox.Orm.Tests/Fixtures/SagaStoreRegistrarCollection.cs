namespace ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

/// <summary>
/// Serializes the tests that pick a saga store. <c>WithEfCoreStore</c>, <c>WithOrmStore</c> and
/// <c>WithRedisStore</c> install a process-wide <see cref="SagaStoreRegistrar"/>, and the
/// generator-emitted <c>With{Saga}()</c> registers whichever store that registrar selects. Two
/// hosts built in parallel would register each other's store.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SagaStoreRegistrarCollection
{
    public const string Name = "SagaStoreRegistrar";
}
