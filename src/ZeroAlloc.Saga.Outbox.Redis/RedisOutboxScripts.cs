namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// The Lua scripts behind <see cref="RedisOutboxStore"/>'s claim, renew, release and marks. Each
/// runs atomically on the Redis server. <see cref="StackExchange.Redis.IDatabaseAsync.ScriptEvaluateAsync(string, StackExchange.Redis.RedisKey[], StackExchange.Redis.RedisValue[], StackExchange.Redis.CommandFlags)"/>
/// sends a script's first call as EVAL and loads it with SCRIPT LOAD, sends EVALSHA afterwards,
/// and falls back to EVAL and reloads it when the server answers NOSCRIPT, for example after a
/// restart or SCRIPT FLUSH.
/// </summary>
/// <remarks>
/// <para>
/// Entry hash fields: typeName, payload, retryCount, status, createdAt, nextRetryAt, processedAt,
/// error, lockedBy, lockedUntil. Times are unix milliseconds from the caller's clock. Status is
/// Pending, Succeeded or DeadLetter; Saga 3.x also wrote Failed for an entry awaiting retry, which the
/// claim reads as Pending.
/// </para>
/// <para>
/// A pending entry's score in the pending sorted set is its lockedUntil while leased, and its due
/// time otherwise. <c>ZRANGEBYSCORE -inf now</c> therefore returns exactly the entries that are due
/// and unleased, and an expired lease is due again with no sweeper.
/// </para>
/// <para>
/// Every key a script touches comes from KEYS, never from ARGV. A key-prefixed
/// <see cref="StackExchange.Redis.IDatabase"/>, from <c>WithKeyPrefix</c>, prefixes KEYS but not
/// ARGV, so a key built from ARGV would miss the prefixed data. The claim cannot know its ids up
/// front, so it receives the entry-key prefix as a key and appends each id to it.
/// </para>
/// </remarks>
internal static class RedisOutboxScripts
{
    /// <summary>
    /// KEYS[1] pending set, KEYS[2] entry key prefix "{P}:entry:". ARGV[1] now, ARGV[2] batch
    /// size, ARGV[3] host id, ARGV[4] lease expiry. Returns a flat array with five values per
    /// claimed entry: id, typeName and payload as bulk strings, then retryCount and createdAt as
    /// integers. A missing or non-numeric retryCount or createdAt is returned as 0, so one corrupt
    /// entry cannot fail the whole leased batch on the client.
    /// </summary>
    internal const string Claim = """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[2]))
        local claimed = {}
        for _, id in ipairs(ids) do
          local key = KEYS[2] .. id
          local f = redis.call('HMGET', key, 'status', 'typeName', 'payload', 'retryCount', 'createdAt')
          if f[1] == 'Pending' or f[1] == 'Failed' then
            redis.call('HSET', key, 'status', 'Pending', 'lockedBy', ARGV[3], 'lockedUntil', ARGV[4])
            redis.call('ZADD', KEYS[1], ARGV[4], id)
            claimed[#claimed + 1] = id
            claimed[#claimed + 1] = f[2] or ''
            claimed[#claimed + 1] = f[3] or ''
            claimed[#claimed + 1] = tonumber(f[4]) or 0
            claimed[#claimed + 1] = tonumber(f[5]) or 0
          else
            redis.call('ZREM', KEYS[1], id)
          end
        end
        return claimed
        """;

    /// <summary>
    /// KEYS[1] entry hash, KEYS[2] pending set. ARGV[1] id, ARGV[2] host id, ARGV[3] now,
    /// ARGV[4] new lease expiry. Returns 1 when this host held an unexpired lease on the pending
    /// entry and it was extended, else 0.
    /// </summary>
    internal const string Renew = """
        local f = redis.call('HMGET', KEYS[1], 'status', 'lockedBy', 'lockedUntil')
        if f[1] ~= 'Pending' or f[2] ~= ARGV[2] or not f[3] or tonumber(f[3]) < tonumber(ARGV[3]) then
          return 0
        end
        redis.call('HSET', KEYS[1], 'lockedUntil', ARGV[4])
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[1])
        return 1
        """;

    /// <summary>
    /// KEYS[1] pending set, KEYS[2..n] entry hashes. ARGV[1] host id, ARGV[2] now, ARGV[3..n]
    /// ids, where ARGV[i + 1] is the id of KEYS[i]. Clears this host's lease on each pending id it
    /// holds and makes it due now. Returns the number released.
    /// </summary>
    internal const string Release = """
        local released = 0
        for i = 2, #KEYS do
          local key = KEYS[i]
          local id = ARGV[i + 1]
          local f = redis.call('HMGET', key, 'status', 'lockedBy')
          if f[1] == 'Pending' and f[2] == ARGV[1] then
            redis.call('HDEL', key, 'lockedBy', 'lockedUntil')
            redis.call('ZADD', KEYS[1], ARGV[2], id)
            released = released + 1
          end
        end
        return released
        """;

    /// <summary>
    /// KEYS[1] entry hash, KEYS[2] pending set, KEYS[3] succeeded set. ARGV[1] id, ARGV[2] host
    /// id, ARGV[3] processedAt. Returns 1 when the entry was pending and leased by this host,
    /// with no expiry check, and is now succeeded, else 0.
    /// </summary>
    internal const string MarkSucceeded = """
        local f = redis.call('HMGET', KEYS[1], 'status', 'lockedBy')
        if f[1] ~= 'Pending' or f[2] ~= ARGV[2] then
          return 0
        end
        redis.call('HSET', KEYS[1], 'status', 'Succeeded', 'processedAt', ARGV[3])
        redis.call('HDEL', KEYS[1], 'lockedBy', 'lockedUntil')
        redis.call('ZREM', KEYS[2], ARGV[1])
        redis.call('SADD', KEYS[3], ARGV[1])
        return 1
        """;

    /// <summary>
    /// KEYS[1] entry hash, KEYS[2] pending set. ARGV[1] id, ARGV[2] host id, ARGV[3] retryCount,
    /// ARGV[4] nextRetryAt. Keeps the entry pending, records the attempt, clears the lease and
    /// makes it due at nextRetryAt. Returns 1 when this host held the lease, else 0.
    /// </summary>
    internal const string MarkFailed = """
        local f = redis.call('HMGET', KEYS[1], 'status', 'lockedBy')
        if f[1] ~= 'Pending' or f[2] ~= ARGV[2] then
          return 0
        end
        redis.call('HSET', KEYS[1], 'retryCount', ARGV[3], 'nextRetryAt', ARGV[4])
        redis.call('HDEL', KEYS[1], 'lockedBy', 'lockedUntil')
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[1])
        return 1
        """;

    /// <summary>
    /// KEYS[1] entry hash, KEYS[2] pending set, KEYS[3] dead-letter set. ARGV[1] id, ARGV[2] host
    /// id, ARGV[3] processedAt, ARGV[4] error. Returns 1 when this host held the lease and the
    /// entry is now dead-lettered, else 0.
    /// </summary>
    internal const string DeadLetter = """
        local f = redis.call('HMGET', KEYS[1], 'status', 'lockedBy')
        if f[1] ~= 'Pending' or f[2] ~= ARGV[2] then
          return 0
        end
        redis.call('HSET', KEYS[1], 'status', 'DeadLetter', 'error', ARGV[4], 'processedAt', ARGV[3])
        redis.call('HDEL', KEYS[1], 'lockedBy', 'lockedUntil')
        redis.call('ZREM', KEYS[2], ARGV[1])
        redis.call('SADD', KEYS[3], ARGV[1])
        return 1
        """;
}
