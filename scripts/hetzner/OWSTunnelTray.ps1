param(
    [string] $Server = "87.99.150.89",
    [string] $SshUser = "root",
    [string] $KeyPath = "$env:USERPROFILE\.ssh\id_ed25519_codex_20260509",
    [int] $RabbitLocalPort = 56720,
    [int] $InstanceManagementLocalPort = 18028,
    [int] $CharacterPersistenceLocalPort = 18023,
    [int] $PublicApiPort = 44302,
    [int] $GlobalDataPort = 44325,
    [int] $ChatGrpcPort = 50051,
    [int] $PartyHttpPort = 44306,
    [int] $PartyGrpcPort = 44364,
    [switch] $NoAutoStart
)

Set-StrictMode -Version 2.0

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"

$state = [ordered]@{
    Enabled = -not $NoAutoStart.IsPresent
    Process = $null
    LastStart = $null
    LastExit = ""
    Status = "Stopped"
}

function Join-CommandArguments {
    param([string[]] $Arguments)

    $quoted = foreach ($arg in $Arguments) {
        if ($arg -match '[\s"]') {
            '"' + ($arg -replace '\\', '\\' -replace '"', '\"') + '"'
        }
        else {
            $arg
        }
    }

    return ($quoted -join " ")
}

function Test-LocalPort {
    param([int] $Port)

    $client = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $async = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(500, $false)) {
            return $false
        }

        $client.EndConnect($async)
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($client) {
            $client.Close()
        }
    }
}

function Test-TunnelProcess {
    return ($state.Process -ne $null -and -not $state.Process.HasExited)
}

function Stop-Tunnel {
    if (Test-TunnelProcess) {
        try {
            $state.Process.Kill()
            $state.Process.WaitForExit(3000) | Out-Null
        }
        catch {
        }
    }

    if ($state.Process -ne $null) {
        try {
            $state.LastExit = "Last exit code: $($state.Process.ExitCode)"
        }
        catch {
            $state.LastExit = "Last tunnel stopped."
        }

        $state.Process.Dispose()
    }

    $state.Process = $null
}

