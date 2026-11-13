using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    public class CharacterInstanceHandoffTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public CharacterInstanceHandoffTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql, true, 2, "Destination")]
        [InlineData(SchemaSource.Migrated, true, 2, "Destination")]
        [InlineData(SchemaSource.SetupSql, false, 3, "Destination")]
        [InlineData(SchemaSource.Migrated, false, 3, "Destination")]
        [InlineData(SchemaSource.SetupSql, false, 2, "")]
        [InlineData(SchemaSource.Migrated, false, 2, "")]
        [InlineData(SchemaSource.SetupSql, false, 2, "   ")]
        [InlineData(SchemaSource.Migrated, false, 2, "   ")]
        [InlineData(SchemaSource.SetupSql, false, 1, "Destination")]
        [InlineData(SchemaSource.Migrated, false, 1, "Destination")]
        [InlineData(SchemaSource.SetupSql, false, 2, "Destination")]
        [InlineData(SchemaSource.Migrated, false, 2, "Destination")]
        public async Task Handoff_OnlyAcceptsLiveDestinationWithZoneName(
            SchemaSource source, bool deleteDestination, int status, string zoneName)
        {
            Guid customerGUID = Guid.NewGuid();
            var parameters = new { CustomerGUID = customerGUID, ZoneName = zoneName, Status = status };
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int mapID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, Width, Height, ZoneName)
                  VALUES (@CustomerGUID, '/Game/Maps/Destination', 1, 1, @ZoneName) RETURNING MapID", parameters);
            int instanceID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@CustomerGUID, 7, @MapID, 7777, @Status, NOW()) RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, MapID = mapID, Status = status });
            string characterSql = source == SchemaSource.SetupSql
                ? @"INSERT INTO Characters (CustomerGUID, Email, CharName, MapName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                    VALUES (@CustomerGUID, '', 'Joiner', 'OldZone', 0, 0, 0, 0, 0, 0, 0, 1) RETURNING CharacterID"
                : @"INSERT INTO Characters (CustomerGUID, CharName, MapName, X, Y, Z, ClassID)
                    VALUES (@CustomerGUID, 'Joiner', 'OldZone', 0, 0, 0, 1) RETURNING CharacterID";
            int characterID = await connection.ExecuteScalarAsync<int>(characterSql, parameters);
            // Preserve an existing membership on failure, including when the destination was deleted after joining.
            var membership = new { CustomerGUID = customerGUID, CharacterID = characterID, MapInstanceID = instanceID };
            await connection.ExecuteAsync(
                "INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID) VALUES (@CustomerGUID, @CharacterID, @MapInstanceID)", membership);
            if (deleteDestination)
            {
                await connection.ExecuteAsync("DELETE FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID", membership);
                await connection.ExecuteAsync("DELETE FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", membership);
            }

            var repository = new CharactersRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBConnectionString = _databases.ConnectionString(source)
            }));
            bool valid = !deleteDestination && status is 1 or 2 && !string.IsNullOrWhiteSpace(zoneName);
            if (valid)
                await repository.AddCharacterToMapInstanceByCharName(customerGUID, "Joiner", instanceID);
            else
                await Assert.ThrowsAnyAsync<Exception>(() => repository.AddCharacterToMapInstanceByCharName(customerGUID, "Joiner", instanceID));

            Assert.Equal(valid ? zoneName : "OldZone", await connection.QuerySingleAsync<string>(
                "SELECT MapName FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", membership));
            Assert.Equal(deleteDestination ? 0 : 1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", membership));
            if (!deleteDestination)
                Assert.Equal(instanceID, await connection.QuerySingleAsync<int>(
                    "SELECT MapInstanceID FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", membership));
        }
    }
}
