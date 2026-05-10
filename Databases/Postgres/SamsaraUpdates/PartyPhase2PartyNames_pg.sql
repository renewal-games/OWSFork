UPDATE Party
SET PartyName = 'Party' || SUBSTRING(REPLACE(PartyGuid::TEXT, '-', '') FROM 1 FOR 8)
WHERE PartyName IS NULL OR LENGTH(BTRIM(PartyName)) = 0;

ALTER TABLE Party
    ALTER COLUMN PartyName SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_party_partyname_not_blank'
    ) THEN
        ALTER TABLE Party
            ADD CONSTRAINT ck_party_partyname_not_blank CHECK (LENGTH(BTRIM(PartyName)) > 0);
    END IF;
END $$;
