# Measured capacity and hosting cost

What the Samsara Saga zone stress test measured on this backend and on Hetzner zone servers, and the hosting cost
projected from it. Measured 2026-11-05 and 2026-11-06 against the Hetzner backend, deployed from `main` at 7ada56a
for runs E and F. The numbers set the targets for [scaling-roadmap.md](scaling-roadmap.md) items 3.1 and 3.2.

The canonical data and the method are in the ragna-remake Perforce depot: `Tools/Agent/stress-results/*.json` (every
measured step, run F's database load, the Hetzner prices, the projection's assumptions),
`Tools/Agent/Plans/2026-11-03-zone-stress-test.md` (the runs, decisions and the "Capacity and cost projection"
section) and `Tools/Agent/stress-test.md` (how to run another test). Work the numbers again from those files after
new runs instead of copying them forward from here.

## Setup

- Zone server: one Hetzner CCX13 (2 dedicated vCPU, 8 GB) in Ashburn, Linux Test build, 30 Hz, Iris replication,
  started by an `OWSInstanceLauncher` on the same box.
- Real players: headless game clients on CPX51 and CCX43 bot boxes. Each logs in through the PublicAPI, travels
  between zones, fights mobs and uses its class skills. Dummies: server-side players that only walk.
- Backend: this repository's Hetzner deployment (a CPX21, 3 shared vCPU, 4 GB), Postgres 14 with
  `pg_stat_statements`.
- A step holds a player count for five minutes. Frame time is the server's busy time per frame (sleep excluded),
  p95 over the hold. The 30 Hz budget is 33.3 ms.

## Zone servers

| Quantity | Value | From |
| --- | --- | --- |
| p95 frame time per real player in combat | 0.376 ms | real-only field steps of 50+ players |
| Real players at the 30 Hz budget | about 99 | the line above |
| p95 frame time per added dummy | 0.058 ms | mixed field steps of 100 to 300 players |
| p95 with 300 players, mostly dummies | 26.8 ms field, 26.4 ms town | runs A3, B6 |
| Zone box CPU at 90 real players | 47 % of 2 vCPU | run F |

- Real players fighting set the limit, not crowd size. `Maps.HardPlayerCap` 100
  (`RaiseMapPlayerCaps_2026-11-06.sql`) is the 30 Hz edge for an all-real fight; the game's `GameSession`
  `MaxPlayers` becomes 100 with ragna-remake CL 1557 (pending).
- The game thread fills one core: plan one busy map instance per CCX13 and size `MaxNumberOfInstances` for quiet
  maps beside it (roadmap 3.1).
- The 90-player step is a lower bound: the bot boxes ran out of CPU there.

## Backend and database

| Quantity | Value | From |
| --- | --- | --- |
| Database commits per second per real player | 1.01 (baseline 8.0) | run F, 10 to 90 players |
| Commits per second at 1,000 real players | about 1,020 | the line above |
| Backend box CPU at 90 real players | 12 % | run F |
| Database time per login (`PlayerLoginAndCreateSession`) | 244 ms | bcrypt, run F |

Where database time went over run F's ramp (`pg_stat_statements`, top by total time):

| Statement | Calls | Mean ms | Share |
| --- | --- | --- | --- |
| `SELECT CharacterID FROM Characters ... FOR UPDATE` (save lock) | 22,049 | 2.74 | 45.9 % |
| `SELECT CharacterID, EconomyRevision, Gold FROM Characters ... FOR UPDATE` (currency save lock) | 6,260 | 5.70 | 27.2 % |
| `PlayerLoginAndCreateSession` | 90 | 244.17 | 16.7 % |
| `charstats` upsert | 3,130 | 0.72 | 1.7 % |
| `CharacterOwners` stale-owner check by name | 18,780 | 0.12 | 1.7 % |

- The time is row-lock waits when one character's saves overlap, not slow plans. With sequential scans disabled,
  every hot `CharacterOwners` statement uses `PK_CharacterOwners`; its sequential scans are the planner's choice on
  a table of about 100 rows. No index is missing.
- A login holds a database core for about a quarter of a second: spread logins after a restart.
- 2,730 `information_schema.columns` existence checks ran during saves; cheap now, avoidable later.
- The PublicAPI per-IP limit (10 `/api/Users` calls per 10 s) used to catch zone servers, which send every
  player's travel and join check from one IP; the 429's empty body showed in the game as "There was a problem
  connecting to the server". Requests with the valid `X-Samsara-Service-Key` are exempt since PR #65.

## Bandwidth

- 32 KB/s per player out of the zone server at 90 real players; 21 KB/s at 75.
- The engine's default per-client limit (100,000 bytes/s, about 86 KB/s on the wire) was full from about 200
  players in a crowd. Ragna-remake CL 1557 (pending) raises it to 200,000 on the server and the client.
- Predictive movement about doubles traffic per player at the same server cost.

## Projected monthly hosting

Zone servers = max(1, ceil(peak / 75)) CCX13, sized for the peak. Traffic = peak x 0.35 (average share of the peak,
assumed) x 25 KB/s x 1024 x 2,629,800 s, minus each box's included traffic, at $1.20 per TB. Backend boxes per tier
are a judgement: the backend was lightly loaded in every run. Hetzner Ashburn monthly prices from the API on
2026-11-06.

| Peak players | Zone servers | Zone cost | Backend | Backend cost | Traffic TB | Traffic cost | Total / month | Per peak player |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 50 | 1 | $51 | CPX21 | $37 | 1.2 | $0 | $88 | $1.77 |
| 100 | 2 | $102 | CPX21 | $37 | 2.4 | $0 | $139 | $1.39 |
| 250 | 4 | $204 | CPX21 | $37 | 5.9 | $0 | $241 | $0.97 |
| 500 | 7 | $357 | CCX23 | $103 | 11.8 | $2 | $462 | $0.92 |
| 1,000 | 14 | $714 | CCX33 | $166 | 23.6 | $6 | $886 | $0.89 |
| 2,000 | 27 | $1,377 | CCX33 + CCX23 | $269 | 47.1 | $14 | $1,660 | $0.83 |

The account's limit of 96 dedicated vCPU allows 48 CCX13 zone servers before a limit increase.

## Not measured

- More than 90 real players at once, or several busy maps on one zone server.
- Many zone servers on one backend at the same time; the backend numbers above come from one zone server.
- Real players: packaged Windows clients, home networks, trading, parties and chat.
- The average share of the peak online (0.35) and the backend sizing per tier are assumptions.
