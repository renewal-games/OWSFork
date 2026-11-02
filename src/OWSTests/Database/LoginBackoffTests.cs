using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Wrong passwords for one email back off per account, whatever IP they come from: from the 5th failure the email is
    /// refused without checking the password until LoginFailures.LockedUntil passes. Runs against both the fresh-install
    /// schema and the migrated one, so setup.sql and LoginBackoff_2026-10-30.sql cannot drift apart.
    /// </summary>
    public class LoginBackoffTests : IClassFixture<PostgresTestDatabases>
    {
        private const int FreeFailures = 4;
        private const string Locked = "Too many failed logins. Try again later";

        private readonly PostgresTestDatabases _databases;

        public LoginBackoffTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task WrongPasswords_LockTheAccount(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = await RegisteredCustomer(users, "victim@example.com", "right");

            for (int i = 0; i < FreeFailures; i++)
            {
                PlayerLoginAndCreateSession miss = await users.LoginAndCreateSession(customerGUID, "victim@example.com", "wrong");
                Assert.Equal("Invalid email or password", miss.ErrorMessage);
            }
            Assert.True(await LockedUntil(source, customerGUID, "victim@example.com") == null);

            // The 5th failure locks; the email's case and padding do not give a fresh counter.
            Assert.False((await users.LoginAndCreateSession(customerGUID, " VICTIM@example.com ", "wrong")).Authenticated);
            DateTime? lockedUntil = await LockedUntil(source, customerGUID, "victim@example.com");
            Assert.NotNull(lockedUntil);
            Assert.InRange(lockedUntil.Value - DateTime.UtcNow, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));

            // Locked: even the right password is refused, and nothing tells a guesser which guess was right.
            PlayerLoginAndCreateSession blocked = await users.LoginAndCreateSession(customerGUID, "victim@example.com", "right");
            Assert.False(blocked.Authenticated);
            Assert.Equal(Locked, blocked.ErrorMessage);
            Assert.Equal(Locked, (await users.LoginAndCreateSession(customerGUID, "victim@example.com", "wrong")).ErrorMessage);

            // Other accounts of the customer are unaffected.
            Assert.True((await users.RegisterUser(customerGUID, "bystander@example.com", "pw", "B", "Y")).Success);
            Assert.True((await users.LoginAndCreateSession(customerGUID, "bystander@example.com", "pw")).Authenticated);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task LockExpires_ThenFurtherFailuresDoubleIt_AndSuccessClearsIt(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = await RegisteredCustomer(users, "victim@example.com", "right");

            for (int i = 0; i <= FreeFailures; i++)
            {
                await users.LoginAndCreateSession(customerGUID, "victim@example.com", "wrong");
            }
            await ExpireLock(source, customerGUID, "victim@example.com");

            // The 6th failure, inside the hour, locks for 60 seconds.
            Assert.Equal("Invalid email or password", (await users.LoginAndCreateSession(customerGUID, "victim@example.com", "wrong")).ErrorMessage);
            Assert.InRange((await LockedUntil(source, customerGUID, "victim@example.com")).Value - DateTime.UtcNow,
                TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(61));

            await ExpireLock(source, customerGUID, "victim@example.com");
            Assert.True((await users.LoginAndCreateSession(customerGUID, "victim@example.com", "right")).Authenticated);
            Assert.Equal(0, await FailureRows(source, customerGUID, "victim@example.com"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task UnknownEmails_AreCountedToo(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = Guid.NewGuid();

            for (int i = 0; i <= FreeFailures; i++)
            {
                Assert.Equal("Invalid email or password", (await users.LoginAndCreateSession(customerGUID, "nobody@example.com", "wrong")).ErrorMessage);
            }

            Assert.Equal(Locked, (await users.LoginAndCreateSession(customerGUID, "nobody@example.com", "wrong")).ErrorMessage);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task LoginWithoutPasswordCheck_IsNeitherCountedNorBlocked(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = await RegisteredCustomer(users, "victim@example.com", "right");

            for (int i = 0; i <= FreeFailures; i++)
            {
                await users.LoginAndCreateSession(customerGUID, "victim@example.com", "wrong");
            }

            Assert.True((await users.LoginAndCreateSession(customerGUID, "victim@example.com", "", dontCheckPassword: true)).Authenticated);
            Assert.Equal(1, await FailureRows(source, customerGUID, "victim@example.com"));
        }

        private UsersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UsersRepository>.Instance);

        private static async Task<Guid> RegisteredCustomer(UsersRepository users, string email, string password)
        {
            Guid customerGUID = Guid.NewGuid();
            Assert.True((await users.RegisterUser(customerGUID, email, password, "Test", "User")).Success);
            return customerGUID;
        }

        private async Task<DateTime?> LockedUntil(SchemaSource source, Guid customerGUID, string email)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleOrDefaultAsync<DateTime?>(
                "SELECT LockedUntil FROM LoginFailures WHERE CustomerGUID = @CustomerGUID AND Email = @Email",
                new { CustomerGUID = customerGUID, Email = email });
        }

        private async Task<int> FailureRows(SchemaSource source, Guid customerGUID, string email)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM LoginFailures WHERE CustomerGUID = @CustomerGUID AND Email = @Email",
                new { CustomerGUID = customerGUID, Email = email });
        }

        private async Task ExpireLock(SchemaSource source, Guid customerGUID, string email)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                "UPDATE LoginFailures SET LockedUntil = NOW() - INTERVAL '1 second' WHERE CustomerGUID = @CustomerGUID AND Email = @Email",
                new { CustomerGUID = customerGUID, Email = email });
        }
    }
}
