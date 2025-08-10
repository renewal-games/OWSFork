BEGIN;

-- Drop tables if they exist
DO $$
BEGIN
    DROP TABLE IF EXISTS Abilities CASCADE;
    DROP TABLE IF EXISTS AbilityTypes CASCADE;
    DROP TABLE IF EXISTS ClassInventory CASCADE;
    DROP TABLE IF EXISTS Class CASCADE;
    DROP TABLE IF EXISTS CharAbilities CASCADE;
    DROP TABLE IF EXISTS CharAbilityBarAbilities CASCADE;
    DROP TABLE IF EXISTS CharAbilityBars CASCADE;
    DROP TABLE IF EXISTS CharHasAbilities CASCADE;
    DROP TABLE IF EXISTS CharHasItems CASCADE;
    DROP TABLE IF EXISTS CustomCharacterData CASCADE;
    DROP TABLE IF EXISTS CharEquipmentItems CASCADE;
    DROP TABLE IF EXISTS CharStats CASCADE;
    DROP TABLE IF EXISTS Characters CASCADE;
    DROP TABLE IF EXISTS CharInventoryItems CASCADE;
    DROP TABLE IF EXISTS CharInventory CASCADE;
    DROP TABLE IF EXISTS CharQuests CASCADE;
    DROP TABLE IF EXISTS Quests CASCADE;
    DROP TABLE IF EXISTS PartyMember CASCADE;
    DROP TABLE IF EXISTS Party CASCADE;
    DROP TABLE IF EXISTS GuildMember CASCADE;
    DROP TABLE IF EXISTS GuildCurrentAbility CASCADE;
    DROP TABLE IF EXISTS GuildStorage CASCADE;
    DROP TABLE IF EXISTS Guild CASCADE;
END $$;

