using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations;
using OWSData.Repositories.Implementations.ValKey;
using OWSData.Repositories.Interfaces;
using OWSShared.Extensions;
using OWSShared.Options;
using Xunit;

namespace OWSTests.SessionCache
{
    /// <summary>
    /// A role change must reach the user's already-cached sessions at once: the PublicAPI's LoginGate lets
    /// staff past maintenance and the player cap on the cached session's Role (code issue #24).
    /// </summary>
    public class RoleChangeSessionEvictionTests
    {
        private static readonly Guid Customer = Guid.NewGuid();

        private static CachingUsersRepository Caching(FakeUsersDb db, IUserSessionRepository cache, bool enabled = true)
        {
            return new CachingUsersRepository(db.Proxy, cache, Microsoft.Extensions.Options.Options.Create(new UserSessionCacheOptions { Enabled = enabled, SessionTtlSeconds = 60 }));
        }

        [Theory]
        [InlineData(UserRoles.Admin, UserRoles.Player)]      // demotion: loses the maintenance / player-cap bypass
        [InlineData(UserRoles.Moderator, UserRoles.Player)]
        [InlineData(UserRoles.Player, UserRoles.GameMaster)] // promotion: gains it
        public async Task Role_change_is_seen_by_a_cached_session_immediately(string before, string after)
        {
            var db = new FakeUsersDb();
            Guid user = Guid.NewGuid();
            Guid session = db.AddSession(user, before);
            var cache = new InMemorySessionCache();
            CachingUsersRepository repo = Caching(db, cache);

            Assert.Equal(before, (await repo.GetUserSession(Customer, session)).Role);
            int dbReadsBefore = db.SessionReads;
            Assert.Equal(before, (await repo.GetUserSession(Customer, session)).Role); // now served from cache
            Assert.Equal(dbReadsBefore, db.SessionReads);

            await repo.UpdateUserRole(Customer, user, after);

            GetUserSession reread = await repo.GetUserSession(Customer, session);
            Assert.Equal(after, reread.Role);
            Assert.Equal(UserRoles.IsStaff(after), UserRoles.IsStaff(reread.Role));
            Assert.Equal(dbReadsBefore + 1, db.SessionReads);
        }

        [Fact]
        public async Task Role_change_evicts_every_session_of_that_user_and_no_other()
        {
            var db = new FakeUsersDb();
            Guid demoted = Guid.NewGuid();
            Guid bystander = Guid.NewGuid();
            Guid first = db.AddSession(demoted, UserRoles.Admin);
            Guid second = db.AddSession(demoted, UserRoles.Admin);
            Guid other = db.AddSession(bystander, UserRoles.Admin);
            var cache = new InMemorySessionCache();
            CachingUsersRepository repo = Caching(db, cache);
            foreach (Guid s in new[] { first, second, other })
            {
                await repo.GetUserSession(Customer, s);
            }

            await repo.UpdateUserRole(Customer, demoted, UserRoles.Player);

            Assert.Null(await cache.TryGetUserSession(Customer, first));
            Assert.Null(await cache.TryGetUserSession(Customer, second));
            Assert.NotNull(await cache.TryGetUserSession(Customer, other));
            Assert.Equal(UserRoles.Player, (await repo.GetUserSession(Customer, first)).Role);
            Assert.Equal(UserRoles.Player, (await repo.GetUserSession(Customer, second)).Role);
        }

        [Fact]
        public async Task Cache_disabled_role_change_does_not_touch_the_cache()
        {
            var db = new FakeUsersDb();
            Guid user = Guid.NewGuid();
            db.AddSession(user, UserRoles.Admin);
            var cache = new InMemorySessionCache();

            await Caching(db, cache, enabled: false).UpdateUserRole(Customer, user, UserRoles.Player);

            Assert.Equal(0, cache.UserEvictions);
        }

