using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// OWSDatabaseMigrations --baseline adopts a database that already has the schema by journaling every manifest script
    /// without running it. A script in its BaselineChecks list is journaled only when its change is in place, so a
    /// database whose login functions predate LoginLockAndSessionReplace keeps that script pending and the next normal run
    /// applies it. Runs the built OWSDatabaseMigrations as a process: referencing it would bring its Npgsql (10) into
    /// OWSTests, which tests OWSData on Npgsql 8.
    /// </summary>
    public class MigrationBaselineTests : IClassFixture<PostgresTestDatabases>
    {
        private const string LoginScript = "SamsaraUpdates/LoginLockAndSessionReplace_2026-11-01.sql";

        private readonly PostgresTestDatabases _databases;

        public MigrationBaselineTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresFact]
        public async Task Baseline_LeavesLoginScriptPending_WhenFunctionsPredateIt_AndUpgradeAppliesIt()
        {
            string connectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);

            // An older database: the login functions as LoginBackoff (password) and BcryptPasswordHashes (Steam) left them.
            await Execute(connectionString, Script("SamsaraUpdates/BcryptPasswordHashes_2026-10-30.sql"));
            await Execute(connectionString, Script("SamsaraUpdates/LoginBackoff_2026-10-30.sql"));
            Assert.False(await LoginFunctionsAreCurrent(connectionString));

            await RunMigrations(connectionString, "--baseline");

            List<string> journal = await Journal(connectionString);
            Assert.DoesNotContain(LoginScript, journal);
            Assert.Equal(PostgresTestDatabases.ManifestScripts().Where(s => s != LoginScript).OrderBy(s => s), journal.OrderBy(s => s));

            string upgrade = await RunMigrations(connectionString);

            Assert.Contains(LoginScript, upgrade);
            Assert.Contains("Applied 1 script(s).", upgrade);
            Assert.Contains(LoginScript, await Journal(connectionString));
            Assert.True(await LoginFunctionsAreCurrent(connectionString));
        }

        [PostgresFact]
        public async Task Baseline_JournalsLoginScript_WhenFunctionsAreCurrent()
        {
            // A fresh install's setup.sql already has the current login functions.
            Assert.True(await LoginFunctionsAreCurrent(_databases.ConnectionString(SchemaSource.SetupSql)));

            // Baseline on a database with every script's changes. setup.sql alone is not one: it lacks the unique
            // character name index, so --baseline rightly leaves UniqueCharacterNames_2026-10-30.sql pending there.
            string connectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            Assert.True(await LoginFunctionsAreCurrent(connectionString));

            await RunMigrations(connectionString, "--baseline");

            Assert.Equal(PostgresTestDatabases.ManifestScripts().OrderBy(s => s), (await Journal(connectionString)).OrderBy(s => s));
        }

        /// <summary>The Steam first-login lock is in, and neither login function deletes the account's sessions.</summary>
        private static async Task<bool> LoginFunctionsAreCurrent(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            Dictionary<string, string> bodies = (await connection.QueryAsync<(string Name, string Body)>(
                    "SELECT proname, prosrc FROM pg_proc WHERE pronamespace = 'public'::regnamespace " +
                    "AND proname IN ('playerloginandcreatesession', 'steamloginandcreatesession')"))
                .ToDictionary(f => f.Name, f => f.Body);

            Assert.Equal(2, bodies.Count);
            return bodies["steamloginandcreatesession"].Contains("pg_advisory_xact_lock")
                && bodies.Values.All(body => !body.Contains("DELETE FROM UserSessions", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<List<string>> Journal(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            return (await connection.QueryAsync<string>("SELECT scriptname FROM schemaversions")).ToList();
        }

        private static string Script(string manifestEntry) =>
            File.ReadAllText(Path.Combine(PostgresTestDatabases.FindRepoRoot(), "Databases", "Postgres", manifestEntry.Replace('/', Path.DirectorySeparatorChar)));

        private static async Task Execute(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.ExecuteAsync(sql);
        }

        /// <summary>Runs OWSDatabaseMigrations against the database, asserts it succeeded, and returns its output.</summary>
        private static async Task<string> RunMigrations(string connectionString, params string[] extraArgs)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(MigrationsAssembly());
            start.ArgumentList.Add("--connection-string");
            start.ArgumentList.Add(connectionString);
            start.ArgumentList.Add("--scripts");
            start.ArgumentList.Add(Path.Combine(PostgresTestDatabases.FindRepoRoot(), "Databases", "Postgres"));
            foreach (string arg in extraArgs)
            {
                start.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(start);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                await process.WaitForExitAsync(timeout.Token);
            }

            string output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, $"OWSDatabaseMigrations {string.Join(" ", extraArgs)} exited {process.ExitCode}:\n{output}");
            return output;
        }

        /// <summary>OWSDatabaseMigrations.dll from the same configuration and framework as this test assembly.</summary>
        private static string MigrationsAssembly()
        {
            // <repo>/src/OWSTests/bin/<Configuration>/<TargetFramework>/
            var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string assembly = Path.Combine(PostgresTestDatabases.FindRepoRoot(), "src", "OWSDatabaseMigrations", "bin",
                testOutput.Parent.Name, testOutput.Name, "OWSDatabaseMigrations.dll");

            Assert.True(File.Exists(assembly), $"{assembly} is missing. Build OWS.sln; OWSTests does not reference OWSDatabaseMigrations.");
            return assembly;
        }
    }
}
