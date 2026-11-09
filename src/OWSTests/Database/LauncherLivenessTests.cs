using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OWSData.Models;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSInstanceManagement.Requests.Instance;
using OWSShared.Implementations;
using OWSShared.Messages;
using OWSShared.Options;
using OWSShared.RequestPayloads;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Issue #172. A launcher that crashed keeps ServerStatus = 1, so spin-up must skip it by its heartbeat, and a spin-up
    /// whose command never reached a launcher must not leave its Status 1 zone instance row behind for joiners to wait on.
    /// </summary>
    public class LauncherLivenessTests : IClassFixture<PostgresTestDatabases>
    {
        private const int StartingStatus = 1;
        private const int ReadyStatus = 2;
        private const string ZoneName = "LivenessZone";
        private const string OtherZoneName = "LivenessOtherZone";

        private readonly PostgresTestDatabases _databases;

        public LauncherLivenessTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SpinUp_SkipsALauncherWhoseHeartbeatStopped(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int mapID = await SeedMap(source, customerGUID, ZoneName);
            int otherMapID = await SeedMap(source, customerGUID, OtherZoneName);
            // The crashed launcher has no instances left, so it sorts first on load unless the heartbeat filter drops it.
            int crashedID = await SeedWorldServer(source, customerGUID, "NOW() - INTERVAL '10 minutes'");
            int liveID = await SeedWorldServer(source, customerGUID, "NOW()");
            await SeedMapInstance(source, customerGUID, otherMapID, liveID, port: 7778, ReadyStatus);
            await SeedCharacter(source, customerGUID, "Joiner");

            JoinMapByCharName result = await Characters(source).JoinMapByCharName(customerGUID, "Joiner", ZoneName, playerGroupType: 0);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.NeedToStartupMap);
            Assert.Equal(liveID, result.WorldServerID);
            Assert.NotEqual(crashedID, result.WorldServerID);
            Assert.Equal(0, await InstanceCount(source, customerGUID, crashedID));
            Assert.Equal(mapID, await InstanceMap(source, customerGUID, result.MapInstanceID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SpinUp_UsesALauncherAgainOnceItChecksIn(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedMap(source, customerGUID, ZoneName);
            int worldServerID = await SeedWorldServer(source, customerGUID, "NULL");
            await SeedCharacter(source, customerGUID, "Joiner");

            JoinMapByCharName beforeCheckIn = await Characters(source).JoinMapByCharName(customerGUID, "Joiner", ZoneName, playerGroupType: 0);

            Assert.False(beforeCheckIn.Success);
            Assert.Equal(0, await InstanceCount(source, customerGUID, worldServerID));

            // What the launcher's health monitor does every cycle.
            var checkIn = new GetZoneInstancesForWorldServerRequest { Request = new WorldServerIDRequestPayload { WorldServerID = worldServerID } };
            checkIn.SetData(InstanceManagement(source), new HeaderCustomerGUID { CustomerGUID = customerGUID });
            await checkIn.Handle();

            JoinMapByCharName afterCheckIn = await Characters(source).JoinMapByCharName(customerGUID, "Joiner", ZoneName, playerGroupType: 0);

            Assert.True(afterCheckIn.Success, afterCheckIn.ErrorMessage);
            Assert.Equal(worldServerID, afterCheckIn.WorldServerID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task StartingTheLauncher_MakesItEligibleBeforeItsFirstCheckIn(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid launcherGUID = Guid.NewGuid();
            await SeedMap(source, customerGUID, ZoneName);
            await SeedCharacter(source, customerGUID, "Joiner");
            InstanceManagementRepository instanceManagement = InstanceManagement(source);
            await instanceManagement.RegisterLauncher(customerGUID, launcherGUID.ToString(), "10.0.0.1", 10, "10.0.0.1", 7778, ServerRegions.Default);

            int worldServerID = await instanceManagement.StartWorldServer(customerGUID, launcherGUID.ToString());
            JoinMapByCharName result = await Characters(source).JoinMapByCharName(customerGUID, "Joiner", ZoneName, playerGroupType: 0);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(worldServerID, result.WorldServerID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task FailedSpinUpPublish_LeavesNoStartingInstanceBehind(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedMap(source, customerGUID, ZoneName);
            await SeedWorldServer(source, customerGUID, "NOW()");
            int characterID = await SeedCharacter(source, customerGUID, "Joiner");

            // Nothing listens on port 1: the connection fails before anything is published, so no launcher can get the command.
            var request = new OWSInstanceManagement.Requests.Instance.GetServerToConnectToRequest { CharacterName = "Joiner", ZoneName = ZoneName };
            request.SetData(Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions { RabbitMQHostName = "127.0.0.1", RabbitMQPort = 1, RabbitMQUserName = "guest", RabbitMQPassword = "guest" }),
                Characters(source), new HeaderCustomerGUID { CustomerGUID = customerGUID });

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.True(result.MapInstanceID > 0);
            Assert.Equal(0, await Scalar(source, "SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = customerGUID }));
            Assert.Equal(0, await Scalar(source, "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SpinUpWhoseConfirmWasLost_KeepsTheStartingInstance(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedMap(source, customerGUID, ZoneName);
            await SeedWorldServer(source, customerGUID, "NOW()");
            int characterID = await SeedCharacter(source, customerGUID, "Joiner");

            // The broker queued the command (a launcher may already be starting the server) and then the confirm was lost.
            var request = new ConfirmLostGetServerToConnectToRequest { CharacterName = "Joiner", ZoneName = ZoneName };
            request.SetData(Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions()), Characters(source), new HeaderCustomerGUID { CustomerGUID = customerGUID });

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.NotNull(request.Published);
            // The row, and with it the port, stays reserved for that server; only this joiner's reservation goes.
            Assert.Equal(1, await InstanceRows(source, customerGUID, result.MapInstanceID));
            Assert.Equal(StartingStatus, await Scalar(source, "SELECT Status FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = result.MapInstanceID }));
            Assert.Equal(request.Published.Port, await Scalar(source, "SELECT Port FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = result.MapInstanceID }));
            Assert.Equal(0, await Scalar(source, "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
        }

        private sealed class ConfirmLostGetServerToConnectToRequest : OWSInstanceManagement.Requests.Instance.GetServerToConnectToRequest
        {
            public MQSpinUpServerMessage Published { get; private set; }

            protected override void PublishSpinUpMessage(MQSpinUpServerMessage serverSpinUpMessage)
            {
                Published = serverSpinUpMessage;
                throw new LauncherCommandNotDeliveredException("RabbitMQ did not confirm the command.", new TimeoutException());
            }
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task RemoveUnstartedMapInstance_DeletesAStartingRowAndEveryReservationOnIt(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int mapID = await SeedMap(source, customerGUID, ZoneName);
            int worldServerID = await SeedWorldServer(source, customerGUID, "NOW()");
            int instanceID = await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, StartingStatus);
            await Reserve(source, customerGUID, await SeedCharacter(source, customerGUID, "First"), instanceID);
            await Reserve(source, customerGUID, await SeedCharacter(source, customerGUID, "Second"), instanceID);

            Assert.True(await Characters(source).RemoveUnstartedMapInstance(customerGUID, instanceID));

            Assert.Equal(0, await InstanceRows(source, customerGUID, instanceID));
            Assert.Equal(0, await ReservationRows(source, customerGUID, instanceID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task RemoveUnstartedMapInstance_LeavesAReadyRowAlone(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int mapID = await SeedMap(source, customerGUID, ZoneName);
            int worldServerID = await SeedWorldServer(source, customerGUID, "NOW()");
            int instanceID = await SeedMapInstance(source, customerGUID, mapID, worldServerID, port: 7778, ReadyStatus);
            await Reserve(source, customerGUID, await SeedCharacter(source, customerGUID, "Player"), instanceID);

            Assert.False(await Characters(source).RemoveUnstartedMapInstance(customerGUID, instanceID));

            Assert.Equal(1, await InstanceRows(source, customerGUID, instanceID));
            Assert.Equal(1, await ReservationRows(source, customerGUID, instanceID));
        }

        private StorageOptions Storage(SchemaSource source) =>
            new() { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) };

        private CharactersRepository Characters(SchemaSource source) => new(Microsoft.Extensions.Options.Options.Create(Storage(source)));

        private InstanceManagementRepository InstanceManagement(SchemaSource source) => new(Microsoft.Extensions.Options.Options.Create(Storage(source)));

        private NpgsqlConnection Connection(SchemaSource source) => new(_databases.ConnectionString(source));

        private async Task<int> SeedMap(SchemaSource source, Guid customerGUID, string zoneName)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, Width, Height, ZoneName)
                  VALUES (@CustomerGUID, @ZoneName, 1, 1, @ZoneName)
                  RETURNING MapID",
                new { CustomerGUID = customerGUID, ZoneName = zoneName });
        }

        // lastHeartbeatSql is a SQL expression, always one of the literals in this file.
        private async Task<int> SeedWorldServer(SchemaSource source, Guid customerGUID, string lastHeartbeatSql)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                $@"INSERT INTO WorldServers (CustomerGUID, ServerIP, MaxNumberOfInstances, ActiveStartTime, ServerStatus, StartingMapInstancePort, ServerRegion, LastHeartbeat)
                  VALUES (@CustomerGUID, '10.0.0.1', 10, NOW(), 1, 7778, @ServerRegion, {lastHeartbeatSql})
                  RETURNING WorldServerID",
                new { CustomerGUID = customerGUID, ServerRegion = ServerRegions.Default });
        }

        private async Task<int> SeedMapInstance(SchemaSource source, Guid customerGUID, int mapID, int worldServerID, int port, int status)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@CustomerGUID, @WorldServerID, @MapID, @Port, @Status, NOW())
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, WorldServerID = worldServerID, MapID = mapID, Port = port, Status = status });
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

        private async Task Reserve(SchemaSource source, Guid customerGUID, int characterID, int mapInstanceID)
        {
            await using NpgsqlConnection connection = Connection(source);
            await connection.ExecuteAsync(
                "INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID) VALUES (@CustomerGUID, @CharacterID, @MapInstanceID)",
                new { CustomerGUID = customerGUID, CharacterID = characterID, MapInstanceID = mapInstanceID });
        }

        private Task<int> InstanceCount(SchemaSource source, Guid customerGUID, int worldServerID) =>
            Scalar(source, "SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND WorldServerID = @WorldServerID",
                new { CustomerGUID = customerGUID, WorldServerID = worldServerID });

        private Task<int> InstanceMap(SchemaSource source, Guid customerGUID, int mapInstanceID) =>
            Scalar(source, "SELECT MapID FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = mapInstanceID });

        private Task<int> InstanceRows(SchemaSource source, Guid customerGUID, int mapInstanceID) =>
            Scalar(source, "SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = mapInstanceID });

        private Task<int> ReservationRows(SchemaSource source, Guid customerGUID, int mapInstanceID) =>
            Scalar(source, "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = mapInstanceID });

        private async Task<int> Scalar(SchemaSource source, string sql, object parameters)
        {
            await using NpgsqlConnection connection = Connection(source);
            return await connection.ExecuteScalarAsync<int>(sql, parameters);
        }
    }
}
