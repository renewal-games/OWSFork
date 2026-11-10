-- Launcher liveness for zone spin-up (issue #172).
--
-- Spin-up picks the least-loaded WorldServers row with ServerStatus = 1, but only a launcher's
-- graceful shutdown sets ServerStatus back to 0. A launcher that crashed stayed "active", and once
-- CleanUpInstances reaped its zone rows it had 0 instances and was picked first; its spin-up command
-- went to a queue nobody reads.
--
-- LastHeartbeat is stamped by InstanceManagement when the launcher starts (StartInstanceLauncher) and
-- each time its health monitor fetches its zone instance list (GetZoneInstancesForWorldServer, every
-- RunServerHealthMonitoringFrequencyInSeconds). Spin-up skips hosts whose heartbeat is older than
-- PostgresQueries.LauncherHeartbeatTimeoutSeconds or NULL. Every launcher version already makes that
-- call, so no launcher redeploy is needed.
--
-- Running launchers are stamped now so they stay eligible until their next health cycle.
--
-- Idempotent: safe to run more than once.
ALTER TABLE WorldServers ADD COLUMN IF NOT EXISTS LastHeartbeat TIMESTAMPTZ NULL;

UPDATE WorldServers SET LastHeartbeat = NOW() WHERE ServerStatus = 1 AND LastHeartbeat IS NULL;
