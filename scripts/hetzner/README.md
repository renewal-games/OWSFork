# OWS Hetzner Tunnel Tray

Use this on the Windows PC that runs your local `OWSInstanceLauncher` and UE
server/editor instances.

## Start

Double-click:

```text
Start-OWSTunnelTray.cmd
```

The tray icon appears in the Windows notification area. Right-click it for:

- status
- start/stop/reconnect
- copy UE config
- copy Instance Launcher config
- open API status

## What It Opens

The tray script keeps this SSH tunnel alive:

```text
127.0.0.1:56720 -> 87.99.150.89:127.0.0.1:5672
127.0.0.1:18028 -> 87.99.150.89:127.0.0.1:44328
127.0.0.1:18023 -> 87.99.150.89:127.0.0.1:44323
```

Keep the tray running while testing local UE instance spin-up.

## Start With Windows

Run once:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-OWSTunnelTrayStartup.ps1
```

Remove the startup shortcut:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-OWSTunnelTrayStartup.ps1
```

## Manual Run

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\OWSTunnelTray.ps1
```

Optional parameters:

```powershell
.\OWSTunnelTray.ps1 -Server 87.99.150.89 -KeyPath "$env:USERPROFILE\.ssh\id_ed25519_codex_20260509"
```
