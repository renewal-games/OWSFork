using System;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    public class InstanceCleanupTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public InstanceCleanupTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql, "read committed")]
        [InlineData(SchemaSource.Migrated, "read committed")]
        [InlineData(SchemaSource.SetupSql, "serializable")]
        [InlineData(SchemaSource.Migrated, "serializable")]
        public async Task Cleanup_RechecksAnInFlightHeartbeatBeforeDeletingInstanceOrMappings(
            SchemaSource source, string defaultIsolation)
        {
            string connectionString = _databases.ConnectionString(source);
            Guid customer = Guid.NewGuid();
            await using var observer = new NpgsqlConnection(connectionString);
            await observer.OpenAsync();

            async Task<int> Seed(int character) => await observer.ExecuteScalarAsync<int>(@"
                WITH instance AS (
                    INSERT INTO MapInstances
                        (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                    VALUES (@Customer, 7, 1, 7777, 2, NOW() - INTERVAL '5 minutes')
                    RETURNING MapInstanceID
                )
                INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                SELECT @Customer, @Character, MapInstanceID FROM instance
                RETURNING MapInstanceID", new { Customer = customer, Character = character });

            int recovered = await Seed(1);
            int stale = await Seed(2);
            // These reservations have no inactive user: the separate LastAccess sweep must not
            // remove them. Neither shipped schema has a mapping-to-instance FK or cascade.
            await using var heartbeat = new NpgsqlConnection(connectionString);
            await heartbeat.OpenAsync();
            await using var heartbeatTransaction = await heartbeat.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            Assert.Equal("read committed", await heartbeat.ExecuteScalarAsync<string>(
                "SHOW transaction_isolation", transaction: heartbeatTransaction));
            await heartbeat.ExecuteAsync(@"
                UPDATE MapInstances SET LastUpdateFromServer = NOW()
                WHERE CustomerGUID = @Customer AND MapInstanceID = @Recovered AND Status <> 3",
                new { Customer = customer, Recovered = recovered }, heartbeatTransaction);

            string applicationName = "cleanup-243-" + Guid.NewGuid().ToString("N");
            var cleanupConnection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = applicationName,
                // Exercise both connection defaults; cleanup explicitly requests READ COMMITTED.
                Options = "-c default_transaction_isolation=" + defaultIsolation.Replace(" ", "\\ ")
            };
            var repository = new CharactersRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres",
                OWSDBConnectionString = cleanupConnection.ConnectionString
            }));
            Task cleanup = repository.CleanUpInstances(customer);
            string blockedStatement;
            try
            {
                blockedStatement = await WaitForHeartbeatWait(observer, applicationName, heartbeat.ProcessID, cleanup);
            }
            finally
            {
                // Always release the writer, including on assertion failure, before draining cleanup.
                await heartbeatTransaction.CommitAsync();
                await cleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.Equal(1, await observer.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID = @Customer AND MapInstanceID = @Recovered",
                new { Customer = customer, Recovered = recovered }));
            Assert.Equal(1, await observer.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @Customer AND MapInstanceID = @Recovered",
                new { Customer = customer, Recovered = recovered }));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID = @Customer AND MapInstanceID = @Stale",
                new { Customer = customer, Stale = stale }));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @Customer AND MapInstanceID = @Stale",
                new { Customer = customer, Stale = stale }));
            // The old code waits only at the final DELETE, after already selecting stale IDs
            // and deleting their mappings. The fixed code must contend during candidate selection.
            Assert.Contains("FOR UPDATE", blockedStatement);
            Assert.StartsWith("SELECT MapInstanceID", blockedStatement.Trim());
        }

        private static async Task<string> WaitForHeartbeatWait(
            NpgsqlConnection observer, string applicationName, int heartbeatPid, Task cleanup)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                string statement = await observer.QuerySingleOrDefaultAsync<string>(@"
                    SELECT query FROM pg_stat_activity
                    WHERE application_name = @ApplicationName AND wait_event_type = 'Lock'
                      AND @HeartbeatPid = ANY(pg_blocking_pids(pid))",
                    new { ApplicationName = applicationName, HeartbeatPid = heartbeatPid });
                if (statement != null)
                    return statement;
                Assert.False(cleanup.IsCompleted, "Cleanup finished before contending with the heartbeat.");
                await Task.Delay(20);
            }
            throw new TimeoutException("Cleanup did not contend with the heartbeat transaction.");
        }
    }
}
