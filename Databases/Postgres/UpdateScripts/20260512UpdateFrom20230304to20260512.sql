UPDATE OWSVersion
SET OWSDBVersion='20260512'
WHERE OWSDBVersion IS NOT NULL;

ALTER TABLE Maps
ADD COLUMN IF NOT EXISTS MinutesToShutdownAfterEmpty INT NOT NULL DEFAULT 5;

ALTER TABLE Maps
ALTER COLUMN MinutesToShutdownAfterEmpty SET DEFAULT 5;

UPDATE Maps
SET MinutesToShutdownAfterEmpty = 5
WHERE MinutesToShutdownAfterEmpty = 0;
