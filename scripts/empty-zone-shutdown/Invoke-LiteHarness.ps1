param(
    [string]$ConnectionString = "Host=localhost;Port=15432;Database=openworldserver;Username=postgres;Password=yourStrong(!)Password;",
    [guid]$CustomerGuid = [guid]::NewGuid(),
    [string]$InstanceManagementUrl = "http://localhost:18028/",
    [string]$LauncherUrl = "http://localhost:18181/",
    [string]$RabbitHost = "localhost",
    [int]$RabbitPort = 5672,
    [string]$RabbitUser = "dev",
    [string]$RabbitPassword = "test",
    [int]$TimeoutSeconds = 120,
    [switch]$UseExistingServices,
    [switch]$KeepStartedServices
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..\..")).Path
$fakeProject = Join-Path $repoRoot "src\OWSTestHarness\FakeZoneServer\FakeZoneServer.csproj"
$harnessProject = Join-Path $repoRoot "src\OWSTestHarness\EmptyZoneShutdownLiteHarness\EmptyZoneShutdownLiteHarness.csproj"
$instanceManagementProject = Join-Path $repoRoot "src\OWSInstanceManagement\OWSInstanceManagement.csproj"
$instanceLauncherProject = Join-Path $repoRoot "src\OWSInstanceLauncher\OWSInstanceLauncher.csproj"
$fakeOutput = Join-Path $repoRoot "src\OWSTestHarness\artifacts\FakeZoneServer"
$markerDirectory = Join-Path $env:TEMP ("ows-empty-zone-lite-" + $CustomerGuid)

if ($IsLinux) {
    $runtimeId = "linux-x64"
    $fakeExecutableName = "FakeZoneServer"
}
elseif ($IsMacOS) {
    $runtimeId = "osx-x64"
    $fakeExecutableName = "FakeZoneServer"
}
else {
    $runtimeId = "win-x64"
    $fakeExecutableName = "FakeZoneServer.exe"
}

Write-Host "Publishing fake zone server..."
dotnet publish $fakeProject -c Release -r $runtimeId --self-contained false -o $fakeOutput

$fakeZoneServerPath = Join-Path $fakeOutput $fakeExecutableName
if (!(Test-Path $fakeZoneServerPath)) {
    throw "Fake zone server executable was not produced at $fakeZoneServerPath"
}

$harnessArgs = @(
    "--connection-string", $ConnectionString,
    "--customer-guid", $CustomerGuid.ToString(),
    "--instance-management-url", $InstanceManagementUrl,
    "--launcher-url", $LauncherUrl,
    "--rabbit-host", $RabbitHost,
    "--rabbit-port", $RabbitPort.ToString(),
    "--rabbit-user", $RabbitUser,
    "--rabbit-password", $RabbitPassword,
    "--timeout-seconds", $TimeoutSeconds.ToString(),
    "--fake-zone-server-path", $fakeZoneServerPath,
    "--marker-directory", $markerDirectory,
    "--instance-management-project", $instanceManagementProject,
    "--instance-launcher-project", $instanceLauncherProject
)

if ($UseExistingServices) {
    $harnessArgs += "--use-existing-services"
}

if ($KeepStartedServices) {
    $harnessArgs += "--keep-started-services"
}

Write-Host "Running empty-zone shutdown lite harness..."
Write-Host "CustomerGUID: $CustomerGuid"
Write-Host "Marker directory: $markerDirectory"

dotnet run --project $harnessProject -- @harnessArgs
exit $LASTEXITCODE
