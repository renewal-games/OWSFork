-- Registers the player classes the game can assign but OWS has no Class row for.
--
-- OWS only ever seeds Wanderer and Apprentice (AddNewSamsaraProcedures_10_14_2024.sql). Any other
-- class name misses the lookup in AddSamsaraCharacterInitialPersistentData, which returns
-- 'Invalid Class Name' -- so PIE provisioning and the class-change persist path both reject the
-- character even though the class exists in game.
--
-- Each row is cloned from that customer's Wanderer row, so starting map, spawn point, team and
-- every stat column match a class the game already ships. Real per-class stats live in
-- DT_CharacterData and GAS; the OWS Class row is spawn data plus bookkeeping.
--
-- Idempotent and per-customer: safe to run more than once, and it skips any class already present.
DO $$
DECLARE
    _classes TEXT[] := ARRAY['Warrior', 'Mercenary', 'Ranger', 'Thief', 'Mender', 'Tinkerer'];
    _class   TEXT;
    _cols    TEXT;
    _vals    TEXT;
BEGIN
    -- Every column except the serial id, so this keeps working as the Class table grows.
    SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position)
    INTO _cols
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'class'
      AND column_name <> 'classid';

    IF _cols IS NULL THEN
        RAISE EXCEPTION 'Class table not found; cannot seed player class rows.';
    END IF;

    FOREACH _class IN ARRAY _classes LOOP
        SELECT string_agg(
                   CASE WHEN column_name = 'classname'
                        THEN quote_literal(_class)
                        ELSE 'w.' || quote_ident(column_name)
                   END,
                   ', ' ORDER BY ordinal_position)
        INTO _vals
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'class'
          AND column_name <> 'classid';

        EXECUTE format(
            'INSERT INTO class (%s)
             SELECT %s
             FROM (
                 SELECT DISTINCT ON (customerguid) *
                 FROM class
                 WHERE lower(classname) = ''wanderer''
                 ORDER BY customerguid, classid
             ) w
             WHERE NOT EXISTS (
                 SELECT 1 FROM class x
                 WHERE x.customerguid = w.customerguid
                   AND lower(x.classname) = lower(%L)
             )',
            _cols, _vals, _class);
    END LOOP;
END $$;
