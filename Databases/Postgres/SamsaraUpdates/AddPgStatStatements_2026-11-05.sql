-- pg_stat_statements: per-statement call counts and times, for the zone stress test's database report
-- (ragna-remake Tools/Agent/Plans/2026-11-03-zone-stress-test.md) and for diagnosing slow queries.
-- The library must also be in shared_preload_libraries (docker-compose.hetzner-dev.yml); without it the extension
-- installs, but reading its view raises an error until the server restarts with the setting.
--
-- Idempotent: safe to run more than once.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
