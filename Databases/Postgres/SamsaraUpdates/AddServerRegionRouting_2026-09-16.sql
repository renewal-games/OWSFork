-- Region routing. Region strings are free-form but must match EXACTLY across
-- GameServersConfig.Servers[].Region, OWSInstanceLauncherOptions.ServerRegion,
-- WorldServers.ServerRegion and Users.PreferredServerRegion.
--
-- The authoritative default is the C# constant OWSData.Models.ServerRegions.Default.
-- The 'NA-EAST' literal below only backfills pre-existing hosts and covers the deploy
-- window in which an owsinstancemanagement image that predates this change still
-- registers launchers without a region -- keep the two in sync.
--
-- Users.PreferredServerRegion stays NULL for "never chosen", which is resolved to the
-- default in C#. That keeps every existing account landing exactly where it did before.
--
-- Idempotent: safe to run more than once.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'users' AND column_name = 'preferredserverregion') THEN
        ALTER TABLE Users ADD COLUMN PreferredServerRegion VARCHAR(32) NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'worldservers' AND column_name = 'serverregion') THEN
        ALTER TABLE WorldServers ADD COLUMN ServerRegion VARCHAR(32) NOT NULL DEFAULT 'NA-EAST';
    END IF;
END $$;
