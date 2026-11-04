-- PurgeStressBots removes the zone stress test's bot accounts and every row they own or wrote, in one transaction.
-- The stress test seeds them before a run and purges them after it (ragna-remake
-- Tools/Agent/Plans/2026-11-03-zone-stress-test.md).
--
-- Bot accounts: email @stressbots.invalid with first name Stress, and the legacy headless bots
-- (ss_bot<N>@samsara.local, first name Headless, last name Bot<N>). Characters are selected by owner account only.
-- Name-keyed rows match (CustomerGUID, lower name), as character names are unique per customer ignoring case.
-- Audit rows match a bot only where the actor or target is a character name, or by the bot's session GUID.
-- Rows real players wrote stay: a whisper to a bot, a report about a bot. A party that keeps a real member keeps its
-- row, and leadership passes to its lowest remaining CharacterID as RemoveCharacter does. There is no orphan or
-- empty-party sweep; those touch other owners' rows. A real buyer's purchase in a bot's shop goes with the shop,
-- as in PurgePIEAccounts.
--
-- Raises, and removes nothing, when a selected account's role is not Player or a Stress account owns a character
-- whose name is not SSBot<digits>. Tables an older schema lacks are skipped. Returns a row count per step.
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
    DROP TABLE IF EXISTS StressUsers, StressChars, StressSessions, StressShops, StressParties;

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

    -- The bots' sessions: the live ones, plus every one their password logins recorded in the audit log.
    CREATE TEMP TABLE StressSessions ON COMMIT DROP AS
    SELECT S.CustomerGUID, S.UserSessionGUID::TEXT AS SessionGUID
    FROM UserSessions S
    WHERE S.UserGUID IN (SELECT UserGUID FROM StressUsers)
    UNION
    SELECT A.CustomerGUID, LOWER(SamsaraTryJsonb(A.Detail) ->> 'userSessionGuid')
    FROM AuditLog A
    WHERE A.Action = 'user_login'
      AND (A.CustomerGUID, LOWER(TRIM(A.Actor))) IN (SELECT CustomerGUID, Email FROM StressUsers)
      AND SamsaraTryJsonb(A.Detail) ->> 'userSessionGuid' IS NOT NULL;

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
                        AND NOT EXISTS (SELECT 1 FROM StressChars B
                                        WHERE B.CustomerGUID = O.CustomerGUID AND B.CharacterID = O.CharacterID));

    -- {table that must exist, label, statement}, in foreign-key order. A step whose table is missing is skipped.
    FOREACH _Step SLICE 1 IN ARRAY ARRAY [
        ['PlayerShopPurchases', 'PlayerShopPurchases', $q$DELETE FROM PlayerShopPurchases
            WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)
               OR BuyerCharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerShopItems', 'PlayerShopItems', $q$DELETE FROM PlayerShopItems WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)$q$],
        ['PlayerShops', 'PlayerShops', $q$DELETE FROM PlayerShops WHERE PlayerShopID IN (SELECT PlayerShopID FROM StressShops)$q$],
        ['CharAbilityBarAbilities', 'CharAbilityBarAbilities', $q$DELETE FROM CharAbilityBarAbilities
            WHERE CharAbilityBarID IN (SELECT B.CharAbilityBarID FROM CharAbilityBars B
                                       WHERE B.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharAbilityBars', 'CharAbilityBars', $q$DELETE FROM CharAbilityBars WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharHasAbilities', 'CharHasAbilities', $q$DELETE FROM CharHasAbilities WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharHasItems', 'CharHasItems', $q$DELETE FROM CharHasItems WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharEquipmentItems', 'CharEquipmentItems', $q$DELETE FROM CharEquipmentItems WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharacterPrimaryProfession', 'CharacterPrimaryProfession', $q$DELETE FROM CharacterPrimaryProfession WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharacterSecondaryProfession', 'CharacterSecondaryProfession', $q$DELETE FROM CharacterSecondaryProfession WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharInventoryCurrency', 'CharInventoryCurrency', $q$DELETE FROM CharInventoryCurrency
            WHERE CharInventoryID IN (SELECT I.CharInventoryID FROM CharInventory I
                                      WHERE I.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharInventoryItems', 'CharInventoryItems', $q$DELETE FROM CharInventoryItems
            WHERE CharInventoryID IN (SELECT I.CharInventoryID FROM CharInventory I
                                      WHERE I.CharacterID IN (SELECT CharacterID FROM StressChars))$q$],
        ['CharInventory', 'CharInventory', $q$DELETE FROM CharInventory WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharOnMapInstance', 'CharOnMapInstance', $q$DELETE FROM CharOnMapInstance WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['ChatGroupUsers', 'ChatGroupUsers', $q$DELETE FROM ChatGroupUsers WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CustomCharacterData', 'CustomCharacterData', $q$DELETE FROM CustomCharacterData WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerGroupCharacters', 'PlayerGroupCharacters', $q$DELETE FROM PlayerGroupCharacters WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PlayerGroupMember', 'PlayerGroupMember', $q$DELETE FROM PlayerGroupMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['GuildMember', 'GuildMember', $q$DELETE FROM GuildMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['PartyMember', 'PartyMember leaders handed over', $q$UPDATE PartyMember M
            SET PartyLeader = (M.CharacterID = H.NextLeader),
                PartyRole   = CASE WHEN M.CharacterID = H.NextLeader THEN 2 ELSE 0 END
            FROM (SELECT L.CustomerGUID, L.PartyID, MIN(O.CharacterID) AS NextLeader
                  FROM PartyMember L
                           JOIN StressChars B ON B.CustomerGUID = L.CustomerGUID AND B.CharacterID = L.CharacterID
                           JOIN PartyMember O ON O.CustomerGUID = L.CustomerGUID AND O.PartyID = L.PartyID
                  WHERE L.PartyLeader
                    AND NOT EXISTS (SELECT 1 FROM StressChars X
                                    WHERE X.CustomerGUID = O.CustomerGUID AND X.CharacterID = O.CharacterID)
                  GROUP BY L.CustomerGUID, L.PartyID) H
            WHERE M.CustomerGUID = H.CustomerGUID AND M.PartyID = H.PartyID$q$],
        ['PartyMember', 'PartyMember', $q$DELETE FROM PartyMember WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['Party', 'Party', $q$DELETE FROM Party P
            WHERE EXISTS (SELECT 1 FROM StressParties S WHERE S.CustomerGUID = P.CustomerGUID AND S.PartyID = P.PartyID)$q$],
        ['CharStats', 'CharStats', $q$DELETE FROM CharStats WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharAbilities', 'CharAbilities', $q$DELETE FROM CharAbilities WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['CharQuests', 'CharQuests', $q$DELETE FROM CharQuests WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['JoinTickets', 'JoinTickets', $q$DELETE FROM JoinTickets WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['CharacterOwners', 'CharacterOwners', $q$DELETE FROM CharacterOwners WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['LivePositions', 'LivePositions', $q$DELETE FROM LivePositions
            WHERE (CustomerGUID, LOWER(CharacterName)) IN (SELECT CustomerGUID, CharName FROM StressChars)$q$],
        ['GameEvents', 'GameEvents', $q$DELETE FROM GameEvents
            WHERE (CustomerGUID, LOWER(CharacterName)) IN (SELECT CustomerGUID, CharName FROM StressChars)$q$],
        ['ZoneCommands', 'ZoneCommands', $q$DELETE FROM ZoneCommands
            WHERE (CustomerGUID, LOWER(CharacterName)) IN (SELECT CustomerGUID, CharName FROM StressChars)$q$],
        ['ChatLog', 'ChatLog', $q$DELETE FROM ChatLog
            WHERE SenderUserGUID IN (SELECT UserGUID FROM StressUsers)
               OR (SenderUserGUID IS NULL
                   AND (CustomerGUID, LOWER(SenderName)) IN (SELECT CustomerGUID, CharName FROM StressChars))$q$],
        ['PlayerReports', 'PlayerReports', $q$DELETE FROM PlayerReports
            WHERE ReporterUserGUID IN (SELECT UserGUID FROM StressUsers)
               OR (ReporterUserGUID IS NULL
                   AND (CustomerGUID, LOWER(ReporterName)) IN (SELECT CustomerGUID, CharName FROM StressChars))$q$],
        ['AccountSanctions', 'AccountSanctions', $q$DELETE FROM AccountSanctions WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        -- user_login: Actor is the typed email. zone_join, trade_completed: Actor (and trade Target) are characters.
        -- user_logout: Actor is the first name; the session GUID in Detail ties it to the bot, Target may be NULL.
        ['AuditLog', 'AuditLog', $q$DELETE FROM AuditLog A
            WHERE (A.Action = 'user_login'
                   AND (A.CustomerGUID, LOWER(TRIM(A.Actor))) IN (SELECT CustomerGUID, Email FROM StressUsers))
               OR (A.Action IN ('zone_join', 'trade_completed')
                   AND (A.CustomerGUID, LOWER(A.Actor)) IN (SELECT CustomerGUID, CharName FROM StressChars))
               OR (A.Action = 'trade_completed'
                   AND (A.CustomerGUID, LOWER(A.Target)) IN (SELECT CustomerGUID, CharName FROM StressChars))
               OR (A.Action = 'user_logout' AND A.Actor IN ('Stress', 'Headless')
                   AND (A.CustomerGUID, LOWER(SamsaraTryJsonb(A.Detail) ->> 'userSessionGuid'))
                       IN (SELECT CustomerGUID, SessionGUID FROM StressSessions))$q$],
        ['Characters', 'Characters', $q$DELETE FROM Characters WHERE CharacterID IN (SELECT CharacterID FROM StressChars)$q$],
        ['LoginQueue', 'LoginQueue', $q$DELETE FROM LoginQueue WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['LoginFailures', 'LoginFailures', $q$DELETE FROM LoginFailures
            WHERE (CustomerGUID, LOWER(TRIM(Email))) IN (SELECT CustomerGUID, Email FROM StressUsers)$q$],
        ['UserSessions', 'UserSessions', $q$DELETE FROM UserSessions WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['UsersInQueue', 'UsersInQueue', $q$DELETE FROM UsersInQueue WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$],
        ['Users', 'Users', $q$DELETE FROM Users WHERE UserGUID IN (SELECT UserGUID FROM StressUsers)$q$]
        ]
        LOOP
            IF to_regclass(_Step[1]) IS NOT NULL THEN
                EXECUTE _Step[3];
                GET DIAGNOSTICS _Count = ROW_COUNT;
                What := _Step[2];
                Removed := _Count;
                RETURN NEXT;
            END IF;
        END LOOP;
END
$$;
