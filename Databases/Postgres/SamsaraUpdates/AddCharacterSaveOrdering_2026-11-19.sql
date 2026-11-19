-- Save ordering for stats, quests and abilities (issue #247).
--
-- These saves upsert only the keys they carry, and each one locks the character row, checks the owner and zone, then
-- writes. The lock orders the writes but not the snapshots: a save that was built first but reached the lock last
-- replaced the newer values of every key it carried.
--
-- Each row now records the save that last wrote it: the caller's CharacterOwners token and the SaveSequence the
-- caller gave its snapshot. A later save from the same token writes a key only with a higher sequence. A save from a
-- different token writes it unless the stored token is the current owner's (a replaced owner's late save during the
-- handover window cannot undo the new owner's write). A save without a sequence works as before and leaves the stored
-- version as it is. Rows written before this script have no version, so the next save writes them.
--
-- Idempotent: safe to run more than once.
ALTER TABLE CharStats ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharStats ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;

ALTER TABLE CharQuests ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharQuests ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;

ALTER TABLE CharAbilities ADD COLUMN IF NOT EXISTS SaveOwnerToken UUID NULL;
ALTER TABLE CharAbilities ADD COLUMN IF NOT EXISTS SaveSequence BIGINT NULL;
