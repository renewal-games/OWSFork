-- Character names are unique per customer, ignoring case and surrounding spaces.
--
-- AddSamsaraCharacter and the default-values create path check the name with a SELECT and then INSERT, with no
-- lock, so two creates with the same new name could both commit. The check was also case-sensitive, so Bobb and
-- bobb could coexist. Lookups by name (AddAbilityToCharacter, RemoveCharacter, login) then pick a row at random or
-- throw. The unique index below makes the second insert fail with 23505; the repository reports that as the
-- usual "name taken" error. The existing SELECT stays as a friendly pre-check.
--
-- The index cannot be built while duplicates exist. Names are referenced by text in other tables (PlayerShops,
-- JoinTickets, management data), so this script does not rename anything: it lists every duplicate group and
-- stops. Rename the extra characters by hand (keep the one the player actually uses) and rerun.
--
-- Idempotent: safe to run more than once.

DO $$
DECLARE
    _duplicates TEXT;
BEGIN
    SELECT string_agg(format('%s / %s: %s', g.customerguid, g.normalizedname, g.characters), E'\n')
    INTO _duplicates
    FROM (
        SELECT C.CustomerGUID AS customerguid,
               LOWER(TRIM(C.CharName)) AS normalizedname,
               string_agg(format('%s (id %s)', C.CharName, C.CharacterID), ', ' ORDER BY C.CharacterID) AS characters
        FROM Characters C
        GROUP BY C.CustomerGUID, LOWER(TRIM(C.CharName))
        HAVING COUNT(*) > 1
    ) g;

    IF _duplicates IS NOT NULL THEN
        RAISE EXCEPTION E'Duplicate character names (customer / name: characters). Rename the extras, then rerun:\n%', _duplicates;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS UX_Characters_CustomerGUID_CharName
    ON Characters (CustomerGUID, LOWER(TRIM(CharName)));
