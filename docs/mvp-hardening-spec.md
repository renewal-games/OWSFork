# MVP v1 Hardening Spec

Fix plan derived from the pre-MVP audit, ordered by expected player impact. Each item states the
defect, the fix, the blast radius if we ship without it, and what "done" means.

Scope note: the Character Persistence and Instance Management APIs bind to `127.0.0.1` on the
Hetzner host (`src/docker-compose.hetzner-dev.yml`), so today network isolation — not application
auth — is what keeps untrusted callers out of the gold-writing endpoints. Several items below are
defense-in-depth against that assumption changing. Items 1, 2 and 6 are exploitable *without* it
changing.

---

## 1. Purchase state machine can pay out twice (P0)

**Defect.** `PlayerShopsRepository.ConfirmDelivery` and `ResolveUndelivered` both read the purchase
row with `PlayerShopQueries.GetPurchaseByOperationId`, a plain `SELECT` with no row lock. Both can
observe `State = 'paid'`. They then serialize on the buyer's character lock, but the loser proceeds
on its stale read: `ConfirmDelivery` grants the item via `ReplaceInventory` and commits, then
`ResolveUndelivered` refunds the gold, restocks the listing, and its guarded
`UPDATE ... AND State = 'paid'` matches zero rows — which nobody checks — and commits anyway.

Net result: item delivered, gold refunded, stock returned for resale. Three-way value creation from
one purchase.

**Fix.**
- Add `GetPurchaseByOperationIdForUpdate` (`SELECT ... FOR UPDATE`) and use it in both transitions.
  Keep the unlocked variant for `Purchase`'s replay probe and `GetOperationResult`, which are
  read-only and run outside a transaction.
- Treat the terminal `UPDATE` as an assertion: capture rows affected from `MarkPurchaseDelivered` /
  `MarkPurchaseRestocked` and roll back when it is not exactly 1. Belt and braces on top of the lock.

**Files.** `src/OWSData/SQL/PlayerShopQueries.cs`, `src/OWSData/Repositories/Implementations/Postgres/PlayerShopsRepository.cs`

**Done when.** Two concurrent calls against one `OperationId` produce exactly one of
{delivered, restocked}; the loser returns `already_resolved` and writes nothing.

---

## 2. Inventory saves are neither atomic nor visible to the economy protocol (P0)

**Defect, part A (durability).** `CharactersRepository.UpdateCharacterInventory` deletes every
`CharInventoryItems` row and re-inserts them one at a time on an auto-committing connection. No
transaction. A crash, timeout, or one bad row mid-loop leaves a truncated bag with no rollback.
`PlayerShopsRepository.ReplaceInventory` performs the identical operations correctly inside a
transaction — the ordinary save path simply never got the same treatment, and it is the highest
frequency write in the system.

**Defect, part B (the surviving dupe vector).** The same method takes no lock on the character row
and never bumps `EconomyRevision`. `UpdateCharacterCurrency` was fixed to do both; inventory was
not. So a shop `Purchase` can read the bag under lock and commit, and a routine autosave carrying
the *pre-trade* bag lands immediately after and clobbers it — duplicating or destroying items with
no revision guard firing. Every optimistic check in PlayerShops is bypassed by the most common
write in the game.

**Fix.**
- Wrap the rewrite in a transaction and take `FOR UPDATE` on the character row for its duration, so
  it serializes against `Purchase` / `ClaimEscrow` / `VendorTrade`.
- Add an **opt-in** `ExpectedRevision` to the request. When supplied, verify it under the lock and
  reject a stale snapshot with `stale_revision`; when absent, preserve today's last-write-wins
  behaviour so existing UE clients keep working.
- Bump `EconomyRevision` **only** when the caller supplied one. Bumping unconditionally would
  invalidate the zone server's cached revision on every autosave and make every subsequent shop op
  fail `stale_revision` for clients that do not read the new value back.
- Return the post-write revision, mirroring `UpdateCharacterCurrencyResponse`, so the client can
  adopt the protocol incrementally.

Serialization alone does **not** fix part B — a stale snapshot still wins after the lock is
released. The `ExpectedRevision` check is what actually closes it, and that requires a matching
UE-side change to send the value. This spec lands the backend half in a backward-compatible shape.

