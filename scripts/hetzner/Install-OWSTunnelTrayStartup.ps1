$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "OWS Hetzner Tunnel Tray.lnk"
$launcherPath = Join-Path $PSScriptRoot "Start-OWSTunnelTray.cmd"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $launcherPath
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = "Starts the OWS Hetzner SSH tunnel tray helper."
$shortcut.Save()

Write-Host "Installed startup shortcut:"
Write-Host $shortcutPath
