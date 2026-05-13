using System.Text.RegularExpressions;

string zoneInstanceId = GetZoneInstanceId(args);
string markerDirectory = Environment.GetEnvironmentVariable("OWS_FAKE_ZONE_MARKER_DIR") ?? Path.GetTempPath();
Directory.CreateDirectory(markerDirectory);

string markerBasePath = Path.Combine(markerDirectory, $"ows-fake-zone-{zoneInstanceId}");
File.WriteAllText(markerBasePath + ".pid", Environment.ProcessId.ToString());
File.WriteAllText(markerBasePath + ".args.txt", string.Join(Environment.NewLine, args));

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    TryWriteStoppedMarker(markerBasePath);
};

Console.WriteLine($"FakeZoneServer running. ZoneInstanceID={zoneInstanceId}; PID={Environment.ProcessId}");

try
{
    while (!shutdown.Token.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token);
    }
}
catch (OperationCanceledException)
{
}
finally
{
    TryWriteStoppedMarker(markerBasePath);
}

static string GetZoneInstanceId(string[] args)
{
    foreach (string arg in args)
    {
        Match match = Regex.Match(arg, @"^-zoneinstanceid=(?<id>\d+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }
    }

    return "unknown";
}

static void TryWriteStoppedMarker(string markerBasePath)
{
    try
    {
        File.WriteAllText(markerBasePath + ".stopped", DateTimeOffset.UtcNow.ToString("O"));
    }
    catch
    {
    }
}