-- Create tables
DO $$
BEGIN
    CREATE TABLE Abilities (
        CustomerGUID UUID NOT NULL,
        AbilityIDTag VARCHAR(50) NOT NULL,
        AbilityName VARCHAR(50) NOT NULL,
        AbilityClassName VARCHAR(50) NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, AbilityIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Abilities already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE Class (
        CustomerGUID UUID NOT NULL,
        ClassID SERIAL NOT NULL,
        ClassName VARCHAR(50) NOT NULL DEFAULT '',
        StartingMapName VARCHAR(50) NOT NULL,
        X FLOAT NOT NULL,
        Y FLOAT NOT NULL,
        Z FLOAT NOT NULL,
        RX FLOAT NOT NULL DEFAULT 0,
        RY FLOAT NOT NULL DEFAULT 0,
        RZ FLOAT NOT NULL DEFAULT 0,
        TeamNumber INT NOT NULL DEFAULT 0,
        Gender SMALLINT NOT NULL DEFAULT 0,
        Description TEXT,
        PRIMARY KEY (CustomerGUID, ClassID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Class already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE Characters (
        CustomerGUID UUID NOT NULL,
        CharacterID SERIAL NOT NULL,
        UserGUID UUID,
        Email VARCHAR(50) NOT NULL,
        CharName VARCHAR(50) NOT NULL,
        CharGUID UUID,
        MapName VARCHAR(50),
        X FLOAT NOT NULL,
        Y FLOAT NOT NULL,
        Z FLOAT NOT NULL,
        ServerIP VARCHAR(50),
        LastActivity TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
        RX FLOAT NOT NULL DEFAULT 0,
        RY FLOAT NOT NULL DEFAULT 0,
        RZ FLOAT NOT NULL DEFAULT 0,
        TeamNumber INT NOT NULL DEFAULT 0,
        Gender SMALLINT NOT NULL DEFAULT 0,
        Description TEXT,
        ClassID INT NOT NULL,
        IsAdmin BOOLEAN NOT NULL DEFAULT FALSE,
        IsModerator BOOLEAN NOT NULL DEFAULT FALSE,
        CreateDate TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
        PRIMARY KEY (CustomerGUID, CharacterID),
        FOREIGN KEY (UserGUID) REFERENCES Users (UserGUID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Characters already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharEquipmentItems (
        CustomerGUID UUID NOT NULL,
        CharacterID INT NOT NULL,
        ItemIDTag VARCHAR(50) NOT NULL,
        Quantity INT NOT NULL,
        InSlotNumber INT NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, CharacterID, ItemIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharEquipmentItems already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharStats (
        CustomerGUID UUID NOT NULL,
        CharacterID INT NOT NULL,
        StatIdentifier VARCHAR(50) NOT NULL,
        Value INT NOT NULL,
        PRIMARY KEY (CustomerGUID, CharacterID, StatIdentifier)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharStats already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharAbilities (
        CustomerGUID UUID NOT NULL,
        CharacterID INT NOT NULL,
        AbilityIDTag VARCHAR(50) NOT NULL,
        CurrentAbilityLevel INT NOT NULL,
        ActualAbilityLevel INT NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, CharacterID, AbilityIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharAbilities already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharInventory (
        CustomerGUID UUID NOT NULL,
        CharacterID INT NOT NULL,
        CharInventoryID SERIAL NOT NULL,
        InventoryName VARCHAR(50) NOT NULL,
        InventorySize INT NOT NULL,
        PRIMARY KEY (CustomerGUID, CharacterID, CharInventoryID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharInventory already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharInventoryItems (
        CustomerGUID UUID NOT NULL,
        CharInventoryID INT NOT NULL,
        ItemIDTag VARCHAR(50) NOT NULL,
        Quantity INT NOT NULL,
        InSlotNumber INT NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, CharInventoryID, InSlotNumber)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharInventoryItems already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE CharQuests (
        CustomerGUID UUID NOT NULL,
        CharacterID INT NOT NULL,
        QuestIDTag VARCHAR(50) NOT NULL,
        QuestJournalTagContainer VARCHAR(150) NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, CharacterID, QuestIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table CharQuests already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE Quest (
        CustomerGUID UUID NOT NULL,
        QuestIDTag VARCHAR(50) NOT NULL,
        QuestOverview TEXT NOT NULL,
        QuestTasks TEXT NOT NULL,
        QuestClassName VARCHAR(50) NOT NULL,
        CustomData TEXT,
        PRIMARY KEY (CustomerGUID, QuestIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Quest already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE Party (
        CustomerGUID UUID NOT NULL,
        PartyID SERIAL NOT NULL,
        PartyGuid UUID NOT NULL,
        RaidingParty BOOLEAN NOT NULL,
        PRIMARY KEY (CustomerGUID, PartyID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Party already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE PartyMember (
        CustomerGUID UUID NOT NULL,
        PartyID INT NOT NULL,
        CharacterID INT NOT NULL,
        PartyLeader BOOLEAN NOT NULL,
        PRIMARY KEY (CustomerGUID, CharacterID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table PartyMember already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE Guild (
        CustomerGUID UUID NOT NULL,
        GuildID SERIAL NOT NULL,
        GuildGuid UUID NOT NULL,
        GuildName VARCHAR(50) NOT NULL,
        GuildMessage VARCHAR(150),
        GuildDescription VARCHAR(150),
        GuildImage INT DEFAULT 0,
        GuildDate TIMESTAMPTZ NOT NULL,
        PRIMARY KEY (CustomerGUID, GuildID),
        UNIQUE (GuildName)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table Guild already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE GuildMember (
        CustomerGUID UUID NOT NULL,
        GuildID INT NOT NULL,
        CharacterID INT NOT NULL,
        GuildRank INT NOT NULL,
        GuildJoinedDate TIMESTAMPTZ NOT NULL,
        PRIMARY KEY (CustomerGUID, CharacterID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table GuildMember already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE GuildCurrentAbility (
        CustomerGUID UUID NOT NULL,
        GuildID INT NOT NULL,
        GuildAbilityId INT NOT NULL,
        PRIMARY KEY (CustomerGUID, GuildID, GuildAbilityId)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table GuildCurrentAbility already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE GuildStorage (
        CustomerGUID UUID NOT NULL,
        GuildID INT NOT NULL,
        ItemIDTag VARCHAR(50) NOT NULL,
        Quantity INT NOT NULL,
        CustomData VARCHAR(150),
        PRIMARY KEY (CustomerGUID, GuildID, ItemIDTag)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table GuildStorage already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE PlayerGroupTypes (
        PlayerGroupTypeID INT NOT NULL,
        PlayerGroupTypeDescription VARCHAR(50),
        PRIMARY KEY (PlayerGroupTypeID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table PlayerGroupTypes already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE PlayerGroupMember (
        CustomerGUID UUID NOT NULL,
        PlayerGroupID INT NOT NULL,
        CharacterID INT NOT NULL,
        DateAdded TIMESTAMPTZ,
        TeamNumber INT,
        PRIMARY KEY (CustomerGUID, PlayerGroupID, CharacterID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table PlayerGroupMember already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE PlayerGroup (
        CustomerGUID UUID NOT NULL,
        PlayerGroupID INT NOT NULL,
        PlayerGroupTypeID INT NOT NULL,
        ReadyState INT,
        DateAdded TIMESTAMPTZ,
        PRIMARY KEY (CustomerGUID, PlayerGroupID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table PlayerGroup already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE ActionHousePlayerItem (
        CustomerGUID UUID NOT NULL,
        SlotIndex INT NOT NULL,
        CharacterID INT NOT NULL,
        ItemIDTag VARCHAR(150) NOT NULL,
        InProgressQuantity INT NOT NULL,
        TotalQuantity INT NOT NULL,
        SetPrice INT NOT NULL,
        TotalItemQuantityInStorage INT NOT NULL,
        TotalCurrencyInStorage INT NOT NULL,
        ActionHouseActionID INT NOT NULL,
        PRIMARY KEY (CustomerGUID, SlotIndex, CharacterID, ActionHouseActionID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table ActionHousePlayerItem already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE ActionHouseActionType (
        ActionHouseActionID INT NOT NULL,
        ActionHouseActionTypeDescription VARCHAR(50),
        PRIMARY KEY (ActionHouseActionID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table ActionHouseActionType already exists. Skipping creation.';
END $$;

DO $$
BEGIN
    CREATE TABLE ActionHouseStateType (
        ActionHouseStateID INT NOT NULL,
        ActionHouseStateTypeDescription VARCHAR(50),
        PRIMARY KEY (ActionHouseStateID)
    );
EXCEPTION
    WHEN duplicate_table THEN RAISE NOTICE 'Table ActionHouseStateType already exists. Skipping creation.';
END $$;


DO $$
DECLARE
    CustomerGuid UUID;
    ClassID INT;
BEGIN
    SELECT CustomerGUID INTO CustomerGuid FROM Customers LIMIT 1;

    IF NOT EXISTS (SELECT 1 FROM Class WHERE CustomerGUID = CustomerGuid AND ClassName = 'Wanderer') THEN
        INSERT INTO Class(
            CustomerGUID,
            ClassName,
            Gender,
            Description,
            StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber
        )
        VALUES (
            CustomerGuid,
            'Wanderer',
            0,
            '',
            'Spawn.Location.TestLevel.TestLevel',
            0, 0, 0, 0, 0, 0, 0
        ) RETURNING ClassID INTO ClassID;
    END IF;
END $$;

DO $$
BEGIN
    INSERT INTO PlayerGroupTypes (PlayerGroupTypeID, PlayerGroupTypeDescription) VALUES
        (1, 'Party'),
        (2, 'Raid'),
        (3, 'Guild'),
        (4, 'Team'),
        (5, 'Faction'),
        (6, 'FightGroup'),
        (7, 'Alliance'),
        (8, 'Squad'),
        (9, 'PvPBattleGroup'),
        (99, 'Other');
EXCEPTION
    WHEN unique_violation THEN
        RAISE NOTICE 'PlayerGroupTypes already exist, skipping insertion';
END $$;

DO $$
BEGIN
    INSERT INTO ActionHouseActionType (ActionHouseActionID, ActionHouseActionTypeDescription) VALUES
        (0, 'Buy'),
        (1, 'Sell');
EXCEPTION
    WHEN unique_violation THEN
        RAISE NOTICE 'ActionHouseActionType entries already exist, skipping insertion';
END $$;

DO $$
BEGIN
    INSERT INTO ActionHouseStateType (ActionHouseStateID, ActionHouseStateTypeDescription) VALUES
        (0, 'In Progress'),
        (1, 'Completed'),
        (2, 'Cancelled');
EXCEPTION
    WHEN unique_violation THEN
        RAISE NOTICE 'ActionHouseStateType entries already exist, skipping insertion';
END $$;

-- Create functions
DO $$
BEGIN
    CREATE OR REPLACE FUNCTION AddClass(
        CustomerGUID UUID,
        ClassName VARCHAR(50),
        Gender SMALLINT,
        Description TEXT,
        StartingMapName VARCHAR(50),
        X FLOAT,
        Y FLOAT,
        Z FLOAT,
        RX FLOAT,
        RY FLOAT,
        RZ FLOAT,
        TeamNumber INT
    )
    RETURNS INT AS $$
    DECLARE
        ClassID INT;
    BEGIN
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

        IF NOT EXISTS (
            SELECT 1
            FROM Class
            WHERE CustomerGUID = AddClass.CustomerGUID
            AND ClassName = AddClass.ClassName
        ) THEN

            INSERT INTO Class (
                CustomerGUID,
                ClassName,
                Gender,
                Description,
                StartingMapName,
                X,
                Y,
                Z,
                RX,
                RY,
                RZ,
                TeamNumber
            )
            VALUES (
                AddClass.CustomerGUID,
                AddClass.ClassName,
                AddClass.Gender,
                AddClass.Description,
                AddClass.StartingMapName,
                AddClass.X,
                AddClass.Y,
                AddClass.Z,
                AddClass.RX,
                AddClass.RY,
                AddClass.RZ,
                AddClass.TeamNumber
            )
            RETURNING ClassID INTO ClassID;

            RETURN ClassID;

        ELSE
            RETURN -1;
        END IF;
    END;
    $FUNC$ LANGUAGE plpgsql;
END
$$;

DO $$
BEGIN
    CREATE OR REPLACE FUNCTION AddCharacter(
        p_CustomerGUID UUID,
        p_UserSessionGUID UUID,
        p_CharacterName VARCHAR(50),
        p_ClassName VARCHAR(50)
    )
    RETURNS TABLE (
        ErrorMessage VARCHAR(100),
        CharacterName VARCHAR(50),
        ClassName VARCHAR(50),
        StartingMapName VARCHAR(50),
        X FLOAT,
        Y FLOAT,
        Z FLOAT,
        RX FLOAT,
        RY FLOAT,
        RZ FLOAT,
        TeamNumber INT,
        Gender INT
    ) AS $$
    DECLARE
        v_UserGUID UUID;
        v_ClassID INT;
        v_CharacterID INT;
        v_CharacterGUID UUID;
        v_SupportUnicode BOOLEAN;
        v_InvalidCharacters INT;
        v_CountOfCharNamesFound INT;
    BEGIN
        SELECT SupportUnicode INTO v_SupportUnicode FROM Customers WHERE CustomerGUID = p_CustomerGUID;

        p_CharacterName := TRIM(p_CharacterName);
        p_CharacterName := REGEXP_REPLACE(p_CharacterName, '\s+', ' ', 'g');

        v_InvalidCharacters := CASE WHEN p_CharacterName ~ '[^a-z0-9 ]' THEN 1 ELSE 0 END;

        IF (v_InvalidCharacters > 0 AND NOT v_SupportUnicode) THEN
            RETURN QUERY SELECT 'Character Name can only contain letters, numbers, and spaces'::VARCHAR(100), ''::VARCHAR(50), ''::VARCHAR(50), ''::VARCHAR(50), 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::INT, 0::INT;
            RETURN;
        END IF;

        SELECT UserGUID INTO v_UserGUID FROM UserSessions WHERE CustomerGUID = p_CustomerGUID AND UserSessionGUID = p_UserSessionGUID;

        IF (v_UserGUID IS NOT NULL) THEN
            SELECT ClassID INTO v_ClassID FROM Class WHERE CustomerGUID = p_CustomerGUID AND ClassName = p_ClassName;

            IF (v_ClassID > 0) THEN
                SELECT COUNT(*) INTO v_CountOfCharNamesFound FROM Characters WHERE CustomerGUID = p_CustomerGUID AND CharName = p_CharacterName;

                IF (v_CountOfCharNamesFound < 1) THEN
                    v_CharacterGUID := gen_random_uuid();

                    WITH inserted AS (
                        INSERT INTO Characters (CustomerGUID, ClassID, UserGUID, CharGUID, CharName, MapName, X, Y, Z, ServerIP, LastActivity, RX, RY, RZ, TeamNumber, Gender, Description)
                        SELECT p_CustomerGUID, v_ClassID, v_UserGUID, v_CharacterGUID, p_CharacterName, StartingMapName, X, Y, Z, '', CURRENT_TIMESTAMP, RX, RY, RZ, TeamNumber, Gender, Description
                        FROM Class
                        WHERE ClassID = v_ClassID AND CustomerGUID = p_CustomerGUID
                        RETURNING CharacterID, MapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender
                    )
                    INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
                    SELECT p_CustomerGUID, CharacterID, 'Bag', 16
                    FROM inserted
                    RETURNING (SELECT ''::VARCHAR(100)), p_CharacterName, p_ClassName, MapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender
                    INTO ErrorMessage, CharacterName, ClassName, StartingMapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender;

                    RETURN NEXT;
                ELSE
                    RETURN QUERY SELECT 'Invalid Character Name'::VARCHAR(100), ''::VARCHAR(50), ''::VARCHAR(50), ''::VARCHAR(50), 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::INT, 0::INT;
                END IF;
            ELSE
                RETURN QUERY SELECT 'Invalid Class Name'::VARCHAR(100), ''::VARCHAR(50), ''::VARCHAR(50), ''::VARCHAR(50), 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::INT, 0::INT;
            END IF;
        ELSE
            RETURN QUERY SELECT 'Invalid User Session'::VARCHAR(100), ''::VARCHAR(50), ''::VARCHAR(50), ''::VARCHAR(50), 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::FLOAT, 0::INT, 0::INT;
        END IF;
    END;
    $FUNC$ LANGUAGE plpgsql;
END
$$;

DO $$
BEGIN
    CREATE OR REPLACE PROCEDURE public.addnewsamsaracustomer(
        IN _customername VARCHAR,
        IN _firstname VARCHAR,
        IN _lastname VARCHAR,
        IN _email VARCHAR,
        IN _password VARCHAR,
        IN _customerguid UUID DEFAULT NULL
    )
    LANGUAGE plpgsql
    AS $PROC$
    DECLARE
        _UserGUID UUID;
        _ClassID INT;
        _CharacterName VARCHAR(50) := 'Test';
        _CharacterID INT;
    BEGIN
        IF _CustomerGUID IS NULL THEN
            _CustomerGUID := gen_random_uuid();
        END IF;

        IF NOT EXISTS(SELECT FROM Customers WHERE CustomerGUID = _CustomerGUID) THEN
            INSERT INTO Customers (CustomerGUID, CustomerName, CustomerEmail, CustomerPhone, CustomerNotes, EnableDebugLogging)
            VALUES (_CustomerGUID, _CustomerName, _Email, '', '', TRUE);

            INSERT INTO WorldSettings (CustomerGUID, StartTime)
            SELECT _CustomerGUID, EXTRACT(EPOCH FROM CURRENT_TIMESTAMP)::BIGINT
            FROM Customers C
            WHERE C.CustomerGUID = _CustomerGUID;

            SELECT UserGUID FROM AddUser(_CustomerGUID, _FirstName, _LastName, _Email, _Password, 'Developer') INTO _UserGUID;

            INSERT INTO Maps (CustomerGUID, MapName, ZoneName, MapData, Width, Height)
            VALUES (_CustomerGUID, 'ThirdPersonExampleMap', 'ThirdPersonExampleMap', NULL, 1, 1),
                   (_CustomerGUID, 'Map2', 'Map2', NULL, 1, 1),
                   (_CustomerGUID, 'DungeonMap', 'DungeonMap', NULL, 1, 1),
                   (_CustomerGUID, 'FourZoneMap', 'Zone1', NULL, 1, 1),
                   (_CustomerGUID, 'FourZoneMap', 'Zone2', NULL, 1, 1);

            INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z, RX, RY, RZ, TeamNumber, Gender, Description)
            VALUES (_CustomerGUID, 'Wanderer', 'ThirdPersonExampleMap', 0, 0, 250, 0, 0, 0, 1, 0, '');

            _ClassID := CURRVAL(PG_GET_SERIAL_SEQUENCE('class', 'classid'));

            INSERT INTO Characters (
                CustomerGUID, ClassID, UserGUID, Email, CharName, MapName, X, Y, Z,
                RX, RY, RZ, TeamNumber, Gender, Description
            )
            SELECT
                _CustomerGUID, _ClassID, _UserGUID, _Email, _CharacterName, StartingMapName,
                X, Y, Z, RX, RY, RZ, TeamNumber, Gender, Description
            FROM Class
            WHERE ClassID = _ClassID;

            _CharacterID := CURRVAL(PG_GET_SERIAL_SEQUENCE('characters', 'characterid'));

            INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
            VALUES (_CustomerGUID, _CharacterID, 'Bag', 16);
        ELSE
            RAISE EXCEPTION 'Duplicate Customer GUID: %', _CustomerGUID USING ERRCODE = 'unique_violation';
        END IF;
    END;
    $PROC$;

    GRANT EXECUTE ON PROCEDURE public.addnewsamsaracustomer(VARCHAR, VARCHAR, VARCHAR, VARCHAR, VARCHAR, UUID) TO PUBLIC;

    RAISE NOTICE 'Procedure addnewsamsaracustomer created successfully.';
EXCEPTION
    WHEN others THEN
        RAISE NOTICE 'Error creating procedure addnewsamsaracustomer: %', SQLERRM;
END $$;

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
        _ClassID               INT;
        _CharacterID           INT;
        _CountOfCharNamesFound INT     = 0;
        _InvalidCharacters     INT;
        _CharGUID              UUID;

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

        SELECT US.UserGUID
        FROM UserSessions US
        WHERE US.CustomerGUID = _CustomerGUID
        AND US.UserSessionGUID = _UserSessionGUID
        INTO _UserGUID;

        SELECT C.ClassID INTO _ClassID FROM Class C WHERE C.CustomerGUID = _CustomerGUID AND C.ClassName = _ClassName;

        SELECT COUNT(*)
        FROM Characters C
        WHERE C.CustomerGUID = _CustomerGUID
        AND C.CharName = _CharacterName
        INTO _CountOfCharNamesFound;

        _CharacterName := TRIM(_CharacterName);
        _CharacterName := REGEXP_REPLACE(_CharacterName, '\s+', ' ', 'g');
        _InvalidCharacters := CASE WHEN _CharacterName ~ '[^a-zA-Z0-9 ]' THEN 1 ELSE 0 END;

        IF _InvalidCharacters > 0 AND _SupportUnicode = FALSE THEN
            INSERT INTO temp_table
            VALUES ('Character Name can only contain letters, numbers, and spaces', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0);
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
            _CharGUID := gen_random_uuid();

            INSERT INTO Characters (
                CustomerGUID, ClassID, UserGUID, CharName, CharGUID, MapName,
                X, Y, Z, ServerIP, LastActivity, RX, RY, RZ,
                TeamNumber, Gender, Description, IsAdmin, IsModerator
            )
            SELECT
                _CustomerGUID, _ClassID, _UserGUID, _CharacterName, _CharGUID, CL.StartingMapName,
                CL.X, CL.Y, CL.Z, '', NOW(), CL.RX, CL.RY, CL.RZ,
                CL.TeamNumber, CL.Gender, CL.Description, FALSE, FALSE
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
$$

CREATE OR REPLACE PROCEDURE public.removecharacter(
    IN _CustomerGUID uuid,
    IN _UserSessionGUID uuid,
    IN _CharacterName varchar
)
LANGUAGE plpgsql
AS $procedure$
DECLARE
    _UserGUID UUID;
    _CharacterID INT;
BEGIN

    SELECT US.UserGUID
    INTO _UserGUID
    FROM UserSessions US
    WHERE US.CustomerGUID = _CustomerGUID
      AND US.UserSessionGUID = _UserSessionGUID;

    IF _UserGUID IS NOT NULL THEN
        SELECT C.CharacterID
        INTO _CharacterID
        FROM Characters C
        WHERE C.CustomerGUID = _CustomerGUID
          AND C.UserGUID = _UserGUID
          AND C.CharName = _CharacterName;

        IF _CharacterID IS NOT NULL THEN

            -- CharAbilityBarAbilities
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charabilitybarabilities';
            IF FOUND THEN
                DELETE FROM CharAbilityBarAbilities
                WHERE CustomerGUID = _CustomerGUID
                  AND CharAbilityBarID IN (
                      SELECT CharAbilityBarID
                      FROM CharAbilityBars
                      WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID
                  );
            END IF;

            -- CharAbilityBars
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charabilitybars';
            IF FOUND THEN
                DELETE FROM CharAbilityBars
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- CharInventoryItems (joined through CharInventory)
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charinventoryitems';
            IF FOUND THEN
                DELETE FROM CharInventoryItems
                WHERE CustomerGUID = _CustomerGUID
                  AND CharInventoryID IN (
                      SELECT CharInventoryID
                      FROM CharInventory
                      WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID
                  );
            END IF;

            -- CharInventoryCurrency (joined through CharInventory)
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charinventorycurrency';
            IF FOUND THEN
                DELETE FROM CharInventoryCurrency
                WHERE CharInventoryID IN (
                    SELECT CharInventoryID
                    FROM CharInventory
                    WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID
                );
            END IF;

            -- CharInventory
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charinventory';
            IF FOUND THEN
                DELETE FROM CharInventory
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- CharEquipmentItems
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charequipmentitems';
            IF FOUND THEN
                DELETE FROM CharEquipmentItems WHERE CharacterID = _CharacterID;
            END IF;

            -- CharQuests
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charquests';
            IF FOUND THEN
                DELETE FROM CharQuests WHERE CharacterID = _CharacterID;
            END IF;

            -- CharStats
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charstats';
            IF FOUND THEN
                DELETE FROM CharStats WHERE CharacterID = _CharacterID;
            END IF;

            -- CharOnMapInstance
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'charonmapinstance';
            IF FOUND THEN
                DELETE FROM CharOnMapInstance
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- CustomCharacterData
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'customcharacterdata';
            IF FOUND THEN
                DELETE FROM CustomCharacterData
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- ChatGroupUsers
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'chatgroupusers';
            IF FOUND THEN
                DELETE FROM ChatGroupUsers
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- PlayerGroupCharacters
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'playergroupcharacters';
            IF FOUND THEN
                DELETE FROM PlayerGroupCharacters
                WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;
            END IF;

            -- PlayerGroupMember
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'playergroupmember';
            IF FOUND THEN
                DELETE FROM PlayerGroupMember
                WHERE CharacterID = _CharacterID;
            END IF;

            -- PartyMember
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'partymember';
            IF FOUND THEN
                DELETE FROM PartyMember
                WHERE CharacterID = _CharacterID;
            END IF;

            -- Party
            PERFORM 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'party';
            IF FOUND THEN
                DELETE FROM Party
                WHERE PartyID IN (
                    SELECT PartyID FROM PartyMember WHERE CharacterID = _CharacterID
                );
            END IF;

            -- Final character delete
            DELETE FROM Characters
            WHERE CustomerGUID = _CustomerGUID AND CharacterID = _CharacterID;

        END IF;
    END IF;

END;
$procedure$;



COMMIT;

