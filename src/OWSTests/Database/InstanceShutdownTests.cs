using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSInstanceManagement.Requests.Instance;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// ShutDownServerInstance must not leave a MapInstances row at status 3 (shutting down) when no launcher will
    /// complete the shutdown: an unknown instance fails, the message goes to the launcher that owns the row, and a
    /// failed publish puts the previous status back.
    /// </summary>
    public class InstanceShutdownTests : IClassFixture<PostgresTestDatabases>
    {
        private const int ReadyStatus = 2;
        private const int ShuttingDownStatus = 3;

        private readonly PostgresTestDatabases _databases;

        public InstanceShutdownTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SetZoneInstanceStatus_UnknownInstanceFails(SchemaSource source)
        {
            SuccessAndErrorMessage result = await Repository(source).SetZoneInstanceStatus(Guid.NewGuid(), 999999, ShuttingDownStatus);

            Assert.False(result.Success);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task ShutDown_UnknownInstanceFailsWithoutPublishing(SchemaSource source)
        {
            var request = new RecordingShutDownRequest { WorldServerID = 1, ZoneInstanceID = 999999 };

            SuccessAndErrorMessage result = await Handle(request, source, Guid.NewGuid());

            Assert.False(result.Success);
            Assert.Empty(request.PublishedTo);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task ShutDown_PublishesToTheOwningWorldServer(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedMapInstance(source, customerGUID, worldServerID: 7, ReadyStatus);
            var request = new RecordingShutDownRequest { WorldServerID = 8, ZoneInstanceID = zoneInstanceID };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(new[] { 7 }, request.PublishedTo);
            Assert.Equal(ShuttingDownStatus, await Status(source, customerGUID, zoneInstanceID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task ShutDown_FailedPublishRestoresStatus(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedMapInstance(source, customerGUID, worldServerID: 7, ReadyStatus);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID, PublishFails = true };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.False(result.Success);
            Assert.Equal(ReadyStatus, await Status(source, customerGUID, zoneInstanceID));
        }

        //#173: the launcher decides an idle shutdown from a player count that can be a monitor tick old. A player routed to
        //the instance since then holds a CharOnMapInstance reservation, and shutting down would delete it with the instance.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task IdleShutDown_KeepsAnInstanceWithAFreshReservation(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedIdleMapInstance(source, customerGUID, emptyMinutesAgo: 10);
            await Reserve(source, customerGUID, zoneInstanceID, characterID: 42);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID, OnlyIfIdle = true };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.False(result.Success);
            Assert.Empty(request.PublishedTo);
            Assert.Equal(ReadyStatus, await Status(source, customerGUID, zoneInstanceID));
            Assert.Equal(1, await Reservations(source, customerGUID, zoneInstanceID));
        }

        //The race itself: a join holds the allocation lock and has written, but not yet committed, its reservation. The
        //shutdown has to wait for the join and then see the reservation, not slip in between.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task IdleShutDown_WaitsForAJoinInProgress_AndKeepsItsReservation(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedIdleMapInstance(source, customerGUID, emptyMinutesAgo: 10);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID, OnlyIfIdle = true };

            await using var join = new NpgsqlConnection(_databases.ConnectionString(source));
            await join.OpenAsync();
            await using (var transaction = await join.BeginTransactionAsync())
            {
                await join.ExecuteAsync(OWSData.SQL.PostgresQueries.AcquireMapAllocationLock, new { CustomerGUID = customerGUID }, transaction);
                await join.ExecuteAsync(
                    "INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID) VALUES (@CustomerGUID, 42, @MapInstanceID)",
                    new { CustomerGUID = customerGUID, MapInstanceID = zoneInstanceID }, transaction);

                Task<SuccessAndErrorMessage> shutdown = Handle(request, source, customerGUID);
                await Task.Delay(500);
                Assert.False(shutdown.IsCompleted, "The shutdown did not wait for the join's allocation lock.");

                await transaction.CommitAsync();

                SuccessAndErrorMessage result = await shutdown;
                Assert.False(result.Success);
            }

            Assert.Empty(request.PublishedTo);
            Assert.Equal(ReadyStatus, await Status(source, customerGUID, zoneInstanceID));
            Assert.Equal(1, await Reservations(source, customerGUID, zoneInstanceID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task IdleShutDown_ShutsDownAnInstanceThatIsStillIdle(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedIdleMapInstance(source, customerGUID, emptyMinutesAgo: 10);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID, OnlyIfIdle = true };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(new[] { 7 }, request.PublishedTo);
            Assert.Equal(ShuttingDownStatus, await Status(source, customerGUID, zoneInstanceID));
        }

        //The rest of the launcher's idle test, rechecked: a player report since the list was read, or an empty timer that
        //restarted (someone came and went), must also stop the shutdown.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql, 1, 10)]
        [InlineData(SchemaSource.Migrated, 1, 10)]
        [InlineData(SchemaSource.SetupSql, 0, 1)]
        [InlineData(SchemaSource.Migrated, 0, 1)]
        public async Task IdleShutDown_KeepsAnInstanceThatIsNoLongerIdle(SchemaSource source, int reportedPlayers, int emptyMinutesAgo)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedIdleMapInstance(source, customerGUID, emptyMinutesAgo, reportedPlayers);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID, OnlyIfIdle = true };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.False(result.Success);
            Assert.Empty(request.PublishedTo);
            Assert.Equal(ReadyStatus, await Status(source, customerGUID, zoneInstanceID));
        }

        //Admin stops and the launcher's dead-server shutdown don't ask for the idle check and still go through.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task ShutDown_WithoutIdleCheck_ShutsDownDespiteAReservation(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedIdleMapInstance(source, customerGUID, emptyMinutesAgo: 0, reportedPlayers: 3);
            await Reserve(source, customerGUID, zoneInstanceID, characterID: 42);
            var request = new RecordingShutDownRequest { WorldServerID = 7, ZoneInstanceID = zoneInstanceID };

            SuccessAndErrorMessage result = await Handle(request, source, customerGUID);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(new[] { 7 }, request.PublishedTo);
            Assert.Equal(ShuttingDownStatus, await Status(source, customerGUID, zoneInstanceID));
        }

        //SpinUpServerInstance compares these values with the request, so a column that stopped mapping would refuse every spin-up.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task GetZoneInstance_ReturnsWorldServerAndPort_ToTheOwningCustomerOnly(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            int zoneInstanceID = await SeedMapInstance(source, customerGUID, worldServerID: 7, ReadyStatus);

            var row = await Repository(source).GetZoneInstance(customerGUID, zoneInstanceID);
            var otherCustomersView = await Repository(source).GetZoneInstance(Guid.NewGuid(), zoneInstanceID);

            Assert.Equal(zoneInstanceID, row.MapInstanceID);
            Assert.Equal(7, row.WorldServerID);
            Assert.Equal(7777, row.Port);
            Assert.Equal(0, otherCustomersView.MapInstanceID);
        }

        //A zone server asks for its own row by IP and port. Anything but exactly one match is an empty row, so an IP that two
        //world servers share must not hand one zone server the other's instance.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task GetServerInstanceFromPort_ReturnsTheOneMatch_AndAnEmptyRowOtherwise(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int mapID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, Width, Height, ZoneName)
                  VALUES (@CustomerGUID, '/Game/Maps/L_ByPort', 1, 1, 'ByPortZone')
                  RETURNING MapID",
                new { CustomerGUID = customerGUID });
            async Task<int> SeedWorldServer() => await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO WorldServers (CustomerGUID, ServerIP, MaxNumberOfInstances, ActiveStartTime, ServerStatus, StartingMapInstancePort, ServerRegion)
                  VALUES (@CustomerGUID, '10.0.0.1', 10, NOW(), 1, 7778, @ServerRegion)
                  RETURNING WorldServerID",
                new { CustomerGUID = customerGUID, ServerRegion = ServerRegions.Default });
            async Task<int> SeedInstance(int worldServerID, int port) => await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@CustomerGUID, @WorldServerID, @MapID, @Port, 2, NOW())
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, WorldServerID = worldServerID, MapID = mapID, Port = port });
            int firstWorldServerID = await SeedWorldServer();
            int secondWorldServerID = await SeedWorldServer();
            int zoneInstanceID = await SeedInstance(firstWorldServerID, 7778);
            await SeedInstance(firstWorldServerID, 7779);
            await SeedInstance(secondWorldServerID, 7779);

            var row = await Repository(source).GetServerInstanceFromPort(customerGUID, "10.0.0.1", 7778);
            var unknownPort = await Repository(source).GetServerInstanceFromPort(customerGUID, "10.0.0.1", 7780);
            var sharedPort = await Repository(source).GetServerInstanceFromPort(customerGUID, "10.0.0.1", 7779);

            Assert.Equal(zoneInstanceID, row.MapInstanceID);
            Assert.Equal("ByPortZone", row.ZoneName);
            Assert.Equal(0, unknownPort.MapInstanceID);
            Assert.Equal(0, sharedPort.MapInstanceID);
        }

        //The launcher and SpinUpServerInstance refuse a command this row doesn't match, so a wrong column here would refuse every spin-up.
        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task GetZoneInstanceForSpinUp_MatchesTheRow_ForItsCustomerOnly(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int mapID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, MapData, Width, Height, ZoneName, WorldCompContainsFilter, WorldCompListFilter, SoftPlayerCap, HardPlayerCap, MapMode, MinutesToShutdownAfterEmpty)
                  VALUES (@CustomerGUID, '/Game/Maps/L_SpinUp', @MapData, 1, 1, 'SpinUpZone', '', '', 1, 10, 1, 1)
                  RETURNING MapID",
                new { CustomerGUID = customerGUID, MapData = new byte[] { 0 } });
            int zoneInstanceID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@CustomerGUID, 7, @MapID, 7777, 1, NOW())
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, MapID = mapID });

            var row = await Repository(source).GetZoneInstanceForSpinUp(customerGUID, zoneInstanceID);

            Assert.True(row.MatchesSpinUp(7, zoneInstanceID, "/Game/Maps/L_SpinUp", 7777));
            Assert.False(row.MatchesSpinUp(8, zoneInstanceID, "/Game/Maps/L_SpinUp", 7777));
            Assert.False(row.MatchesSpinUp(7, zoneInstanceID + 1, "/Game/Maps/L_SpinUp", 7777));
            Assert.False(row.MatchesSpinUp(7, zoneInstanceID, "/Game/Maps/L_Other", 7777));
            Assert.False(row.MatchesSpinUp(7, zoneInstanceID, "/Game/Maps/L_SpinUp", 7778));
            Assert.Null(await Repository(source).GetZoneInstanceForSpinUp(Guid.NewGuid(), zoneInstanceID));
        }

        private async Task<SuccessAndErrorMessage> Handle(RecordingShutDownRequest request, SchemaSource source, Guid customerGUID)
        {
            request.SetData(Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions()), Repository(source),
                new HeaderCustomerGUID { CustomerGUID = customerGUID });

            IActionResult result = await request.Handle();

            return Assert.IsType<SuccessAndErrorMessage>(Assert.IsType<OkObjectResult>(result).Value);
        }

        private InstanceManagementRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private async Task<int> SeedMapInstance(SchemaSource source, Guid customerGUID, int worldServerID, int status)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@CustomerGUID, @WorldServerID, 1, 7777, @Status, NOW())
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, WorldServerID = worldServerID, Status = status });
        }

        //A ready instance on world server 7 whose zone shuts down after 5 empty minutes. emptyMinutesAgo is when its
        //empty timer started; reportedPlayers above 0 has no empty timer, as UpdateNumberOfPlayers leaves it.
        private async Task<int> SeedIdleMapInstance(SchemaSource source, Guid customerGUID, int emptyMinutesAgo, int reportedPlayers = 0)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int mapID = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Maps (CustomerGUID, MapName, MapData, Width, Height, ZoneName, WorldCompContainsFilter, WorldCompListFilter, SoftPlayerCap, HardPlayerCap, MapMode, MinutesToShutdownAfterEmpty)
                  VALUES (@CustomerGUID, '/Game/Maps/L_Idle', @MapData, 1, 1, 'IdleZone', '', '', 1, 10, 1, 5)
                  RETURNING MapID",
                new { CustomerGUID = customerGUID, MapData = new byte[] { 0 } });
            return await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, NumberOfReportedPlayers, LastUpdateFromServer, LastServerEmptyDate)
                  VALUES (@CustomerGUID, 7, @MapID, 7777, 2, @ReportedPlayers, NOW(),
                          CASE WHEN @ReportedPlayers = 0 THEN NOW()::TIMESTAMP - make_interval(mins => @EmptyMinutesAgo) END)
                  RETURNING MapInstanceID",
                new { CustomerGUID = customerGUID, MapID = mapID, ReportedPlayers = reportedPlayers, EmptyMinutesAgo = emptyMinutesAgo });
        }

        private async Task Reserve(SchemaSource source, Guid customerGUID, int zoneInstanceID, int characterID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                "INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID) VALUES (@CustomerGUID, @CharacterID, @MapInstanceID)",
                new { CustomerGUID = customerGUID, CharacterID = characterID, MapInstanceID = zoneInstanceID });
        }

        private async Task<int> Reservations(SchemaSource source, Guid customerGUID, int zoneInstanceID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = zoneInstanceID });
        }

        private async Task<int> Status(SchemaSource source, Guid customerGUID, int zoneInstanceID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.ExecuteScalarAsync<int>(
                "SELECT Status FROM MapInstances WHERE CustomerGUID = @CustomerGUID AND MapInstanceID = @MapInstanceID",
                new { CustomerGUID = customerGUID, MapInstanceID = zoneInstanceID });
        }

        /// <summary>Records where the shutdown would be published instead of talking to RabbitMQ.</summary>
        private sealed class RecordingShutDownRequest : ShutDownServerInstanceRequest
        {
            public bool PublishFails { get; set; }

            public List<int> PublishedTo { get; } = new();

            protected override void PublishShutDownMessage(int worldServerID)
            {
                if (PublishFails)
                {
                    throw new InvalidOperationException("RabbitMQ is unreachable.");
                }

                PublishedTo.Add(worldServerID);
            }
        }
    }
}
