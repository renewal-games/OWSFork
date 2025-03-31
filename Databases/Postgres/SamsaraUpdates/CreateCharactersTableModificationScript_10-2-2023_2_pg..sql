-- Start transaction
BEGIN;

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
    RAISE NOTICE 'Dropped all existing tables';
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'abilities') THEN
        CREATE TABLE Abilities (
            CustomerGUID UUID NOT NULL,
            AbilityIDTag VARCHAR(50) NOT NULL,
            AbilityName VARCHAR(50) NOT NULL,
            AbilityClassName VARCHAR(50) NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, AbilityIDTag)
        );
        RAISE NOTICE 'Created Abilities table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'class') THEN
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
        RAISE NOTICE 'Created Class table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'characters') THEN
        CREATE TABLE Characters (
            CustomerGUID UUID NOT NULL,
            CharacterID SERIAL NOT NULL,
            UserGUID UUID,
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
        RAISE NOTICE 'Created Characters table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charequipmentitems') THEN
        CREATE TABLE CharEquipmentItems (
            CustomerGUID UUID NOT NULL,
            CharacterID INT NOT NULL,
            ItemIDTag VARCHAR(50) NOT NULL,
            Quantity INT NOT NULL,
            InSlotNumber INT NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, CharacterID, ItemIDTag)
        );
        RAISE NOTICE 'Created CharEquipmentItems table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charstats') THEN
        CREATE TABLE CharStats (
            CustomerGUID UUID NOT NULL,
            CharacterID INT NOT NULL,
            StatIdentifier VARCHAR(50) NOT NULL,
            Value INT NOT NULL,
            PRIMARY KEY (CustomerGUID, CharacterID, StatIdentifier)
        );
        RAISE NOTICE 'Created CharStats table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charabilities') THEN
        CREATE TABLE CharAbilities (
            CustomerGUID UUID NOT NULL,
            CharacterID INT NOT NULL,
            AbilityIDTag VARCHAR(50) NOT NULL,
            CurrentAbilityLevel INT NOT NULL,
            ActualAbilityLevel INT NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, CharacterID, AbilityIDTag)
        );
        RAISE NOTICE 'Created CharAbilities table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charinventory') THEN
        CREATE TABLE CharInventory (
            CustomerGUID UUID NOT NULL,
            CharacterID INT NOT NULL,
            CharInventoryID SERIAL NOT NULL,
            InventoryName VARCHAR(50) NOT NULL,
            InventorySize INT NOT NULL,
            PRIMARY KEY (CustomerGUID, CharacterID, CharInventoryID)
        );
        RAISE NOTICE 'Created CharInventory table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charinventoryitems') THEN
        CREATE TABLE CharInventoryItems (
            CustomerGUID UUID NOT NULL,
            CharInventoryID INT NOT NULL,
            ItemIDTag VARCHAR(50) NOT NULL,
            Quantity INT NOT NULL,
            InSlotNumber INT NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, CharInventoryID, InSlotNumber)
        );
        RAISE NOTICE 'Created CharInventoryItems table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'charquests') THEN
        CREATE TABLE CharQuests (
            CustomerGUID UUID NOT NULL,
            CharacterID INT NOT NULL,
            QuestIDTag VARCHAR(50) NOT NULL,
            QuestJournalTagContainer VARCHAR(150) NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, CharacterID, QuestIDTag)
        );
        RAISE NOTICE 'Created CharQuests table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'quest') THEN
        CREATE TABLE Quest (
            CustomerGUID UUID NOT NULL,
            QuestIDTag VARCHAR(50) NOT NULL,
            QuestOverview TEXT NOT NULL,
            QuestTasks TEXT NOT NULL,
            QuestClassName VARCHAR(50) NOT NULL,
            CustomData TEXT,
            PRIMARY KEY (CustomerGUID, QuestIDTag)
        );
        RAISE NOTICE 'Created Quest table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'party') THEN
        CREATE TABLE Party (
            CustomerGUID UUID NOT NULL,
            PartyID SERIAL NOT NULL,
            PartyGuid UUID NOT NULL,
            RaidingParty BOOLEAN NOT NULL,
            PRIMARY KEY (CustomerGUID, PartyID)
        );
        RAISE NOTICE 'Created Party table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'partymember') THEN
        CREATE TABLE PartyMember (
            CustomerGUID UUID NOT NULL,
            PartyID INT NOT NULL,
            CharacterID INT NOT NULL,
            PartyLeader BOOLEAN NOT NULL,
            PRIMARY KEY (CustomerGUID, CharacterID)
        );
        RAISE NOTICE 'Created PartyMember table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guild') THEN
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
        RAISE NOTICE 'Created Guild table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guildmember') THEN
        CREATE TABLE GuildMember (
            CustomerGUID UUID NOT NULL,
            GuildID INT NOT NULL,
            CharacterID INT NOT NULL,
            GuildRank INT NOT NULL,
            GuildJoinedDate TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (CustomerGUID, CharacterID)
        );
        RAISE NOTICE 'Created GuildMember table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guildcurrentability') THEN
        CREATE TABLE GuildCurrentAbility (
            CustomerGUID UUID NOT NULL,
            GuildID INT NOT NULL,
            GuildAbilityId INT NOT NULL,
            PRIMARY KEY (CustomerGUID, GuildID, GuildAbilityId)
        );
        RAISE NOTICE 'Created GuildCurrentAbility table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guildstorage') THEN
        CREATE TABLE GuildStorage (
            CustomerGUID UUID NOT NULL,
            GuildID INT NOT NULL,
            ItemIDTag VARCHAR(50) NOT NULL,
            Quantity INT NOT NULL,
            CustomData VARCHAR(150),
            PRIMARY KEY (CustomerGUID, GuildID, ItemIDTag)
        );
        RAISE NOTICE 'Created GuildStorage table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'playergrouptypes') THEN
        CREATE TABLE PlayerGroupTypes (
            PlayerGroupTypeID INT NOT NULL,
            PlayerGroupTypeDescription VARCHAR(50),
            PRIMARY KEY (PlayerGroupTypeID)
        );
        RAISE NOTICE 'Created PlayerGroupTypes table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'playergroupmember') THEN
        CREATE TABLE PlayerGroupMember (
            CustomerGUID UUID NOT NULL,
            PlayerGroupID INT NOT NULL,
            CharacterID INT NOT NULL,
            DateAdded TIMESTAMPTZ,
            TeamNumber INT,
            PRIMARY KEY (CustomerGUID, PlayerGroupID, CharacterID)
        );
        RAISE NOTICE 'Created PlayerGroupMember table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'playergroup') THEN
        CREATE TABLE PlayerGroup (
            CustomerGUID UUID NOT NULL,
            PlayerGroupID INT NOT NULL,
            PlayerGroupTypeID INT NOT NULL,
            ReadyState INT,
            DateAdded TIMESTAMPTZ,
            PRIMARY KEY (CustomerGUID, PlayerGroupID)
        );
        RAISE NOTICE 'Created PlayerGroup table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'actionhouseplayeritem') THEN
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
        RAISE NOTICE 'Created ActionHousePlayerItem table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'actionhouseactiontype') THEN
        CREATE TABLE ActionHouseActionType (
            ActionHouseActionID INT NOT NULL,
            ActionHouseActionTypeDescription VARCHAR(50),
            PRIMARY KEY (ActionHouseActionID)
        );
        RAISE NOTICE 'Created ActionHouseActionType table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'actionhousestatetype') THEN
        CREATE TABLE ActionHouseStateType (
            ActionHouseStateID INT NOT NULL,
            ActionHouseStateTypeDescription VARCHAR(50),
            PRIMARY KEY (ActionHouseStateID)
        );
        RAISE NOTICE 'Created ActionHouseStateType table';
    END IF;
