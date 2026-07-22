-- Hot-path indexes for high-CCU operation.
--
-- Characters is a wide table with PK (CustomerGUID, CharacterID), but almost every
-- realtime lookup (session validation join, position/stat saves, inventory/quest
-- helpers) filters by (CustomerGUID, CharName). For a single-game deployment
-- CustomerGUID is constant, so without this index those queries degrade to a full
-- sequential scan of the whole Characters table on every authenticated request.
--
-- Users(CustomerGUID, LastAccess) backs the inactive-instance cleanup predicate
-- (RemoveCharactersFromAllInactiveInstances joins Characters to Users on a
-- LastAccess < now() - interval condition).
--
-- These are plain CREATE INDEX statements: on the current table sizes the build is
-- effectively instant. If this is ever applied to a table with millions of rows,
-- build the equivalent indexes with CREATE INDEX CONCURRENTLY out of band first
-- (IF NOT EXISTS makes this script a no-op in that case).

CREATE INDEX IF NOT EXISTS IX_Characters_CustomerGUID_CharName
    ON Characters (CustomerGUID, CharName);

CREATE INDEX IF NOT EXISTS IX_Users_CustomerGUID_LastAccess
    ON Users (CustomerGUID, LastAccess);
