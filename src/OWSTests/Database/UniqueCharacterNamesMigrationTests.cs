using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// UniqueCharacterNames_2026-10-30.sql renames names that already clash (exactly or by case) instead of failing,
    /// so a database with duplicates still migrates. A renamed character never takes a name that is already in use, and
    /// characters of other customers are left alone. Runs the script by hand against a setup.sql database, which has no
    /// unique index yet; it gets its own fixture because the script adds the index to that database.
    /// </summary>
    public class UniqueCharacterNamesMigrationTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public UniqueCharacterNamesMigrationTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task ExistingDuplicates_AreRenamedAndIndexIsCreated(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid otherCustomerGUID = Guid.NewGuid();
            string longName = new string('L', 50);

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await DropNameIndex(connection);
            foreach ((Guid customer, string name) in new[]
            {
                (customerGUID, "Bob"), (customerGUID, "bob"), (customerGUID, "Bob"), (customerGUID, "Alice"),
                (customerGUID, longName), (customerGUID, longName.ToLowerInvariant()), (otherCustomerGUID, "Bob")
            })
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO Characters (CustomerGUID, Email, CharName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                      VALUES (@CustomerGUID, '', @CharName, 0, 0, 0, 0, 0, 0, 0, 0)",
                    new { CustomerGUID = customer, CharName = name });
            }

            await RunScriptTwice(connection);

            var rows = (await connection.QueryAsync<(int CharacterID, string CharName)>(
                "SELECT CharacterID, CharName FROM Characters WHERE CustomerGUID = @CustomerGUID ORDER BY CharacterID",
                new { CustomerGUID = customerGUID })).ToList();

            Assert.Equal("Bob", rows[0].CharName);
            Assert.Equal($"bob_{rows[1].CharacterID}", rows[1].CharName);
            Assert.Equal($"Bob_{rows[2].CharacterID}", rows[2].CharName);
            Assert.Equal("Alice", rows[3].CharName);
            Assert.Equal(longName, rows[4].CharName);
            Assert.Equal(50, rows[5].CharName.Length);
            Assert.EndsWith($"_{rows[5].CharacterID}", rows[5].CharName);
            Assert.Equal("Bob", await connection.QuerySingleAsync<string>(
                "SELECT CharName FROM Characters WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = otherCustomerGUID }));

            PostgresException clash = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                @"INSERT INTO Characters (CustomerGUID, Email, CharName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                  VALUES (@CustomerGUID, '', 'ALICE', 0, 0, 0, 0, 0, 0, 0, 0)",
                new { CustomerGUID = customerGUID }));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, clash.SqlState);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task CharacterIdSharedWithAnotherCustomer_OnlyTheDuplicateIsRenamed(SchemaSource source)
        {
            // Characters is keyed by (CustomerGUID, CharacterID), so two customers can hold the same CharacterID.
            Guid customerGUID = Guid.NewGuid();
            Guid otherCustomerGUID = Guid.NewGuid();

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await DropNameIndex(connection);
            await InsertCharacter(connection, customerGUID, 900001, "Bob");
            await InsertCharacter(connection, customerGUID, 900002, "bob");
            await InsertCharacter(connection, otherCustomerGUID, 900002, "Zed");

            await RunScriptTwice(connection);

            Assert.Equal(new[] { (900001, "Bob"), (900002, "bob_900002") }, await Names(connection, customerGUID));
            Assert.Equal(new[] { (900002, "Zed") }, await Names(connection, otherCustomerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task ReplacementNameAlreadyTaken_GetsAnotherSuffix(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid generatedClashCustomerGUID = Guid.NewGuid();

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await DropNameIndex(connection);

            // bob_900012 and bob_900012_1 are already taken (ignoring case) by characters that are not duplicates.
            await InsertCharacter(connection, customerGUID, 900011, "Bob");
            await InsertCharacter(connection, customerGUID, 900012, "bob");
            await InsertCharacter(connection, customerGUID, 900013, "BOB_900012");
            await InsertCharacter(connection, customerGUID, 900014, "bob_900012_1");

            // A name given earlier in the same run counts too. Character 1 duplicates ANN_5 and is renamed first, to
            // Ann_5_1. Character 5 duplicates Ann; ann_5 is held by character 0 and ann_5_1 now by character 1.
            await InsertCharacter(connection, generatedClashCustomerGUID, 0, "ANN_5");
            await InsertCharacter(connection, generatedClashCustomerGUID, 1, "Ann_5");
            await InsertCharacter(connection, generatedClashCustomerGUID, 3, "Ann");
            await InsertCharacter(connection, generatedClashCustomerGUID, 5, "ann");

            await RunScriptTwice(connection);

            Assert.Equal(
                new[] { (900011, "Bob"), (900012, "bob_900012_2"), (900013, "BOB_900012"), (900014, "bob_900012_1") },
                await Names(connection, customerGUID));
            Assert.Equal(
                new[] { (0, "ANN_5"), (1, "Ann_5_1"), (3, "Ann"), (5, "ann_5_2") },
                await Names(connection, generatedClashCustomerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MigratedSchema_RejectsNameInAnotherCase(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            const string insert = @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                                    VALUES (@CustomerGUID, @CharName, 0, 0, 0, 0)";

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(insert, new { CustomerGUID = customerGUID, CharName = "Carol" });

            PostgresException clash = await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(insert, new { CustomerGUID = customerGUID, CharName = "CAROL" }));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, clash.SqlState);
        }

        // The tests in this class share one setup.sql database, and an earlier test's run of the script leaves the index
        // behind; without it the duplicates below can be inserted.
        private static Task DropNameIndex(NpgsqlConnection connection) =>
            connection.ExecuteAsync("DROP INDEX IF EXISTS UX_Characters_CustomerGUID_LowerCharName");

        private static Task InsertCharacter(NpgsqlConnection connection, Guid customerGUID, int characterID, string name) =>
            connection.ExecuteAsync(
                @"INSERT INTO Characters (CustomerGUID, CharacterID, Email, CharName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                  VALUES (@CustomerGUID, @CharacterID, '', @CharName, 0, 0, 0, 0, 0, 0, 0, 0)",
                new { CustomerGUID = customerGUID, CharacterID = characterID, CharName = name });

        private static async Task RunScriptTwice(NpgsqlConnection connection)
        {
            string script = await File.ReadAllTextAsync(ScriptPath());
            await connection.ExecuteAsync(script);
            await connection.ExecuteAsync(script); // idempotent
        }

        private static async Task<(int CharacterID, string CharName)[]> Names(NpgsqlConnection connection, Guid customerGUID) =>
            (await connection.QueryAsync<(int CharacterID, string CharName)>(
                "SELECT CharacterID, CharName FROM Characters WHERE CustomerGUID = @CustomerGUID ORDER BY CharacterID",
                new { CustomerGUID = customerGUID })).ToArray();

        internal static string ScriptPath()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "Databases", "Postgres", "SamsaraUpdates", "UniqueCharacterNames_2026-10-30.sql");
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException("UniqueCharacterNames_2026-10-30.sql not found above " + AppContext.BaseDirectory);
        }
    }
}