function Start-Tunnel {
    if (Test-TunnelProcess) {
        return
    }

    if (-not (Test-Path -LiteralPath $KeyPath)) {
        $state.Status = "Missing SSH key"
        $notify.Icon = [System.Drawing.SystemIcons]::Error
        $notify.Text = "OWS tunnel: missing key"
        $notify.ShowBalloonTip(5000, "OWS tunnel", "SSH key not found: $KeyPath", [System.Windows.Forms.ToolTipIcon]::Error)
        return
    }

    $sshArgs = @(
        "-F", "NUL",
        "-i", $KeyPath,
        "-N",
        "-o", "ExitOnForwardFailure=yes",
        "-o", "ServerAliveInterval=30",
        "-o", "ServerAliveCountMax=3",
        "-o", "StrictHostKeyChecking=accept-new",
        "-L", "127.0.0.1:$RabbitLocalPort`:127.0.0.1:5672",
        "-L", "127.0.0.1:$InstanceManagementLocalPort`:127.0.0.1:44328",
        "-L", "127.0.0.1:$CharacterPersistenceLocalPort`:127.0.0.1:44323",
        "$SshUser@$Server"
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "ssh.exe"
    $psi.Arguments = Join-CommandArguments $sshArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    try {
        $state.Process = [System.Diagnostics.Process]::Start($psi)
        $state.LastStart = Get-Date
        $state.LastExit = ""
    }
    catch {
        $state.Status = "Start failed"
        $notify.Icon = [System.Drawing.SystemIcons]::Error
        $notify.Text = "OWS tunnel: start failed"
        $notify.ShowBalloonTip(5000, "OWS tunnel", $_.Exception.Message, [System.Windows.Forms.ToolTipIcon]::Error)
    }
}

function Get-TunnelSummary {
    $statusLine = "Status: $($state.Status)"
    if ($state.LastStart) {
        $statusLine += "`nStarted: $($state.LastStart.ToString("g"))"
    }
    if ($state.LastExit) {
        $statusLine += "`n$($state.LastExit)"
    }

    return @"
$statusLine

Public API: http://$Server`:$PublicApiPort/
Global Data API: http://$Server`:$GlobalDataPort/
Chat gRPC: $Server`:$ChatGrpcPort
Party REST: http://$Server`:$PartyHttpPort/
Party gRPC: $Server`:$PartyGrpcPort

RabbitMQ tunnel: 127.0.0.1:$RabbitLocalPort
Instance Management tunnel: http://127.0.0.1:$InstanceManagementLocalPort/
Character Persistence tunnel: http://127.0.0.1:$CharacterPersistenceLocalPort/
"@
}

function Get-UeConfigSnippet {
    return @"
OWS2APIPath="http://$Server`:$PublicApiPort/"
OWS2InstanceManagementAPIPath="http://127.0.0.1:$InstanceManagementLocalPort/"
OWS2CharacterPersistenceAPIPath="http://127.0.0.1:$CharacterPersistenceLocalPort/"
OWS2GlobalDataAPIPath="http://$Server`:$GlobalDataPort/"
OWS2PartyAPIPath="http://$Server`:$PartyHttpPort/"

[/Script/TurboLinkGrpc.TurboLinkGrpcConfig]
DefaultEndPoint="$Server`:$ChatGrpcPort"
ServiceEndPoint=(("PartyApp","$Server`:$PartyGrpcPort"))
"@
}

function Get-LauncherConfigSnippet {
    return @"
"RabbitMQOptions": {
  "RabbitMQHostName": "127.0.0.1",
  "RabbitMQPort": $RabbitLocalPort,
  "RabbitMQUserName": "dev",
  "RabbitMQPassword": "test"
},
"OWSAPIPathConfig": {
  "InternalPublicApiURL": "http://$Server`:$PublicApiPort/",
  "InternalInstanceManagementApiURL": "http://127.0.0.1:$InstanceManagementLocalPort/",
  "InternalCharacterPersistenceApiURL": "http://127.0.0.1:$CharacterPersistenceLocalPort/"
}
"@
}

function Update-Status {
    if ($state.Enabled -and -not (Test-TunnelProcess)) {
        Start-Tunnel
    }

    $alive = Test-TunnelProcess
    $rabbit = Test-LocalPort $RabbitLocalPort
    $instanceManagement = Test-LocalPort $InstanceManagementLocalPort
    $characterPersistence = Test-LocalPort $CharacterPersistenceLocalPort

    if ($alive -and $rabbit -and $instanceManagement -and $characterPersistence) {
        $state.Status = "Connected"
        $notify.Icon = [System.Drawing.SystemIcons]::Information
        $notify.Text = "OWS tunnel: connected"
    }
    elseif ($state.Enabled -and $alive) {
        $state.Status = "Starting"
        $notify.Icon = [System.Drawing.SystemIcons]::Warning
        $notify.Text = "OWS tunnel: starting"
    }
    elseif ($state.Enabled) {
        $state.Status = "Reconnecting"
        $notify.Icon = [System.Drawing.SystemIcons]::Warning
        $notify.Text = "OWS tunnel: reconnecting"
    }
    else {
        $state.Status = "Stopped"
        $notify.Icon = [System.Drawing.SystemIcons]::Error
        $notify.Text = "OWS tunnel: stopped"
    }

    $statusItem.Text = "Status: $($state.Status)"
    $stopItem.Enabled = $alive -or $state.Enabled
    $startItem.Enabled = -not $state.Enabled
}

$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Text = "OWS tunnel: starting"
$notify.Icon = [System.Drawing.SystemIcons]::Warning
$notify.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip

$statusItem = New-Object System.Windows.Forms.ToolStripMenuItem
$statusItem.Text = "Status: Starting"
$statusItem.Enabled = $false
[void] $menu.Items.Add($statusItem)
[void] $menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$startItem = New-Object System.Windows.Forms.ToolStripMenuItem
$startItem.Text = "Start tunnel"
$startItem.Add_Click({
    $state.Enabled = $true
    Start-Tunnel
    Update-Status
})
[void] $menu.Items.Add($startItem)

$stopItem = New-Object System.Windows.Forms.ToolStripMenuItem
$stopItem.Text = "Stop tunnel"
$stopItem.Add_Click({
    $state.Enabled = $false
    Stop-Tunnel
    Update-Status
})
[void] $menu.Items.Add($stopItem)

$reconnectItem = New-Object System.Windows.Forms.ToolStripMenuItem
$reconnectItem.Text = "Reconnect"
$reconnectItem.Add_Click({
    $state.Enabled = $true
    Stop-Tunnel
    Start-Tunnel
    Update-Status
})
[void] $menu.Items.Add($reconnectItem)

$showStatusItem = New-Object System.Windows.Forms.ToolStripMenuItem
$showStatusItem.Text = "Show status"
$showStatusItem.Add_Click({
    $notify.ShowBalloonTip(7000, "OWS tunnel", (Get-TunnelSummary), [System.Windows.Forms.ToolTipIcon]::Info)
})
[void] $menu.Items.Add($showStatusItem)

[void] $menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$copyUeItem = New-Object System.Windows.Forms.ToolStripMenuItem
$copyUeItem.Text = "Copy UE config"
$copyUeItem.Add_Click({
    [System.Windows.Forms.Clipboard]::SetText((Get-UeConfigSnippet))
    $notify.ShowBalloonTip(3000, "OWS tunnel", "UE config copied to clipboard.", [System.Windows.Forms.ToolTipIcon]::Info)
})
[void] $menu.Items.Add($copyUeItem)

$copyLauncherItem = New-Object System.Windows.Forms.ToolStripMenuItem
$copyLauncherItem.Text = "Copy launcher config"
$copyLauncherItem.Add_Click({
    [System.Windows.Forms.Clipboard]::SetText((Get-LauncherConfigSnippet))
    $notify.ShowBalloonTip(3000, "OWS tunnel", "Instance Launcher config copied to clipboard.", [System.Windows.Forms.ToolTipIcon]::Info)
})
[void] $menu.Items.Add($copyLauncherItem)

$openApiItem = New-Object System.Windows.Forms.ToolStripMenuItem
$openApiItem.Text = "Open API status"
$openApiItem.Add_Click({
    Start-Process "http://$Server`:$PublicApiPort/api/system/status"
})
[void] $menu.Items.Add($openApiItem)

[void] $menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem
$exitItem.Text = "Exit"
$exitItem.Add_Click({
    $timer.Stop()
    $state.Enabled = $false
    Stop-Tunnel
    $notify.Visible = $false
    $notify.Dispose()
    $context.ExitThread()
})
[void] $menu.Items.Add($exitItem)

$notify.ContextMenuStrip = $menu
$notify.Add_DoubleClick({
    $notify.ShowBalloonTip(7000, "OWS tunnel", (Get-TunnelSummary), [System.Windows.Forms.ToolTipIcon]::Info)
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 5000
$timer.Add_Tick({ Update-Status })

$context = New-Object System.Windows.Forms.ApplicationContext

Update-Status
$timer.Start()
[System.Windows.Forms.Application]::Run($context)
