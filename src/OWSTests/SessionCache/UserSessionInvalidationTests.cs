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
            private readonly HashSet<string> _tombstones = new();
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
                            if (!_tombstones.Contains(tombstoneKey)
                                && (!hasUser || !_tombstones.Contains(userTombstoneKey)))
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
                            Invalidate(sessionKey, tombstoneKey);
                        }
                        return Task.FromResult<RedisResult>(null);
                    // These paths model the old separate operations, so this test fails
                    // against the implementation before the atomic-script change.
                    case "KeyExistsAsync":
                        return Task.FromResult(_tombstones.Contains(key));
                    case "StringSetAsync":
                        if (key.EndsWith(":invalidated"))
                        {
                            _tombstones.Add(key);
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
                _tombstones.Add(tombstoneKey);
                _values.Remove(sessionKey);
            }

            private void Invalidate(string sessionKey, string tombstoneKey)
            {
                InvalidationAttempts++;
                if (FailNextInvalidation)
                {
                    FailNextInvalidation = false;
                    throw new RedisException("Simulated invalidation failure");
                }
                _tombstones.Add(tombstoneKey);
                _values.Remove(sessionKey);
            }
        }

        public class SessionStoreProxy : DispatchProxy
        {
            public const string LogoutError = "Simulated database failure";

            public GetUserSession Session { get; set; }
            public bool FailLogout { get; set; }
            public List<Guid> EarlierSessions { get; } = new();
            public int EndedCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "GetUserSession":
                        return Task.FromResult(Session);
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
