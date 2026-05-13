using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

HarnessOptions options = HarnessOptions.Parse(args);
List<Process> managedProcesses = new();

Console.WriteLine("OWS empty-zone shutdown lite harness");
Console.WriteLine($"CustomerGUID: {options.CustomerGuid}");
Console.WriteLine($"Instance Management URL: {options.InstanceManagementUrl}");
Console.WriteLine($"Launcher URL: {options.LauncherUrl}");
Console.WriteLine($"Marker directory: {options.MarkerDirectory}");

try
{
    Directory.CreateDirectory(options.MarkerDirectory);

    await PrepareDatabase(options);

    if (!options.UseExistingServices)
    {
        managedProcesses.Add(StartDotNetService(
            "OWSInstanceManagement",
            options.InstanceManagementProject,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = options.InstanceManagementUrl.TrimEnd('/'),
                ["Kestrel__Endpoints__Http__Url"] = options.InstanceManagementUrl.TrimEnd('/'),
                ["OWSStorageConfig__OWSDBBackend"] = "postgres",
                ["OWSStorageConfig__OWSDBConnectionString"] = options.ConnectionString,
                ["RabbitMQOptions__RabbitMQHostName"] = options.RabbitHost,
                ["RabbitMQOptions__RabbitMQPort"] = options.RabbitPort.ToString(),
                ["RabbitMQOptions__RabbitMQUserName"] = options.RabbitUser,
                ["RabbitMQOptions__RabbitMQPassword"] = options.RabbitPassword
            }));

        await WaitForInstanceManagement(options);

        managedProcesses.Add(StartDotNetService(
            "OWSInstanceLauncher",
            options.InstanceLauncherProject,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = options.LauncherUrl.TrimEnd('/'),
                ["Kestrel__Endpoints__Http__Url"] = options.LauncherUrl.TrimEnd('/'),
                ["OWS_FAKE_ZONE_MARKER_DIR"] = options.MarkerDirectory,
                ["RabbitMQOptions__RabbitMQHostName"] = options.RabbitHost,
                ["RabbitMQOptions__RabbitMQPort"] = options.RabbitPort.ToString(),
                ["RabbitMQOptions__RabbitMQUserName"] = options.RabbitUser,
                ["RabbitMQOptions__RabbitMQPassword"] = options.RabbitPassword,
                ["OWSInstanceLauncherOptions__OWSAPIKey"] = options.CustomerGuid.ToString(),
                ["OWSInstanceLauncherOptions__LauncherGuid"] = options.LauncherGuid.ToString(),
                ["OWSInstanceLauncherOptions__ServerIP"] = "127.0.0.1",
                ["OWSInstanceLauncherOptions__InternalServerIP"] = "127.0.0.1",
                ["OWSInstanceLauncherOptions__MaxNumberOfInstances"] = "2",
                ["OWSInstanceLauncherOptions__StartingInstancePort"] = options.ZonePort.ToString(),
                ["OWSInstanceLauncherOptions__IsServerEditor"] = "false",
                ["OWSInstanceLauncherOptions__PathToDedicatedServer"] = options.FakeZoneServerPath,
                ["OWSInstanceLauncherOptions__RunServerHealthMonitoringFrequencyInSeconds"] = "3",
                ["OWSInstanceLauncherOptions__UseServerLog"] = "false",
                ["OWSInstanceLauncherOptions__UseNoSteam"] = "true",
                ["OWSAPIPathConfig__InternalInstanceManagementApiURL"] = options.InstanceManagementUrl,
                ["OWSAPIPathConfig__InternalPublicApiURL"] = options.InstanceManagementUrl,
                ["OWSAPIPathConfig__InternalCharacterPersistenceApiURL"] = options.InstanceManagementUrl
            }));
    }
    else
    {
        await WaitForInstanceManagement(options);
    }

    int worldServerId = await WaitForWorldServer(options);
    Console.WriteLine($"Using WorldServerID={worldServerId}");

    int mapId = await CreateMap(options);
    int zoneInstanceId = await CreateMapInstance(options, worldServerId, mapId);
    await AddCharacterOnMapInstance(options, zoneInstanceId);

    Console.WriteLine($"Created test zone instance: MapID={mapId}; ZoneInstanceID={zoneInstanceId}");

    await RequestSpinUp(options, worldServerId, zoneInstanceId);

    int fakeZonePid = await WaitForFakeZoneServerPid(options, zoneInstanceId);
    Console.WriteLine($"Fake zone server started with PID={fakeZonePid}");

    await MarkZoneReadyAndEmpty(options, zoneInstanceId);
    Console.WriteLine("Marked zone instance ready, empty, and past the shutdown timeout.");

    await WaitForShutdownCleanup(options, zoneInstanceId, fakeZonePid);

    Console.WriteLine("PASS: shutdown message killed the fake zone process and Postgres cleanup removed the test rows.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex.Message);
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    if (!options.KeepStartedServices)
    {
        foreach (Process process in managedProcesses)
        {
            TryKillProcessTree(process);
        }
    }
}

