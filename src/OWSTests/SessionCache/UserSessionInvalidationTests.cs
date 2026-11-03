using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations;
using OWSData.Repositories.Implementations.ValKey;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using OWSPublicAPI.Services;
using StackExchange.Redis;
using Xunit;

namespace OWSTests.SessionCache
{
    public class UserSessionInvalidationTests
    {
        private static readonly TimeSpan ShortTombstone = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task LogoutWhileCircuitOpen_RemovesCachedSessionBeforeRecovery()
        {
            var (users, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                Assert.NotNull(await users.GetUserSession(customer, session));
                database.FailNextGet = true;
                Assert.Null(await cache.TryGetUserSession(customer, session)); // opens the circuit

                Assert.True((await users.Logout(customer, session)).Success);
                UserSessionRepository.CircuitReset(); // simulate the circuit cooldown ending

                Assert.Null(await users.GetUserSession(customer, session));
                Assert.Equal(0, database.SessionCount);
                Assert.Null(store.Session);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task SetRacingWithInvalidation_DoesNotRestoreRevokedSession()
        {
            var (_, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                // Invalidate at the old EXISTS/SET gap. The atomic script must check the
                // tombstone after this invalidation, within the same Redis operation.
                database.InvalidateBeforeNextSet = true;
                await cache.SetUserSession(customer, session, store.Session, TimeSpan.FromSeconds(60));

                Assert.Null(await cache.TryGetUserSession(customer, session));
                Assert.Equal(0, database.SessionCount);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task FailedInvalidation_IsReportedByLogout()
        {
            var (users, _, database, store, customer, session) = CreateRepositories();
            try
            {
                Assert.NotNull(await users.GetUserSession(customer, session));
                database.FailNextInvalidation = true;

                SuccessAndErrorMessage result = await users.Logout(customer, session);
                Assert.False(result.Success);
                Assert.Contains("cache invalidation failed", result.ErrorMessage);
                Assert.Null(store.Session);
                Assert.Equal(1, database.SessionCount);

                // The row is already gone, so logging out again only has the eviction left to do.
                Assert.True((await users.Logout(customer, session)).Success);
                Assert.Equal(0, database.SessionCount);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task LogoutWhoseDatabaseWriteFailed_StillEvictsAndReportsTheDatabaseError()
        {
            var (users, _, database, store, customer, session) = CreateRepositories();
            try
            {
                Assert.NotNull(await users.GetUserSession(customer, session));
                store.FailLogout = true;

                SuccessAndErrorMessage result = await users.Logout(customer, session);

                Assert.False(result.Success);
                Assert.Equal(SessionStoreProxy.LogoutError, result.ErrorMessage);
                Assert.Equal(0, database.SessionCount);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task RepopulateDelayedPastTheShortTombstone_DoesNotRestoreLoggedOutSession()
        {
            var (users, _, database, store, customer, session) = CreateRepositories();
            try
            {
                // The reader has its row when the logout commits, then stalls past the short tombstone.
                store.AfterNextRead = () =>
                {
                    Assert.True(users.Logout(customer, session).GetAwaiter().GetResult().Success);
                    database.AdvanceTime(ShortTombstone + TimeSpan.FromSeconds(1));
                };

                Assert.NotNull(await users.GetUserSession(customer, session));

                Assert.Equal(0, database.SessionCount);
                Assert.Null(await users.GetUserSession(customer, session));
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task RepopulateDelayedPastTheShortTombstone_DoesNotRestoreEndedEarlierSession()
        {
            var (users, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                Guid earlier = Guid.NewGuid();
                store.EarlierSessions.Add(earlier);
                GetUserSession staleRead = Session(customer, earlier, store.Session.UserGuid);

                await users.EndEarlierSessions(customer, session);
                database.AdvanceTime(ShortTombstone + TimeSpan.FromSeconds(1));
                await cache.SetUserSession(customer, earlier, staleRead, TimeSpan.FromSeconds(60));

                Assert.Null(await cache.TryGetUserSession(customer, earlier));
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task LogoutOfAnUnknownSession_LeavesOnlyAShortTombstone()
        {
            var (users, _, database, store, customer, _) = CreateRepositories();
            try
            {
                store.Session = null;

                Assert.True((await users.Logout(customer, Guid.NewGuid())).Success);

                Assert.Equal(ShortTombstone, database.LongestTombstone);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task RepeatedLogout_DoesNotShortenTheLongTombstone()
        {
            var (users, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                GetUserSession staleRead = store.Session;
                Assert.True((await users.Logout(customer, session)).Success);
                Assert.True((await users.Logout(customer, session)).Success); // the row is gone by now

                database.AdvanceTime(ShortTombstone + TimeSpan.FromSeconds(1));
                await cache.SetUserSession(customer, session, staleRead, TimeSpan.FromSeconds(60));

                Assert.Equal(0, database.SessionCount);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task CharacterSelection_BlocksRecachingOnlyBriefly()
        {
            var (users, _, database, _, customer, session) = CreateRepositories();
            try
            {
                Assert.True((await users.UserSessionSetSelectedCharacter(customer, session, "Hero")).Success);
                Assert.Equal(ShortTombstone, database.LongestTombstone);

                database.AdvanceTime(ShortTombstone + TimeSpan.FromSeconds(1));
                Assert.NotNull(await users.GetUserSession(customer, session));

                Assert.Equal(1, database.SessionCount); // the live session is cached again
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        /// <summary>
        /// The revocation tombstone on a real server: its length, that a shorter invalidation cannot cut it
        /// back, and a write delayed past the short tombstone. Runs only when OWS_TEST_VALKEY names a server.
        /// </summary>
        [ValkeyFact]
        public async Task Valkey_revocation_outlasts_the_short_tombstone_and_blocks_a_delayed_repopulate()
        {
            string prefix = $"test:{Guid.NewGuid():N}:";
            var cache = new UserSessionRepository(Microsoft.Extensions.Options.Options.Create(new UserSessionCacheOptions
            {
                Enabled = true,
                ConnectionString = ValkeyFactAttribute.ConnectionString,
                KeyPrefix = prefix,
            }));
            Guid customer = Guid.NewGuid();
            Guid session = Guid.NewGuid();
            Guid live = Guid.NewGuid();
            Guid user = Guid.NewGuid();
            GetUserSession staleRead = Session(customer, session, user);
            TimeSpan ttl = TimeSpan.FromSeconds(60);
            using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ValkeyFactAttribute.ConnectionString);
            IDatabase raw = connection.GetDatabase();
            try
            {
                await cache.SetUserSession(customer, session, staleRead, ttl);
                await cache.RevokeUserSession(customer, session, TimeSpan.FromMinutes(5));
                Assert.InRange((await raw.KeyTimeToLiveAsync($"{prefix}{customer}:{session}:invalidated")).Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

                // Neither a later brief invalidation nor a repeated logout of the deleted row shortens it.
                await cache.RemoveUserSession(customer, session);
                await cache.RevokeUserSession(customer, session, TimeSpan.Zero);
                Assert.InRange((await raw.KeyTimeToLiveAsync($"{prefix}{customer}:{session}:invalidated")).Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

                // A live session's tombstone stays short, and revoking it later still extends it.
                await cache.RemoveUserSession(customer, live);
                Assert.InRange((await raw.KeyTimeToLiveAsync($"{prefix}{customer}:{live}:invalidated")).Value, TimeSpan.FromSeconds(1), ShortTombstone);
                await cache.RevokeUserSession(customer, live, TimeSpan.FromMinutes(5));
                Assert.InRange((await raw.KeyTimeToLiveAsync($"{prefix}{customer}:{live}:invalidated")).Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

                // The writer arrives after a short tombstone would have expired.
                await Task.Delay(ShortTombstone + TimeSpan.FromSeconds(1));
                await cache.SetUserSession(customer, session, staleRead, ttl);
                Assert.Null(await cache.TryGetUserSession(customer, session));
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        /// <summary>
        /// The Lua scripts themselves, which the proxy above only models. Runs only when OWS_TEST_VALKEY
        /// names a server; CI has no Valkey, so it is skipped there.
        /// </summary>
        [ValkeyFact]
        public async Task Valkey_invalidation_removes_the_session_and_blocks_a_stale_repopulate()
        {
            var cache = new UserSessionRepository(Microsoft.Extensions.Options.Options.Create(new UserSessionCacheOptions
            {
                Enabled = true,
                ConnectionString = ValkeyFactAttribute.ConnectionString,
                KeyPrefix = $"test:{Guid.NewGuid():N}:",
            }));
            Guid customer = Guid.NewGuid();
            Guid session = Guid.NewGuid();
            Guid noUser = Guid.NewGuid();
            GetUserSession cached = Session(customer, session, Guid.NewGuid());
            TimeSpan ttl = TimeSpan.FromSeconds(60);
            try
            {
                await cache.SetUserSession(customer, session, cached, ttl);
                Assert.NotNull(await cache.TryGetUserSession(customer, session));
                await cache.SetUserSession(customer, noUser, Session(customer, noUser, null), ttl);
                Assert.NotNull(await cache.TryGetUserSession(customer, noUser));

                await cache.RemoveUserSession(customer, session);
                Assert.Null(await cache.TryGetUserSession(customer, session));

                // A reader that loaded the row before the logout must not re-cache it.
                await cache.SetUserSession(customer, session, cached, ttl);
                Assert.Null(await cache.TryGetUserSession(customer, session));
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task CharacterSelection_SucceedsWhenCacheInvalidationFails()
        {
            var (users, _, database, store, customer, session) = CreateRepositories();
            try
            {
                database.FailNextInvalidation = true;

                SuccessAndErrorMessage result = await users.UserSessionSetSelectedCharacter(customer, session, "Hero");

                Assert.True(result.Success);
                Assert.Equal("Hero", store.Session.SelectedCharacterName);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task LoginGate_AdmitsNewSessionAndAttemptsEveryEarlierEvictionOnCacheFailure()
        {
            var (users, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                Guid first = Guid.NewGuid();
                Guid second = Guid.NewGuid();
                store.EarlierSessions.Add(first);
                store.EarlierSessions.Add(second);
                await cache.SetUserSession(customer, first, Session(customer, first, store.Session.UserGuid), TimeSpan.FromSeconds(60));
                await cache.SetUserSession(customer, second, Session(customer, second, store.Session.UserGuid), TimeSpan.FromSeconds(60));
                database.FailNextInvalidation = true;

                (string result, _) = await CreateLoginGate(users).AdmitNewSession(customer, session);

                Assert.Null(result);
                Assert.Equal(2, database.InvalidationAttempts);
                Assert.Equal(2, store.EndedCount);
                Assert.NotNull(store.Session);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task LoginGate_RefusalKeepsItsMessageWhenLogoutCacheFails()
        {
            var (users, _, database, store, customer, session) = CreateRepositories();
            try
            {
                database.FailNextInvalidation = true;

                (string result, string message) = await CreateLoginGate(users, closed: true).AdmitNewSession(customer, session);

                Assert.Equal("maintenance", result);
                Assert.Equal(LoginGate.MaintenanceMessage, message);
                Assert.Null(store.Session);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task UserTombstoneBlocksSetAndUserIndexEnablesRoleEviction()
        {
            var (users, cache, database, store, customer, session) = CreateRepositories();
            try
            {
                Guid second = Guid.NewGuid();
                Assert.NotNull(await users.GetUserSession(customer, session));
                await cache.SetUserSession(customer, second,
                    Session(customer, second, store.Session.UserGuid), TimeSpan.FromSeconds(60));
                Assert.Equal(2, database.IndexedSessionCount);

                await users.UpdateUserRole(customer, store.Session.UserGuid.Value, UserRoles.Player);
                Assert.Null(await cache.TryGetUserSession(customer, session));
                Assert.Null(await cache.TryGetUserSession(customer, second));

                await cache.SetUserSession(customer, session, store.Session, TimeSpan.FromSeconds(60));
                Assert.Null(await cache.TryGetUserSession(customer, session));
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        [Fact]
        public async Task SessionWithoutUserGuid_CanBeCachedWithoutUserIndex()
        {
            var (_, cache, database, _, customer, session) = CreateRepositories();
            try
            {
                await cache.SetUserSession(customer, session,
                    Session(customer, session, null), TimeSpan.FromSeconds(60));

                Assert.NotNull(await cache.TryGetUserSession(customer, session));
                Assert.Equal(0, database.IndexedSessionCount);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
        }

        private static GetUserSession Session(Guid customer, Guid session, Guid? user) => new GetUserSession
        {
            CustomerGuid = customer,
            UserSessionGUID = session,
            UserGuid = user,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        private static LoginGate CreateLoginGate(IUsersRepository users, bool closed = false)
        {
            var sanctions = DispatchProxy.Create<ISanctionsRepository, SanctionsProxy>();
            var settings = DispatchProxy.Create<IConsoleSettingsRepository, SettingsProxy>();
            ((SettingsProxy)settings).Closed = closed;
            return new LoginGate(sanctions, settings, users);
        }

        private static (CachingUsersRepository users, UserSessionRepository cache,
            CacheDatabaseProxy database, SessionStoreProxy store, Guid customer, Guid session)
            CreateRepositories()
        {
            Guid customer = Guid.NewGuid();
            Guid session = Guid.NewGuid();
            var options = Microsoft.Extensions.Options.Options.Create(
                new UserSessionCacheOptions { Enabled = true, SessionTtlSeconds = 60 });
            IDatabase databaseInterface = DispatchProxy.Create<IDatabase, CacheDatabaseProxy>();
            var database = (CacheDatabaseProxy)databaseInterface;
            var cache = new UserSessionRepository(options, databaseInterface);
            IUsersRepository storeInterface = DispatchProxy.Create<IUsersRepository, SessionStoreProxy>();
            var store = (SessionStoreProxy)storeInterface;
            store.Session = Session(customer, session, Guid.NewGuid());
            return (new CachingUsersRepository(storeInterface, cache, options), cache, database, store, customer, session);
        }

        public class CacheDatabaseProxy : DispatchProxy
        {
            private readonly Dictionary<string, string> _values = new();
            private readonly Dictionary<string, TimeSpan> _tombstones = new(); // key -> expiry on _now
            private TimeSpan _now;
            private readonly Dictionary<string, HashSet<string>> _indexes = new();

            public bool FailNextGet { get; set; }
            public bool FailNextInvalidation { get; set; }
            public bool InvalidateBeforeNextSet { get; set; }
            public int SessionCount => _values.Count;
            public int IndexedSessionCount
            {
                get
                {
                    int count = 0;
                    foreach (HashSet<string> members in _indexes.Values) count += members.Count;
                    return count;
                }
            }
            public int InvalidationAttempts { get; private set; }
            public TimeSpan LongestTombstone { get; private set; }

            public void AdvanceTime(TimeSpan by) => _now += by;

            private bool TombstoneLive(string key) => _tombstones.TryGetValue(key, out TimeSpan expiry) && expiry > _now;

            // Extends only, as the invalidation script does.
            private void WriteTombstone(string key, TimeSpan ttl)
            {
                if (!_tombstones.TryGetValue(key, out TimeSpan expiry) || expiry < _now + ttl)
                {
                    _tombstones[key] = _now + ttl;
                }
                if (ttl > LongestTombstone)
                {
                    LongestTombstone = ttl;
                }
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                string key = args.Length > 0 ? args[0].ToString() : null;
                switch (targetMethod.Name)
                {
                    case "StringGetAsync":
                        if (FailNextGet)
                        {
                            FailNextGet = false;
                            throw new RedisException("Simulated cache blip");
                        }
                        return Task.FromResult(_values.TryGetValue(key, out string value) ? (RedisValue)value : RedisValue.Null);
                    case "ScriptEvaluateAsync":
                        var keys = (RedisKey[])args[1];
                        string sessionKey = keys[0].ToString();
                        string tombstoneKey = keys[1].ToString();
                        if (targetMethod.GetParameters()[0].ParameterType == typeof(string)
                            && ((string)args[0]).Contains("EXISTS"))
                        {
                            InvalidateBeforeSet(sessionKey, tombstoneKey);
                            RedisValue[] scriptArgs = (RedisValue[])args[2];
                            bool hasUser = scriptArgs[3].ToString() == "1";
                            string userTombstoneKey = keys[2].ToString();
                            if (!TombstoneLive(tombstoneKey)
                                && (!hasUser || !TombstoneLive(userTombstoneKey)))
                            {
                                _values[sessionKey] = scriptArgs[0].ToString();
                                if (hasUser)
                                {
                                    string indexKey = keys[3].ToString();
                                    if (!_indexes.TryGetValue(indexKey, out HashSet<string> members))
                                    {
                                        members = new HashSet<string>();
                                        _indexes[indexKey] = members;
                                    }
                                    members.Add(scriptArgs[2].ToString());
                                }
                            }
                        }
                        else
                        {
                            Invalidate(sessionKey, tombstoneKey, TimeSpan.FromMilliseconds((long)((RedisValue[])args[2])[0]));
                        }
                        return Task.FromResult<RedisResult>(null);
                    // These paths model the old separate operations, so this test fails
                    // against the implementation before the atomic-script change.
                    case "KeyExistsAsync":
                        return Task.FromResult(TombstoneLive(key));
                    case "StringSetAsync":
                        if (key.EndsWith(":invalidated"))
                        {
                            WriteTombstone(key, ShortTombstone);
                        }
                        else
                        {
                            InvalidateBeforeSet(key, key + ":invalidated");
                            _values[key] = args[1].ToString();
                        }
                        return Task.FromResult(true);
                    case "KeyDeleteAsync":
                        if (FailNextInvalidation)
                        {
                            FailNextInvalidation = false;
                            throw new RedisException("Simulated invalidation failure");
                        }
                        return Task.FromResult(_values.Remove(key) || _indexes.Remove(key));
                    case "SetMembersAsync":
                        if (!_indexes.TryGetValue(key, out HashSet<string> indexed))
                        {
                            return Task.FromResult(Array.Empty<RedisValue>());
                        }
                        var membersResult = new RedisValue[indexed.Count];
                        int memberIndex = 0;
                        foreach (string member in indexed) membersResult[memberIndex++] = member;
                        return Task.FromResult(membersResult);
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }

            private void InvalidateBeforeSet(string sessionKey, string tombstoneKey)
            {
                if (!InvalidateBeforeNextSet) return;
                InvalidateBeforeNextSet = false;
                WriteTombstone(tombstoneKey, ShortTombstone);
                _values.Remove(sessionKey);
            }

            private void Invalidate(string sessionKey, string tombstoneKey, TimeSpan ttl)
            {
                InvalidationAttempts++;
                if (FailNextInvalidation)
                {
                    FailNextInvalidation = false;
                    throw new RedisException("Simulated invalidation failure");
                }
                WriteTombstone(tombstoneKey, ttl);
                _values.Remove(sessionKey);
            }
        }

        public class SessionStoreProxy : DispatchProxy
        {
            public const string LogoutError = "Simulated database failure";

            public GetUserSession Session { get; set; }
            public bool FailLogout { get; set; }
            public Action AfterNextRead { get; set; }
            public List<Guid> EarlierSessions { get; } = new();
            public int EndedCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "GetUserSession":
                        GetUserSession read = Session;
                        Action afterRead = AfterNextRead;
                        AfterNextRead = null;
                        afterRead?.Invoke();
                        return Task.FromResult(read);
                    case "Logout":
                        if (FailLogout)
                        {
                            return Task.FromResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = LogoutError });
                        }
                        Session = null;
                        return Task.FromResult(new SuccessAndErrorMessage { Success = true });
                    case "UserSessionSetSelectedCharacter":
                        Session.SelectedCharacterName = (string)args[2];
                        return Task.FromResult(new SuccessAndErrorMessage { Success = true });
                    case "EndEarlierSessions":
                        EndedCount = EarlierSessions.Count;
                        return Task.FromResult<IReadOnlyList<Guid>>(EarlierSessions.ToArray());
                    case "UpdateUserRole":
                        Session.Role = (string)args[2];
                        return Task.FromResult(new SuccessAndErrorMessage { Success = true });
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }
        }

        public class SanctionsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
                => targetMethod.Name == "GetActive"
                    ? Task.FromResult(new ActiveSanctions())
                    : throw new NotSupportedException(targetMethod.Name);
        }

        public class SettingsProxy : DispatchProxy
        {
            public bool Closed { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name != "GetAll") throw new NotSupportedException(targetMethod.Name);
                IReadOnlyDictionary<string, string> settings = Closed
                    ? new Dictionary<string, string> { [ConsoleSettingKeys.LoginsOpen] = "false" }
                    : new Dictionary<string, string>();
                return Task.FromResult(settings);
            }
        }
    }
}
