using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// JoinMapByCharName must not route a character into another player group's zone instance. PlayerGroupType 0 means
    /// public instances only, and a group type the character has no group of is refused rather than treated as public.
    /// </summary>
    public class ZoneInstanceGroupRoutingTests : IClassFixture<PostgresTestDatabases>
    {
        private const int ReadyStatus = 2;
        private const int PartyGroupType = 1;
        private const string ZoneName = "GroupRoutingZone";

        private readonly PostgresTestDatabases _databases;

        public ZoneInstanceGroupRoutingTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task PublicRequest_SpinsUpRatherThanJoiningAGroupInstance(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            (int mapID, int worldServerID) = await SeedZone(source, customerGUID);
            int groupInstanceID = await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, playerGroupID: 4242);
            await SeedCharacter(source, customerGUID, "Outsider");

            JoinMapByCharName result = await Repository(source).JoinMapByCharName(customerGUID, "Outsider", ZoneName, playerGroupType: 0);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEqual(groupInstanceID, result.MapInstanceID);
            Assert.True(result.NeedToStartupMap);
            Assert.Equal(0, await InstanceGroup(source, customerGUID, result.MapInstanceID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task PublicRequest_JoinsThePublicInstanceNotTheGroupInstance(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            (int mapID, int worldServerID) = await SeedZone(source, customerGUID);
            // The group instance has the lower ID, so it wins the tie-break whenever the group filter lets it through.
            await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, playerGroupID: 4242);
            int publicInstanceID = await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7779, playerGroupID: null);
            await SeedCharacter(source, customerGUID, "Outsider");

            JoinMapByCharName result = await Repository(source).JoinMapByCharName(customerGUID, "Outsider", ZoneName, playerGroupType: 0);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(publicInstanceID, result.MapInstanceID);
            Assert.False(result.NeedToStartupMap);
        }

        // PlayerGroupMember only exists in the migrated schema; setup.sql alone has the older PlayerGroupCharacters.
        [PostgresFact]
        public async Task GroupRequest_FromACharacterOutsideTheGroupIsRefused()
        {
            const SchemaSource source = SchemaSource.Migrated;
            Guid customerGUID = Guid.NewGuid();
            (int mapID, int worldServerID) = await SeedZone(source, customerGUID);
            int memberID = await SeedCharacter(source, customerGUID, "Member");
            int groupID = await SeedGroup(source, customerGUID, PartyGroupType, memberID);
            await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, playerGroupID: groupID);
            int outsiderID = await SeedCharacter(source, customerGUID, "Outsider");

            JoinMapByCharName result = await Repository(source).JoinMapByCharName(customerGUID, "Outsider", ZoneName, PartyGroupType);

            Assert.False(result.Success);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.Equal(0, await ReservationCount(source, customerGUID, outsiderID));
        }

        [PostgresFact]
        public async Task GroupRequest_FromAMemberJoinsTheGroupInstance()
        {
            const SchemaSource source = SchemaSource.Migrated;
            Guid customerGUID = Guid.NewGuid();
            (int mapID, int worldServerID) = await SeedZone(source, customerGUID);
            int memberID = await SeedCharacter(source, customerGUID, "Member");
            int groupID = await SeedGroup(source, customerGUID, PartyGroupType, memberID);
            await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, playerGroupID: null);
            int groupInstanceID = await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7779, playerGroupID: groupID);

            JoinMapByCharName result = await Repository(source).JoinMapByCharName(customerGUID, "Member", ZoneName, PartyGroupType);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(groupInstanceID, result.MapInstanceID);
        }

        private CharactersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private NpgsqlConnection Connection(SchemaSource source) => new(_databases.ConnectionString(source));

        private async Task<(int MapID, int WorldServerID)> SeedZone(SchemaSource source, Guid customerGUID)
        {
            await using NpgsqlConnection connection = Connection(source);

            int mapID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, Width, Height, ZoneName)
                  VALUES (@CustomerGUID, @ZoneName, 1, 1, @ZoneName)
                  RETURNING MapID",
                new { CustomerGUID = customerGUID, ZoneName });

            int worldServerID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO WorldServers (CustomerGUID, ServerIP, MaxNumberOfInstances, ActiveStartTime, ServerStatus, StartingMapInstancePort, ServerRegion)
                  VALUES (@CustomerGUID, '10.0.0.1', 10, NOW(), 1, 7778, @ServerRegion)
                  RETURNING WorldServerID",
                new { CustomerGUID = customerGUID, ServerRegion = ServerRegions.Default });

            return (mapID, worldServerID);
        }

        private async Task<int> SeedMapInstance(SchemaSource source, Guid customerGUID, int mapID, int worldServerID, int port, int? playerGroupID)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, PlayerGroupID, LastUpdateFromServer)
                  VALUES (@CustomerGUID, @WorldServerID, @MapID, @Port, @Status, @PlayerGroupID, NOW())
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, WorldServerID = worldServerID, MapID = mapID, Port = port, Status = ReadyStatus, PlayerGroupID = playerGroupID });
        }

        private async Task<int> SeedCharacter(SchemaSource source, Guid customerGUID, string charName)
        {
            // The two schemas disagree on Characters: setup.sql has required Email and stat columns the migrated table dropped.
            string sql = source == SchemaSource.SetupSql
                ? @"INSERT INTO Characters (CustomerGUID, Email, CharName, MapName, X, Y, Z, Perception, Acrobatics, Climb, Stealth, ClassID)
                    VALUES (@CustomerGUID, '', @CharName, @ZoneName, 0, 0, 0, 0, 0, 0, 0, 1)
                    RETURNING CharacterID"
                : @"INSERT INTO Characters (CustomerGUID, CharName, MapName, X, Y, Z, ClassID)
                    VALUES (@CustomerGUID, @CharName, @ZoneName, 0, 0, 0, 1)
                    RETURNING CharacterID";

            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(sql, new { CustomerGUID = customerGUID, CharName = charName, ZoneName });
        }

        private async Task<int> SeedGroup(SchemaSource source, Guid customerGUID, int playerGroupType, int memberCharacterID)
        {
            await using NpgsqlConnection connection = Connection(source);

            int groupID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO PlayerGroup (CustomerGUID, PlayerGroupName, PlayerGroupTypeID)
                  VALUES (@CustomerGUID, 'Private group', @PlayerGroupType)
                  RETURNING PlayerGroupID",
                new { CustomerGUID = customerGUID, PlayerGroupType = playerGroupType });

            await connection.ExecuteAsync(
                @"INSERT INTO PlayerGroupMember (CustomerGUID, PlayerGroupID, CharacterID, DateAdded)
                  VALUES (@CustomerGUID, @PlayerGroupID, @CharacterID, NOW())",
                new { CustomerGUID = customerGUID, PlayerGroupID = groupID, CharacterID = memberCharacterID });

            return groupID;
        }

        private async Task<int> InstanceGroup(SchemaSource source, Guid customerGUID, int mapInstanceID)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COALESCE(PlayerGroupID, 0) FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = mapInstanceID });
        }

        private async Task<int> ReservationCount(SchemaSource source, Guid customerGUID, int characterID)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });
        }
    }
}