static Process StartDotNetService(string name, string projectPath, Dictionary<string, string> environment)
{
    if (!File.Exists(projectPath))
    {
        throw new FileNotFoundException($"{name} project was not found.", projectPath);
    }

    ProcessStartInfo startInfo = new("dotnet")
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
    };
    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--no-launch-profile");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(projectPath);

    foreach ((string key, string value) in environment)
    {
        startInfo.Environment[key] = value;
    }

    Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {name}.");
    Console.WriteLine($"Started {name}; PID={process.Id}");
    return process;
}

static async Task PrepareDatabase(HarnessOptions options)
{
    await using NpgsqlConnection connection = new(options.ConnectionString);
    await connection.OpenAsync();

    string sql = @"
ALTER TABLE Maps
ADD COLUMN IF NOT EXISTS MinutesToShutdownAfterEmpty INT NOT NULL DEFAULT 5;

ALTER TABLE Maps
ALTER COLUMN MinutesToShutdownAfterEmpty SET DEFAULT 5;

ALTER TABLE WorldServers
ADD COLUMN IF NOT EXISTS ZoneServerGUID UUID NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ak_zoneservers'
    ) THEN
        ALTER TABLE WorldServers
        ADD CONSTRAINT AK_ZoneServers UNIQUE (CustomerGUID, ZoneServerGUID);
    END IF;
END $$;

DELETE FROM CharOnMapInstance
WHERE CustomerGUID = @CustomerGUID
  AND MapInstanceID IN (
      SELECT MI.MapInstanceID
      FROM MapInstances MI
      INNER JOIN Maps M ON M.CustomerGUID = MI.CustomerGUID AND M.MapID = MI.MapID
      WHERE MI.CustomerGUID = @CustomerGUID
        AND M.ZoneName = @ZoneName
  );

DELETE FROM MapInstances
WHERE CustomerGUID = @CustomerGUID
  AND MapID IN (
      SELECT MapID
      FROM Maps
      WHERE CustomerGUID = @CustomerGUID
        AND ZoneName = @ZoneName
  );

DELETE FROM Maps
WHERE CustomerGUID = @CustomerGUID
  AND ZoneName = @ZoneName;";

    await using NpgsqlCommand command = new(sql, connection);
    command.Parameters.AddWithValue("CustomerGUID", options.CustomerGuid);
    command.Parameters.AddWithValue("ZoneName", options.ZoneName);
    await command.ExecuteNonQueryAsync();
}

static async Task WaitForInstanceManagement(HarnessOptions options)
{
    using HttpClient httpClient = CreateInstanceManagementClient(options);
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds);

    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                "api/Instance/GetZoneInstancesForWorldServer",
                new { request = new { worldServerID = 0 } });

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Instance Management API is responding.");
                return;
            }
        }
        catch
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException("Timed out waiting for OWSInstanceManagement to respond.");
}

