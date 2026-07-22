# OWSFork Scaling Roadmap (High CCU)

Where this fits: Phases 1 and 2 are implemented (see the change log at the bottom). They move the
**backend** ceiling from "a few hundred CCU" toward "~1000 CCU on the current single box" and close
the immediate realtime-abuse surface (gRPC connection caps + rate limits). They do **not** move the
two hard ceilings that cap total CCU: the single-host deployment and the single home-hosted zone-server
launcher. Phases 3 and 4 below address those. Effort estimates assume one engineer familiar with the stack.

The ordering is deliberate: Phase 3 removes the ceilings that stop you reaching thousands of CCU at all;
Phase 4 makes running at that scale safe, observable, and multi-replica. Do Phase 3 first.

---

## Phase 3 — Capacity (reach thousands of CCU)

### 3.1 Move zone-server launchers to datacenter hosts (the #1 CCU ceiling)
- **Problem:** One `OWSInstanceLauncher` on a home Windows PC, `MaxNumberOfInstances=10`, on a residential
  IP/uplink. Hard ceiling ~10 UE server processes; realistic sustainable CCU is low hundreds regardless of
  backend capacity.
- **Action:** OWS already supports multiple launchers (they register as `WorldServers` rows and spin-up
  load-balances across them by instance count — no code change needed). Stand up launchers on datacenter
  hosts, each with its own `LauncherGuid`, a routable public `ServerIP`, its own `StartingInstancePort`
  range, and network reach to the shared Postgres/RabbitMQ/InstanceManagement/CharacterPersistence. Raise
  `MaxNumberOfInstances` per host to match its CPU. Retire the `EnableAutoLoopback`/`NoPortForwarding`
  home-hosting workarounds.
- **Effort:** ~2-3 days (infra + config + one packaged Linux/Windows server build per host). Pure ops.
- **Unlocks:** CCU now scales with (number of launcher hosts × instances × HardPlayerCap).

### 3.2 Move Postgres and RabbitMQ off the app host
- **Problem:** Postgres, RabbitMQ, six services, Caddy, and Valkey share one 4 GB box; the docs recommend a
  swapfile, i.e. memory pressure is designed in. Postgres tuning is dev-sized (`shared_buffers=128MB`,
  `work_mem=4MB`).
- **Action:** Put Postgres on a dedicated/managed instance sized for the working set (raise `shared_buffers`
  to ~25% RAM, `work_mem`, `max_connections`). Move RabbitMQ to its own node. Keep app services on the app
  host(s).
- **Effort:** ~2 days. Ops + connection-string/env changes.
- **Depends on:** nothing; do alongside 3.1.

### 3.3 PgBouncer in front of Postgres (raise the connection ceiling properly)
- **Problem:** Phase 1 capped each service's Npgsql pool (6 × 5 = 30 < 40) to prevent exhaustion, but that
  is a floor, not headroom — a busy service can queue on connections under load.
- **Action:** Put PgBouncer (transaction pooling) in front of Postgres. Point services at PgBouncer, raise
  the per-service Npgsql cap (`OWS_DB_MAX_POOL_SIZE`) accordingly, and set Postgres `max_connections` to what
  the hardware supports. This decouples app concurrency from raw Postgres connections.
- **Effort:** ~1 day.
- **Depends on:** 3.2 (dedicated Postgres).

### 3.4 Validate the connection token at the zone server (security ceiling)
- **Problem:** The client's connect blob is AES-encrypted with a **shared static key shipped in the client**,
  and the UE server stores `UserSessionGUID` without ever validating it against the backend. Anyone with the
  key can connect to any public zone port as any character at arbitrary coordinates.
- **Action:** In the UE server `InitNewPlayer`/`PreLogin`, call the backend `GetUserSession` to verify the
  `UserSessionGUID` (and that it owns the claimed character) before admitting the player. Reject on failure.
  This is a **UE client/server change** (Perforce), coordinated with the backend — the backend endpoint
  already exists and is now index-backed and cached.
- **Effort:** ~2-3 days (UE-side, plus test). Coordinated cross-repo change.
- **Note:** This is also the prerequisite for turning on `OWS_GRPC_REQUIRE_SESSION_AUTH` (Phase 2 shipped the
  server-side validation gated off precisely because no session token is sent yet — see 4.1).
- **Client status (drafted, needs compile + test):** `ASSGameMode::InitNewPlayer` now performs a gated,
  fail-open connect-time session check against `api/Users/GetUserSession` and kicks on a definitive
  invalid/expired session. It is **off** unless the zone server has env `OWS_VALIDATE_ZONE_SESSION=true`,
  and it only ever kicks on a clear negative (any error/ambiguity fails open), so it is safe to enable
  incrementally. Currently validates session existence only; binding the session to the specific
  connecting character is a follow-on tightening.

---

## Phase 4 — Scale-out, resilience, observability (run at scale safely)

### 4.1 Enable gRPC session auth (finish Phase 2)
- **Problem:** Chat/Party accept streams with client-asserted identity; the Phase 2 validation is wired but
  gated off (`OWS_GRPC_REQUIRE_SESSION_AUTH=false`) because the client sends no session token.
