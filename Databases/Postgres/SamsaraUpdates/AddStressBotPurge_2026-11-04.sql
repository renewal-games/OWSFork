-- PurgeStressBots removes the zone stress test's bot accounts and every row they own or wrote, in one transaction.
-- The stress test seeds them before a run and purges them after it (ragna-remake
-- Tools/Agent/Plans/2026-11-03-zone-stress-test.md).
--
-- Bot accounts: email @stressbots.invalid with first name Stress, and the legacy headless bots
-- (ss_bot<N>@samsara.local, first name Headless, last name Bot<N>). Characters are selected by owner account only.
-- Character names are unique per customer ignoring case, so name-keyed rows (audit, events, positions) match by name.
-- Rows a real player wrote stay: a whisper to a bot, a report about a bot. There is no orphan or empty-party sweep;
-- those touch other owners' rows. A real buyer's purchase in a bot's shop goes with the shop, as in PurgePIEAccounts.
--
-- Raises, and removes nothing, when a selected account's role is not Player or a Stress account owns a character
-- whose name is not SSBot<digits>. Tables an older schema lacks are skipped. Returns a row count per table.
--
-- Idempotent: safe to run more than once.
CREATE OR REPLACE FUNCTION PurgeStressBots()
    RETURNS TABLE (What TEXT, Removed BIGINT)
    LANGUAGE plpgsql
AS
$$
DECLARE
    _Step  TEXT[];
    _Count BIGINT;
    _Bad   TEXT;