        [Theory]
        [InlineData(true, 0, false)]
        [InlineData(true, -5, false)]
        [InlineData(true, 60, true)]
        [InlineData(false, 0, true)] // caching off: the TTL is never used
        public void Session_ttl_must_be_positive_while_caching_is_enabled(bool enabled, int ttl, bool valid)
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["UserSessionCacheOptions:Enabled"] = enabled.ToString(),
                ["UserSessionCacheOptions:SessionTtlSeconds"] = ttl.ToString(),
            }).Build();
            ServiceProvider provider = new ServiceCollection().AddUserSessionCacheOptions(config).BuildServiceProvider();

            if (valid)
            {
                Assert.Equal(ttl, provider.GetRequiredService<IOptions<UserSessionCacheOptions>>().Value.SessionTtlSeconds);
            }
            else
            {
                Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<UserSessionCacheOptions>>().Value);
            }
        }

        /// <summary>
        /// The real Valkey cache. Runs only when OWS_TEST_VALKEY names a server ("localhost:6379,password=...");
        /// CI has no Valkey, so it is skipped there.
        /// </summary>
        [ValkeyFact]
        public async Task Valkey_cache_evicts_a_users_sessions_and_blocks_a_straddling_repopulate()
        {
            var cache = new UserSessionRepository(Microsoft.Extensions.Options.Options.Create(new UserSessionCacheOptions
            {
                Enabled = true,
                ConnectionString = ValkeyFactAttribute.ConnectionString,
                KeyPrefix = $"test:{Guid.NewGuid():N}:",
            }));
            Guid demoted = Guid.NewGuid();
            Guid bystander = Guid.NewGuid();
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            Guid other = Guid.NewGuid();
            TimeSpan ttl = TimeSpan.FromSeconds(60);
            await cache.SetUserSession(Customer, first, Session(demoted, first, UserRoles.Admin), ttl);
            await cache.SetUserSession(Customer, second, Session(demoted, second, UserRoles.Admin), ttl);
            await cache.SetUserSession(Customer, other, Session(bystander, other, UserRoles.Admin), ttl);
            Assert.NotNull(await cache.TryGetUserSession(Customer, first));

            await cache.RemoveUserSessionsForUser(Customer, demoted);

            Assert.Null(await cache.TryGetUserSession(Customer, first));
            Assert.Null(await cache.TryGetUserSession(Customer, second));
            Assert.Equal(UserRoles.Admin, (await cache.TryGetUserSession(Customer, other)).Role);

            // A reader that loaded the pre-change row before the eviction must not re-cache it.
            await cache.SetUserSession(Customer, first, Session(demoted, first, UserRoles.Admin), ttl);
            Assert.Null(await cache.TryGetUserSession(Customer, first));
        }

        private static GetUserSession Session(Guid user, Guid session, string role) => new GetUserSession
        {
            CustomerGuid = Customer,
            UserGuid = user,
            UserSessionGUID = session,
            Role = role,
            // The caching decorator only caches (and serves) a session with a live deadline (#31).
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        };

        /// <summary>The Postgres side: sessions and roles, served through IUsersRepository.</summary>
        private sealed class FakeUsersDb
        {
            private readonly Dictionary<Guid, Guid> _sessionUser = new();
            private readonly Dictionary<Guid, string> _roles = new();

            public FakeUsersDb()
            {
                Proxy = DispatchProxy.Create<IUsersRepository, UsersProxy>();
                ((UsersProxy)(object)Proxy).Db = this;
            }

            public IUsersRepository Proxy { get; }
            public int SessionReads { get; private set; }

            public Guid AddSession(Guid user, string role)
            {
                Guid session = Guid.NewGuid();
                _sessionUser[session] = user;
                _roles[user] = role;
                return session;
            }

            public GetUserSession GetUserSession(Guid session)
            {
                SessionReads++;
                Guid user = _sessionUser[session];
                return Session(user, session, _roles[user]);
            }

            public SuccessAndErrorMessage UpdateUserRole(Guid user, string role)
            {
                _roles[user] = role;
                return new SuccessAndErrorMessage { Success = true };
            }
        }

        public class UsersProxy : DispatchProxy
        {
            internal object Db;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var db = (FakeUsersDb)Db;
                switch (targetMethod.Name)
                {
                    case nameof(IUsersRepository.GetUserSession):
                        return Task.FromResult(db.GetUserSession((Guid)args[1]));
                    case nameof(IUsersRepository.UpdateUserRole):
                        return Task.FromResult(db.UpdateUserRole((Guid)args[1], (string)args[2]));
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }
        }

        /// <summary>The IUserSessionRepository contract, in memory.</summary>
        private sealed class InMemorySessionCache : IUserSessionRepository
        {
            private readonly ConcurrentDictionary<(Guid, Guid), GetUserSession> _sessions = new();

            public int UserEvictions { get; private set; }

            public Task<GetUserSession> TryGetUserSession(Guid customerGuid, Guid userSessionGuid)
                => Task.FromResult(_sessions.TryGetValue((customerGuid, userSessionGuid), out GetUserSession s) ? s : null);

            public Task SetUserSession(Guid customerGuid, Guid userSessionGuid, GetUserSession userSession, TimeSpan timeToLive)
            {
                _sessions[(customerGuid, userSessionGuid)] = userSession;
                return Task.CompletedTask;
            }

            public Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid)
            {
                _sessions.TryRemove((customerGuid, userSessionGuid), out _);
                return Task.CompletedTask;
            }

            public Task RemoveUserSessionsForUser(Guid customerGuid, Guid userGuid)
            {
                UserEvictions++;
                foreach (KeyValuePair<(Guid, Guid), GetUserSession> entry in _sessions)
                {
                    if (entry.Key.Item1 == customerGuid && entry.Value.UserGuid == userGuid)
                    {
                        _sessions.TryRemove(entry.Key, out _);
                    }
                }
                return Task.CompletedTask;
            }
        }
    }

    public sealed class ValkeyFactAttribute : FactAttribute
    {
        public const string ConnectionStringVariable = "OWS_TEST_VALKEY";

        public static string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringVariable);

        public ValkeyFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                Skip = $"Set {ConnectionStringVariable} to run the Valkey session-cache test.";
            }
        }
    }
}
