using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OWSCharacterPersistence.Controllers;
using OWSData.Models.Composites;
using OWSShared;
using OWSShared.Middleware;
using OWSTests.Database;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// The tests that build a service host set process-wide variables its Startup reads, so they must not overlap.
    /// </summary>
    [CollectionDefinition(Name)]
    public class ServiceHostEnvironmentCollection
    {
        public const string Name = "Service host environment";
    }

    /// <summary>
    /// Hosts OWSCharacterPersistence once per OWS_REQUIRE_SERVICE_KEY posture, with the process-wide test key
    /// (<see cref="TestServiceKey"/>) configured. The missing-key and Development cases are read once per process,
    /// so RequireServiceKeyMiddlewareTests covers those through the settings.
    /// </summary>
    public sealed class CharacterPersistenceHost : IAsyncLifetime
    {
        private readonly PostgresTestDatabases _databases = new();
        private readonly Dictionary<bool, WebApplicationFactory<OWSCharacterPersistence.Startup>> _factories = new();

        public string ConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        public async Task InitializeAsync()
        {
            await _databases.InitializeAsync();

            // Without a test server the rejection tests still run: a rejected request never reaches the database.
            Environment.SetEnvironmentVariable("OWSStorageConfig__OWSDBBackend", "postgres");
            Environment.SetEnvironmentVariable("OWSStorageConfig__OWSDBConnectionString", PostgresTestDatabases.SkipReason == null
                ? ConnectionString
                : "Host=127.0.0.1;Port=1;Username=none;Password=none");

            string previousRequire = Environment.GetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar);
            try
            {
                foreach (bool requireServiceKey in new[] { false, true })
                {
                    Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, requireServiceKey ? "true" : "false");
                    var factory = new WebApplicationFactory<OWSCharacterPersistence.Startup>();
                    _ = factory.Server;
                    _factories[requireServiceKey] = factory;
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServiceKeyGateSettings.RequireEnvVar, previousRequire);
            }
        }

        public HttpClient CreateClient(bool requireServiceKey, Guid customerGUID, string serviceKey)
        {
            HttpClient client = _factories[requireServiceKey].CreateClient();
            client.DefaultRequestHeaders.Add("X-CustomerGUID", customerGUID.ToString());
            if (serviceKey != null)
            {
                client.DefaultRequestHeaders.Add(SamsaraServiceKeyCheck.Header, serviceKey);
            }
            return client;
        }

        public async Task DisposeAsync()
        {
            foreach (WebApplicationFactory<OWSCharacterPersistence.Startup> factory in _factories.Values)
            {
                factory.Dispose();
            }
            await _databases.DisposeAsync();
        }
    }

    /// <summary>
    /// X-CustomerGUID only names the tenant, and neither OWS_REQUIRE_SERVICE_KEY nor the owner token stands in for the
    /// key: a character without an owner row and the tenant's quest catalog have nothing else protecting them.
    /// </summary>
    [Collection(ServiceHostEnvironmentCollection.Name)]
    public class CharacterPersistenceServiceKeyTests : IClassFixture<CharacterPersistenceHost>
    {
        private const string WrongKey = "not-the-key";
        private const string ConfiguredKey = TestServiceKey.Key;

        private readonly CharacterPersistenceHost _host;

        public CharacterPersistenceServiceKeyTests(CharacterPersistenceHost host)
        {
            _host = host;
        }

        // Every action on the service except the status probe, read from the controllers so a new route can't be missed.
        private static List<string> ProtectedRoutes() =>
            typeof(OWSCharacterPersistence.Startup).Assembly.GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract && type != typeof(SystemController))
                .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(action => action.GetCustomAttribute<RouteAttribute>())
                    .Where(route => route != null)
                    .Select(route => $"api/{controller.Name.Replace("Controller", "")}/{route.Template}"))
                .ToList();

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "")]
        [InlineData(false, WrongKey)]
        [InlineData(true, null)]
        [InlineData(true, "")]
        [InlineData(true, WrongKey)]
        public async Task EveryRoute_RejectsACallerWithoutTheKey(bool requireServiceKey, string serviceKey)
        {
            List<string> routes = ProtectedRoutes();
            Assert.Contains("api/Characters/UpdateAllPlayerPositions", routes);
            Assert.Contains("api/Characters/AddQuestListToDatabase", routes);
            using HttpClient client = _host.CreateClient(requireServiceKey, Guid.NewGuid(), serviceKey);

            var notRejected = new List<string>();
            foreach (string route in routes)
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(route, new { });
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    notRejected.Add($"{route}: {(int)response.StatusCode}");
                }
            }

            Assert.Empty(notRejected);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatusProbe_DoesNotNeedTheKey(bool requireServiceKey)
        {
            using HttpClient client = _host.CreateClient(requireServiceKey, Guid.NewGuid(), null);

            using HttpResponseMessage response = await client.GetAsync("api/System/Status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [PostgresTheory]
        [InlineData(false, null)]
        [InlineData(false, "")]
        [InlineData(false, WrongKey)]
        [InlineData(true, null)]
        public async Task PositionSave_WithoutTheKey_LeavesACharacterWithNoOwnerRowWhereItWas(bool requireServiceKey, string serviceKey)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(customerGUID, "Unowned");

            using HttpResponseMessage response = await SavePosition(requireServiceKey, customerGUID, serviceKey, "Unowned:1:2:3:0:0:0");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal((0.0, 0.0, 0.0), await Location(customerGUID, "Unowned"));
        }

        [PostgresTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PositionSave_WithTheKey_MovesACharacterWithNoOwnerRow(bool requireServiceKey)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(customerGUID, "Unowned");

            using HttpResponseMessage response = await SavePosition(requireServiceKey, customerGUID, ConfiguredKey, "Unowned:1:2:3:0:0:0");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal((1.0, 2.0, 4.0), await Location(customerGUID, "Unowned"));
        }

        // The key doesn't replace the owner token, and the owner token doesn't replace the key.
        [PostgresTheory]
        [InlineData(null, true, HttpStatusCode.Unauthorized, false)]
        [InlineData(ConfiguredKey, true, HttpStatusCode.OK, true)]
        [InlineData(ConfiguredKey, false, HttpStatusCode.OK, false)]
        public async Task PositionSave_OnAnOwnedCharacter_NeedsBothTheKeyAndTheOwnerToken(string serviceKey, bool currentOwner, HttpStatusCode expectedStatus, bool moves)
        {
            Guid customerGUID = Guid.NewGuid();
            Guid owner = Guid.NewGuid();
            await SeedOwner(customerGUID, await SeedCharacter(customerGUID, "Owned"), owner);
            Guid writer = currentOwner ? owner : Guid.NewGuid();

            using HttpResponseMessage response = await SavePosition(false, customerGUID, serviceKey, $"Owned:1:2:3:0:0:0:{writer}");

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal(moves ? (1.0, 2.0, 4.0) : (0.0, 0.0, 0.0), await Location(customerGUID, "Owned"));
        }

        [PostgresTheory]
        [InlineData(false, null)]
        [InlineData(false, "")]
        [InlineData(false, WrongKey)]
        [InlineData(true, null)]
        public async Task QuestCatalogWrite_WithoutTheKey_AddsNoQuest(bool requireServiceKey, string serviceKey)
        {
            Guid customerGUID = Guid.NewGuid();

            using HttpResponseMessage response = await AddQuest(requireServiceKey, customerGUID, serviceKey);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, await QuestCount(customerGUID));
        }

        [PostgresTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task QuestCatalogWrite_WithTheKey_AddsTheQuest(bool requireServiceKey)
        {
            Guid customerGUID = Guid.NewGuid();

            using HttpResponseMessage response = await AddQuest(requireServiceKey, customerGUID, ConfiguredKey);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await QuestCount(customerGUID));
        }

        // The controller's own check, which runs once the key has been accepted.
        [PostgresFact]
        public async Task WalletSave_WithTheKeyButAStaleOwnerToken_GetsTheStaleOwnerReply()
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedOwner(customerGUID, await SeedCharacter(customerGUID, "Owned"), Guid.NewGuid());
            using HttpClient client = _host.CreateClient(false, customerGUID, ConfiguredKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Characters/UpdateCharacterCurrency",
                new { CharacterName = "Owned", OwnerToken = Guid.NewGuid(), Gold = 500 });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            SuccessAndErrorMessage reply = await response.Content.ReadFromJsonAsync<SuccessAndErrorMessage>();
            Assert.False(reply.Success);
            Assert.Equal("stale_owner", reply.ErrorMessage);
            Assert.Equal(0, await Gold(customerGUID, "Owned"));
        }

        [PostgresFact]
        public async Task WalletSave_WithTheKeyAndTheOwnerToken_Saves()
        {
            Guid customerGUID = Guid.NewGuid();
            Guid owner = Guid.NewGuid();
            await SeedOwner(customerGUID, await SeedCharacter(customerGUID, "Owned"), owner);
            using HttpClient client = _host.CreateClient(false, customerGUID, ConfiguredKey);

            using HttpResponseMessage response = await client.PostAsJsonAsync("api/Characters/UpdateCharacterCurrency",
                new { CharacterName = "Owned", OwnerToken = owner, Gold = 500 });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(500, await Gold(customerGUID, "Owned"));
        }

        private async Task<HttpResponseMessage> SavePosition(bool requireServiceKey, Guid customerGUID, string serviceKey, string serialized)
        {
            using HttpClient client = _host.CreateClient(requireServiceKey, customerGUID, serviceKey);
            return await client.PostAsJsonAsync("api/Characters/UpdateAllPlayerPositions",
                new { SerializedPlayerLocationData = serialized, MapName = "" });
        }

        private async Task<HttpResponseMessage> AddQuest(bool requireServiceKey, Guid customerGUID, string serviceKey)
        {
            using HttpClient client = _host.CreateClient(requireServiceKey, customerGUID, serviceKey);
            return await client.PostAsJsonAsync("api/Characters/AddQuestListToDatabase", new
            {
                questsToAdd = new[] { new { QuestIDTag = "Quest", QuestOverview = "Overview", QuestTasks = "Tasks", QuestClassName = "Class", CustomData = "" } }
            });
        }

        private async Task<int> SeedCharacter(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            return await connection.QuerySingleAsync<int>(
                @"WITH U AS (
                      INSERT INTO Users (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, CreateDate, LastAccess, Role)
                      VALUES (gen_random_uuid(), @CustomerGUID, 'Test', 'User', @Email, 'x', NOW(), '2000-01-01', 'Player')
                      RETURNING UserGUID)
                  INSERT INTO Characters (CustomerGUID, UserGUID, CharName, MapName, X, Y, Z, ClassID, Gold)
                  SELECT @CustomerGUID, U.UserGUID, @Name, 'Map', 0, 0, 0, 1, 0 FROM U
                  RETURNING CharacterID",
                new { CustomerGUID = customerGUID, Email = name + "@example.com", Name = name });
        }

        private async Task SeedOwner(Guid customerGUID, int characterID, Guid owner)
        {
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            await connection.ExecuteAsync(
                @"INSERT INTO CharacterOwners (CustomerGUID, CharacterID, OwnerToken, MapInstanceID, ClaimKind, LastConfirmedAt)
                  VALUES (@CustomerGUID, @CharacterID, @Owner, 1, 2, NOW())",
                new { CustomerGUID = customerGUID, CharacterID = characterID, Owner = owner });
        }

        private sealed class LocationRow
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        private async Task<(double, double, double)> Location(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            LocationRow row = await connection.QuerySingleAsync<LocationRow>(
                "SELECT X, Y, Z FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @Name",
                new { CustomerGUID = customerGUID, Name = name });
            return (row.X, row.Y, row.Z);
        }

        private async Task<int> Gold(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            return await connection.QuerySingleAsync<int>(
                "SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @Name",
                new { CustomerGUID = customerGUID, Name = name });
        }

        private async Task<int> QuestCount(Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_host.ConnectionString);
            return await connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM Quest WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID });
        }
    }
}
