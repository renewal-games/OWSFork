using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations;
using OWSData.Repositories.Implementations.ValKey;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using StackExchange.Redis;
using Xunit;

namespace OWSTests.Cache
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

                await users.Logout(customer, session);
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

                await Assert.ThrowsAsync<InvalidOperationException>(() => users.Logout(customer, session));
                Assert.Null(store.Session);
            }
            finally
            {
                UserSessionRepository.CircuitReset();
            }
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
            store.Session = new GetUserSession { CustomerGuid = customer, UserSessionGUID = session, UserGuid = Guid.NewGuid() };
            // Newer session models carry an expiration used by the cache decorator.
            typeof(GetUserSession).GetProperty("ExpiresAt")?.SetValue(store.Session, DateTime.UtcNow.AddMinutes(5));
            return (new CachingUsersRepository(storeInterface, cache, options), cache, database, store, customer, session);
        }

        public class CacheDatabaseProxy : DispatchProxy
        {
            private readonly Dictionary<string, string> _values = new();
            private readonly HashSet<string> _tombstones = new();

            public bool FailNextGet { get; set; }
            public bool FailNextInvalidation { get; set; }
            public bool InvalidateBeforeNextSet { get; set; }
            public int SessionCount => _values.Count;

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
                            if (!_tombstones.Contains(tombstoneKey))
                            {
                                _values[sessionKey] = ((RedisValue[])args[2])[0].ToString();
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
                        return Task.FromResult(_values.Remove(key));
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
            public GetUserSession Session { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "GetUserSession":
                        return Task.FromResult(Session);
                    case "Logout":
                        Session = null;
                        return Task.FromResult(new SuccessAndErrorMessage { Success = true });
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }
        }
    }
}
