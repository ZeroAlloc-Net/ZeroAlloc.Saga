namespace ZeroAlloc.Saga.Outbox.Orm;

/// <summary>
/// Per-scope buffer for the outbox rows a saga step enlists. They are written by
/// <see cref="OrmOutboxTransactionContributor"/> inside the ORM saga store's transaction, at the
/// next <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/> or
/// <see cref="ISagaStore{TSaga,TKey}.RemoveAsync"/>, so they commit or roll back with the saga row.
/// </summary>
/// <remarks>
/// <para>
/// Nothing reaches the database when a row is enlisted. That is what makes a failed save discard
/// it: the rows exist only here until the saga store's transaction writes them, and a conflict
/// rolls that transaction back. The generated handler retries in a fresh scope, so the retry
/// starts with an empty buffer.
/// </para>
/// <para>
/// One saga handler per scope at a time, as for the Redis unit of work. The buffer is locked, so
/// enlisting and draining are each thread-safe, but two handlers saving concurrently in one scope
/// could drain each other's rows into the wrong transaction.
/// </para>
/// </remarks>
internal sealed class OrmSagaUnitOfWork : ISagaUnitOfWork
{
    private readonly List<PendingWrite> _pending = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        // Copied: the dispatcher's serializer may reuse or pool the buffer once this returns, and
        // the row is written only at the saga store's commit.
        var entry = new PendingWrite(typeName, payload.ToArray());
        lock (_gate)
        {
            _pending.Add(entry);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes and returns every buffered row, oldest first.</summary>
    internal PendingWrite[] Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];

            var snapshot = _pending.ToArray();
            _pending.Clear();
            return snapshot;
        }
    }

    /// <summary>An enlisted outbox row awaiting the saga store's transaction.</summary>
    internal readonly record struct PendingWrite(string TypeName, byte[] Payload);
}