static async Task<int> WaitForWorldServer(HarnessOptions options)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds);

    while (DateTimeOffset.UtcNow < deadline)
    {
        int? worldServerId = await QuerySingleOrDefault<int?>(options.ConnectionString, @"
SELECT WorldServerID
FROM WorldServers
WHERE CustomerGUID = @CustomerGUID
  AND ZoneServerGUID = @LauncherGuid
ORDER BY WorldServerID DESC
LIMIT 1;",
            ("CustomerGUID", options.CustomerGuid),
            ("LauncherGuid", options.LauncherGuid));

        if (worldServerId.HasValue && worldServerId.Value > 0)
        {
            return worldServerId.Value;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException("Timed out waiting for the launcher to register a WorldServers row.");
}

static async Task<int> CreateMap(HarnessOptions options)
{
    return await QuerySingle<int>(options.ConnectionString, @"
INSERT INTO Maps
    (CustomerGUID, MapName, MapData, Width, Height, ZoneName, WorldCompContainsFilter, WorldCompListFilter, SoftPlayerCap, HardPlayerCap, MapMode, MinutesToShutdownAfterEmpty)
VALUES
    (@CustomerGUID, @MapName, @MapData, 1, 1, @ZoneName, '', '', 1, 10, 1, @MinutesToShutdownAfterEmpty)
RETURNING MapID;",
        ("CustomerGUID", options.CustomerGuid),
        ("MapName", options.MapName),
        ("MapData", new byte[] { 0 }),
        ("ZoneName", options.ZoneName),
        ("MinutesToShutdownAfterEmpty", options.MinutesToShutdownAfterEmpty));
}

static async Task<int> CreateMapInstance(HarnessOptions options, int worldServerId, int mapId)
{
    return await QuerySingle<int>(options.ConnectionString, @"
INSERT INTO MapInstances
    (CustomerGUID, WorldServerID, MapID, Port, Status, PlayerGroupID, NumberOfReportedPlayers, LastUpdateFromServer, LastServerEmptyDate)
VALUES
    (@CustomerGUID, @WorldServerID, @MapID, @Port, 1, NULL, 0, NOW(), NULL)
RETURNING MapInstanceID;",
        ("CustomerGUID", options.CustomerGuid),
        ("WorldServerID", worldServerId),
        ("MapID", mapId),
        ("Port", options.ZonePort));
}

static async Task AddCharacterOnMapInstance(HarnessOptions options, int zoneInstanceId)
{
    await Execute(options.ConnectionString, @"
INSERT INTO CharOnMapInstance
    (CustomerGUID, CharacterID, MapInstanceID)
VALUES
    (@CustomerGUID, @CharacterID, @ZoneInstanceID);",
        ("CustomerGUID", options.CustomerGuid),
        ("CharacterID", options.TestCharacterId),
        ("ZoneInstanceID", zoneInstanceId));
}

static async Task RequestSpinUp(HarnessOptions options, int worldServerId, int zoneInstanceId)
{
    using HttpClient httpClient = CreateInstanceManagementClient(options);
    using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/Instance/SpinUpServerInstance", new
    {
        WorldServerID = worldServerId,
        ZoneInstanceID = zoneInstanceId,
        ZoneName = options.ZoneName,
        Port = options.ZonePort
    });

    string responseText = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"SpinUpServerInstance failed with HTTP {(int)response.StatusCode}: {responseText}");
    }

    Console.WriteLine("Requested zone spin-up through Instance Management.");
}

