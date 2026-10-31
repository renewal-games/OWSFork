using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace OWSData.Health
{
    /// <summary>
    /// Whether the database schema is the one this build expects. The schemaversions journal (written by
    /// OWSDatabaseMigrations) must list the last script of Databases/Postgres/migration-manifest.txt, and a few objects
    /// the code relies on must exist: a --baseline run journals scripts without running them, and the runner has
    /// no transaction, so a journal alone can claim scripts whose changes are not there.
    /// </summary>
    public static class SchemaVersionCheck
    {
        /// <summary>The last line of migration-manifest.txt. OWSTests fails until this follows a new script.</summary>
        public const string ExpectedLastScript = "SamsaraUpdates/SteamLoginFirstLoginLock_2026-10-31.sql";

        /// <summary>Tables and columns the code reads directly, as (table, column); a null column checks the table only.</summary>
        public static readonly IReadOnlyList<(string Table, string Column)> RequiredObjects = new[]
        {
            ("users", "isinternalnetworktestuser"), // AddUserNetworkTestFlag_2026-10-29.sql, read by every join
            ("loginqueue", (string)null),           // AddSamsaraManagement2_2026-10-22.sql, read by every login
            ("loginfailures", (string)null)         // LoginBackoff_2026-10-30.sql, read by every password login
        };

        /// <summary>Indexes the code relies on, such as the target of an ON CONFLICT upsert.</summary>
        public static readonly IReadOnlyList<string> RequiredIndexes = new[]
        {
            "ak_customcharacterdata_characterfield",   // CustomCharacterDataUniqueField_2026-11-01.sql, every custom data save
            "ux_characters_customerguid_lowercharname" // UniqueCharacterNames_2026-10-30.sql, the only guard against duplicate character names
        };

        public static bool IsCurrent(IEnumerable<string> appliedScripts, string expectedLastScript) =>
            appliedScripts != null && appliedScripts.Contains(expectedLastScript, StringComparer.Ordinal);

        /// <summary>Returns what is wrong with the schema, or null when it matches this build.</summary>
        public static async Task<string> FindProblemAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using (var journal = new NpgsqlCommand("SELECT to_regclass('public.schemaversions') IS NOT NULL", connection))
            {
                if (!(bool)await journal.ExecuteScalarAsync(cancellationToken))
                    return "Schema journal schemaversions is missing; run OWSDatabaseMigrations.";
            }

            var applied = new List<string>();
            await using (var scripts = new NpgsqlCommand("SELECT scriptname FROM schemaversions", connection))
            await using (NpgsqlDataReader reader = await scripts.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    applied.Add(reader.GetString(0));
            }

            if (!IsCurrent(applied, ExpectedLastScript))
                return $"Migration {ExpectedLastScript} is not in schemaversions; run OWSDatabaseMigrations.";

            var missing = new List<string>();
            foreach ((string table, string column) in RequiredObjects)
            {
                await using var probe = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = @table AND (@column::text IS NULL OR column_name = @column))",
                    connection);
                probe.Parameters.AddWithValue("table", table);
                probe.Parameters.AddWithValue("column", NpgsqlTypes.NpgsqlDbType.Text, (object)column ?? DBNull.Value);
                if (!(bool)await probe.ExecuteScalarAsync(cancellationToken))
                    missing.Add(column == null ? table : $"{table}.{column}");
            }

            foreach (string index in RequiredIndexes)
            {
                await using var probe = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @index)", connection);
                probe.Parameters.AddWithValue("index", index);
                if (!(bool)await probe.ExecuteScalarAsync(cancellationToken))
                    missing.Add("index " + index);
            }

            return missing.Count == 0
                ? null
                : $"Schema is missing {string.Join(", ", missing)} although schemaversions lists it as migrated.";
        }
    }
}
