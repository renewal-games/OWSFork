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
    startingmapname varchar(50) DEFAULT 'L_MVP_2' NOT NULL,
    x float8 DEFAULT -14319.548852 NOT NULL,
    y float8 DEFAULT -3045.964828 NOT NULL,
    z float8 DEFAULT 2151.217753 NOT NULL,
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


-- 2) Insert base (non-derived) stats for Wanderer and Apprentice.
------------------------------------------------------------------------
DO $$
DECLARE
    v_customerGUID UUID;
    v_class record;
BEGIN
    -- Lookup CustomerGUID by unique customer name
    SELECT customerguid INTO v_customerGUID
    FROM customers
    WHERE customername = 'CustomerName';

    IF v_customerGUID IS NULL THEN
        RAISE EXCEPTION 'Customer not found.';
    END IF;

    FOR v_class IN
        SELECT c.classid
        FROM class c
        WHERE c.customerguid = v_customerGUID
          AND c.classname IN ('Wanderer', 'Apprentice')
    LOOP
        INSERT INTO public.defaultsamsaracharactervalues (customerGUID, classID, statIdentifier, statValue)
        SELECT v_customerGUID, v_class.classid, v.statidentifier, v.statvalue
        FROM (
            VALUES
                ('Health', 100),
                ('MaxHealth', 100),
                ('Mana', 50),
                ('MaxMana', 50),
                ('Might', 1),
                ('Dexterity', 1),
                ('Agility', 1),
                ('Endurance', 1),
                ('Intelligence', 1),
                ('Concentration', 1),
                ('AttackSpeed', 1),
                ('BaseLevel', 1),
                ('BaseExperienceRequired', 100)
        ) AS v(statidentifier, statvalue)
        WHERE NOT EXISTS (
            SELECT 1
            FROM public.defaultsamsaracharactervalues d
            WHERE d.customerguid = v_customerGUID
              AND d.classid = v_class.classid
              AND d.statidentifier = v.statidentifier
        );
    END LOOP;
END $$;

ALTER TABLE defaultsamsaracharactervalues
ADD CONSTRAINT uq_defaults_customer_class_stat UNIQUE (customerGUID, classID, statIdentifier);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'characters'
          AND column_name = 'IsInternalNetworkTestUser'
    ) THEN
        ALTER TABLE Characters
            RENAME COLUMN "IsInternalNetworkTestUser" TO isinternalnetworktestuser;
    ELSIF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'characters'
          AND column_name = 'isinternalnetworktestuser'
    ) THEN
        ALTER TABLE Characters
            ADD COLUMN IsInternalNetworkTestUser BOOLEAN DEFAULT FALSE NOT NULL;
    END IF;
END $$;

------------------------------------------------------------------------
-- 3) Update addsamsaracharacter function to use the new defaults table.
------------------------------------------------------------------------
DROP FUNCTION IF EXISTS public.addsamsaracharacter(uuid, uuid, varchar, varchar);