**Files.** `src/OWSData/SQL/PlayerShopQueries.cs`, `src/OWSData/Models/Composites/`,
`src/OWSData/Repositories/Interfaces/ICharactersRepository.cs`,
`src/OWSData/Repositories/Implementations/{Postgres,MSSQL}/CharactersRepository.cs`,
`src/OWSCharacterPersistence/Requests/Characters/UpdateCharacter{Inventory,Data}Request.cs`

**Done when.** A failure part-way through the insert loop leaves the previous bag fully intact; a
caller sending a stale `ExpectedRevision` is rejected instead of clobbering.

**Follow-up (UE side, not in this change).** Have the zone server send `ExpectedRevision` on
inventory saves and resync from `NewEconomyRevision`. Until then part B remains open by design.

---

## 3. `GetServerToConnectTo` does not bind the session to the character (P0)

**Defect.** `GetServerToConnectToRequest` accepts `UserSessionGUID` and `CharacterName` and never
relates them. `usersRepository` is injected and assigned in `SetData` — and never read. The handler
goes straight to `GetCharByCharName` → `JoinMapByCharName` → `AddCharacterToMapInstanceByCharName`.
Its own doc comment says `UserSessionSetSelectedCharacter` "MUST be called first"; nothing enforces
it.

Any caller can request a connection as any character by name, move that character's
`CharOnMapInstance` row, and force instance spin-ups by name. Even if the UE zone server
independently re-validates identity on connect, the DB-side placement and the spin-up amplification
stand on their own as grief/DoS vectors.

**Fix.** Load the session, require a live `UserGuid`, and require
`SelectedCharacterName == CharacterName` (case-insensitive) before any character work. Fail with a
message that names the missing step.

**Files.** `src/OWSPublicAPI/Requests/Users/GetServerToConnectToRequest.cs`

**Done when.** A request whose `CharacterName` differs from the session's selected character is
rejected before any DB mutation.

---

## 4. Persistence API service key is opt-in (P1 — blocked on UE client)

**Defect.** `CharactersController.RequireServiceKey` reads `OWS_REQUIRE_CHARACTER_WRITE_KEY`,
default **off**, leaving `X-CustomerGUID` — a non-secret present in every launcher config — as the
only gate on `UpdateCharacterCurrency`, `UpdateCharacterInventory`, and friends. The sibling
`PlayerShopsController` fails *closed* in production. Same service, same process, opposite posture.

**Blocked.** `src/docker-compose.hetzner-dev.yml:23` says "Keep false until the UE persistence calls
send the key", and grep over the bundled `plugins/OWSPluginUE5` finds no `X-Samsara-Service-Key`
sender. The shipping client lives in Perforce and cannot be verified from this repo. Flipping the
default blind would 401 all persistence traffic.

**Fix (this change).** Do not change the default. Make the gap loud and the flip a one-liner:
- Align `ServiceKeyOk()` with the PlayerShops implementation (a configured-but-unmatched key must
  fail regardless of the toggle, so a *wrong* key is never treated as "not required").
- Emit a startup warning when the API is serving unauthenticated writes outside Development.

**Files.** `src/OWSCharacterPersistence/Controllers/CharactersController.cs`,
`src/OWSCharacterPersistence/Startup.cs`

**Done when.** An unauthenticated non-Development boot logs a warning naming the env var; setting
`OWS_REQUIRE_CHARACTER_WRITE_KEY=true` is the only step needed once the client sends the header.

---

## 5. `CreateShop` writes caller-supplied gold verbatim (P2)

**Defect.** `PlayerShopsRepository.CreateShop` writes `Gold = input.PostEscrowGold` — a pre-computed
wallet value straight from the request. Every sibling path recomputes gold from the freshly-locked
row and carries a comment saying why (`Purchase`, `ClaimEscrow`, `VendorTrade`). `CreateShop` is the
lone exception, making it an unconditional gold-set primitive: the one place in the economy where a
stale or wrong number wins outright rather than being caught.

Not player-exploitable while the endpoint stays behind the fail-closed service key and its
`ExpectedRevision` check, which is why this sits below the items above.

**Fix.** Recompute as `character.Gold - input.OpeningFeeGold` (the field already exists on
`CreateShopInput` and is otherwise unused), reject a negative fee as `bad_request` and an
unaffordable one as `insufficient_funds`.

**Files.** `src/OWSData/Repositories/Implementations/Postgres/PlayerShopsRepository.cs`

