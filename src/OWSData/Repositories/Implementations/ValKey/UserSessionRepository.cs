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
    /// Valkey/Redis-backed session cache. Every operation is best-effort: connection or
    /// serialization failures are swallowed so a cache outage degrades to the database
    /// instead of breaking authentication.
    /// </summary>
    public class UserSessionRepository : IUserSessionRepository
    {
        private static readonly ConcurrentDictionary<string, Lazy<ConnectionMultiplexer>> Connections = new();

        // Circuit breaker: after a cache op fails, skip the cache entirely for a cooldown so a
        // Valkey outage costs at most one slow (timed-out) call per window instead of one per
        // request. Shared across all repo instances in the process.
        private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(10);
        private static long _skipCacheUntilTicks;

        // When a session is invalidated (logout / selected-character change / role change) a short-lived
        // tombstone is written next to the deleted key. A concurrent reader that fetched the
        // DB row just BEFORE the invalidation committed is blocked from re-populating the cache
        // with the now-stale session while the tombstone is live — closing the read-repopulate
        // race that a plain delete leaves open.
        private static readonly TimeSpan InvalidationTombstoneTtl = TimeSpan.FromSeconds(10);

        private readonly ConfigurationOptions _configurationOptions;
        private readonly string _connectionKey;
        private readonly int _databaseIndex;
        private readonly string _keyPrefix;

        public UserSessionRepository(IOptions<UserSessionCacheOptions> options)
        {
            UserSessionCacheOptions optionsValue = options?.Value ?? new UserSessionCacheOptions();
            _configurationOptions = BuildConfigurationOptions(optionsValue);
            _connectionKey = _configurationOptions.ToString(true);
            _databaseIndex = optionsValue.Database;
            _keyPrefix = string.IsNullOrWhiteSpace(optionsValue.KeyPrefix)
                ? UserSessionCacheOptions.DefaultKeyPrefix
                : optionsValue.KeyPrefix;
        }

        private IDatabase Database => Connections
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
                // Do not re-populate if an invalidation tombstone is live: this caller may have
                // read the session from the DB just before a concurrent logout/char-change/role
                // change, so caching it now would serve a stale-but-"valid" session until the TTL.
                string userIndexKey = userSession.UserGuid.HasValue ? ConstructUserIndexKey(customerGuid, userSession.UserGuid.Value) : null;
                if (await Database.KeyExistsAsync(TombstoneKey(key))
                    || (userIndexKey != null && await Database.KeyExistsAsync(TombstoneKey(userIndexKey))))
                {
                    CircuitReset();
                    return;
                }

                TimeSpan? expiry = timeToLive > TimeSpan.Zero ? timeToLive : null;
                string userSessionJson = JsonSerializer.Serialize(userSession);
                await Database.StringSetAsync(key, userSessionJson, expiry);

                // Index the session under its user so a user-level change (role) can find and
                // evict it (RemoveUserSessionsForUser). Every session shares one TTL, so the index
                // expiring with its newest member outlives all of them.
                if (userIndexKey != null)
                {
                    await Database.SetAddAsync(userIndexKey, userSessionGuid.ToString());
                    if (expiry.HasValue)
                    {
                        await Database.KeyExpireAsync(userIndexKey, expiry);
                    }
                }
                CircuitReset();
            }
            catch (Exception)
            {
                // Best-effort write; a failure just means the next read is a cache miss.
                CircuitTrip();
            }
        }

        public async Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid)
        {
            string key = ConstructUserSessionKey(customerGuid, userSessionGuid);
            if (key == null || CircuitOpen)
            {
                return;
            }

            try
            {
                await Database.KeyDeleteAsync(key);
                // Tombstone blocks a straddling reader from re-caching the stale session (see
                // InvalidationTombstoneTtl). Written after the delete so it always outlives it.
                await Database.StringSetAsync(TombstoneKey(key), "1", InvalidationTombstoneTtl);
                CircuitReset();
            }
            catch (Exception)
            {
                // Best-effort invalidation; the short TTL bounds staleness if this fails.
                CircuitTrip();
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

        private static void CircuitReset() => Interlocked.Exchange(ref _skipCacheUntilTicks, 0);

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
