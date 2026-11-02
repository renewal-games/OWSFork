-- Character names are unique per customer, ignoring case. CreateCharacterUsingDefaultCharacterValues and AddCharacter
-- check the name before inserting, but the check is case-sensitive and races a concurrent create of the same name.
-- The index is the real guard; the Postgres UsersRepository maps its unique violation (23505) to each create path's
-- duplicate-name message.
--
-- The migrator runs without a transaction and every API service waits on it, so a duplicate already in the table must
-- not make CREATE UNIQUE INDEX fail. Within each customer the oldest character (lowest CharacterID) keeps each name;
-- later duplicates (exact or differing only in case) are renamed to <name>_<CharacterID>, trimmed to fit VARCHAR(50).
-- When another character of that customer already has the new name, ignoring case (an existing name, or one given
-- earlier in this run), _1, _2, ... is added until it is free. Characters are keyed by (CustomerGUID, CharacterID), so
-- every row is matched on both.
--
-- Writes to Characters wait until the index exists, so no new duplicate can slip in between the renames and the index
-- build; reads are not blocked. If the index build fails anyway, the renames roll back with it.
--
-- Idempotent: safe to run more than once.

BEGIN;

LOCK TABLE Characters IN SHARE ROW EXCLUSIVE MODE;

DO $$
DECLARE
    _Duplicate RECORD;
    _Attempt   INT;
    _Suffix    TEXT;
    _NewName   TEXT;
BEGIN
    FOR _Duplicate IN
        SELECT Ranked.CustomerGUID, Ranked.CharacterID, Ranked.CharName
        FROM (
            SELECT CustomerGUID, CharacterID, CharName,
                   ROW_NUMBER() OVER (PARTITION BY CustomerGUID, LOWER(CharName) ORDER BY CharacterID) AS NameRank
            FROM Characters
        ) Ranked
        WHERE Ranked.NameRank > 1
        ORDER BY Ranked.CustomerGUID, Ranked.CharacterID
    LOOP
        _Attempt := 0;
        LOOP
            _Suffix := '_' || _Duplicate.CharacterID::TEXT
                || CASE WHEN _Attempt > 0 THEN '_' || _Attempt::TEXT ELSE '' END;
            _NewName := LEFT(_Duplicate.CharName, 50 - LENGTH(_Suffix)) || _Suffix;

            EXIT WHEN NOT EXISTS (
                SELECT 1
                FROM Characters C
                WHERE C.CustomerGUID = _Duplicate.CustomerGUID
                  AND LOWER(C.CharName) = LOWER(_NewName));

            _Attempt := _Attempt + 1;
        END LOOP;

        UPDATE Characters
        SET CharName = _NewName
        WHERE CustomerGUID = _Duplicate.CustomerGUID
          AND CharacterID = _Duplicate.CharacterID;
    END LOOP;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS UX_Characters_CustomerGUID_LowerCharName
    ON Characters (CustomerGUID, LOWER(CharName));

COMMIT;
