using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.StoredProcs;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Two first logins for one SteamId at once (a double submit or a retry) must both authenticate and leave one Users
    /// row. Without a lock both counted 0, and the second INSERT failed on AK_User_SteamId (23505). Runs against both the
    /// fresh-install schema and the migrated one, so setup.sql and the migration cannot drift apart.
    /// </summary>
    public class SteamLoginConcurrencyTests : IClassFixture<PostgresTestDatabases>
    {
        private const string LoginSql = "SELECT * FROM SteamLoginAndCreateSession(@CustomerGUID, @SteamId, @PersonaName)";

        private readonly PostgresTestDatabases _databases;

        public SteamLoginConcurrencyTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task ConcurrentFirstLogins_BothAuthenticate_OneUser(SchemaSource source)
        {
            string connectionString = _databases.ConnectionString(source);
            var args = new { CustomerGUID = Guid.NewGuid(), SteamId = "76561198000000042", PersonaName = "Racer" };

            await using var a = new NpgsqlConnection(connectionString);
            await using var b = new NpgsqlConnection(connectionString);
            await using var observer = new NpgsqlConnection(connectionString);
            await a.OpenAsync();
            await b.OpenAsync();
            await observer.OpenAsync();

            // A creates the user inside an open transaction, so its row is not yet visible to B.
            await using NpgsqlTransaction transaction = await a.BeginTransactionAsync();
            PlayerLoginAndCreateSession first = await a.QuerySingleAsync<PlayerLoginAndCreateSession>(LoginSql, args, transaction);
            Assert.True(first.Authenticated, first.ErrorMessage);

            // B's login runs while A's transaction is still open; wait until it is blocked on a lock.
            Task<PlayerLoginAndCreateSession> secondTask = b.QuerySingleAsync<PlayerLoginAndCreateSession>(LoginSql, args);
            await WaitUntilWaitingOnLock(observer, b.ProcessID, secondTask);

            await transaction.CommitAsync();
            PlayerLoginAndCreateSession second = await secondTask;

            Assert.True(second.Authenticated, second.ErrorMessage);
            Assert.NotEqual(first.UserSessionGuid, second.UserSessionGuid);

            long users = await observer.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM Users WHERE CustomerGUID = @CustomerGUID AND SteamId = @SteamId", args);
            Assert.Equal(1, users);
        }

        private static async Task WaitUntilWaitingOnLock(NpgsqlConnection observer, int pid, Task pending)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (pending.IsCompleted)
                {
                    await pending; // surfaces an exception, or fails below because B did not wait for A
                    Assert.Fail("The second login finished while the first login's transaction was still open.");
                }

                bool waiting = await observer.QuerySingleAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @Pid AND wait_event_type = 'Lock')", new { Pid = pid });
                if (waiting)
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail("The second login never waited on a lock.");
        }
    }
}
