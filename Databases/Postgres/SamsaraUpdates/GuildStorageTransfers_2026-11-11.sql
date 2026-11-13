-- Guild storage becomes a persisted, server-owned transfer (code issue #188).
--
-- OWSGuild kept guild storage in a static in-memory dictionary and trusted the caller's guild, items and
-- quantities. Deposits and withdrawals now move items between CharInventoryItems and this table in one
-- transaction (GuildStorageRepository), so the table has to hold what the bag holds:
--   * a stack is keyed by ItemIDTag *and* CustomData (refine/star/cards ride in CustomData), not ItemIDTag alone;
--   * CustomData is TEXT, like CharInventoryItems.CustomData, instead of VARCHAR(150);
--   * '' stands for "no custom data" so the key has no NULLs;
--   * a stored stack always has a positive quantity.
-- The key uses md5(CustomData) so a long CustomData cannot exceed the btree row limit.
--
-- Nothing wrote this table before this change, so there is no data to carry over; the statements below are
-- still written to keep any existing rows.

ALTER TABLE GuildStorage ALTER COLUMN CustomData TYPE TEXT;
UPDATE GuildStorage SET CustomData = '' WHERE CustomData IS NULL;
ALTER TABLE GuildStorage ALTER COLUMN CustomData SET DEFAULT '';
ALTER TABLE GuildStorage ALTER COLUMN CustomData SET NOT NULL;

DELETE FROM GuildStorage WHERE Quantity <= 0;
ALTER TABLE GuildStorage DROP CONSTRAINT IF EXISTS CK_GuildStorage_Quantity;
ALTER TABLE GuildStorage ADD CONSTRAINT CK_GuildStorage_Quantity CHECK (Quantity > 0);

ALTER TABLE GuildStorage DROP CONSTRAINT IF EXISTS guildstorage_pkey;
ALTER TABLE GuildStorage ADD COLUMN IF NOT EXISTS GuildStorageID BIGINT GENERATED ALWAYS AS IDENTITY;
ALTER TABLE GuildStorage ADD CONSTRAINT PK_GuildStorage PRIMARY KEY (GuildStorageID);

CREATE UNIQUE INDEX IF NOT EXISTS UX_GuildStorage_Stack
    ON GuildStorage (CustomerGUID, GuildID, ItemIDTag, md5(CustomData));
