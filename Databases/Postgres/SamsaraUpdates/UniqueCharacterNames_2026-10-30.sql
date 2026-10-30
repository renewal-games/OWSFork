-- Character names are unique per customer, ignoring case. CreateCharacterUsingDefaultCharacterValues and AddCharacter
-- check the name before inserting, but the check is case-sensitive and races a concurrent create of the same name.
-- The index is the real guard; the repositories map its unique violation (23505) to "Character name already exists!".
--
-- The migrator runs without a transaction and every API service waits on it, so a duplicate already in the table must
-- not make CREATE UNIQUE INDEX fail. The oldest character keeps each name; later duplicates (exact or differing only in
-- case) are renamed to <name>_<CharacterID>, trimmed to fit VARCHAR(50). CharacterID is unique, so the new names are too.
--
-- Idempotent: safe to run more than once.
UPDATE Characters C
SET CharName = LEFT(C.CharName, 50 - LENGTH('_' || C.CharacterID::TEXT)) || '_' || C.CharacterID::TEXT
FROM (
    SELECT CharacterID,
           ROW_NUMBER() OVER (PARTITION BY CustomerGUID, LOWER(CharName) ORDER BY CharacterID) AS NameRank
    FROM Characters
) D
WHERE D.CharacterID = C.CharacterID
  AND D.NameRank > 1;

CREATE UNIQUE INDEX IF NOT EXISTS UX_Characters_CustomerGUID_LowerCharName
    ON Characters (CustomerGUID, LOWER(CharName));