END $$;

DO $$
DECLARE
    v_CustomerGuid UUID;
    v_ClassID INT;
BEGIN
    SELECT c.CustomerGUID INTO v_CustomerGuid
    FROM Customers c
    LIMIT 1;

    IF NOT EXISTS (
        SELECT 1
        FROM Class c
        WHERE c.CustomerGUID = v_CustomerGuid
        AND c.ClassName = 'Wanderer'
    ) THEN
        INSERT INTO Class(
            CustomerGUID,
            ClassName,
            Gender,
            Description,
            StartingMapName,
            X, Y, Z, RX, RY, RZ, TeamNumber
        )
        VALUES (
            v_CustomerGuid,
            'Wanderer',
            0,
            '',
            'Spawn.Location.TestLevel.TestLevel',
            0, 0, 0, 0, 0, 0, 0
        ) RETURNING ClassID INTO v_ClassID;
        RAISE NOTICE 'Inserted default Wanderer class';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'playergrouptypes') THEN
        CREATE TABLE PlayerGroupTypes (
            playergrouptype_id INT NOT NULL,  -- lowercase
            playergrouptype_description VARCHAR(50),  -- lowercase
            PRIMARY KEY (playergrouptype_id)
        );
        RAISE NOTICE 'Created PlayerGroupTypes table';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM playergrouptypes WHERE playergrouptypeid = 1) THEN
        INSERT INTO playergrouptypes (playergrouptypeid, playergrouptypedesc) VALUES
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
        RAISE NOTICE 'Inserted default PlayerGroupTypes';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ActionHouseActionType WHERE ActionHouseActionID = 0) THEN
        INSERT INTO ActionHouseActionType (ActionHouseActionID, ActionHouseActionTypeDescription) VALUES
            (0, 'Buy'),
            (1, 'Sell');
        RAISE NOTICE 'Inserted default ActionHouseActionType values';
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM actionhousestatetype WHERE actionhousestateid = 0) THEN
        INSERT INTO actionhousestatetype (actionhousestateid, actionhousestatetypeid, actionhousestatetypeidescription) VALUES
            (0, 0, 'In Progress'),
            (1, 1, 'Completed'),
            (2, 2, 'Cancelled');
        RAISE NOTICE 'Inserted default ActionHouseStateType values';
    END IF;
END $$;

COMMIT;