CREATE OR REPLACE FUNCTION public.addsamsaracharacter(_customerguid uuid, _usersessionguid uuid, _charactername character varying, _classname character varying)
 RETURNS TABLE(errormessage character varying, charactername character varying, classname character varying, startingmapname character varying, x double precision, y double precision, z double precision, rx double precision, ry double precision, rz double precision, teamnumber integer, gender integer)
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
    _StartMapName VARCHAR(50);
    _X FLOAT8;
    _Y FLOAT8;
    _Z FLOAT8;
    _RX FLOAT8;
    _RY FLOAT8;
    _RZ FLOAT8;
	_Email VARCHAR(100);
	_IsInternalNetworkTestUser BOOLEAN;
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

    SELECT US.UserGUID, U.Email
      INTO _UserGUID, _Email
      FROM UserSessions US
      JOIN Users U ON US.CustomerGUID = U.CustomerGUID AND US.UserGUID = U.UserGUID
    WHERE US.CustomerGUID = _CustomerGUID
      AND US.UserSessionGUID = _UserSessionGUID;

    SELECT CC.ClassID
      INTO _ClassID
      FROM Class CC
     WHERE CC.CustomerGUID = _CustomerGUID
       AND CC.ClassName = _ClassName;

    _charactername := TRIM(_charactername);
    _charactername := REGEXP_REPLACE(_charactername, '\s+', ' ', 'g');
    _InvalidCharacters := CASE WHEN _charactername ~ '[^a-zA-Z0-9_ ]' THEN 1 ELSE 0 END;

    SELECT COUNT(*)
      INTO _CountOfCharNamesFound
      FROM Characters Ch
     WHERE Ch.CustomerGUID = _CustomerGUID
       AND Ch.CharName = _charactername;

    IF _InvalidCharacters > 0 AND _SupportUnicode = FALSE THEN
        INSERT INTO temp_table (
            ErrorMessage, CharacterName, ClassName, StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber, Gender
        )
        VALUES (
            'Character Name can only contain letters, numbers, spaces, and underscores', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0
        );
        _ErrorRaised := TRUE;
    END IF;

    IF _ErrorRaised = FALSE AND _UserGUID IS NULL THEN
        INSERT INTO temp_table (
            ErrorMessage, CharacterName, ClassName, StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber, Gender
        )
        VALUES (
            'Invalid User Session', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0
        );
        _ErrorRaised := TRUE;
    END IF;

    IF _ErrorRaised = FALSE AND _ClassID IS NULL THEN
        INSERT INTO temp_table (
            ErrorMessage, CharacterName, ClassName, StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber, Gender
        )
        VALUES (
            'Invalid Class Name', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0
        );
        _ErrorRaised := TRUE;
    END IF;

    -- Block duplicate character names
    IF _ErrorRaised = FALSE AND _CountOfCharNamesFound > 0 THEN
        INSERT INTO temp_table (
            ErrorMessage, CharacterName, ClassName, StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber, Gender
        )
        VALUES (
            'Character Name Already Exists', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0
        );
        _ErrorRaised := TRUE;
    END IF;

    -- Fetch default map location values for this class
	SELECT dcv.startingmapname, dcv.x, dcv.y, dcv.z, dcv.rx, dcv.ry, dcv.rz
	  INTO _StartMapName, _X, _Y, _Z, _RX, _RY, _RZ
	  FROM defaultsamsaracharactervalues dcv
	 WHERE dcv.classID = _ClassID
	   AND dcv.customerGUID = _CustomerGUID
	 LIMIT 1;

    -- Fallback to defaults if not found
    IF _StartMapName IS NULL THEN
        _StartMapName := 'L_MVP_2';
        _X := -14319.548852; _Y := -3045.964828; _Z := 2151.217753;
        _RX := 0.0; _RY := 0.0; _RZ := 0.0;
    END IF;

    IF NOT _ErrorRaised THEN
        -- Determine internal network test user flag
        _IsInternalNetworkTestUser := RIGHT(_charactername, 4) = '_NTU';

        -- Insert new character
        INSERT INTO Characters (
            CustomerGUID, ClassID, UserGUID, CharName, MapName,
            X, Y, Z, ServerIP, LastActivity, RX, RY, RZ,
            TeamNumber, Gender, Description, IsAdmin, IsModerator, Email, IsInternalNetworkTestUser
        )
        VALUES (
            _CustomerGUID, _ClassID, _UserGUID, _charactername, _StartMapName,
            _X, _Y, _Z, '', NOW(), _RX, _RY, _RZ,
            1, 1, '', FALSE, FALSE, _Email, _IsInternalNetworkTestUser
        );

        _CharacterID := CURRVAL(PG_GET_SERIAL_SEQUENCE('characters', 'characterid'));

        -- Insert default stats into charstats
		INSERT INTO charstats (customerGUID, characterID, statIdentifier, value)
		SELECT DISTINCT dcv.customerGUID,
		                _CharacterID,
		                dcv.statIdentifier,
		                dcv.statValue
		FROM defaultsamsaracharactervalues dcv
		WHERE dcv.customerGUID = _CustomerGUID
		  AND dcv.classid = _ClassID;

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
$function$

COMMIT;

DO $$ 
BEGIN 
    RAISE NOTICE 'addsamsaracharacter updated with new default map coordinates.'; 
END $$;
