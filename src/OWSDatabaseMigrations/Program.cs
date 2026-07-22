using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace OWSDatabaseMigrations
{
    /// <summary>
    /// Applies the Postgres schema scripts under Databases/Postgres in the order defined by
    /// migration-manifest.txt, journaling applied scripts in the schemaversions table so each
    /// runs exactly once. Run with --baseline against a database that already has the schema
    /// to record every manifest script as applied without executing anything.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string connectionString = GetArg(args, "--connection-string")
                ?? Environment.GetEnvironmentVariable("OWS_DB_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");

            string scriptsDir = GetArg(args, "--scripts")
                ?? Environment.GetEnvironmentVariable("OWS_MIGRATIONS_SCRIPTS")
                ?? Path.Combine("..", "..", "Databases", "Postgres");

            bool baseline = args.Contains("--baseline")
                || string.Equals(Environment.GetEnvironmentVariable("OWS_MIGRATIONS_BASELINE"), "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine("No connection string. Pass --connection-string or set OWS_DB_CONNECTION_STRING / DATABASE_CONNECTION_STRING.");
                return 1;
            }

            string manifestPath = Path.Combine(scriptsDir, "migration-manifest.txt");
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Manifest not found: {manifestPath}");
                return 1;
            }

            List<SqlScript> scripts;
            try
            {
                scripts = LoadScripts(scriptsDir, manifestPath);
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            Console.WriteLine($"{scripts.Count} script(s) in manifest ({manifestPath}).");

            return baseline ? Baseline(connectionString, scripts) : Upgrade(connectionString, scripts);
        }

        private static List<SqlScript> LoadScripts(string scriptsDir, string manifestPath)
        {
            var scripts = new List<SqlScript>();
            int order = 0;

            foreach (string rawLine in File.ReadAllLines(manifestPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                string scriptPath = Path.Combine(scriptsDir, line.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException($"Manifest entry not found on disk: {scriptPath}");
                }

                // RunGroupOrder pins execution to manifest order regardless of file names.
                scripts.Add(new SqlScript(line, File.ReadAllText(scriptPath), new SqlScriptOptions
                {
                    ScriptType = DbUp.Support.ScriptType.RunOnce,
                    RunGroupOrder = order++
                }));
            }

            return scripts;
        }

        private static int Upgrade(string connectionString, List<SqlScript> scripts)
        {
            // A database that predates this runner has the migrations applied but no journal.
            // Running the manifest against it would re-execute everything, so refuse and ask
            // for an explicit baseline. A freshly seeded database (base schema only, from
            // .docker/postgres/setup.sql) must still migrate normally, so the sentinel is a
            // table created by the newest fork migration, not one from the base schema.
            if (HasExistingSchemaWithoutJournal(connectionString))
            {
                Console.Error.WriteLine(
                    "This database already has an OWS schema but no schemaversions journal. " +
                    "Run once with --baseline (or OWS_MIGRATIONS_BASELINE=true) to adopt it, then re-run normally.");
                return 1;
            }

            UpgradeEngine upgrader = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScripts(scripts)
                // Postgres dollar-quoting ($function$ ... $function$) would otherwise be
                // parsed as DbUp substitution variables.
                .WithVariablesDisabled()
                // Several legacy scripts carry their own BEGIN/COMMIT (written for psql),
                // which conflicts with an outer DbUp transaction. Scripts that want
                // atomicity manage it themselves.
                .WithoutTransaction()
                .LogToConsole()
                .Build();

            DatabaseUpgradeResult result = upgrader.PerformUpgrade();
            if (!result.Successful)
            {
                Console.Error.WriteLine($"Migration failed on '{result.ErrorScript?.Name}': {result.Error}");
                return 1;
            }

            Console.WriteLine(result.Scripts.Any()
                ? $"Applied {result.Scripts.Count()} script(s)."
                : "Database is up to date.");
            return 0;
        }

        // Records every manifest script as already applied, creating the same journal table
        // DbUp's Postgres provider uses. For adopting the runner on an existing database.
        private static int Baseline(string connectionString, List<SqlScript> scripts)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using (var createJournal = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schemaversions (" +
                "schemaversionsid serial NOT NULL PRIMARY KEY, " +
                "scriptname character varying(255) NOT NULL, " +
                "applied timestamp without time zone NOT NULL)", connection))
            {
                createJournal.ExecuteNonQuery();
            }

            int marked = 0;
            foreach (SqlScript script in scripts)
            {
                using var insert = new NpgsqlCommand(
                    "INSERT INTO schemaversions (scriptname, applied) " +
                    "SELECT @name, now() AT TIME ZONE 'utc' " +
                    "WHERE NOT EXISTS (SELECT 1 FROM schemaversions WHERE scriptname = @name)", connection);
                insert.Parameters.AddWithValue("name", script.Name);
                marked += insert.ExecuteNonQuery();
            }

            Console.WriteLine($"Baseline complete: {marked} script(s) marked as applied without executing.");
            return 0;
        }

        private static bool HasExistingSchemaWithoutJournal(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            // playershops is created by the newest migration in the manifest; keep this
            // sentinel current when the manifest grows so the check stays meaningful.
            using var command = new NpgsqlCommand(
                "SELECT to_regclass('public.playershops') IS NOT NULL AND to_regclass('public.schemaversions') IS NULL",
                connection);
            return (bool)command.ExecuteScalar();
        }

        private static string GetArg(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
