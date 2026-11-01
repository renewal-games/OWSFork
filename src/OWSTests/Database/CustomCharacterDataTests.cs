using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.StoredProcs;
using OWSData.Models.Tables;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// A custom character data field has one row per character. AddOrUpdateCustomCharacterData used to check for the field
    /// and then INSERT as a separate statement, so simultaneous first writes could each insert; after that every write of
    /// the field threw. It is now one upsert on AK_CustomCharacterData_CharacterField, which both setup.sql and
    /// CustomCharacterDataUniqueField_2026-11-01.sql create, and the migration first removes duplicates already stored.
    /// </summary>
    public class CustomCharacterDataTests : IClassFixture<PostgresTestDatabases>
    {
        private const string MigrationScript = "SamsaraUpdates/CustomCharacterDataUniqueField_2026-11-01.sql";
        private const int Fields = 8;
        private const int WritersPerField = 8;

        private readonly PostgresTestDatabases _databases;

        public CustomCharacterDataTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SimultaneousFirstWrites_LeaveOneRowPerField_AndTheFieldStaysWritable(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(source, customerGUID, "Racer");
            string[] fields = Enumerable.Range(0, Fields).Select(i => $"Field{i}").ToArray();

            // Every writer waits on the same signal, so the first writes of each field arrive together.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            List<Task> writers = fields
                .SelectMany(field => Enumerable.Range(0, WritersPerField).Select(writer => Task.Run(async () =>
                {
                    await start.Task;
                    await Repository(source).AddOrUpdateCustomCharacterData(customerGUID, Write("Racer", field, $"{field}-writer{writer}"));
                })))
                .ToList();
            start.SetResult();
            await Task.WhenAll(writers);

            Dictionary<string, List<string>> stored = await StoredValues(source, customerGUID, "Racer");
            Assert.Equal(fields.OrderBy(f => f), stored.Keys.OrderBy(f => f));
            foreach (string field in fields)
            {
                string value = Assert.Single(stored[field]);
                Assert.StartsWith(field + "-writer", value);
            }

            // A later save of each field still goes through and is the one value read back.
            foreach (string field in fields)
            {
                await Repository(source).AddOrUpdateCustomCharacterData(customerGUID, Write("Racer", field, field + "-last"));
            }
            IEnumerable<CustomCharacterData> rows = await Repository(source).GetCustomCharacterData(customerGUID, "Racer");
            Assert.Equal(fields.Select(f => (f, f + "-last")).OrderBy(r => r.f),
                rows.Select(r => (r.CustomFieldName, r.FieldValue)).OrderBy(r => r.CustomFieldName));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SecondRowForAField_IsRejected(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(source, customerGUID, "Unique");
            await InsertRow(source, customerGUID, characterID, "Hair", "Short");

            PostgresException duplicate = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRow(source, customerGUID, characterID, "Hair", "Long"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

            // The same field name on another character is its own field.
            int otherID = await SeedCharacter(source, customerGUID, "Other");
            await InsertRow(source, customerGUID, otherID, "Hair", "Long");
        }

        [PostgresFact]
        public async Task Migration_KeepsTheNewestRowOfEachDuplicatedField()
        {
            const SchemaSource source = SchemaSource.Migrated;
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(source, customerGUID, "Duplicated");
            int otherID = await SeedCharacter(source, customerGUID, "Bystander");

            // A database from before the migration: no index, and a field a race already wrote twice (and one three times).
            await Execute(source, "DROP INDEX AK_CustomCharacterData_CharacterField");
            await InsertRow(source, customerGUID, characterID, "Raced", "first");
            await InsertRow(source, customerGUID, characterID, "Raced", "second");
            await InsertRow(source, customerGUID, characterID, "RacedTwice", "first");
            await InsertRow(source, customerGUID, characterID, "RacedTwice", "second");
            await InsertRow(source, customerGUID, characterID, "RacedTwice", "third");
            await InsertRow(source, customerGUID, characterID, "Single", "only");
            await InsertRow(source, customerGUID, otherID, "Raced", "bystander");

            string script = await File.ReadAllTextAsync(Path.Combine(PostgresTestDatabases.FindRepoRoot(),
                "Databases", "Postgres", MigrationScript.Replace('/', Path.DirectorySeparatorChar)));
            await Execute(source, script);
            await Execute(source, script); // idempotent

            Dictionary<string, List<string>> stored = await StoredValues(source, customerGUID, "Duplicated");
            Assert.Equal(new[] { "second" }, stored["Raced"]);
            Assert.Equal(new[] { "third" }, stored["RacedTwice"]);
            Assert.Equal(new[] { "only" }, stored["Single"]);
            Assert.Equal(new[] { "bystander" }, (await StoredValues(source, customerGUID, "Bystander"))["Raced"]);

            await Repository(source).AddOrUpdateCustomCharacterData(customerGUID, Write("Duplicated", "Raced", "saved"));
            Assert.Equal(new[] { "saved" }, (await StoredValues(source, customerGUID, "Duplicated"))["Raced"]);
        }

        private CharactersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private static AddOrUpdateCustomCharacterData Write(string characterName, string field, string value) =>
            new() { CharacterName = characterName, CustomFieldName = field, FieldValue = value };

        /// <summary>A bare character row. The migrated Characters table lacks Email and the stat columns setup.sql requires.</summary>
        private async Task<int> SeedCharacter(SchemaSource source, Guid customerGUID, string charName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            if (source == SchemaSource.SetupSql)
            {
                return await connection.QuerySingleAsync<int>(
                    @"INSERT INTO Characters (CustomerGUID, Email, CharName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                      VALUES (@CustomerGUID, '', @CharName, 0, 0, 0, 0, 0, 0, 0, 0)
                      RETURNING CharacterID",
                    new { CustomerGUID = customerGUID, CharName = charName });
            }

            return await connection.QuerySingleAsync<int>(
                @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                  VALUES (@CustomerGUID, @CharName, 0, 0, 0, 0)
                  RETURNING CharacterID",
                new { CustomerGUID = customerGUID, CharName = charName });
        }

        private async Task InsertRow(SchemaSource source, Guid customerGUID, int characterID, string field, string value)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                @"INSERT INTO CustomCharacterData (CustomerGUID, CharacterID, CustomFieldName, FieldValue)
                  VALUES (@CustomerGUID, @CharacterID, @CustomFieldName, @FieldValue)",
                new { CustomerGUID = customerGUID, CharacterID = characterID, CustomFieldName = field, FieldValue = value });
        }

        /// <summary>Every stored value of each field of the character, oldest row first.</summary>
        private async Task<Dictionary<string, List<string>>> StoredValues(SchemaSource source, Guid customerGUID, string charName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            IEnumerable<(string Field, string Value)> rows = await connection.QueryAsync<(string, string)>(
                @"SELECT CCD.CustomFieldName, CCD.FieldValue
                  FROM CustomCharacterData CCD
                  INNER JOIN Characters C ON C.CustomerGUID = CCD.CustomerGUID AND C.CharacterID = CCD.CharacterID
                  WHERE C.CustomerGUID = @CustomerGUID AND C.CharName = @CharName
                  ORDER BY CCD.CustomCharacterDataID",
                new { CustomerGUID = customerGUID, CharName = charName });
            return rows.GroupBy(r => r.Field).ToDictionary(g => g.Key, g => g.Select(r => r.Value).ToList());
        }

        private async Task Execute(SchemaSource source, string sql)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
