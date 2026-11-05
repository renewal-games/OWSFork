-- Map player caps: 80 soft / 100 hard, from the zone stress test (ragna-remake
-- Tools/Agent/Plans/2026-11-03-zone-stress-test.md): a CCX13 zone server holds about 110 real players at 30 Hz, and
-- the game's GameSession MaxPlayers is 100. Only maps still at the old defaults (60 / 80) change.
--
-- Idempotent: safe to run more than once.
UPDATE Maps SET SoftPlayerCap = 80, HardPlayerCap = 100 WHERE SoftPlayerCap = 60 AND HardPlayerCap = 80;
ALTER TABLE Maps ALTER COLUMN SoftPlayerCap SET DEFAULT 80;
ALTER TABLE Maps ALTER COLUMN HardPlayerCap SET DEFAULT 100;
