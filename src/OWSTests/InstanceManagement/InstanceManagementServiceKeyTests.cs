using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSShared;
using OWSShared.Middleware;
using OWSTests.Database;
using Xunit;

namespace OWSTests.InstanceManagement
{
    /// <summary>
    /// Hosts OWSInstanceManagement with the process-wide test service key (<see cref="TestServiceKey"/>) and
    /// both global-gate settings. With the global gate off, the endpoint filters must still reject keyless callers.
    /// </summary>
    public sealed class InstanceManagementHost : IAsyncLifetime
    {
        public const string ServiceKey = TestServiceKey.Key;

        private readonly PostgresTestDatabases _databases = new();

        public WebApplicationFactory<OWSInstanceManagement.Startup> Factory { get; private set; }
        public WebApplicationFactory<OWSInstanceManagement.Startup> RequiredFactory { get; private set; }
        public string ConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        public async Task InitializeAsync()
        {
            await _databases.InitializeAsync();

            // Without a test server the requests that pass the gate fail at the database; the gate tests don't get that far.
            string connectionString = PostgresTestDatabases.SkipReason == null
                ? _databases.ConnectionString(SchemaSource.Migrated)
                : "Host=127.0.0.1;Port=1;Username=none;Password=none";

            // Startup builds its own configuration from appsettings.json (this project's, in the test output) and
            // environment variables. RabbitMQ is only needed for the health check's settings; no test publishes.
            Environment.SetEnvironmentVariable("OWSStorageConfig__OWSDBBackend", "postgres");
            Environment.SetEnvironmentVariable("OWSStorageConfig__OWSDBConnectionString", connectionString);
            Environment.SetEnvironmentVariable("RabbitMQOptions__RabbitMQHostName", "localhost");
            Environment.SetEnvironmentVariable("RabbitMQOptions__RabbitMQUserName", "guest");
            Environment.SetEnvironmentVariable("RabbitMQOptions__RabbitMQPassword", "guest");

            // Startup reads OWS_REQUIRE_SERVICE_KEY when the server is built, so build it now and restore the variable.
            string previousRequire = Environment.GetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar);
            Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, "false");
            try
            {
                Factory = new WebApplicationFactory<OWSInstanceManagement.Startup>();
                _ = Factory.Server;
                Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, "true");
                RequiredFactory = new WebApplicationFactory<OWSInstanceManagement.Startup>();
                _ = RequiredFactory.Server;
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, previousRequire);
            }
        }

        public HttpClient CreateClient(string serviceKey = null, bool globalGateRequired = false)
        {
            HttpClient client = (globalGateRequired ? RequiredFactory : Factory).CreateClient();
            client.DefaultRequestHeaders.Add("X-CustomerGUID", Guid.NewGuid().ToString());
            if (serviceKey != null)
            {
                client.DefaultRequestHeaders.Add(SamsaraServiceKeyCheck.Header, serviceKey);
            }
            return client;
        }

        public async Task DisposeAsync()
        {
            Factory?.Dispose();
            RequiredFactory?.Dispose();
            await _databases.DisposeAsync();
        }
    }

    /// <summary>
    /// X-CustomerGUID is not a secret, so the endpoints that spin up, shut down and reap zone instances, and AddZone,
    /// also require X-Samsara-Service-Key. Map names that reach the launcher's command line are allowlisted.
    /// </summary>
    [Collection(OWSTests.CharacterPersistence.ServiceHostEnvironmentCollection.Name)]
    public class InstanceManagementServiceKeyTests : IClassFixture<InstanceManagementHost>
    {
        private readonly InstanceManagementHost _host;

        public InstanceManagementServiceKeyTests(InstanceManagementHost host)
        {
            _host = host;
        }

        private static object AddZoneBody(string mapName) => new
        {
            addOrUpdateZone = new { MapName = mapName, ZoneName = "Zone_" + Guid.NewGuid().ToString("N"), SoftPlayerCap = 10, HardPlayerCap = 20, MapMode = 1, MinutesToShutdownAfterEmpty = 1 }
        };

        [Theory]
        [InlineData("api/Instance/CleanUpInstances")]
        [InlineData("api/Instance/SpinUpServerInstance")]
        [InlineData("api/Instance/ShutDownServerInstance")]
        [InlineData("api/Instance/CompleteZoneInstanceShutdown")]
        [InlineData("api/Instance/SetZoneInstanceStatus")]
        [InlineData("api/Instance/RegisterLauncher")]
        [InlineData("api/Instance/ZoneProcessExited")]
        [InlineData("api/Zones/AddZone")]
        public async Task CustomerGuidWithoutServiceKey_IsUnauthorized(string path)
        {
            using HttpClient client = _host.CreateClient();

            using HttpResponseMessage response = await client.PostAsJsonAsync(path, new { });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("api/Instance/GetZoneInstance")]
        [InlineData("api/Instance/GetZoneInstancesForZone")]
        [InlineData("api/Instance/GetCurrentWorldTime")]
        [InlineData("api/Instance/GetServerInstanceFromPort")]
        [InlineData("api/Instance/UpdateNumberOfPlayers")]
        public async Task ZoneServerCall_WithoutServiceKey_IsUnauthorized(string path)
        {
            using HttpClient client = _host.CreateClient();
            using var body = new StringContent(path.EndsWith("/GetZoneInstance") ? "1" : "{}",
                System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(path, body);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [PostgresTheory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task Heartbeat_RequiresKeyBeforeChangingInstance(bool globalGateRequired, int initialStatus)
        {
            Guid customer = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            int id = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status,
                    NumberOfReportedPlayers, LastUpdateFromServer)
                VALUES (@customer, 7, 1, 7777, @initialStatus, 0, '2000-01-01') RETURNING MapInstanceID",
                new { customer, initialStatus });
            var body = new { ZoneInstanceId = id, NumberOfConnectedPlayers = 4 };
            const string read = "SELECT Status, NumberOfReportedPlayers, LastUpdateFromServer FROM MapInstances WHERE CustomerGUID=@customer AND MapInstanceID=@id";
            var before = await connection.QuerySingleAsync(read, new { customer, id });
            foreach (string key in new[] { null, "wrong-key" })
            {
                using HttpClient client = _host.CreateClient(key, globalGateRequired);
                client.DefaultRequestHeaders.Remove("X-CustomerGUID");
                client.DefaultRequestHeaders.Add("X-CustomerGUID", customer.ToString());
                using var response = await client.PostAsJsonAsync("api/Instance/UpdateNumberOfPlayers", body);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                var after = await connection.QuerySingleAsync(read, new { customer, id });
                Assert.Equal((int)before.status, (int)after.status);
                Assert.Equal((int)before.numberofreportedplayers, (int)after.numberofreportedplayers);
                Assert.Equal((DateTime)before.lastupdatefromserver, (DateTime)after.lastupdatefromserver);
            }
            using HttpClient authorized = _host.CreateClient(InstanceManagementHost.ServiceKey, globalGateRequired);
            authorized.DefaultRequestHeaders.Remove("X-CustomerGUID");
            authorized.DefaultRequestHeaders.Add("X-CustomerGUID", customer.ToString());
            using var success = await authorized.PostAsJsonAsync("api/Instance/UpdateNumberOfPlayers", body);
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            var updated = await connection.QuerySingleAsync(read, new { customer, id });
            Assert.Equal(2, (int)updated.status);
            Assert.Equal(4, (int)updated.numberofreportedplayers);
            Assert.True((DateTime)updated.lastupdatefromserver > (DateTime)before.lastupdatefromserver);
        }

        [Fact]
        public async Task WrongServiceKey_IsUnauthorized()
        {
            using HttpClient client = _host.CreateClient("not-the-key");

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/CleanUpInstances", new { });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [PostgresFact]
        public async Task CleanUpInstances_WithServiceKey_IsOk()
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/CleanUpInstances", new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        //Takes a bare ID, so unlike the endpoints above it needs a real body to get as far as the key check.
        [Fact]
        public async Task GetZoneInstanceForSpinUp_WithoutServiceKey_IsUnauthorized()
        {
            using HttpClient client = _host.CreateClient();

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/GetZoneInstanceForSpinUp", 1);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [PostgresFact]
        public async Task GetZoneInstanceForSpinUp_UnknownInstance_ReturnsAnEmptyRow()
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/GetZoneInstanceForSpinUp", 999999);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            GetServerInstanceFromPort row = await response.Content.ReadFromJsonAsync<GetServerInstanceFromPort>();
            Assert.False(row.MatchesSpinUp(0, 999999, null, 0));
            Assert.Equal(0, row.MapInstanceID);
        }

        [PostgresFact]
        public async Task AddZone_WithServiceKey_IsOk()
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Zones/AddZone", AddZoneBody("/Game/Maps/L_Test_1"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            SuccessAndErrorMessage result = await response.Content.ReadFromJsonAsync<SuccessAndErrorMessage>();
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public async Task AddZone_RejectsMapNameWithLaunchArguments()
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Zones/AddZone", AddZoneBody("L_Test -ExecCmds=quit"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task SpinUpServerInstance_RejectsZoneNameWithLaunchArguments()
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/SpinUpServerInstance",
                new { WorldServerID = 1, ZoneInstanceID = 1, ZoneName = "L_Test?game=X -ini:Engine:[Core.Log]:LogNet=off", Port = 7777 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData("L_MVP_2", true)]
        [InlineData("/Game/Maps/L_Foraas", true)]
        [InlineData("L_Test -ExecCmds=quit", false)]
        [InlineData("L_Test?listen", false)]
        [InlineData("L_Test\r\nforged log line", false)]
        [InlineData("../L_Test", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ZoneMapName_AllowsOnlyPlainMapNames(string mapName, bool expected)
        {
            Assert.Equal(expected, ZoneMapName.IsValid(mapName));
        }

        [Fact]
        public void ZoneMapName_RejectsOverlongNames()
        {
            Assert.False(ZoneMapName.IsValid(new string('A', ZoneMapName.MaxLength + 1)));
        }
    }
}
