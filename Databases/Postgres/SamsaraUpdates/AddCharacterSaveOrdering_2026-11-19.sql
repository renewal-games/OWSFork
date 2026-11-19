-- Save ordering for stats, quests and abilities (issue #247).
--
-- These saves upsert only the keys they carry, and each one locks the character row, checks the owner and zone, then
-- writes. The lock orders the writes but not the snapshots: a save that was built first but reached the lock last
-- replaced the newer values of every key it carried.
--
-- Each row now records the save that last wrote it: the caller's CharacterOwners token, the SaveSequence the caller
-- gave its snapshot (if it sends one), and when CharacterPersistence received the save. A later save from the same
-- token writes a key only if it is newer: by SaveSequence when both saves carry one, else by receipt time. A save from
-- a different token writes it unless the stored token is the current owner's (a replaced owner's late save during the
-- handover window cannot undo the new owner's write). Rows written before this script have no version, so the next
-- save writes them. See PostgresQueries.SaveOrderSet.
--
-- Idempotent: safe to run more than once.
ALTER TABLE CharStats ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharStats ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;
ALTER TABLE CharStats ADD COLUMN IF NOT EXISTS SaveReceivedAt TIMESTAMPTZ NULL;

ALTER TABLE CharQuests ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharQuests ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;
ALTER TABLE CharQuests ADD COLUMN IF NOT EXISTS SaveReceivedAt TIMESTAMPTZ NULL;

ALTER TABLE CharAbilities ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharAbilities ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;
ALTER TABLE CharAbilities ADD COLUMN IF NOT EXISTS SaveReceivedAt TIMESTAMPTZ NULL;
