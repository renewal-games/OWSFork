ALTER TABLE charstats
ADD CONSTRAINT charstats_uk
UNIQUE (customerguid, characterid, statidentifier);
