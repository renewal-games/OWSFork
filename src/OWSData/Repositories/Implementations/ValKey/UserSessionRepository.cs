using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using StackExchange.Redis;

namespace OWSData.Repositories.Implementations.ValKey
{
    /// <summary>
    /// Valkey/Redis-backed session cache. Reads and writes are best-effort. Per-session
    /// invalidation reports failures so callers can decide whether their operation may succeed.
    /// </summary>
    public class UserSessionRepository : IUserSessionRepository
    {
        private static readonly ConcurrentDictionary<string, Lazy<ConnectionMultiplexer>> Connections = new();

        // Circuit breaker: after a cache op fails, skip the cache entirely for a cooldown so a
        // Valkey outage costs at most one slow (timed-out) call per window instead of one per
        // request. Shared across all repo instances in the process.
        private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(10);
        private static long _skipCacheUntilTicks;

        // When a session is invalidated (selected-character change / role change) a short-lived
        // tombstone is written next to the deleted key. A concurrent reader that fetched the
        // DB row just BEFORE the invalidation committed is blocked from re-populating the cache
        // with the now-stale session while the tombstone is live. A revoked session's tombstone
        // lasts for its remaining lifetime instead (RevokeUserSession), so no delay lets a reader
        // re-cache it: past its ExpiresAt a cached copy is no longer served.
        private static readonly TimeSpan InvalidationTombstoneTtl = TimeSpan.FromSeconds(10);

        private const string SetSessionScript = @"
if redis.call('EXISTS', KEYS[2]) == 1 then return 0 end
if ARGV[4] == '1' and redis.call('EXISTS', KEYS[3]) == 1 then return 0 end
if tonumber(ARGV[2]) > 0 then
    redis.call('PSETEX', KEYS[1], ARGV[2], ARGV[1])
else
    redis.call('SET', KEYS[1], ARGV[1])
end
if ARGV[4] == '1' then
    local oldIndexTtl = redis.call('PTTL', KEYS[4])
    redis.call('SADD', KEYS[4], ARGV[3])
    if tonumber(ARGV[2]) > 0 then
        if oldIndexTtl < tonumber(ARGV[2]) then
            redis.call('PEXPIRE', KEYS[4], ARGV[2])
        end
    else
        redis.call('PERSIST', KEYS[4])
    end
end
return 1";

        // Only ever extends the tombstone: a repeated logout or a late selected-character change
        // must not cut a revoked session's tombstone back to the short one.
        private const string InvalidateSessionScript = @"
if redis.call('PTTL', KEYS[2]) < tonumber(ARGV[1]) then
    redis.call('PSETEX', KEYS[2], ARGV[1], '1')
end
redis.call('DEL', KEYS[1])
return 1";

        private readonly ConfigurationOptions _configurationOptions;
        private readonly string _connectionKey;
        private readonly int _databaseIndex;
        private readonly string _keyPrefix;
        private readonly IDatabase _databaseOverride;

        public UserSessionRepository(IOptions<UserSessionCacheOptions> options) : this(options, null)
        {
        }

        internal UserSessionRepository(IOptions<UserSessionCacheOptions> options, IDatabase database)
        {
            UserSessionCacheOptions optionsValue = options?.Value ?? new UserSessionCacheOptions();
            _configurationOptions = BuildConfigurationOptions(optionsValue);
            _connectionKey = _configurationOptions.ToString(true);
            _databaseIndex = optionsValue.Database;
            _keyPrefix = string.IsNullOrWhiteSpace(optionsValue.KeyPrefix)
                ? UserSessionCacheOptions.DefaultKeyPrefix
                : optionsValue.KeyPrefix;
            _databaseOverride = database;
        }

        private IDatabase Database => _databaseOverride ?? Connections
            .GetOrAdd(_connectionKey, _ => new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(_configurationOptions)))
            .Value
            .GetDatabase(_databaseIndex);

        public async Task<GetUserSession> TryGetUserSession(Guid customerGuid, Guid userSessionGuid)
        {
            string key = ConstructUserSessionKey(customerGuid, userSessionGuid);
            if (key == null || CircuitOpen)
            {
                return null;
            }

            try
            {
                RedisValue userSessionJson = await Database.StringGetAsync(key);
                CircuitReset();
                if (userSessionJson.IsNullOrEmpty)
                {
                    return null;
                }

                return JsonSerializer.Deserialize<GetUserSession>(userSessionJson.ToString());
            }
            catch (Exception)
            {
                // Cache unreachable or value unreadable — trip the breaker and treat as a miss.
                CircuitTrip();
                return null;
            }
        }

        public async Task SetUserSession(Guid customerGuid, Guid userSessionGuid, GetUserSession userSession, TimeSpan timeToLive)
        {
            if (userSession == null)
            {
                return;
            }

            string key = ConstructUserSessionKey(customerGuid, userSessionGuid);
            if (key == null || CircuitOpen)
            {
                return;
            }

            try
            {
                string userSessionJson = JsonSerializer.Serialize(userSession);
                long ttlMilliseconds = timeToLive > TimeSpan.Zero
                    ? (long)Math.Max(1, Math.Ceiling(timeToLive.TotalMilliseconds))
                    : 0;
                // Session and user tombstone checks, the cache write, and the user-index update
                // are one operation: a logout or role change can run between separate calls.
                // The index lets a user-level change find this session (RemoveUserSessionsForUser).
                string userIndexKey = userSession.UserGuid.HasValue
                    ? ConstructUserIndexKey(customerGuid, userSession.UserGuid.Value)
                    : null;
                await Database.ScriptEvaluateAsync(SetSessionScript,
                    new RedisKey[] { key, TombstoneKey(key),
                        userIndexKey == null ? key : TombstoneKey(userIndexKey),
                        userIndexKey ?? key },
                    new RedisValue[] { userSessionJson, ttlMilliseconds, userSessionGuid.ToString(),
                        userIndexKey == null ? 0 : 1 });
                CircuitReset();
            }
            catch (Exception)
            {
                // Best-effort write; a failure just means the next read is a cache miss.
                CircuitTrip();
            }
        }

