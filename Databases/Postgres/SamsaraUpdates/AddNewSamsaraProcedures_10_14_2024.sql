BEGIN;

SET client_min_messages TO warning;

DROP PROCEDURE IF EXISTS public.addnewsamsaracustomer(VARCHAR, VARCHAR, VARCHAR, VARCHAR, VARCHAR);

CREATE OR REPLACE PROCEDURE public.addnewsamsaracustomer(
    IN _customername VARCHAR,
    IN _firstname VARCHAR,
    IN _lastname VARCHAR,
    IN _email VARCHAR,
    IN _password VARCHAR
)
LANGUAGE plpgsql
AS $$
DECLARE
    _CustomerGUID UUID := gen_random_uuid();
    _UserGUID UUID;
    _ClassID INT;
    _CharacterName VARCHAR(50) := 'Test';
    _CharacterID INT;
BEGIN
    INSERT INTO Customers (CustomerGUID, CustomerName, CustomerEmail, CustomerPhone, CustomerNotes, EnableDebugLogging)
    VALUES (_CustomerGUID, _CustomerName, _Email, '', '', TRUE);

    INSERT INTO WorldSettings (CustomerGUID, StartTime)
    SELECT _CustomerGUID, EXTRACT(EPOCH FROM CURRENT_TIMESTAMP)::BIGINT
    FROM Customers C
    WHERE C.CustomerGUID = _CustomerGUID;

    SELECT UserGUID FROM AddUser(_CustomerGUID, _FirstName, _LastName, _Email, _Password, 'Developer') INTO _UserGUID;

    INSERT INTO Maps (CustomerGUID, MapName, ZoneName, MapData, Width, Height)
    VALUES
        (_CustomerGUID, 'L_MVP_2', 'L_MVP_2', NULL, 1, 1),
        (_CustomerGUID, 'L_Foraas', 'L_Foraas', NULL, 1, 1),
        (_CustomerGUID, 'ThirdPersonExampleMap', 'ThirdPersonExampleMap', NULL, 1, 1),
        (_CustomerGUID, 'Map2', 'Map2', NULL, 1, 1),
        (_CustomerGUID, 'DungeonMap', 'DungeonMap', NULL, 1, 1),
        (_CustomerGUID, 'FourZoneMap', 'Zone1', NULL, 1, 1),
        (_CustomerGUID, 'FourZoneMap', 'Zone2', NULL, 1, 1);

    INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender, Description)
    VALUES (_CustomerGUID, 'Wanderer', 'L_MVP_2', -14319.548852, -3045.964828, 2151.217753, 0, 0, 0, 1, 1, '');

    INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender, Description)
    VALUES (_CustomerGUID, 'Apprentice', 'L_MVP_2', -14319.548852, -3045.964828, 2151.217753, 0, 0, 0, 1, 1, '');

    _ClassID := CURRVAL(PG_GET_SERIAL_SEQUENCE('class', 'classid'));

    INSERT INTO Characters (
        CustomerGUID, ClassID, UserGUID, Email, CharName, MapName,
        X, Y, Z, ServerIP, LastActivity, RX, RY, RZ,
        TeamNumber, Gender, Description, IsAdmin, IsModerator, IsInternalNetworkTestUser
    )
    SELECT
        _CustomerGUID, _ClassID, _UserGUID, _Email, _CharacterName, StartingMapName,
        X, Y, Z, '', NOW(), RX, RY, RZ,
        TeamNumber, Gender, Description, FALSE, FALSE, FALSE
    FROM Class
    WHERE ClassID = _ClassID;

    _CharacterID := CURRVAL(PG_GET_SERIAL_SEQUENCE('characters', 'characterid'));

    INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
    VALUES (_CustomerGUID, _CharacterID, 'Bag', 16);
END
$$;

GRANT EXECUTE ON PROCEDURE public.addnewsamsaracustomer(VARCHAR, VARCHAR, VARCHAR, VARCHAR, VARCHAR) TO PUBLIC;

COMMIT;

DO $$
BEGIN
    RAISE NOTICE 'addnewsamsaracustomer procedure has been successfully updated.';
END $$;