**Done when.** No economy write path takes a wallet value from its caller.

---

## 6. One malformed position entry drops the whole map's save (P2)

**Defect.** `UpdateAllPlayerPositionsRequest.Handle` splits a packed string and indexes
`PlayerDataValues[1..6]` unguarded, then calls culture-sensitive `float.Parse`. A short segment
throws `IndexOutOfRangeException`; a comma-decimal locale on the zone server host throws
`FormatException`. Either aborts the loop mid-way, so **every player after the bad entry** loses
their position save — and the exception escapes to a 500 rather than a `SuccessAndErrorMessage`.

**Fix.** Parse with `CultureInfo.InvariantCulture`, validate segment count, and isolate each entry
so one bad record is skipped and reported rather than taking the batch down.

**Files.** `src/OWSCharacterPersistence/Requests/Characters/UpdateAllPlayerPositionsRequest.cs`

**Done when.** A batch containing one malformed entry still persists every well-formed entry and
returns a non-throwing result describing what was skipped.

---

## Explicitly not doing

- **`CharacterSaveData.CharCurrency`** loads via the inventory query and its element type
  `CharacterCurrency` has no properties — nothing could ever have deserialized gold from it, so this
  is dead code, not live data loss. Delete during cleanup.
- **`UpdatePosition` Z+1** is applied once per load against a physics-reported position; there is no
  feedback loop and gravity settles it. Not a defect.
- **Guild on Postgres** calls stored procedures (`AddNewGuild`, `AddNewGuildMember`,
  `GetInitialGuildMembers`) that do not exist in any Postgres migration, and swallows the resulting
  exceptions into empty `catch` blocks. Broken — but OWSGuild is deliberately omitted from the
  deployment profile. Out of MVP scope; confirm the guild UI is disabled client-side.
- **TLS.** The deployment profile ships a Caddy TLS proxy for the public-facing services. Real gap
  only if `OWS_PUBLIC_API_DOMAIN` is unset while `OWS_API_BIND=0.0.0.0`. Config checklist, not code.

## Post-review amendments

Applied after code review of the implementation:

- **Item 2.** `UpdateCharacterData` no longer early-returns on an inventory failure. The three
  writes are separate transactions, so bailing left quests committed and silently dropped the stats
  save; it now completes the stats write and reports the inventory failure afterwards.
- **Item 6.** A batch with skipped entries now returns `Success = true` with the skips named in
  `ErrorMessage`. Returning false made a zone server that retries on `!Success` resend the whole map
  every tick without converging, since a malformed entry stays malformed. Exception text is no
  longer echoed to the caller.
- **Item 4.** An empty `X-Samsara-Service-Key` header now counts as absence rather than a wrong key,
  so a proxy that injects it blank does not get 401s under the permissive posture.
- **VendorTrade** (pre-existing work in the same diff): `OperationId` is validated non-empty *before*
  the replay probe — an omitted key meant every later trade replayed the first one's cached success
  while moving nothing — and the configurable gold cap now only blocks trades that increase gold, so
  a character already over the cap can still spend back down.

Known and accepted:

- **MSSQL is not supported at all** — Postgres is the only deployment target (see `AGENTS.md` →
  Data Access). The MSSQL repository implementations are dead code kept only so shared interfaces
  compile. `UpdateCharacterInventory` there hard-fails any call carrying `ExpectedRevision`
  (`revision_unsupported`) because no `EconomyRevision` column exists, and silently ignoring a
  caller's optimistic lock is precisely the failure mode this spec exists to remove. Loud failure is
  intended, and a review finding that treats it as a shipping regression is out of scope. Deleting
  `src/OWSData/Repositories/Implementations/MSSQL/` outright is reasonable future cleanup.
- **`FOR UPDATE` hold time.** The inventory save now holds a per-character row lock across the
  DELETE plus one round-trip per item. Contention is per-character, not global, so this is a
  throughput cost rather than a correctness one — batch the inserts into a single multi-row
  statement if bag saves show up hot.
- **`PostEscrowGold`** is now unread but stays on the wire for client compatibility. Remove it from
  `CreateShopInput` once the client stops sending it.

## Verification

Static review only — no builds run, per project convention. Nothing here is exercised by an
automated test today; the concurrency items (1, 2) need a manual two-client check against the dev
stack before launch.
