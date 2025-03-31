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
$$ LANGUAGE plpgsql;