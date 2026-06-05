[CmdletBinding()]
param(
    [switch]$DryRun,
    [int]$TimeoutSeconds = 120,
    [int]$PollSeconds = 2
)

$ErrorActionPreference = "Stop"

$launcherRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appSettingsPath = Join-Path $launcherRoot "appsettings.json"
$launcherDllPath = Join-Path $launcherRoot "OWSInstanceLauncher.dll"
$launcherProjectPath = Join-Path $launcherRoot "OWSInstanceLauncher.csproj"
$webhookUrl = $env:OWS_DISCORD_WEBHOOK_URL
$greenCircle = [char]::ConvertFromUtf32(0x1F7E2)
$redCircle = [char]::ConvertFromUtf32(0x1F534)

function Write-Status {
    param([string]$Message)

    Write-Host "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
}

function Get-LauncherSettings {
    if (-not (Test-Path -LiteralPath $appSettingsPath)) {
        throw "Could not find appsettings.json at '$appSettingsPath'."
    }

    $settings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
    $readinessUrl = $settings.Kestrel.Endpoints.Http.Url

    if ([string]::IsNullOrWhiteSpace($readinessUrl)) {
        $readinessUrl = "http://localhost:8181"
    }

    [PSCustomObject]@{
        ReadinessUrl = $readinessUrl
        ServerIP = $settings.OWSInstanceLauncherOptions.ServerIP
        LauncherGuid = $settings.OWSInstanceLauncherOptions.LauncherGuid
    }
}

function Get-TcpEndpoint {
    param([string]$ReadinessUrl)

    try {
        $uri = [System.Uri]$ReadinessUrl
        $hostName = $uri.Host
        $port = $uri.Port
    }
    catch {
        if ($ReadinessUrl -notmatch ":(\d+)(/|$)") {
            throw "Could not read a port from readiness URL '$ReadinessUrl'."
        }

        $hostName = "localhost"
        $port = [int]$Matches[1]
    }

    if ([string]::IsNullOrWhiteSpace($hostName) -or $hostName -in @("*", "+", "0.0.0.0", "[::]")) {
        $hostName = "localhost"
    }

    [PSCustomObject]@{
        Host = $hostName
        Port = $port
    }
}

function Test-TcpPort {
    param(
        [string]$HostName,
        [int]$Port
    )

    $client = [System.Net.Sockets.TcpClient]::new()

    try {
        $connectTask = $client.ConnectAsync($HostName, $Port)

        if (-not $connectTask.Wait(1000)) {
            return $false
        }

        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-ForLauncherReady {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$TimeoutSeconds,
        [int]$PollSeconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) {
            return $false
        }

        if (Test-TcpPort -HostName $HostName -Port $Port) {
            return $true
        }

        Start-Sleep -Seconds $PollSeconds
    }

    return $false
}

function Send-DiscordMessage {
    param(
        [string]$Title,
        [string]$Description,
        [int]$Color,
        [hashtable[]]$Fields
    )

    $payload = @{
        username = "Server Status"
        embeds = @(
            @{
                title = $Title
                description = $Description
                color = $Color
                timestamp = (Get-Date).ToUniversalTime().ToString("o")
                fields = $Fields
            }
        )
    }

    $json = $payload | ConvertTo-Json -Depth 8

    if ($DryRun) {
        Write-Status "Dry run: would send Discord payload:"
        Write-Host $json
        return $true
    }

    try {
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        Invoke-RestMethod -Method Post -Uri $webhookUrl -Body $jsonBytes -ContentType "application/json; charset=utf-8" | Out-Null
        return $true
    }
    catch {
        Write-Warning "Discord webhook post failed: $($_.Exception.Message)"
        return $false
    }
}

if (-not $DryRun -and [string]::IsNullOrWhiteSpace($webhookUrl)) {
    throw "OWS_DISCORD_WEBHOOK_URL is not set. Run: setx OWS_DISCORD_WEBHOOK_URL `"https://discord.com/api/webhooks/...`""
}

if ($TimeoutSeconds -lt 1) {
    throw "TimeoutSeconds must be at least 1."
}

if ($PollSeconds -lt 1) {
    throw "PollSeconds must be at least 1."
}

$settings = Get-LauncherSettings
$endpoint = Get-TcpEndpoint -ReadinessUrl $settings.ReadinessUrl
$startedAt = Get-Date

if (Test-Path -LiteralPath $launcherProjectPath) {
    $dotnetArguments = @("run", "--project", $launcherProjectPath, "--no-launch-profile")
    $launchTargetDescription = "OWSInstanceLauncher.csproj"
}
elseif (Test-Path -LiteralPath $launcherDllPath) {
    $dotnetArguments = @("OWSInstanceLauncher.dll")
    $launchTargetDescription = "OWSInstanceLauncher.dll"
}
else {
    throw "Could not find OWSInstanceLauncher.dll or OWSInstanceLauncher.csproj in '$launcherRoot'."
}

Write-Status "Starting $launchTargetDescription from '$launcherRoot'."
Write-Status "Readiness check: $($endpoint.Host):$($endpoint.Port) from '$($settings.ReadinessUrl)'."

$process = Start-Process -FilePath "dotnet" -ArgumentList $dotnetArguments -WorkingDirectory $launcherRoot -NoNewWindow -PassThru
$onlineSent = $false

try {
    if (Wait-ForLauncherReady -HostName $endpoint.Host -Port $endpoint.Port -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Process $process) {
        Write-Status "Launcher is ready on $($settings.ReadinessUrl)."

        $onlineSent = Send-DiscordMessage `
            -Title "$greenCircle Server Status: Online" `
            -Description "The Instance Launcher is online and reachable." `
            -Color 3066993 `
            -Fields @(
                @{ name = "Machine"; value = $env:COMPUTERNAME; inline = $true },
                @{ name = "Process ID"; value = [string]$process.Id; inline = $true },
                @{ name = "Server IP"; value = [string]$settings.ServerIP; inline = $true },
                @{ name = "Launcher GUID"; value = [string]$settings.LauncherGuid; inline = $false },
                @{ name = "Started"; value = $startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"); inline = $false }
            )
    }
    else {
        if ($process.HasExited) {
            Write-Warning "Launcher exited before becoming ready. No online Discord message was sent."
        }
        else {
            Write-Warning "Launcher did not become ready within $TimeoutSeconds seconds. No online Discord message was sent."
        }
    }

    $process.WaitForExit()
}
finally {
    if ($onlineSent) {
        $endedAt = Get-Date
        $duration = New-TimeSpan -Start $startedAt -End $endedAt

        Send-DiscordMessage `
            -Title "$redCircle Server Status: Offline" `
            -Description "The Instance Launcher process has stopped." `
            -Color 15158332 `
            -Fields @(
                @{ name = "Machine"; value = $env:COMPUTERNAME; inline = $true },
                @{ name = "Process ID"; value = [string]$process.Id; inline = $true },
                @{ name = "Stopped"; value = $endedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"); inline = $false },
                @{ name = "Uptime"; value = $duration.ToString("c"); inline = $true }
            ) | Out-Null
    }
}
