-- One CustomCharacterData row per character and field.
--
-- AddOrUpdateCustomCharacterData checked for the field, then ran UPDATE or INSERT as a separate statement, and nothing
-- unique backed it. Two first writes of the same field at once (a logout save racing a periodic save) could both see
-- no row and both insert. From then on reads returned both rows, and every later write of the field failed: the check
-- reads its result with QuerySingleOrDefault, which throws on two rows. The repository now writes the field in one
-- INSERT ... ON CONFLICT (CustomerGUID, CharacterID, CustomFieldName) DO UPDATE, which needs the unique index below.
--
-- Existing duplicates are removed first, keeping the newest row (highest CustomCharacterDataID) of each field, the same
-- row the console queries already pick. Writes to the table wait until the index exists, so none can slip a new
-- duplicate in between the DELETE and the index build; reads are not blocked.
--
-- Idempotent: safe to run more than once.

BEGIN;

LOCK TABLE CustomCharacterData IN SHARE ROW EXCLUSIVE MODE;

DELETE FROM CustomCharacterData Older
    USING CustomCharacterData Newer
WHERE Newer.CustomerGUID = Older.CustomerGUID
  AND Newer.CharacterID = Older.CharacterID
  AND Newer.CustomFieldName = Older.CustomFieldName
  AND Newer.CustomCharacterDataID > Older.CustomCharacterDataID;

CREATE UNIQUE INDEX IF NOT EXISTS AK_CustomCharacterData_CharacterField
    ON CustomCharacterData (CustomerGUID, CharacterID, CustomFieldName);

COMMIT;
