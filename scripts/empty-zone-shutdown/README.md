# Empty Zone Shutdown Lite Harness

This harness runs a lightweight local flow for automatic empty-zone shutdown without a real Unreal dedicated server.

It:
- publishes a tiny fake zone server executable
- starts `OWSInstanceManagement` and `OWSInstanceLauncher` with test environment variables
- seeds a Postgres `Maps` and `MapInstances` row
- requests zone spin-up so the launcher tracks the fake process
- marks the zone ready and empty past the timeout
- waits for health monitoring to request shutdown
- verifies the fake process is killed and Postgres rows are cleaned up

Zones default to `MinutesToShutdownAfterEmpty = 5`. Set it to `0` to disable automatic empty-zone shutdown for a specific zone. The lite harness uses a one-minute timeout by default so the test completes quickly.

Prerequisites:
- Postgres schema already created and reachable
- RabbitMQ reachable
- .NET 8 SDK

Example:

```powershell
.\scripts\empty-zone-shutdown\Invoke-LiteHarness.ps1 `
  -ConnectionString "Host=localhost;Port=15432;Database=openworldserver;Username=postgres;Password=yourStrong(!)Password;" `
  -RabbitHost localhost
```

If `OWSInstanceManagement` and `OWSInstanceLauncher` are already running with matching test configuration, add `-UseExistingServices`.