static async Task<int> WaitForFakeZoneServerPid(HarnessOptions options, int zoneInstanceId)
{
    string pidPath = Path.Combine(options.MarkerDirectory, $"ows-fake-zone-{zoneInstanceId}.pid");
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds);

    while (DateTimeOffset.UtcNow < deadline)
    {
        if (File.Exists(pidPath)
            && int.TryParse(await File.ReadAllTextAsync(pidPath), out int pid)
            && IsProcessRunning(pid))
        {
            return pid;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException($"Timed out waiting for fake zone server marker at {pidPath}.");
}

static async Task MarkZoneReadyAndEmpty(HarnessOptions options, int zoneInstanceId)
{
    await Execute(options.ConnectionString, @"
UPDATE MapInstances
SET Status = 2,
    NumberOfReportedPlayers = 0,
    LastUpdateFromServer = NOW(),
    LastServerEmptyDate = NOW() - INTERVAL '10 minutes'
WHERE CustomerGUID = @CustomerGUID
  AND MapInstanceID = @ZoneInstanceID;",
        ("CustomerGUID", options.CustomerGuid),
        ("ZoneInstanceID", zoneInstanceId));
}

static async Task WaitForShutdownCleanup(HarnessOptions options, int zoneInstanceId, int fakeZonePid)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds);

    while (DateTimeOffset.UtcNow < deadline)
    {
        int mapInstanceCount = await QuerySingle<int>(options.ConnectionString, @"
SELECT COUNT(*)::INT
FROM MapInstances
WHERE CustomerGUID = @CustomerGUID
  AND MapInstanceID = @ZoneInstanceID;",
            ("CustomerGUID", options.CustomerGuid),
            ("ZoneInstanceID", zoneInstanceId));

        int characterMapInstanceCount = await QuerySingle<int>(options.ConnectionString, @"
SELECT COUNT(*)::INT
FROM CharOnMapInstance
WHERE CustomerGUID = @CustomerGUID
  AND MapInstanceID = @ZoneInstanceID;",
            ("CustomerGUID", options.CustomerGuid),
            ("ZoneInstanceID", zoneInstanceId));

        if (mapInstanceCount == 0
            && characterMapInstanceCount == 0
            && !IsProcessRunning(fakeZonePid))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException("Timed out waiting for fake process termination and Postgres cleanup.");
}

static HttpClient CreateInstanceManagementClient(HarnessOptions options)
{
    HttpClient httpClient = new()
    {
        BaseAddress = new Uri(options.InstanceManagementUrl)
    };
    httpClient.DefaultRequestHeaders.Add("X-CustomerGUID", options.CustomerGuid.ToString());
    return httpClient;
}

static async Task Execute(string connectionString, string sql, params (string Name, object Value)[] parameters)
{
    await using NpgsqlConnection connection = new(connectionString);
    await connection.OpenAsync();
    await using NpgsqlCommand command = new(sql, connection);
    foreach ((string name, object value) in parameters)
    {
        command.Parameters.AddWithValue(name, value);
    }

    await command.ExecuteNonQueryAsync();
}

