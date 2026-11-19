-- Order for bag saves (issue #227).
--
-- UpdateCharacterInventory and the bag half of UpdateCharacterData replace the whole bag, and each request runs on its
-- own connection. The Characters row lock makes two saves take turns, but in the order they reach the lock, not the
-- order the zone server sent them: at a disconnect the final bag save could commit before an older one still on its
-- way, and the older bag then replaced it.
--
-- InventorySaveSeq is the SaveSeq of the last bag save that was written with one, and InventorySaveSeqOwner the
-- CharacterOwners.OwnerToken that sent it (NULL for a save without a token). A SaveSeq orders the saves of one owner
-- only: a save from the same owner whose SaveSeq is not above the stored one is answered "superseded" and writes no
-- bag. Owners are ordered by their claim, not by SaveSeq, so a new owner's SaveSeq may start anywhere: two zone
-- servers' counters and clocks are never compared. Saves without a SaveSeq neither read nor change either column.
-- 0 = no such save yet.
--
-- Idempotent: safe to run more than once.
ALTER TABLE Characters ADD COLUMN IF NOT EXISTS InventorySaveSeq BIGINT NOT NULL DEFAULT 0;
ALTER TABLE Characters ADD COLUMN IF NOT EXISTS InventorySaveSeqOwner UUID NULL;