        public Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid)
            => InvalidateUserSession(customerGuid, userSessionGuid, InvalidationTombstoneTtl);

        public Task RevokeUserSession(Guid customerGuid, Guid userSessionGuid, TimeSpan remainingLifetime)
            => InvalidateUserSession(customerGuid, userSessionGuid,
                remainingLifetime > InvalidationTombstoneTtl ? remainingLifetime : InvalidationTombstoneTtl);

        private async Task InvalidateUserSession(Guid customerGuid, Guid userSessionGuid, TimeSpan tombstoneTtl)
        {
            string key = ConstructUserSessionKey(customerGuid, userSessionGuid);
            if (key == null)
            {
                return;
            }

            try
            {
                // Invalidation is attempted even while the read/write circuit is open. A
                // successful script makes the tombstone and deletion indivisible to readers.
                await Database.ScriptEvaluateAsync(InvalidateSessionScript,
                    new RedisKey[] { key, TombstoneKey(key) },
                    new RedisValue[] { (long)Math.Ceiling(tombstoneTtl.TotalMilliseconds) });
                CircuitReset();
            }
            catch (Exception ex)
            {
                CircuitTrip();
                throw new InvalidOperationException("Could not invalidate the cached user session.", ex);
            }
        }

        public async Task RemoveUserSessionsForUser(Guid customerGuid, Guid userGuid)
        {
            string userIndexKey = ConstructUserIndexKey(customerGuid, userGuid);
            if (userIndexKey == null || CircuitOpen)
            {
                return;
            }

            try
            {
                // Tombstone first, so a reader that loaded one of this user's sessions from the DB
                // before the change committed cannot re-cache it while the members are deleted.
                await Database.StringSetAsync(TombstoneKey(userIndexKey), "1", InvalidationTombstoneTtl);

                RedisValue[] sessionGuids = await Database.SetMembersAsync(userIndexKey);
                foreach (RedisValue sessionGuid in sessionGuids)
                {
                    if (Guid.TryParse(sessionGuid.ToString(), out Guid userSessionGuid))
                    {
                        await Database.KeyDeleteAsync(ConstructUserSessionKey(customerGuid, userSessionGuid));
                    }
                }
                await Database.KeyDeleteAsync(userIndexKey);
                CircuitReset();
            }
            catch (Exception)
            {
                // Best-effort invalidation; the session TTL bounds staleness if this fails.
                CircuitTrip();
            }
        }

        private string TombstoneKey(string sessionKey) => sessionKey + ":invalidated";

        private static bool CircuitOpen => DateTime.UtcNow.Ticks < Interlocked.Read(ref _skipCacheUntilTicks);

        private static void CircuitTrip() => Interlocked.Exchange(ref _skipCacheUntilTicks, DateTime.UtcNow.Add(CircuitCooldown).Ticks);

        internal static void CircuitReset() => Interlocked.Exchange(ref _skipCacheUntilTicks, 0);

        private string ConstructUserSessionKey(Guid customerGuid, Guid userSessionGuid)
        {
            if (customerGuid == Guid.Empty || userSessionGuid == Guid.Empty)
            {
                return null;
            }

            return $"{_keyPrefix}{customerGuid}:{userSessionGuid}";
        }

        // The set of a user's cached session GUIDs. The "user:" segment keeps it apart from session
        // keys, whose segment after the prefix is always a GUID.
        private string ConstructUserIndexKey(Guid customerGuid, Guid userGuid)
        {
            if (customerGuid == Guid.Empty || userGuid == Guid.Empty)
            {
                return null;
            }

            return $"{_keyPrefix}user:{customerGuid}:{userGuid}";
        }

        private static ConfigurationOptions BuildConfigurationOptions(UserSessionCacheOptions options)
        {
            string connectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
                ? UserSessionCacheOptions.DefaultConnectionString
                : options.ConnectionString;

            ConfigurationOptions configurationOptions = ConfigurationOptions.Parse(connectionString);
            configurationOptions.AbortOnConnectFail = false;
            // Fail fast when the cache is unreachable so a request degrades to the DB quickly
            // instead of blocking on connect/command timeouts. The circuit breaker then skips
            // the cache entirely for a cooldown, so only one request per window pays this cost.
            configurationOptions.ConnectTimeout = 1000;
            configurationOptions.SyncTimeout = 1000;
            configurationOptions.AsyncTimeout = 1000;
            configurationOptions.ConnectRetry = 1;

            if (!string.IsNullOrWhiteSpace(options.Password))
            {
                configurationOptions.Password = options.Password;
            }

            return configurationOptions;
        }
    }
}
