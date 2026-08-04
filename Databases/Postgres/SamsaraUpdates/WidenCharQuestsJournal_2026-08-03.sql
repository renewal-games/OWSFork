-- Widens CharQuests.QuestJournalTagContainer from VARCHAR(150) to TEXT.
--
-- The column stores FGameplayTagContainer::ToString() export text, which is verbose
-- (~50 characters for a single tag, ~30 more per additional tag) and grows without bound during play:
-- UQuestComponent::UpdateLocationQuestTasks appends every matched location tag to the journal
-- container. Postgres raises "22001: value too long for type character varying(150)" rather than
-- truncating, so a long-running character's quest save would fail outright.
--
-- This never mattered before because nothing wrote the column: the UE save path
-- (USSPlayerCharacterPersistenceComponent::SaveCharacterQuestsToDatabase) discarded its payload and
-- api/Characters/UpdateCharacterQuests had no caller. It does now.
--
-- CustomData is already TEXT.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
        AND table_name = 'charquests'
        AND column_name = 'questjournaltagcontainer'
        AND data_type <> 'text'
    ) THEN
        ALTER TABLE CharQuests ALTER COLUMN QuestJournalTagContainer TYPE TEXT;
        RAISE NOTICE 'Widened CharQuests.QuestJournalTagContainer to TEXT';
    END IF;
END $$;
