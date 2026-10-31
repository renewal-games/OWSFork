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
    /// CreateCharacterUsingDefaultCharacterValues fails on an unknown default set, and reports a name taken by a concurrent
    /// create or in another case once UniqueCharacterNames_2026-10-30.sql is applied. Runs on the setup.sql schema plus
    /// that script: the query inserts Characters.Email and the stat columns, which the Samsara migrations drop.
    /// </summary>
    public class CreateCharacterUsingDefaultCharacterValuesTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public CreateCharacterUsingDefaultCharacterValuesTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        public async Task UnknownDefaultSet_Fails(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = await Seed(source, customerGUID);

            SuccessAndErrorMessage result = await Repository(source).CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "Nobody", "NoSuchSet");

            Assert.False(result.Success);
            Assert.Equal("Unknown default character set", result.ErrorMessage);
            Assert.Equal(0, await CharacterCount(source, customerGUID));
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
            Assert.Equal("Character name already exists!", second.ErrorMessage);
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
            Assert.All(results.Where(r => !r.Success), r => Assert.Equal("Character name already exists!", r.ErrorMessage));
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

        private UsersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

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
