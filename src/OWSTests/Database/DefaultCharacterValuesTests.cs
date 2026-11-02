using System;
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
    /// UsersRepository.CreateCharacterUsingDefaultCharacterValues copies a DefaultCharacterValues row into Characters.
    /// A set name that matches no row must fail instead of reporting a character that was never created.
    /// SetupSql only: the migrated schema lacks the Characters columns this insert writes.
    /// </summary>
    public class DefaultCharacterValuesTests : IClassFixture<PostgresTestDatabases>
    {
        private const SchemaSource Source = SchemaSource.SetupSql;

        private readonly PostgresTestDatabases _databases;

        public DefaultCharacterValuesTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData("Starter")]
        public async Task KnownSet_CreatesCharacter(string setName)
        {
            (UsersRepository users, Guid customerGUID, Guid userGUID) = await Seed();

            SuccessAndErrorMessage result = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "KnownSetChar", setName);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, await CharacterCount(customerGUID, "KnownSetChar"));
            Assert.Equal(1, await CustomDataCount(customerGUID, "KnownSetChar"));
        }

        [PostgresTheory]
        [InlineData("doesnotexist")]
        [InlineData(null)]
        public async Task UnknownSet_FailsAndCreatesNothing(string setName)
        {
            (UsersRepository users, Guid customerGUID, Guid userGUID) = await Seed();

            SuccessAndErrorMessage result = await users.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, "UnknownSetChar", setName);

            Assert.False(result.Success);
            Assert.Equal(CharacterCreationMessages.UnknownDefaultCharacterSet, result.ErrorMessage);
            Assert.Equal(0, await CharacterCount(customerGUID, "UnknownSetChar"));
        }

        private async Task<(UsersRepository, Guid, Guid)> Seed()
        {
            UsersRepository users = new(Microsoft.Extensions.Options.Options.Create(
                new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(Source) }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UsersRepository>.Instance);
            Guid customerGUID = Guid.NewGuid();

            Assert.True((await users.RegisterUser(customerGUID, "defaults@example.com", "pw", "Default", "User")).Success);

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            Guid userGUID = await connection.QuerySingleAsync<Guid>(
                "SELECT UserGUID FROM Users WHERE CustomerGUID = @CustomerGUID AND Email = 'defaults@example.com'",
                new { CustomerGUID = customerGUID });
            await connection.ExecuteAsync(
                @"WITH DCV AS (
                      INSERT INTO DefaultCharacterValues (CustomerGUID, DefaultSetName, StartingMapName, X, Y, Z)
                      VALUES (@CustomerGUID, 'Starter', 'StartMap', 1, 2, 3)
                      RETURNING DefaultCharacterValuesID)
                  INSERT INTO DefaultCustomCharacterData (CustomerGUID, DefaultCharacterValuesID, CustomFieldName, FieldValue)
                  SELECT @CustomerGUID, DefaultCharacterValuesID, 'Hair', 'Short' FROM DCV",
                new { CustomerGUID = customerGUID });

            return (users, customerGUID, userGUID);
        }

        private async Task<int> CharacterCount(Guid customerGUID, string characterName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            return await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*)::INT FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName",
                new { CustomerGUID = customerGUID, CharName = characterName });
        }

        private async Task<int> CustomDataCount(Guid customerGUID, string characterName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            return await connection.QuerySingleAsync<int>(
                @"SELECT COUNT(*)::INT FROM CustomCharacterData CCD
                  INNER JOIN Characters C ON C.CustomerGUID = CCD.CustomerGUID AND C.CharacterID = CCD.CharacterID
                  WHERE C.CustomerGUID = @CustomerGUID AND C.CharName = @CharName",
                new { CustomerGUID = customerGUID, CharName = characterName });
        }
    }
}
