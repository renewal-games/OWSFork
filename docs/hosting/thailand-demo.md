# Thailand-Region Game Servers (Investor Demo)

A second OWSInstanceLauncher + Linux UE dedicated servers on a **Hetzner Singapore** VM
(~30ms to Bangkok) for the investor demo. The OWS API/DB stack on the main Hetzner box
keeps running as-is; every delta lives on the demo VM and in the region-routing config.
Deployment bundle staged at `E:\SSPackagedLinux\` (Linux server package, self-contained
linux-x64 launcher, `deploy/` with the VM appsettings, systemd units, and `vm-setup.sh`).

**The PC launcher stays running throughout.** Players are routed by region, not by which
launcher happens to be least loaded, so testers keep landing on the PC while demo accounts
land in Singapore. That is the whole point of the region-routing change — earlier versions
of this runbook required stopping the PC launcher for the duration of the demo.

## How it works

- Each launcher registers a **region** (`OWSInstanceLauncherOptions.ServerRegion` ->
  `WorldServers.ServerRegion`): the PC is `NA-EAST`, the VM is `SEA`.
- Each account carries `Users.PreferredServerRegion`, set either by the login screen's
  server selector (`POST api/Users/SetPreferredServerRegion`) or by hand in SQL. NULL means
  "never chose" and resolves to the default region, so existing testers are unaffected.
- Both routing queries filter on that region — new zone spin-ups
  (`GetActiveWorldServersByLoad`) *and* reuse of an already-running instance
  (`GetZoneInstancesByZoneAndGroup`). Reuse matters as much as spin-up: without it a demo
  account would be handed whatever instance the PC already had open.
- Routing **fails closed**: if no host in the account's region is active, the player gets
  "No active World Servers ... in region SEA" rather than a silent 250ms fallback to the PC.
- The VM launcher registers with a **blank `LauncherGuid`** (self-generates on first run,
  written back to its appsettings) and `ServerIP` = the VM's public IP, handed verbatim to
  game clients.
- It replicates the PC's SSH-tunnel topology (`56720->5672` RabbitMQ, `18028->44328`
  InstanceManagement, `18023->44323` CharacterPersistence), so the `-OWS2*APIPath`
  command-line flags and all ini values work unchanged. GlobalData/Party/Chat are reached
  directly on the API box's public ports.

## VM

- `ccx33` (dedicated vCPU) or `ccx23`, `ubuntu-22.04`, location `sin`, name `ows-demo-sin`.
- Firewall: TCP 22 from admin IP only; **UDP 7778-7787 open** (game traffic);
  **TCP 8181 open** (the launcher's `/ping`, which the login screen times to show latency);
  everything else closed.
- Setup: upload bundle to `/opt/ows/upload/`, run `deploy/vm-setup.sh` as root, authorize
  the printed pubkey on the API box, fill `/etc/ows/launcher.env`, start `ows-tunnels` then
  `ows-launcher`.
- `/etc/ows/launcher.env` holds `OWS_SAMSARA_SERVICE_KEY`, copied from the API box's
  `.env.hetzner-dev`. The launcher does not use it; the **zone servers it spawns** do, for
  player-shop calls. Without it shops fail only on SEA-hosted zones.

## API box config

Add to `/opt/owsfork/src/.env.hetzner-dev`, then
`docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml up -d --no-deps --force-recreate owspublicapi`:

```
OWS_SERVER_SEA_NAME=Samsara SEA
OWS_SERVER_SEA_REGION=SEA
OWS_SERVER_SEA_STATUS=online
OWS_SERVER_SEA_PINGHOST=http://<vm-ip>:8181/ping
```

`OWS_SERVER_SEA_REGION` must match the VM launcher's `ServerRegion` exactly (case
sensitive). Leave `OWS_SERVER_SEA_NAME` blank to hide the row; leave `STATUS=maintenance`
to show it greyed out and unselectable.

## Pre-demo checklist

- Both hosts registered, one per region:
  `SELECT WorldServerID, ServerIP, ServerRegion, ServerStatus, ActiveStartTime FROM WorldServers;`
  -> the PC as `NA-EAST` and the VM as `SEA`, both `ServerStatus = 1` with a start time.
- Demo accounts pinned (only needed if they will not use the client's server selector):
  `UPDATE Users SET PreferredServerRegion = 'SEA' WHERE LOWER(TRIM(Email)) IN (...);`
  then confirm with `SELECT Email, PreferredServerRegion FROM Users WHERE PreferredServerRegion IS NOT NULL;`
- Demo accounts: email does **not** contain `@localhost`, `IsInternalNetworkTestUser = false`
  (else they are handed `127.0.0.1` regardless of region).
- Demo client packaged with `bUseDevelopmentURL=False` in `DefaultGame.ini`.
- `curl -i http://<vm-ip>:8181/ping` answers 200 from outside the VM.
- Pre-warm the demo zone with a staff login on an SEA-pinned account (avoids the spin-up wait).
- A tester on an unpinned account logs in at the same time and lands on the PC.

## Teardown (~15 min)

1. `systemctl stop ows-launcher` on the VM — graceful shutdown posts
   `ShutDownInstanceLauncher`, deactivating the row and killing zone servers.
2. Un-pin the demo accounts, or they fail closed with no SEA host:
   `UPDATE Users SET PreferredServerRegion = NULL WHERE PreferredServerRegion = 'SEA';`
3. Set `OWS_SERVER_SEA_STATUS=maintenance` (or blank `OWS_SERVER_SEA_NAME`) and
   `up -d --no-deps --force-recreate owspublicapi`.
4. Delete the Hetzner server + firewall.
5. Remove the VM's pubkey from the API box's `authorized_keys`.
6. Optional: `DELETE FROM WorldServers WHERE ServerRegion = 'SEA';`

The PC launcher needs no restart — it was never stopped. Nothing in the repo or in client
builds needs reverting; region routing is inert with a single region registered.