static async Task<T> QuerySingle<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
{
    object? output = await QueryScalar(connectionString, sql, parameters);
    if (output == null || output is DBNull)
    {
        throw new InvalidOperationException("Query returned no rows.");
    }

    return (T)Convert.ChangeType(output, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
}

static async Task<T?> QuerySingleOrDefault<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
{
    object? output = await QueryScalar(connectionString, sql, parameters);
    if (output == null || output is DBNull)
    {
        return default;
    }

    return (T)Convert.ChangeType(output, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
}

static async Task<object?> QueryScalar(string connectionString, string sql, params (string Name, object Value)[] parameters)
{
    await using NpgsqlConnection connection = new(connectionString);
    await connection.OpenAsync();
    await using NpgsqlCommand command = new(sql, connection);
    foreach ((string name, object value) in parameters)
    {
        command.Parameters.AddWithValue(name, value);
    }

    return await command.ExecuteScalarAsync();
}

static bool IsProcessRunning(int processId)
{
    try
    {
        using Process process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static void TryKillProcessTree(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }
    catch
    {
    }
}

sealed class HarnessOptions
{
    public string ConnectionString { get; init; } = "";
    public Guid CustomerGuid { get; init; }
    public Guid LauncherGuid { get; init; }
    public string InstanceManagementUrl { get; init; } = "http://localhost:18028/";
    public string LauncherUrl { get; init; } = "http://localhost:18181/";
    public string InstanceManagementProject { get; init; } = "";
    public string InstanceLauncherProject { get; init; } = "";
    public string FakeZoneServerPath { get; init; } = "";
    public string MarkerDirectory { get; init; } = "";
    public string RabbitHost { get; init; } = "localhost";
    public int RabbitPort { get; init; } = 5672;
    public string RabbitUser { get; init; } = "dev";
    public string RabbitPassword { get; init; } = "test";
    public int TimeoutSeconds { get; init; } = 90;
    public bool UseExistingServices { get; init; }
    public bool KeepStartedServices { get; init; }
    public string ZoneName { get; init; } = "LiteHarnessZone";
    public string MapName { get; init; } = "LiteHarnessMap";
    public int ZonePort { get; init; } = 7788;
    public int MinutesToShutdownAfterEmpty { get; init; } = 1;
    public int TestCharacterId { get; init; } = 900001;

    public static HarnessOptions Parse(string[] args)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = args[++i];
            }
            else
            {
                values[key] = "true";
            }
        }

        string requiredConnectionString = Get(values, "connection-string", "");
        string requiredFakeServerPath = Get(values, "fake-zone-server-path", "");
        string requiredInstanceManagementProject = Get(values, "instance-management-project", "");
        string requiredInstanceLauncherProject = Get(values, "instance-launcher-project", "");

        if (string.IsNullOrWhiteSpace(requiredConnectionString))
        {
            throw new ArgumentException("--connection-string is required.");
        }

        if (string.IsNullOrWhiteSpace(requiredFakeServerPath))
        {
            throw new ArgumentException("--fake-zone-server-path is required.");
        }

        Guid customerGuid = Guid.TryParse(Get(values, "customer-guid", ""), out Guid parsedCustomerGuid)
            ? parsedCustomerGuid
            : Guid.NewGuid();

        Guid launcherGuid = Guid.TryParse(Get(values, "launcher-guid", ""), out Guid parsedLauncherGuid)
            ? parsedLauncherGuid
            : Guid.NewGuid();

        string markerDirectory = Get(values, "marker-directory", Path.Combine(Path.GetTempPath(), "ows-empty-zone-lite"));

        return new HarnessOptions
        {
            ConnectionString = requiredConnectionString,
            CustomerGuid = customerGuid,
            LauncherGuid = launcherGuid,
            InstanceManagementUrl = EnsureTrailingSlash(Get(values, "instance-management-url", "http://localhost:18028/")),
            LauncherUrl = EnsureTrailingSlash(Get(values, "launcher-url", "http://localhost:18181/")),
            InstanceManagementProject = requiredInstanceManagementProject,
            InstanceLauncherProject = requiredInstanceLauncherProject,
            FakeZoneServerPath = requiredFakeServerPath,
            MarkerDirectory = markerDirectory,
            RabbitHost = Get(values, "rabbit-host", "localhost"),
            RabbitPort = int.Parse(Get(values, "rabbit-port", "5672")),
            RabbitUser = Get(values, "rabbit-user", "dev"),
            RabbitPassword = Get(values, "rabbit-password", "test"),
            TimeoutSeconds = int.Parse(Get(values, "timeout-seconds", "90")),
            UseExistingServices = bool.Parse(Get(values, "use-existing-services", "false")),
            KeepStartedServices = bool.Parse(Get(values, "keep-started-services", "false")),
            ZoneName = Get(values, "zone-name", "LiteHarnessZone"),
            MapName = Get(values, "map-name", "LiteHarnessMap"),
            ZonePort = int.Parse(Get(values, "zone-port", "7788")),
            MinutesToShutdownAfterEmpty = int.Parse(Get(values, "minutes-to-shutdown-after-empty", "1"))
        };
    }

    private static string Get(Dictionary<string, string?> values, string key, string defaultValue)
    {
        return values.TryGetValue(key, out string? value) && value != null ? value : defaultValue;
    }

    private static string EnsureTrailingSlash(string url)
    {
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }
}
