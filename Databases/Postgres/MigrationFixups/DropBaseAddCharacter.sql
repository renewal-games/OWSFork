-- Fresh-install fix-up: the 20230304 base schema (setup.sql) defines AddCharacter with a
-- different return type than SamsaraUpdates/AddCharacter_10-2-2023_2_pg.sql, and Postgres
-- cannot CREATE OR REPLACE across return types. Databases migrated before the runner
-- existed are baselined, so this only ever executes on fresh installs.
DROP FUNCTION IF EXISTS AddCharacter(UUID, UUID, VARCHAR, VARCHAR);