BEGIN
    PERFORM set_config('client_min_messages', 'warning', true);
    DROP TABLE IF EXISTS StressUsers, StressChars, StressShops, StressParties;

    CREATE TEMP TABLE StressUsers ON COMMIT DROP AS
    SELECT U.CustomerGUID, U.UserGUID, LOWER(TRIM(U.Email)) AS Email, U.FirstName, U.Role
    FROM Users U
    WHERE (LOWER(TRIM(U.Email)) LIKE '%@stressbots.invalid' AND U.FirstName = 'Stress')
       OR (LOWER(TRIM(U.Email)) ~ '^ss_bot[0-9]+@samsara\.local$' AND U.FirstName = 'Headless'
           AND U.LastName ~ '^Bot[0-9]+$');

    SELECT string_agg(Email, ', ') INTO _Bad FROM StressUsers WHERE LOWER(TRIM(Role)) <> 'player';
    IF _Bad IS NOT NULL THEN
        RAISE EXCEPTION 'PurgeStressBots: refusing; these accounts are not Players: %', _Bad;
    END IF;

    CREATE TEMP TABLE StressChars ON COMMIT DROP AS
    SELECT C.CustomerGUID, C.CharacterID, LOWER(C.CharName) AS CharName, S.FirstName
    FROM Characters C
             JOIN StressUsers S ON S.CustomerGUID = C.CustomerGUID AND S.UserGUID = C.UserGUID;

    SELECT string_agg(CharName, ', ') INTO _Bad
    FROM StressChars
    WHERE FirstName = 'Stress' AND CharName !~ '^ssbot[0-9]+$';
    IF _Bad IS NOT NULL THEN
        RAISE EXCEPTION 'PurgeStressBots: refusing; stress accounts own characters not named SSBot<N>: %', _Bad;
    END IF;

    CREATE TEMP TABLE StressShops ON COMMIT DROP AS
    SELECT S.PlayerShopID
    FROM PlayerShops S
             JOIN StressUsers U ON U.CustomerGUID = S.CustomerGUID AND U.UserGUID = S.OwnerUserGUID;

    -- Parties whose members are all bots; a party with a real member keeps its row.
    CREATE TEMP TABLE StressParties ON COMMIT DROP AS
    SELECT DISTINCT M.CustomerGUID, M.PartyID
    FROM PartyMember M
             JOIN StressChars C ON C.CustomerGUID = M.CustomerGUID AND C.CharacterID = M.CharacterID
    WHERE NOT EXISTS (SELECT 1
                      FROM PartyMember O
                      WHERE O.CustomerGUID = M.CustomerGUID
                        AND O.PartyID = M.PartyID
                        AND NOT EXISTS (SELECT 1 FROM StressChars B WHERE B.CharacterID = O.CharacterID));

    -- {table, delete}, in foreign-key order. A table missing from this schema is skipped.
    FOREACH _Step SLICE 1 IN ARRAY ARRAY [
        ['PlayerShopPurchases', $q$DELETE FROM PlayerShopPurchases
            WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)
               OR BuyerCharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerShopItems', $q$DELETE FROM PlayerShopItems WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)$q$],
        ['PlayerShops', $q$DELETE FROM PlayerShops WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)$q$],
        ['CharAbilityBarAbilities', $q$DELETE FROM CharAbilityBarAbilities
            WHERE CharAbilityBarID IN (SELECT B.CharAbilityBarID FROM CharAbilityBars B
                                       WHERE B.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharAbilityBars', $q$DELETE FROM CharAbilityBars WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharHasAbilities', $q$DELETE FROM CharHasAbilities WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharHasItems', $q$DELETE FROM CharHasItems WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharEquipmentItems', $q$DELETE FROM CharEquipmentItems WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharacterPrimaryProfession', $q$DELETE FROM CharacterPrimaryProfession WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharacterSecondaryProfession', $q$DELETE FROM CharacterSecondaryProfession WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharInventoryCurrency', $q$DELETE FROM CharInventoryCurrency
            WHERE CharInventoryID IN (SELECT I.CharInventoryID FROM CharInventory I
                                      WHERE I.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharInventoryItems', $q$DELETE FROM CharInventoryItems
            WHERE CharInventoryID IN (SELECT I.CharInventoryID FROM CharInventory I
                                      WHERE I.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharInventory', $q$DELETE FROM CharInventory WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharOnMapInstance', $q$DELETE FROM CharOnMapInstance WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['ChatGroupUsers', $q$DELETE FROM ChatGroupUsers WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CustomCharacterData', $q$DELETE FROM CustomCharacterData WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerGroupCharacters', $q$DELETE FROM PlayerGroupCharacters WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerGroupMember', $q$DELETE FROM PlayerGroupMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['GuildMember', $q$DELETE FROM GuildMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PartyMember', $q$DELETE FROM PartyMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['Party', $q$DELETE FROM Party P
            WHERE EXISTS (SELECT 1 FROM StressParties S WHERE S.CustomerGUID = P.CustomerGUID AND S.PartyID = P.PartyID)$q$],
        ['CharStats', $q$DELETE FROM CharStats WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharAbilities', $q$DELETE FROM CharAbilities WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharQuests', $q$DELETE FROM CharQuests WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['JoinTickets', $q$DELETE FROM JoinTickets WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['CharacterOwners', $q$DELETE FROM CharacterOwners WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['LivePositions', $q$DELETE FROM LivePositions WHERE LOWER(CharacterName) IN (SELECT CharName FROM StressChars)$q$],
        ['GameEvents', $q$DELETE FROM GameEvents WHERE LOWER(CharacterName) IN (SELECT CharName FROM StressChars)$q$],
        ['ZoneCommands', $q$DELETE FROM ZoneCommands WHERE LOWER(CharacterName) IN (SELECT CharName FROM StressChars)$q$],
        ['ChatLog', $q$DELETE FROM ChatLog
            WHERE SenderUserGUID IN (SELECT UserGUID FROM StressUsers)
               OR (SenderUserGUID IS NULL AND LOWER(SenderName) IN (SELECT CharName FROM StressChars))$q$],
        ['PlayerReports', $q$DELETE FROM PlayerReports
            WHERE ReporterUserGUID IN (SELECT UserGUID FROM StressUsers)
               OR (ReporterUserGUID IS NULL AND LOWER(ReporterName) IN (SELECT CharName FROM StressChars))$q$],
        ['AccountSanctions', $q$DELETE FROM AccountSanctions WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['AuditLog', $q$DELETE FROM AuditLog
            WHERE (Action = 'user_login' AND LOWER(TRIM(Actor)) IN (SELECT Email FROM StressUsers))
               OR LOWER(Actor) IN (SELECT CharName FROM StressChars)
               OR (Action = 'user_logout' AND Actor IN ('Stress', 'Headless')
                   AND LOWER(Target) IN (SELECT CharName FROM StressChars))
               OR (Action = 'trade_completed' AND LOWER(Target) IN (SELECT CharName FROM StressChars))$q$],
        ['Characters', $q$DELETE FROM Characters WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['LoginQueue', $q$DELETE FROM LoginQueue WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['LoginFailures', $q$DELETE FROM LoginFailures WHERE LOWER(TRIM(Email)) IN (SELECT Email FROM StressUsers)$q$],
        ['UserSessions', $q$DELETE FROM UserSessions WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['UsersInQueue', $q$DELETE FROM UsersInQueue WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['Users', $q$DELETE FROM Users WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$]
        ]
        LOOP
            IF to_regclass(_Step[1]) IS NOT NULL THEN
                EXECUTE _Step[2];
                GET DIAGNOSTICS _Count = ROW_COUNT;
                What := _Step[1];
                Removed := _Count;
                RETURN NEXT;
            END IF;
        END LOOP;
END
$$;
