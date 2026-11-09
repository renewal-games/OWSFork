using Dapper;
using Npgsql;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
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
    /// OWS_REQUIRE_SERVICE_KEY=false, the Hetzner profile's posture. The global gate then lets keyless callers through,
    /// so these tests show the per-endpoint [RequireSamsaraServiceKey] still holds on the launcher endpoints.
    /// </summary>
    public sealed class InstanceManagementHost : IAsyncLifetime
    {
        public const string ServiceKey = TestServiceKey.Key;

        private readonly PostgresTestDatabases _databases = new();

        public WebApplicationFactory<OWSInstanceManagement.Startup> Factory { get; private set; }

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
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, previousRequire);
            }
        }

        public string ConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        public HttpClient CreateClient(string serviceKey = null)
        {
            HttpClient client = Factory.CreateClient();
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

        [Fact]
        public async Task ZoneServerCall_WithoutServiceKey_PassesTheGate_WhileTheGlobalGateIsOff()
        {
            using HttpClient client = _host.CreateClient();

            // The UE zone server doesn't send the key on InstanceManagementAPI calls yet; without a database this may
            // fail further in, but not at either gate.
            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Instance/GetZoneInstance", new { ZoneInstanceID = 1 });

            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
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

        [Theory]
        [InlineData(null)]
        [InlineData("invalid")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task LauncherEndpoints_RejectInvalidLauncherHeader(string launcherGuid)
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);
            if (launcherGuid != null)
                client.DefaultRequestHeaders.Add("X-LauncherGUID", launcherGuid);

            using var shutdown = await client.PostAsJsonAsync("api/Instance/ShutDownInstanceLauncher", new { request = new { WorldServerID = 1 } });
            using var start = await client.GetAsync("api/Instance/StartInstanceLauncher");

            Assert.Equal(HttpStatusCode.BadRequest, shutdown.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"request\":null}")]
        public async Task LauncherShutdown_RejectsMissingRequest(string body)
        {
            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);
            client.DefaultRequestHeaders.Add("X-LauncherGUID", Guid.NewGuid().ToString());
            using var response = await client.PostAsync("api/Instance/ShutDownInstanceLauncher",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [PostgresFact]
        public async Task LauncherShutdown_OnlyCleansUpItsOwnWorldServer()
        {
            Guid customer = Guid.NewGuid(), launcherA = Guid.NewGuid(), launcherB = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            async Task<int> Seed(Guid launcher) => await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO WorldServers (CustomerGUID, ZoneServerGUID, ServerIP, MaxNumberOfInstances, ActiveStartTime, ServerStatus, StartingMapInstancePort)
                  VALUES (@customer, @launcher, '127.0.0.1', 10, NOW(), 1, 7777) RETURNING WorldServerID",
                new { customer, launcher });
            int a = await Seed(launcherA), b = await Seed(launcherB);
            async Task<int> SeedInstance(int server) => await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO MapInstances (CustomerGUID, WorldServerID, MapID, Port, Status, LastUpdateFromServer)
                  VALUES (@customer, @server, 1, 7777, 2, NOW()) RETURNING MapInstanceID", new { customer, server });
            int instanceA = await SeedInstance(a), instanceB = await SeedInstance(b);
            await connection.ExecuteAsync(
                @"INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                  VALUES (@customer, 41, @instanceA), (@customer, 42, @instanceB)", new { customer, instanceA, instanceB });

            using HttpClient client = _host.CreateClient(InstanceManagementHost.ServiceKey);
            client.DefaultRequestHeaders.Remove("X-CustomerGUID");
            client.DefaultRequestHeaders.Add("X-CustomerGUID", customer.ToString());
            client.DefaultRequestHeaders.Add("X-LauncherGUID", launcherA.ToString());
            async Task AssertServer(int server, int instance, int count, int status)
            {
                Assert.Equal(count, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM MapInstances WHERE CustomerGUID=@customer AND MapInstanceID=@instance", new { customer, instance }));
                Assert.Equal(count, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM CharOnMapInstance WHERE CustomerGUID=@customer AND MapInstanceID=@instance", new { customer, instance }));
                Assert.Equal(status, await connection.ExecuteScalarAsync<int>(
                    "SELECT ServerStatus FROM WorldServers WHERE CustomerGUID=@customer AND WorldServerID=@server", new { customer, server }));
            }

            using var mismatch = await client.PostAsJsonAsync("api/Instance/ShutDownInstanceLauncher", new { request = new { WorldServerID = b } });
            Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
            await AssertServer(b, instanceB, 1, 1);
            await AssertServer(a, instanceA, 1, 1);

            // An unknown launcher and a launcher from another tenant must also fail closed.
            client.DefaultRequestHeaders.Remove("X-LauncherGUID");
            client.DefaultRequestHeaders.Add("X-LauncherGUID", Guid.NewGuid().ToString());
            using var unknown = await client.PostAsJsonAsync("api/Instance/ShutDownInstanceLauncher", new { request = new { WorldServerID = b } });
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            client.DefaultRequestHeaders.Remove("X-LauncherGUID");
            client.DefaultRequestHeaders.Add("X-LauncherGUID", launcherB.ToString());
            client.DefaultRequestHeaders.Remove("X-CustomerGUID");
            client.DefaultRequestHeaders.Add("X-CustomerGUID", Guid.NewGuid().ToString());
            using var otherTenant = await client.PostAsJsonAsync("api/Instance/ShutDownInstanceLauncher", new { request = new { WorldServerID = b } });
            Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
            await AssertServer(b, instanceB, 1, 1);

            client.DefaultRequestHeaders.Remove("X-LauncherGUID");
            client.DefaultRequestHeaders.Add("X-LauncherGUID", launcherA.ToString());
            client.DefaultRequestHeaders.Remove("X-CustomerGUID");
            client.DefaultRequestHeaders.Add("X-CustomerGUID", customer.ToString());
            using var own = await client.PostAsJsonAsync("api/Instance/ShutDownInstanceLauncher", new { request = new { WorldServerID = a } });
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.True((await own.Content.ReadFromJsonAsync<SuccessAndErrorMessage>()).Success);
            await AssertServer(a, instanceA, 0, 0);
            await AssertServer(b, instanceB, 1, 1);
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
