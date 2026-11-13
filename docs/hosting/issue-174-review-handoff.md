# Issue #174: blocked on deployed zone-server callers

**Status: BLOCKED.** The backend fix and repository plugin examples are implemented.
The deployed game caller fix has not been made in this worktree. Keep any future PR
blocked/draft; do not merge or deploy it until the prerequisite below is resolved.
No PR has been opened or edited as part of this handoff.

## Draft PR body

`UpdateNumberOfPlayers` accepted a tenant GUID without a server credential, allowing
forged readiness, liveness and player counts. The endpoint and related zone-server
routes now require the service key even when the optional global gate is disabled.
The Hetzner profile enables the global gate by default. The plugin copies in this
repository send the header, but the separately maintained Samsara game build does
not use those copies and still needs the caller change.

**Hard deployment prerequisite: Perforce caller CL: UNRESOLVED; rebuilt dedicated-server
artifact: UNRESOLVED.** Replace both with actual references and attach validation results
before marking this PR ready. Repository plugin edits alone do not satisfy this prerequisite.

In the Perforce project at
`C:/Users/Julian/Perforce/perforce_julian_7571/depot/ragna-remake`, inspect `p4 opened`,
preserve existing work, and use `p4 edit` for these three files (four helpers):

| File relative to game root | Helper and inspected InstanceManagement branch |
| --- | --- |
| `Plugins/OWSPlugin/Source/OWSPlugin/Private/OWSGameMode.cpp` | `AOWSGameMode::ProcessOWS2POSTRequest`, line 99 |
| `Plugins/OWSPlugin/Source/OWSPlugin/Private/OWSAPISubsystem.cpp` | `UOWSAPISubsystem::ProcessOWS2POSTRequest`, line 83; `ProcessOWS2GETRequest`, line 123 |
| `Source/SamsaraSaga/Private/Gameplay/GameStateData.cpp` | `AGameStateData::ProcessOWS2POSTRequest`, line 505 |

Each `InstanceManagementAPI` branch must set the following header on its request,
matching the existing CharacterPersistence behavior:

```cpp
Request->SetHeader(TEXT("X-Samsara-Service-Key"), FPlatformMisc::GetEnvironmentVariable(TEXT("OWS_SAMSARA_SERVICE_KEY")));
```

`ASSGameMode` inherits the game-mode request helper; changing a character-persistence
override alone will not update its heartbeat. Review the resulting `p4 diff`, build
the dedicated server, and identify that build and the caller CL here. These edits and
the Unreal build remain outstanding: the game checkout is outside this task's writable
worktree. No Perforce changelist or rebuilt binary is claimed by this handoff.

Before backend deployment, prove that the rebuilt caller sends the header for all four
helpers, including the GameStateData path. With `OWS_REQUIRE_SERVICE_KEY=true`, a keyless
heartbeat for a status-1 row must return 401 and leave status, timestamp and count unchanged;
the rebuilt server's heartbeat must succeed, set status 2, and advance the timestamp.
Also exercise its GetZoneInstance, GetCurrentWorldTime, GetZoneInstancesForZone, AddZone
and UpdateZone calls. Confirm the endpoint heartbeat protection remains active with
the global gate disabled. Do not record or publish the key itself.

Deploy the completed caller build first. An older caller receives 401s from this backend
and cannot become ready. Setting `OWS_REQUIRE_SERVICE_KEY=false` cannot bypass the endpoint
attributes; recovery requires the completed caller or a backend rollback.

Backend validation from the implementation pass: Release restore/build passed; five
keyless route regressions failed before the fix; the final PostgreSQL 17 suite passed
521 tests with 3 skipped after temporary database tuning and a longer command timeout.
PostgreSQL 14 CI equivalence, the NuGet vulnerability scan, the Unreal build and deployed
caller validation remain unverified. Docker access and NuGet TLS errors prevented the
first two checks locally. This documentation-only review response does not rerun builds.
