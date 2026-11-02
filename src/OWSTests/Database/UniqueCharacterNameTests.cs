using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Once UniqueCharacterNames_2026-10-30.sql is applied, the create paths report a name taken by a concurrent create or
    /// in another case, instead of creating a second character or returning the raw unique violation.
    /// (DefaultCharacterValuesTests covers the unknown default set.) Runs on the setup.sql schema plus that script: the
    /// inserts write Characters.Email and the stat columns, which the Samsara migrations drop.
    /// </summary>
    public class UniqueCharacterNameTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public UniqueCharacterNameTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task SameNameInAnotherCase_Fails(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = await Seed(source, customerGUID);
            UsersRepository users = Repository(source);

            Assert.True((await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Bob", "Default")).Success);
            SuccessAndErrorMessage second = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "bob", "Default");

            Assert.False(second.Success);
            Assert.Equal(CharacterCreationMessages.CharacterNameAlreadyExists, second.ErrorMessage);
            Assert.Equal(1, await CharacterCount(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task ConcurrentCreatesOfOneName_CreateOneCharacter(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = await Seed(source, customerGUID);
            UsersRepository users = Repository(source);

            SuccessAndErrorMessage[] results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Racer", "Default"))));

            Assert.Single(results, r => r.Success);
            Assert.All(results.Where(r => !r.Success), r => Assert.Equal(CharacterCreationMessages.CharacterNameAlreadyExists, r.ErrorMessage));
            Assert.Equal(1, await CharacterCount(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task SameNameForAnotherCustomer_Succeeds(SchemaSource source)
        {
            Guid customerA = Guid.NewGuid();
            Guid customerB = Guid.NewGuid();
            Guid userA = await Seed(source, customerA);
            Guid userB = await Seed(source, customerB);
            UsersRepository users = Repository(source);

            Assert.True((await users.CreateCharacterUsingDefaultCharacterValues(customerA, userA, "Shared", "Default")).Success);
            Assert.True((await users.CreateCharacterUsingDefaultCharacterValues(customerB, userB, "Shared", "Default")).Success);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task CreateCharacter_SameNameInAnotherCase_ReturnsInvalidCharacterName(SchemaSource source)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(await File.ReadAllTextAsync(UniqueCharacterNamesMigrationTests.ScriptPath()));

            // AddNewCustomer seeds a customer, a user and the MaleWarrior class that AddCharacter needs.
            string customerName = $"Unique{Guid.NewGuid():N}";
            await connection.ExecuteAsync("CALL AddNewCustomer(@CustomerName, 'Test', 'User', @Email, 'pw')",
                new { CustomerName = customerName, Email = $"{customerName}@example.com" });
            (Guid customerGUID, Guid userGUID) = await connection.QuerySingleAsync<(Guid, Guid)>(
                @"SELECT C.CustomerGUID, U.UserGUID FROM Customers C
                  INNER JOIN Users U ON U.CustomerGUID = C.CustomerGUID
                  WHERE C.CustomerName = @CustomerName",
                new { CustomerName = customerName });
            Guid userSessionGUID = await connection.QuerySingleAsync<Guid>(
                @"INSERT INTO UserSessions (CustomerGUID, UserGUID, LoginDate) VALUES (@CustomerGUID, @UserGUID, LOCALTIMESTAMP)
                  RETURNING UserSessionGUID",
                new { CustomerGUID = customerGUID, UserGUID = userGUID });
            UsersRepository users = Repository(source);

            CreateCharacter first = await users.CreateCharacter(customerGUID, userSessionGUID, "Dana", "MaleWarrior");
            CreateCharacter second = await users.CreateCharacter(customerGUID, userSessionGUID, "dana", "MaleWarrior");

            Assert.True(first.Success, first.ErrorMessage);
            Assert.False(second.Success);
            Assert.Equal("Invalid Character Name", second.ErrorMessage);
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*)::INT FROM Characters WHERE CustomerGUID = @CustomerGUID AND LOWER(CharName) = 'dana'",
                new { CustomerGUID = customerGUID }));
        }

        private UsersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UsersRepository>.Instance);

        /// <summary>Applies the unique-name migration, adds a user and a "Default" default character set for the customer; returns the user.</summary>
        private async Task<Guid> Seed(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(await File.ReadAllTextAsync(UniqueCharacterNamesMigrationTests.ScriptPath()));
            await connection.ExecuteAsync(
                @"INSERT INTO DefaultCharacterValues (CustomerGUID, DefaultSetName, StartingMapName, X, Y, Z)
                  VALUES (@CustomerGUID, 'Default', 'StartMap', 0, 0, 0)",
                new { CustomerGUID = customerGUID });

            return await connection.QuerySingleAsync<Guid>(
                @"INSERT INTO Users (CustomerGUID, FirstName, LastName, Email, PasswordHash, Role)
                  VALUES (@CustomerGUID, 'Test', 'User', @Email, 'x', 'Player')
                  RETURNING UserGUID",
                new { CustomerGUID = customerGUID, Email = $"{Guid.NewGuid():N}@example.com" });
        }

        private async Task<int> CharacterCount(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<int>("SELECT COUNT(*)::INT FROM Characters WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID });
        }
    }
}
