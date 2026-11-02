using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations;
using OWSData.Repositories.Implementations.Postgres;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// A session stops resolving UserSessionMaxAge() after its LoginDate, through the database lookups and through the
    /// session cache, and PurgeExpiredUserSessions deletes it. Runs against both the fresh-install schema and the migrated
    /// one, so setup.sql and UserSessionExpiry_2026-10-30.sql cannot drift apart.
    /// </summary>
    public class UserSessionExpiryTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public UserSessionExpiryTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task GetUserSession_StopsResolvingPastMaxAge(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = Guid.NewGuid();
            Guid session = await Login(users, customerGUID, "expiry@example.com");

            GetUserSession live = await users.GetUserSession(customerGUID, session);
            Assert.True(live?.UserGuid.HasValue);
            Assert.NotNull(live.ExpiresAt);
            Assert.InRange(live.ExpiresAt.Value.ToUniversalTime() - DateTime.UtcNow, TimeSpan.FromHours(71), TimeSpan.FromHours(72));

            await BackdateLogin(source, session, "UserSessionMaxAge() + INTERVAL '1 minute'");

            Assert.Null(await users.GetUserSession(customerGUID, session));
            Assert.Null(await users.GetUserSessionORM(customerGUID, session));
        }

        // setup.sql alone has no CharStats, which the roster query reads, so only the migrated schema runs these.
        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SessionLookups_IgnoreExpiredSession(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = Guid.NewGuid();
            Guid session = await Login(users, customerGUID, "roster@example.com");
            await AddCharacter(source, customerGUID, "roster@example.com", "ExpiryHero");

            Assert.Single(await users.GetAllCharacters(customerGUID, session));
            Assert.True((await users.UserSessionSetSelectedCharacter(customerGUID, session, "ExpiryHero")).Success);

            await BackdateLogin(source, session, "UserSessionMaxAge() + INTERVAL '1 minute'");

            Assert.Empty(await users.GetAllCharacters(customerGUID, session));
            Assert.Empty(await users.GetAllSamsaraCharacters(customerGUID, session));
            Assert.False((await users.UserSessionSetSelectedCharacter(customerGUID, session, "ExpiryHero")).Success);
            // RemoveCharacter reports success when nothing matched, so check the row instead.
            await users.RemoveCharacter(customerGUID, session, "ExpiryHero");
            Assert.Equal(1, await CharacterCount(source, customerGUID, "ExpiryHero"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task PurgeExpiredUserSessions_DeletesOnlyExpiredRows(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = Guid.NewGuid();
            Guid expired = await Login(users, customerGUID, "old@example.com");
            Guid live = await Login(users, customerGUID, "new@example.com");
            await BackdateLogin(source, expired, "UserSessionMaxAge() + INTERVAL '1 minute'");

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            Assert.True(await connection.QuerySingleAsync<long>("SELECT PurgeExpiredUserSessions()") >= 1);

            Assert.Equal(0, await SessionCount(source, expired));
            Assert.Equal(1, await SessionCount(source, live));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task Cache_StopsServingSessionAtItsDeadline(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            var cache = new InMemorySessionCache();
            CachingUsersRepository caching = Caching(users, cache);
            Guid customerGUID = Guid.NewGuid();
            Guid session = await Login(users, customerGUID, "cached@example.com");

            Assert.True((await caching.GetUserSession(customerGUID, session))?.UserGuid.HasValue);
            Assert.Equal(TimeSpan.FromSeconds(60), cache.LastTtl);

            // The deadline passes while the entry is still cached.
            await BackdateLogin(source, session, "UserSessionMaxAge() + INTERVAL '1 minute'");
            cache.Entries[(customerGUID, session)].ExpiresAt = DateTime.UtcNow.AddSeconds(-1);

            Assert.Null(await caching.GetUserSession(customerGUID, session));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task Cache_TtlIsCappedAtRemainingLifetime(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            var cache = new InMemorySessionCache();
            CachingUsersRepository caching = Caching(users, cache);
            Guid customerGUID = Guid.NewGuid();
            Guid session = await Login(users, customerGUID, "nearlyexpired@example.com");
            await BackdateLogin(source, session, "UserSessionMaxAge() - INTERVAL '20 seconds'");

            Assert.True((await caching.GetUserSession(customerGUID, session))?.UserGuid.HasValue);
            Assert.InRange(cache.LastTtl, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task Cache_RevalidatesEntryWithoutDeadline(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            var cache = new InMemorySessionCache();
            CachingUsersRepository caching = Caching(users, cache);
            Guid customerGUID = Guid.NewGuid();
            Guid session = await Login(users, customerGUID, "precache@example.com");

            // An entry cached before sessions carried ExpiresAt.
            GetUserSession stale = await users.GetUserSession(customerGUID, session);
            stale.ExpiresAt = null;
            cache.Entries[(customerGUID, session)] = stale;
            await BackdateLogin(source, session, "UserSessionMaxAge() + INTERVAL '1 minute'");

            Assert.Null(await caching.GetUserSession(customerGUID, session));
        }

        private UsersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private static CachingUsersRepository Caching(UsersRepository users, InMemorySessionCache cache) =>
            new(users, cache, Microsoft.Extensions.Options.Options.Create(new UserSessionCacheOptions { Enabled = true, SessionTtlSeconds = 60 }));

        private static async Task<Guid> Login(UsersRepository users, Guid customerGUID, string email)
        {
            SuccessAndErrorMessage registered = await users.RegisterUser(customerGUID, email, "pw", "Session", "User");
            Assert.True(registered.Success, registered.ErrorMessage);

            PlayerLoginAndCreateSession login = await users.LoginAndCreateSession(customerGUID, email, "pw");
            Assert.True(login.Authenticated, login.ErrorMessage);
            return login.UserSessionGuid.Value;
        }

        private async Task BackdateLogin(SchemaSource source, Guid session, string age)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            Assert.Equal(1, await connection.ExecuteAsync(
                $"UPDATE UserSessions SET LoginDate = LOCALTIMESTAMP - ({age}) WHERE UserSessionGUID = @Session",
                new { Session = session }));
        }

        private async Task AddCharacter(SchemaSource source, Guid customerGUID, string email, string name)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                @"INSERT INTO Characters (CustomerGUID, UserGUID, CharName, X, Y, Z, ClassID)
                  SELECT CustomerGUID, UserGUID, @Name, 0, 0, 0, 0
                  FROM Users WHERE CustomerGUID = @CustomerGUID AND Email = @Email",
                new { CustomerGUID = customerGUID, Email = email, Name = name });
        }

        private async Task<int> CharacterCount(SchemaSource source, Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @Name",
                new { CustomerGUID = customerGUID, Name = name });
        }

        private async Task<int> SessionCount(SchemaSource source, Guid session)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM UserSessions WHERE UserSessionGUID = @Session", new { Session = session });
        }

        private sealed class InMemorySessionCache : IUserSessionRepository
        {
            public Dictionary<(Guid, Guid), GetUserSession> Entries { get; } = new();
            public TimeSpan LastTtl { get; private set; }

            public Task<GetUserSession> TryGetUserSession(Guid customerGuid, Guid userSessionGuid) =>
                Task.FromResult(Entries.GetValueOrDefault((customerGuid, userSessionGuid)));

            public Task SetUserSession(Guid customerGuid, Guid userSessionGuid, GetUserSession userSession, TimeSpan timeToLive)
            {
                Entries[(customerGuid, userSessionGuid)] = userSession;
                LastTtl = timeToLive;
                return Task.CompletedTask;
            }

            public Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid)
            {
                Entries.Remove((customerGuid, userSessionGuid));
                return Task.CompletedTask;
            }

            public Task RemoveUserSessionsForUser(Guid customerGuid, Guid userGuid)
            {
                foreach ((Guid, Guid) key in Entries.Where(e => e.Key.Item1 == customerGuid && e.Value.UserGuid == userGuid).Select(e => e.Key).ToList())
                {
                    Entries.Remove(key);
                }
                return Task.CompletedTask;
            }
        }
    }
}
