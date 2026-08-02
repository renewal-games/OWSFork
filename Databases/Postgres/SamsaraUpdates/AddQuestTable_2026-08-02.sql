-- Creates the Quest table if it is missing.
--
-- Quest is part of the intended schema (see CumulativeUpdateToCurrentVersion_Samsara10-14-2024_pg.sql
-- and CreateCharactersTableModificationScript_10-2-2023_2_pg..sql), but databases that were adopted
-- by the DbUp runner via a one-time baseline had every manifest entry journaled as applied without
-- being executed. Any such database that never actually got the table is missing it, and
-- GetCharQuestsByCharName -- which INNER JOINs CharQuests to Quest for QuestClassName -- fails with
-- "42P01: relation \"quest\" does not exist", surfacing as an HTTP 500 from
-- OWSCharacterPersistence /api/Characters/GetQuestsByName.
--
-- Column definitions are copied verbatim from the original scripts so this is a no-op on any
-- database where those scripts really ran.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'quest') THEN
        CREATE TABLE Quest (
            CustomerGUID UUID NOT NULL,
            QuestIDTag VARCHAR(50) NOT NULL,
            QuestOverview TEXT NOT NULL,
            QuestTasks TEXT NOT NULL,
            QuestClassName VARCHAR(50) NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, QuestIDTag)
        );
        RAISE NOTICE 'Created Quest table';
    END IF;
END $$;
