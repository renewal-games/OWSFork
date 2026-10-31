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
    /// so a database with duplicates still migrates. Runs the script by hand against a setup.sql database, which has no
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

            string script = await File.ReadAllTextAsync(ScriptPath());
            await connection.ExecuteAsync(script);
            await connection.ExecuteAsync(script); // idempotent

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
