-- Normalize OWS email identity for login/register lookups.
-- Run the duplicate report first. If it returns rows, resolve those accounts before relying on the unique index.

SELECT CustomerGUID,
       LOWER(TRIM(Email)) AS NormalizedEmail,
       COUNT(*) AS DuplicateCount
FROM Users
GROUP BY CustomerGUID, LOWER(TRIM(Email))
HAVING COUNT(*) > 1;

UPDATE Users
SET Role = CASE LOWER(TRIM(Role))
    WHEN 'player' THEN 'Player'
    WHEN 'moderator' THEN 'Moderator'
    WHEN 'mod' THEN 'Moderator'
    WHEN 'gamemaster' THEN 'GameMaster'
    WHEN 'gm' THEN 'GameMaster'
    WHEN 'admin' THEN 'Admin'
    ELSE TRIM(Role)
END
WHERE Role <> CASE LOWER(TRIM(Role))
    WHEN 'player' THEN 'Player'
    WHEN 'moderator' THEN 'Moderator'
    WHEN 'mod' THEN 'Moderator'
    WHEN 'gamemaster' THEN 'GameMaster'
    WHEN 'gm' THEN 'GameMaster'
    WHEN 'admin' THEN 'Admin'
    ELSE TRIM(Role)
END;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM Users
        GROUP BY CustomerGUID, LOWER(TRIM(Email))
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate normalized emails exist. Resolve duplicate report rows before creating AK_User_NormalizedEmail.';
    END IF;

    ALTER TABLE Users DROP CONSTRAINT IF EXISTS AK_User;

    CREATE UNIQUE INDEX IF NOT EXISTS AK_User_NormalizedEmail
        ON Users (CustomerGUID, LOWER(TRIM(Email)));
END $$;

CREATE OR REPLACE FUNCTION AddUser(_CustomerGUID UUID,
                                   _FirstName VARCHAR(50),
                                   _LastName VARCHAR(50),
                                   _Email VARCHAR(256),
                                   _Password VARCHAR(256),
                                   _Role VARCHAR(10))
    RETURNS TABLE
            (
                UserGUID UUID
            )
    LANGUAGE PLPGSQL
AS
$$
DECLARE
    _PasswordHash VARCHAR(128);
    _UserGUID     UUID;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS temp_table
    (
        UserGUID UUID
    ) ON COMMIT DROP;

    _PasswordHash = crypt(_Password, gen_salt('md5'));
    _UserGUID := gen_random_uuid();
    INSERT INTO temp_table (UserGUID) VALUES (_UserGUID);

    INSERT INTO Users (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, CreateDate, LastAccess,
                       ROLE)
    VALUES (_UserGUID, _CustomerGUID, _FirstName, _LastName, TRIM(_Email), _PasswordHash, NOW(), NOW(),
            CASE LOWER(TRIM(_Role))
                WHEN 'player' THEN 'Player'
                WHEN 'moderator' THEN 'Moderator'
                WHEN 'mod' THEN 'Moderator'
                WHEN 'gamemaster' THEN 'GameMaster'
                WHEN 'gm' THEN 'GameMaster'
                WHEN 'admin' THEN 'Admin'
                ELSE TRIM(_Role)
            END);
    RETURN QUERY SELECT *
                 FROM temp_table;
END
$$;

DROP FUNCTION IF EXISTS PlayerLoginAndCreateSession(UUID, VARCHAR, VARCHAR, BOOLEAN);

CREATE OR REPLACE FUNCTION PlayerLoginAndCreateSession(_CustomerGUID UUID,
                                                       _Email VARCHAR(256),
                                                       _Password VARCHAR(256),
                                                       _DontCheckPassword BOOLEAN = FALSE
)
    RETURNS TABLE
            (
                Authenticated   BOOLEAN,
                UserSessionGUID UUID,
                ErrorMessage    VARCHAR(100)
            )
    LANGUAGE PLPGSQL
AS
$$
DECLARE
    _Authenticated   BOOLEAN = FALSE;
    _PasswordCheck   BOOLEAN = FALSE;
    _UserGUID        UUID;
    _UserSessionGUID UUID;
    _ErrorMessage    VARCHAR(100) = '';
    _MatchingUsers   INT = 0;
BEGIN

    CREATE TEMP TABLE IF NOT EXISTS temp_table
    (
        Authenticated   BOOLEAN,
        UserSessionGUID UUID,
        ErrorMessage    VARCHAR(100)
    ) ON COMMIT DROP;

    SELECT COUNT(*)
    INTO _MatchingUsers
    FROM Users
    WHERE CustomerGUID = _CustomerGUID
      AND LOWER(TRIM(Email)) = LOWER(TRIM(_Email));

    IF (_MatchingUsers > 1) THEN
        _ErrorMessage := 'Account configuration error';
        INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
        VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
        RETURN QUERY SELECT * FROM temp_table;
        RETURN;
    ELSIF (_MatchingUsers = 0) THEN
        _ErrorMessage := 'Invalid email or password';
        INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
        VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
        RETURN QUERY SELECT * FROM temp_table;
        RETURN;
    END IF;

    SELECT (PasswordHash = crypt(_Password, PasswordHash)), UserGUID
    INTO _PasswordCheck, _UserGUID
    FROM Users
    WHERE CustomerGUID = _CustomerGUID
      AND LOWER(TRIM(Email)) = LOWER(TRIM(_Email));

    IF (_PasswordCheck = TRUE OR _DontCheckPassword = TRUE) THEN
        _Authenticated := TRUE;
        DELETE FROM UserSessions WHERE UserGUID = _UserGUID;
        _UserSessionGUID := gen_random_uuid();
        INSERT INTO UserSessions (CustomerGUID, UserSessionGUID, UserGUID, LoginDate)
        VALUES (_CustomerGUID, _UserSessionGUID, _UserGUID, NOW());
    ELSE
        _ErrorMessage := 'Invalid email or password';
    END IF;
    INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
    VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);

    RETURN QUERY SELECT * FROM temp_table;

END
$$;
