-- Adds the Gold currency column to Characters (game-side "juri").
-- Idempotent: safe to run more than once.
IF COL_LENGTH('dbo.Characters', 'Gold') IS NULL
BEGIN
    ALTER TABLE [dbo].[Characters] ADD [Gold] INT NOT NULL CONSTRAINT [DF_Characters_Gold] DEFAULT (0);
END
GO