- **Action:** Update the UE Chat/Party clients to send `customerguid` + `usersessionguid` as gRPC call
  metadata (the login flow already has both). Then flip the flag on. Party already does full DB validation
  bound to the session's selected character; extend Chat from credential-presence to full validation (small
  follow-up — Chat needs `IUsersRepository` wired, or an internal call to the PublicAPI `GetUserSession`).
- **Client status (drafted, needs compile + test):** the UE clients now attach the metadata via TurboLink's
  `FGrpcMetaData` — `USSChatClientManager` on chat stream open, `UPartyComponent` on `RegisterParty`. The
  customer key comes from project ini (client) / the game-mode override; the session GUID from
  `ASSPlayerState::GetUserSessionGUID()`. Once these are verified in a build, set
  `OWS_GRPC_REQUIRE_SESSION_AUTH=true` on the backend to enforce.
- **Effort:** ~0.5 day (verify/build the drafted client change) + ~0.5 day (Chat full validation).

### 4.2 Externalize per-instance state so app services can run 2+ replicas
- **Problem:** Three things break with more than one replica: the in-memory rate limiter (OWSPublicAPI), the
  local-disk DataProtection keyring (OWSPublicAPI, OWSManagement), and the static in-process client maps in
  OWSChat and OWSParty.
- **Action:**
  - Rate limiter → distributed (Redis/Valkey-backed) or enforce at the ingress (Caddy/Traefik).
  - DataProtection → persist the keyring to a shared volume or Redis (`PersistKeysToStackExchangeRedis`) so
    replicas share it and survive container recreation.
  - Chat/Party → back the client registry and broadcast with Redis/Valkey pub-sub (or a dedicated realtime
    tier) so fan-out spans replicas. This is a genuine re-architecture, not a config change.
- **Effort:** ~1 day (rate limiter + DataProtection) + ~1 week (Chat/Party pub-sub).
- **Depends on:** the DB/connection work (3.2-3.3) so replicas don't just re-saturate Postgres.

### 4.3 Health checks + observability
- **Problem:** Health endpoints just `return Ok(true)` and are not wired as Docker healthchecks for app
  services; there is no metrics/APM. At high CCU you are blind and a wedged container keeps taking traffic.
- **Action:** Add real `AddHealthChecks()` (DB, RabbitMQ, Valkey) + `MapHealthChecks`, wire them as compose
  healthchecks for every app service and Caddy. Add OpenTelemetry (or Prometheus) for request rate, latency,
  error rate, DB-pool usage, and queue depth; ship to a dashboard with alerts.
- **Effort:** ~2-3 days.

### 4.4 Durable RabbitMQ + connection reuse
- **Problem:** Spin-up/shut-down exchanges/queues are `durable:false`, published transient with `autoAck:true`,
  and a new connection is opened per publish. A broker restart or launcher crash loses in-flight spin-up
  messages (failed connects, reserved-but-dead instances); recovery is polling-only.
- **Action:** Make the exchange/queue durable and persistent, switch consumers to manual ack (ack after the
  process actually starts), reuse a single long-lived broker connection per service, and add automatic
  recovery/reconnect. The empty-handler `RabbitMQ_ConnectionShutdown` should re-establish consumption.
- **Effort:** ~2 days.

### 4.5 Automated backups / DR
- **Problem:** Only a manual `pg_dump` in the docs. No WAL archiving, snapshots, or offsite copy — RPO is
  "whenever someone last remembered."
- **Action:** Scheduled `pg_dump` (or `pgBackRest`) with WAL archiving for PITR, offsite storage, and a
  tested restore runbook. Volume snapshots for Valkey/RabbitMQ if their state matters.
- **Effort:** ~1-2 days.
- **Depends on:** 3.2 (dedicated Postgres makes this cleaner).

### 4.6 Refresh EOL base images + add a circuit breaker to Steam auth
- **Problem:** `postgres:14.2-alpine3.15` (Feb-2022, EOL Alpine) and `rabbitmq:3.9.0` (EOL series) carry
  unpatched CVEs. Steam auth has a 10s timeout, no circuit breaker — a Steam outage holds request threads and
  can cascade to thread-pool exhaustion on the Public API.
- **Action:** Move to a current supported Postgres (14.latest or 16.x) on a supported base, current RabbitMQ,
  and pin digests for reproducible prod builds. Wrap the Steam WebAPI call in a Polly circuit breaker +
  fast-fail fallback.
- **Effort:** ~1-2 days.

---

## Suggested sequence

1. **3.1 + 3.2** in parallel (multi-launcher + move DB/RabbitMQ off-box) — biggest CCU unlock.
2. **3.3** PgBouncer, then **3.4** zone-server token validation (UE change).
3. **4.3** observability (you want eyes on before pushing load), **4.4** durable queues, **4.5** backups.
4. **4.1** enable gRPC auth (rides on 3.4's client work), then **4.2** the replica/scale-out work.
5. **4.6** image refresh + Steam breaker (can slot in any time; do before a public launch).

Phases 1-2 make the backend ready for ~1000 CCU on one box. Phase 3 is what turns "one box, one home PC"
into a datacenter footprint that scales to thousands. Phase 4 makes running there safe and observable.
