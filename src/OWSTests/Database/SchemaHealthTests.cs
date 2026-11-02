using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OWSData.Health;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// The Postgres health check behind /health reports Unhealthy when the schemaversions journal is missing or behind
    /// the manifest, or when a journaled migration's columns or indexes are not there (a --baseline run or a half-applied
    /// script).
    /// </summary>
    public class SchemaHealthTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public SchemaHealthTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task Health_Unhealthy_WithoutJournal(SchemaSource source)
        {
            HealthReport report = await CheckHealth(_databases.ConnectionString(source));

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.Contains("schemaversions", report.Entries["postgres"].Description);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task Health_FollowsJournalAndSchema(SchemaSource source)
        {
            string connectionString = _databases.ConnectionString(source);
            List<string> manifest = File.ReadAllLines(Path.Combine(PostgresTestDatabases.FindRepoRoot(), "Databases", "Postgres", "migration-manifest.txt"))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#"))
                .ToList();

            // The fixture applies the scripts directly, so journal them the way OWSDatabaseMigrations does.
            await Execute(connectionString,
                "CREATE TABLE schemaversions (schemaversionsid serial NOT NULL PRIMARY KEY, " +
                "scriptname character varying(255) NOT NULL, applied timestamp without time zone NOT NULL)");
            foreach (string script in manifest)
                await Execute(connectionString, "INSERT INTO schemaversions (scriptname, applied) VALUES (@name, now())", script);

            Assert.Equal(HealthStatus.Healthy, (await CheckHealth(connectionString)).Status);

            // Truncated journal: the newest migration never ran.
            await Execute(connectionString, "DELETE FROM schemaversions WHERE scriptname = @name", manifest.Last());
            HealthReport behind = await CheckHealth(connectionString);
            Assert.Equal(HealthStatus.Unhealthy, behind.Status);
            Assert.Contains(manifest.Last(), behind.Entries["postgres"].Description);

            // Journal complete but a journaled column is missing, as after a --baseline run.
            await Execute(connectionString, "INSERT INTO schemaversions (scriptname, applied) VALUES (@name, now())", manifest.Last());
            await Execute(connectionString, "ALTER TABLE Users DROP COLUMN IsInternalNetworkTestUser");
            HealthReport drifted = await CheckHealth(connectionString);
            Assert.Equal(HealthStatus.Unhealthy, drifted.Status);
            Assert.Contains("users.isinternalnetworktestuser", drifted.Entries["postgres"].Description);

            await Execute(connectionString, "ALTER TABLE Users ADD COLUMN IsInternalNetworkTestUser BOOLEAN NOT NULL DEFAULT FALSE");

            // Journal complete but the custom character data upsert has no unique index to conflict on.
            await Execute(connectionString, "DROP INDEX AK_CustomCharacterData_CharacterField");
            HealthReport noIndex = await CheckHealth(connectionString);
            Assert.Equal(HealthStatus.Unhealthy, noIndex.Status);
            Assert.Contains("index ak_customcharacterdata_characterfield", noIndex.Entries["postgres"].Description);

            await Execute(connectionString,
                "CREATE UNIQUE INDEX AK_CustomCharacterData_CharacterField ON CustomCharacterData (CustomerGUID, CharacterID, CustomFieldName)");
            Assert.Equal(HealthStatus.Healthy, (await CheckHealth(connectionString)).Status);

            // Journal complete but nothing stops two characters taking the same name.
            await Execute(connectionString, "DROP INDEX UX_Characters_CustomerGUID_CharName");
            HealthReport noNameIndex = await CheckHealth(connectionString);
            Assert.Equal(HealthStatus.Unhealthy, noNameIndex.Status);
            Assert.Contains("index ux_characters_customerguid_charname", noNameIndex.Entries["postgres"].Description);

            await Execute(connectionString,
                "CREATE UNIQUE INDEX UX_Characters_CustomerGUID_CharName ON Characters (CustomerGUID, LOWER(TRIM(CharName)))");
            Assert.Equal(HealthStatus.Healthy, (await CheckHealth(connectionString)).Status);

            await Execute(connectionString, "DROP TABLE LoginQueue");
            HealthReport noQueue = await CheckHealth(connectionString);
            Assert.Equal(HealthStatus.Unhealthy, noQueue.Status);
            Assert.Contains("loginqueue", noQueue.Entries["postgres"].Description);
        }

        // A fresh provider per call: the check caches its result for 10 seconds.
        private static async Task<HealthReport> CheckHealth(string connectionString)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["OWSStorageConfig:OWSDBBackend"] = "postgres",
                    ["OWSStorageConfig:OWSDBConnectionString"] = connectionString
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHealthChecks().AddOWSPostgres(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        }

        private static async Task Execute(string connectionString, string sql, string name = null)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            if (name != null)
                command.Parameters.AddWithValue("name", name);
            await command.ExecuteNonQueryAsync();
        }
    }
}
