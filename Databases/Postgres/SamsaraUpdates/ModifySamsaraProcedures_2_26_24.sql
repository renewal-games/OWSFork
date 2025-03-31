BEGIN;

------------------------------------------------------------------------
-- 1) Create the updated default values table with new columns.
------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.defaultsamsaracharactervalues
(
    customerGUID uuid NOT NULL,
    classID      int  NOT NULL,
    statIdentifier varchar(50) NOT NULL,
    statValue int NOT NULL,
    startingmapname varchar(50) DEFAULT 'TestLevel' NOT NULL,
    x float8 DEFAULT 0.0 NOT NULL,
    y float8 DEFAULT 0.0 NOT NULL,
    z float8 DEFAULT 0.0 NOT NULL,
    rx float8 DEFAULT 0.0 NOT NULL,
    ry float8 DEFAULT 0.0 NOT NULL,
    rz float8 DEFAULT 0.0 NOT NULL
);

ALTER TABLE Characters
ADD CONSTRAINT UQ_Characters_CustomerGUID_CharacterID 
UNIQUE (CustomerGUID, CharacterID);

CREATE TABLE CustomCharacterData
(
    CustomerGUID          UUID        NOT NULL,
    CustomCharacterDataID SERIAL      NOT NULL,
    CharacterID           INT         NOT NULL,
    CustomFieldName       VARCHAR(50) NOT NULL,
    FieldValue            TEXT        NOT NULL,
    CONSTRAINT PK_CustomCharacterData
        PRIMARY KEY (CustomerGUID, CustomCharacterDataID),
    CONSTRAINT FK_CustomCharacterData_CharID
        FOREIGN KEY (CustomerGUID, CharacterID) REFERENCES Characters (CustomerGUID, CharacterID)
);


------------------------------------------------------------------------
-- 2) Insert base (non-derived) stats for Wanderer (classID=2).
------------------------------------------------------------------------
INSERT INTO public.defaultsamsaracharactervalues (customerGUID, classID, statIdentifier, statValue)
VALUES
    -- Basic HP & MP
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Health',                100),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'MaxHealth',             100),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Mana',                  50),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'MaxMana',               50),

    -- Core Stats
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Might',                 1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Dexterity',             1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Agility',               1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Endurance',             1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Intelligence',          1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'Concentration',         1),

    -- Attack Speed
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'AttackSpeed',           1),

    -- Base Level & XP
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'BaseLevel',             1),
    ('ea7814c7-7863-4181-887d-eca30b0e6be1', 2, 'BaseExperienceRequired', 100);

------------------------------------------------------------------------
-- 3) Update addsamsaracharacter function to use the new defaults table.
------------------------------------------------------------------------
DROP FUNCTION IF EXISTS public.addsamsaracharacter(uuid, uuid, varchar, varchar);

CREATE OR REPLACE FUNCTION public.addsamsaracharacter(
    _customerguid uuid,
    _usersessionguid uuid,
    _charactername character varying,
    _classname character varying
)
RETURNS TABLE(
    errormessage character varying,
    charactername character varying,
    classname character varying,
    startingmapname character varying,
    x double precision,
    y double precision,
    z double precision,
    rx double precision,
    ry double precision,
    rz double precision,
    teamnumber integer,
    gender integer
)
LANGUAGE plpgsql
AS $function$
DECLARE
    _ErrorRaised BOOLEAN = FALSE;
    _SupportUnicode BOOLEAN = FALSE;
    _UserGUID UUID;
    _ClassID INT;
    _CharacterID INT;
    _CountOfCharNamesFound INT = 0;
    _InvalidCharacters INT;
    _CharGUID UUID;
    _StartMapName VARCHAR(50);
    _X FLOAT8;
    _Y FLOAT8;
    _Z FLOAT8;
    _RX FLOAT8;
    _RY FLOAT8;
    _RZ FLOAT8;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS temp_table
    (
        ErrorMessage    VARCHAR(100),
        CharacterName   VARCHAR(50),
        ClassName       VARCHAR(50),
        StartingMapName VARCHAR(50),
        X               FLOAT8,
        Y               FLOAT8,
        Z               FLOAT8,
        RX              FLOAT8,
        RY              FLOAT8,
        RZ              FLOAT8,
        TeamNumber      INT,
        Gender          INT
    ) ON COMMIT DROP;

    -- Validate session & gather base info
    SELECT C.SupportUnicode
      INTO _SupportUnicode
      FROM Customers C
     WHERE C.CustomerGUID = _CustomerGUID;

    SELECT US.UserGUID
      INTO _UserGUID
      FROM UserSessions US
     WHERE US.CustomerGUID = _CustomerGUID
       AND US.UserSessionGUID = _UserSessionGUID;

    SELECT CC.ClassID
      INTO _ClassID
      FROM Class CC
     WHERE CC.CustomerGUID = _CustomerGUID
       AND CC.ClassName = _ClassName;

    SELECT COUNT(*)
      INTO _CountOfCharNamesFound
      FROM Characters Ch
     WHERE Ch.CustomerGUID = _CustomerGUID
       AND Ch.CharName = _charactername;

    -- Fetch default map location values for this class
    SELECT startingmapname, x, y, z, rx, ry, rz
      INTO _StartMapName, _X, _Y, _Z, _RX, _RY, _RZ
      FROM defaultsamsaracharactervalues
     WHERE classID = _ClassID
       AND customerGUID = _CustomerGUID
     LIMIT 1;

    -- Fallback to defaults if not found
    IF _StartMapName IS NULL THEN
        _StartMapName := 'TestLevel';
        _X := 0.0; _Y := 0.0; _Z := 0.0;
        _RX := 0.0; _RY := 0.0; _RZ := 0.0;
    END IF;

    IF NOT _ErrorRaised THEN
        _CharGUID := gen_random_uuid();

        -- Insert new character
        INSERT INTO Characters (
            CustomerGUID, ClassID, UserGUID, CharName, CharGUID, MapName,
            X, Y, Z, ServerIP, LastActivity, RX, RY, RZ,
            TeamNumber, Gender, Description, IsAdmin, IsModerator
        )
        VALUES (
            _CustomerGUID, _ClassID, _UserGUID, _charactername, _CharGUID, _StartMapName,
            _X, _Y, _Z, '', NOW(), _RX, _RY, _RZ,
            1, 1, '', FALSE, FALSE
        );

        _CharacterID := CURRVAL(PG_GET_SERIAL_SEQUENCE('characters', 'characterid'));

        -- Insert default stats into charstats
        INSERT INTO charstats (customerGUID, characterID, statIdentifier, value)
        SELECT dcv.customerGUID,
               _CharacterID,
               dcv.statIdentifier,
               dcv.statValue
        FROM defaultsamsaracharactervalues dcv
        WHERE dcv.customerGUID = _CustomerGUID
          AND dcv.classID = _ClassID;

        -- Insert into temp table to return data
        INSERT INTO temp_table (
            ErrorMessage,
            CharacterName,
            ClassName,
            StartingMapName,
            X, Y, Z,
            RX, RY, RZ,
            TeamNumber, Gender
        )
        VALUES (
            '',
            _charactername,
            _classname,
            _StartMapName,
            _X, _Y, _Z,
            _RX, _RY, _RZ,
            1, 1
        );
    END IF;

    RETURN QUERY SELECT * FROM temp_table;
END
$function$;

COMMIT;

DO $$ 
BEGIN 
    RAISE NOTICE 'addsamsaracharacter updated with new default map coordinates.'; 
END $$;
