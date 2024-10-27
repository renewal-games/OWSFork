BEGIN;

-- Drop tables if they exist
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

CREATE TABLE Abilities (
    CustomerGUID UUID NOT NULL,
    AbilityIDTag VARCHAR(50) NOT NULL,
    AbilityName VARCHAR(50) NOT NULL,
    AbilityClassName VARCHAR(50) NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, AbilityIDTag)
);

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

CREATE TABLE CharEquipmentItems (
    CustomerGUID UUID NOT NULL,
    CharacterID INT NOT NULL,
    ItemIDTag VARCHAR(50) NOT NULL,
    Quantity INT NOT NULL,
    InSlotNumber INT NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, CharacterID, ItemIDTag)
);

CREATE TABLE CharStats (
    CustomerGUID UUID NOT NULL,
    CharacterID INT NOT NULL,
    StatIdentifier VARCHAR(50) NOT NULL,
    Value INT NOT NULL,
    PRIMARY KEY (CustomerGUID, CharacterID, StatIdentifier)
);

CREATE TABLE CharAbilities (
    CustomerGUID UUID NOT NULL,
    CharacterID INT NOT NULL,
    AbilityIDTag VARCHAR(50) NOT NULL,
    CurrentAbilityLevel INT NOT NULL,
    ActualAbilityLevel INT NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, CharacterID, AbilityIDTag)
);

CREATE TABLE CharInventory (
    CustomerGUID UUID NOT NULL,
    CharacterID INT NOT NULL,
    CharInventoryID SERIAL NOT NULL,
    InventoryName VARCHAR(50) NOT NULL,
    InventorySize INT NOT NULL,
    PRIMARY KEY (CustomerGUID, CharacterID, CharInventoryID)
);

CREATE TABLE CharInventoryItems (
    CustomerGUID UUID NOT NULL,
    CharInventoryID INT NOT NULL,
    ItemIDTag VARCHAR(50) NOT NULL,
    Quantity INT NOT NULL,
    InSlotNumber INT NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, CharInventoryID, InSlotNumber)
);

CREATE TABLE CharQuests (
    CustomerGUID UUID NOT NULL,
    CharacterID INT NOT NULL,
    QuestIDTag VARCHAR(50) NOT NULL,
    QuestJournalTagContainer VARCHAR(150) NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, CharacterID, QuestIDTag)
);

CREATE TABLE Quest (
    CustomerGUID UUID NOT NULL,
    QuestIDTag VARCHAR(50) NOT NULL,
    QuestOverview TEXT NOT NULL,
    QuestTasks TEXT NOT NULL,
    QuestClassName VARCHAR(50) NOT NULL,
    CustomData TEXT,
    PRIMARY KEY (CustomerGUID, QuestIDTag)
);

CREATE TABLE Party (
    CustomerGUID UUID NOT NULL,
    PartyID SERIAL NOT NULL,
    PartyGuid UUID NOT NULL,
    RaidingParty BOOLEAN NOT NULL,
    PRIMARY KEY (CustomerGUID, PartyID)
);

CREATE TABLE PartyMember (
    CustomerGUID UUID NOT NULL,
    PartyID INT NOT NULL,
    CharacterID INT NOT NULL,
    PartyLeader BOOLEAN NOT NULL,
    PRIMARY KEY (CustomerGUID, CharacterID)
);

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

CREATE TABLE GuildMember (
    CustomerGUID UUID NOT NULL,
    GuildID INT NOT NULL,
    CharacterID INT NOT NULL,
    GuildRank INT NOT NULL,
    GuildJoinedDate TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (CustomerGUID, CharacterID)
);

CREATE TABLE GuildCurrentAbility (
    CustomerGUID UUID NOT NULL,
    GuildID INT NOT NULL,
    GuildAbilityId INT NOT NULL,
    PRIMARY KEY (CustomerGUID, GuildID, GuildAbilityId)
);

CREATE TABLE GuildStorage (
    CustomerGUID UUID NOT NULL,
    GuildID INT NOT NULL,
    ItemIDTag VARCHAR(50) NOT NULL,
    Quantity INT NOT NULL,
    CustomData VARCHAR(150),
    PRIMARY KEY (CustomerGUID, GuildID, ItemIDTag)
);

CREATE TABLE PlayerGroupTypes (
    PlayerGroupTypeID INT NOT NULL,
    PlayerGroupTypeDescription VARCHAR(50),
    PRIMARY KEY (PlayerGroupTypeID)
);

CREATE TABLE PlayerGroupMember (
    CustomerGUID UUID NOT NULL,
    PlayerGroupID INT NOT NULL,
    CharacterID INT NOT NULL,
    DateAdded TIMESTAMPTZ,
    TeamNumber INT,
    PRIMARY KEY (CustomerGUID, PlayerGroupID, CharacterID)
);

CREATE TABLE PlayerGroup (
    CustomerGUID UUID NOT NULL,
    PlayerGroupID INT NOT NULL,
    PlayerGroupTypeID INT NOT NULL,
    ReadyState INT,
    DateAdded TIMESTAMPTZ,
    PRIMARY KEY (CustomerGUID, PlayerGroupID)
);

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

CREATE TABLE ActionHouseActionType (
    ActionHouseActionID INT NOT NULL,
    ActionHouseActionTypeDescription VARCHAR(50),
    PRIMARY KEY (ActionHouseActionID)
);

CREATE TABLE ActionHouseStateType (
    ActionHouseStateID INT NOT NULL,
    ActionHouseStateTypeDescription VARCHAR(50),
    PRIMARY KEY (ActionHouseStateID)
);

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

INSERT INTO ActionHouseActionType (ActionHouseActionID, ActionHouseActionTypeDescription) VALUES
    (0, 'Buy'),
    (1, 'Sell');

INSERT INTO ActionHouseStateType (ActionHouseStateID, ActionHouseStateTypeDescription) VALUES
    (0, 'In Progress'),
    (1, 'Completed'),
    (2, 'Cancelled');

COMMIT;