DO $$
BEGIN
    CREATE OR REPLACE FUNCTION public.addsamsaracharacter(_customerguid uuid, _usersessionguid uuid, _charactername character varying, _classname character varying)
    RETURNS TABLE(errormessage character varying, charactername character varying, classname character varying, startingmapname character varying, x double precision, y double precision, z double precision, rx double precision, ry double precision, rz double precision, teamnumber integer, gender integer)
    LANGUAGE plpgsql
    AS $function$
    DECLARE
        _ErrorRaised           BOOLEAN = FALSE;
        _SupportUnicode        BOOLEAN = FALSE;
        _UserGUID              UUID;
        _Email                 VARCHAR(100);
        _ClassID               INT;
        _CharacterID           INT;
        _CountOfCharNamesFound INT     = 0;
        _InvalidCharacters     INT;

    BEGIN
        CREATE TEMP TABLE IF NOT EXISTS temp_table
        (
            ErrorMessage    VARCHAR(100),
            CharacterName   VARCHAR(50),
            ClassName       VARCHAR(50),
            StartingMapName VARCHAR(50),
            X               FLOAT,
            Y               FLOAT,
            Z               FLOAT,
            RX              FLOAT,
            RY              FLOAT,
            RZ              FLOAT,
            TeamNumber      INT,
            Gender          INT
        ) ON COMMIT DROP;

        SELECT C.SupportUnicode INTO _SupportUnicode FROM Customers C WHERE C.CustomerGUID = _CustomerGUID;

        SELECT US.UserGUID, U.Email
        FROM UserSessions US
        INNER JOIN Users U ON U.CustomerGUID = US.CustomerGUID AND U.UserGUID = US.UserGUID
        WHERE US.CustomerGUID = _CustomerGUID
        AND US.UserSessionGUID = _UserSessionGUID
        INTO _UserGUID, _Email;

        SELECT C.ClassID
        INTO _ClassID
        FROM Class C
        WHERE C.CustomerGUID = _CustomerGUID
        AND C.ClassName = _ClassName
        ORDER BY C.ClassID
        LIMIT 1;

        _CharacterName := TRIM(_CharacterName);
        _CharacterName := REGEXP_REPLACE(_CharacterName, '\s+', ' ', 'g');
        _InvalidCharacters := CASE WHEN _CharacterName ~ '[^a-zA-Z0-9_ ]' THEN 1 ELSE 0 END;

        SELECT COUNT(*)
        FROM Characters C
        WHERE C.CustomerGUID = _CustomerGUID
        AND C.CharName = _CharacterName
        INTO _CountOfCharNamesFound;

        IF _InvalidCharacters > 0 AND _SupportUnicode = FALSE THEN
            INSERT INTO temp_table
            VALUES ('Character Name can only contain letters, numbers, spaces, and underscores', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0);
            _ErrorRaised := TRUE;
        END IF;

        IF _ErrorRaised = FALSE AND _UserGUID IS NULL THEN
            INSERT INTO temp_table
            VALUES ('Invalid User Session', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0);
            _ErrorRaised := TRUE;
        END IF;

        IF _ErrorRaised = FALSE AND _ClassID IS NULL THEN
            INSERT INTO temp_table
            VALUES ('Invalid Class Name', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0);
            _ErrorRaised := TRUE;
        END IF;

        IF _ErrorRaised = FALSE AND _CountOfCharNamesFound > 0 THEN
            INSERT INTO temp_table
            VALUES ('Invalid Character Name', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0);
            _ErrorRaised := TRUE;
        END IF;

        IF _ErrorRaised = FALSE THEN
            INSERT INTO Characters (
                CustomerGUID, ClassID, UserGUID, Email, CharName, MapName,
                X, Y, Z, ServerIP, LastActivity, RX, RY, RZ,
                TeamNumber, Gender, Description, IsAdmin, IsModerator, IsInternalNetworkTestUser
            )
            SELECT
                _CustomerGUID, _ClassID, _UserGUID, _Email, _CharacterName, CL.StartingMapName,
                CL.X, CL.Y, CL.Z, '', NOW(), CL.RX, CL.RY, CL.RZ,
                CL.TeamNumber, CL.Gender, CL.Description, FALSE, FALSE, FALSE
            FROM Class CL
            WHERE CL.ClassID = _ClassID AND CL.CustomerGUID = _CustomerGUID;

            _CharacterID := CURRVAL(PG_GET_SERIAL_SEQUENCE('characters', 'characterid'));

            INSERT INTO temp_table (
                CharacterName, ClassName, StartingMapName, X, Y, Z, RX, RY, RZ,
                TeamNumber, Gender
            )
            SELECT
                _CharacterName, _ClassName, C.MapName,
                C.X, C.Y, C.Z, C.RX, C.RY, C.RZ,
                C.TeamNumber, C.Gender
            FROM Characters C
            WHERE C.CharacterID = _CharacterID;

            INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
            VALUES (_CustomerGUID, _CharacterID, 'Bag', 16);
        END IF;

        RETURN QUERY SELECT * FROM temp_table;
    END
    $function$;
END
$$;

DO $$
BEGIN
    RAISE NOTICE 'addsamsaracharacter procedure has been successfully updated.';
END $$;
