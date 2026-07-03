-- Adds the Gold currency column to Characters (game-side "juri").
-- Idempotent: safe to run more than once.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'characters'
          AND column_name = 'gold'
    ) THEN
        ALTER TABLE Characters ADD COLUMN Gold INT NOT NULL DEFAULT 0;
    END IF;
END $$;
