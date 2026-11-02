using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSData.SQL;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// A character name is unique per customer, ignoring case and surrounding spaces. The unique index decides it, so
    /// two creates that both pass the name pre-check cannot both commit; the loser gets the normal "name taken" error.
    /// The create paths run on the setup.sql schema only: the migrated schema's Characters table (rebuilt by
    /// CreateCharactersTableModificationScript) lacks Email and the stat columns both create paths write.
    /// </summary>
    public class CharacterNameUniquenessTests : IClassFixture<PostgresTestDatabases>
    {
        private const string MigrationScript = "SamsaraUpdates/UniqueCharacterNames_2026-10-30.sql";
        private const string NameTaken = CharacterCreationMessages.CharacterNameTaken;

        private readonly PostgresTestDatabases _databases;

        public CharacterNameUniquenessTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task CreateCharacter_RejectsTakenNameInAnyCase(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            (Guid customerGUID, Guid userGUID) = await SeedPlayer(source);

            SuccessAndErrorMessage first = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Admin", "Default");
            SuccessAndErrorMessage second = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "ADMIN", "Default");
            SuccessAndErrorMessage third = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, " admin ", "Default");

            Assert.True(first.Success, first.ErrorMessage);
            Assert.False(second.Success);
            Assert.Equal(NameTaken, second.ErrorMessage);
            Assert.False(third.Success);
            Assert.Equal(NameTaken, third.ErrorMessage);
            Assert.Equal(1, await CountNamed(source, customerGUID, "admin"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task CreateCharacter_ConcurrentSameNameOnlyOneCommits(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            (Guid customerGUID, Guid userGUID) = await SeedPlayer(source);

            // The first create has inserted its row but not committed, like a request still inside its transaction.
            await using var first = new NpgsqlConnection(_databases.ConnectionString(source));
            await first.OpenAsync();
            await using NpgsqlTransaction transaction = await first.BeginTransactionAsync();
            await first.ExecuteAsync(PostgresQueries.AddCharacterUsingDefaultCharacterValues,
                new { CustomerGUID = customerGUID, UserGUID = userGUID, CharacterName = "Racer", DefaultSetName = "Default" }, transaction);

            // The second cannot see that row, so any pre-check passes and it goes on to insert.
            Task<SuccessAndErrorMessage> second = users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Racer", "Default");
            await WaitUntilBlockedOrDone(source, second);

            await transaction.CommitAsync();
            SuccessAndErrorMessage secondResult = await second;

            Assert.False(secondResult.Success);
            Assert.Equal(NameTaken, secondResult.ErrorMessage);
            Assert.Equal(1, await CountNamed(source, customerGUID, "Racer"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task Migration_RefusesToRunWhileDuplicateNamesExist(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            (Guid customerGUID, Guid userGUID) = await SeedPlayer(source);
            string script = await File.ReadAllTextAsync(Path.Combine(PostgresTestDatabases.PostgresDirectory,
                MigrationScript.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True((await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Twin", "Default")).Success);

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.OpenAsync();
            // Roll the database back to before the migration and add the duplicate an unguarded create could leave.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("DROP INDEX UX_Characters_CustomerGUID_CharName", transaction: transaction);
            await connection.ExecuteAsync(PostgresQueries.AddCharacterUsingDefaultCharacterValues,
                new { CustomerGUID = customerGUID, UserGUID = userGUID, CharacterName = "TWIN", DefaultSetName = "Default" }, transaction);

            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(script, transaction: transaction));
            Assert.Contains("twin", refused.MessageText);
            await transaction.RollbackAsync();

            // With no duplicates it is a no-op: the index survived the rollback and the rerun keeps it.
            await connection.ExecuteAsync(script);
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'ux_characters_customerguid_charname'"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MigratedSchema_RejectsNameThatDiffersOnlyInCase(SchemaSource source)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            const string insert = @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                                    VALUES (@CustomerGUID, @CharName, 0, 0, 0, 0)";
            Guid customerGUID = Guid.NewGuid();

            await connection.ExecuteAsync(insert, new { CustomerGUID = customerGUID, CharName = "Bobb" });
            PostgresException duplicate = await Assert.ThrowsAsync<PostgresException>(
                () => connection.ExecuteAsync(insert, new { CustomerGUID = customerGUID, CharName = "bobb " }));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            Assert.Equal("ux_characters_customerguid_charname", duplicate.ConstraintName);
            // Names are unique per customer only.
            await connection.ExecuteAsync(insert, new { CustomerGUID = Guid.NewGuid(), CharName = "Bobb" });
        }

        private UsersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        /// <summary>A customer with a "Default" character value set and one registered player.</summary>
        private async Task<(Guid CustomerGUID, Guid UserGUID)> SeedPlayer(SchemaSource source)
        {
            UsersRepository users = Repository(source);
            Guid customerGUID = Guid.NewGuid();

            Assert.True((await users.RegisterUser(customerGUID, "player@example.com", "pw", "Player", "One")).Success);
            PlayerLoginAndCreateSession login = await users.LoginAndCreateSession(customerGUID, "player@example.com", "pw");
            Assert.True(login.Authenticated, login.ErrorMessage);
            Guid userGUID = (await users.GetUserSession(customerGUID, login.UserSessionGuid.Value)).UserGuid.Value;

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                @"INSERT INTO DefaultCharacterValues (CustomerGUID, DefaultSetName, StartingMapName, X, Y, Z)
                  VALUES (@CustomerGUID, 'Default', 'L_MVP_2', 0, 0, 0)",
                new { CustomerGUID = customerGUID });

            return (customerGUID, userGUID);
        }

        private async Task<int> CountNamed(SchemaSource source, Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM Characters WHERE CustomerGUID = @CustomerGUID AND LOWER(TRIM(CharName)) = LOWER(@Name)",
                new { CustomerGUID = customerGUID, Name = name });
        }

        /// <summary>Waits until <paramref name="pending"/> finishes or some session in the database waits on a lock.</summary>
        private async Task WaitUntilBlockedOrDone(SchemaSource source, Task pending)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.OpenAsync();

            for (int attempt = 0; attempt < 200 && !pending.IsCompleted; attempt++)
            {
                bool blocked = await connection.QuerySingleAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock')");
                if (blocked)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.True(pending.IsCompleted, "The second create neither finished nor blocked.");
        }
    }
}
