using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
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
