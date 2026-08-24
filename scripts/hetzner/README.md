# OWS Hetzner Helpers

Two unrelated tools live here: the tunnel tray for local UE instance launching, and the
admin console launcher.

## Admin Console

Starts the loopback-only admin console on the dev server, tunnels to it, opens the browser,
and stops it again when you close the window with Ctrl+C.

Double-click:

```text
Update-AdminConsole.cmd     first run, and after pushing any console change
Start-AdminConsole.cmd      every time after that
```

`Update-AdminConsole.cmd` pulls `origin/main` on the server and rebuilds the image, so it is
the slow one; `Start-AdminConsole.cmd` just starts what is already built.

Both pass extra arguments through to `Start-AdminConsole.ps1`:

```powershell
.\Start-AdminConsole.cmd -KeepRunning        # leave it running server-side
.\Start-AdminConsole.cmd -Port 44411         # if 44410 is busy on this PC
.\Start-AdminConsole.cmd -Server 1.2.3.4     # a different box
```

`admin-console.sh` is the server-side half (`up` / `rebuild` / `down` / `status` / `logs`);
run it directly on the box when you do not want the tunnel. See
`docs/hosting/hetzner-dev.md` for why the console must stay on 127.0.0.1.

## OWS Hetzner Tunnel Tray

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
