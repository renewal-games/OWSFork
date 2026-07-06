-- Adds Steam authentication support: Users.SteamId column + SteamLoginAndCreateSession function.
-- Ticket validation happens in OWSPublicAPI (SteamAuthService); this function only maps a
-- verified SteamId to a User (JIT-creating one on first login) and issues a session.
-- Idempotent: safe to run more than once.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'users'
          AND column_name = 'steamid'
    ) THEN
        ALTER TABLE Users ADD COLUMN SteamId VARCHAR(20) NULL;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS AK_User_SteamId
    ON Users (CustomerGUID, SteamId)
    WHERE SteamId IS NOT NULL;

CREATE OR REPLACE FUNCTION SteamLoginAndCreateSession(_CustomerGUID UUID,
                                                      _SteamId VARCHAR(20),
                                                      _PersonaName VARCHAR(50)
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

    IF (_SteamId IS NULL OR TRIM(_SteamId) = '') THEN
        _ErrorMessage := 'Invalid SteamId';
        INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
        VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
        RETURN QUERY SELECT * FROM temp_table;
        RETURN;
    END IF;

    SELECT COUNT(*)
    INTO _MatchingUsers
    FROM Users
    WHERE CustomerGUID = _CustomerGUID
      AND SteamId = TRIM(_SteamId);

    IF (_MatchingUsers > 1) THEN
        _ErrorMessage := 'Account configuration error';
        INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
        VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
        RETURN QUERY SELECT * FROM temp_table;
        RETURN;
    ELSIF (_MatchingUsers = 0) THEN
        -- First login for this SteamId: JIT-create a player account.
        -- Synthetic email keeps the NOT NULL + unique-email constraints satisfied;
        -- random crypted password means the account can never password-login.
        _UserGUID := gen_random_uuid();
        INSERT INTO Users (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, CreateDate, LastAccess,
                           Role, SteamId)
        VALUES (_UserGUID, _CustomerGUID, COALESCE(NULLIF(TRIM(_PersonaName), ''), 'SteamUser'), '',
                'steam_' || TRIM(_SteamId) || '@steam.samsarasaga.invalid',
                crypt(gen_random_uuid()::text, gen_salt('md5')), NOW(), NOW(),
                'Player', TRIM(_SteamId));
    ELSE
        SELECT UserGUID
        INTO _UserGUID
        FROM Users
        WHERE CustomerGUID = _CustomerGUID
          AND SteamId = TRIM(_SteamId);

        UPDATE Users
        SET LastAccess = NOW()
        WHERE UserGUID = _UserGUID;
    END IF;

    _Authenticated := TRUE;
    DELETE FROM UserSessions WHERE UserGUID = _UserGUID;
    _UserSessionGUID := gen_random_uuid();
    INSERT INTO UserSessions (CustomerGUID, UserSessionGUID, UserGUID, LoginDate)
    VALUES (_CustomerGUID, _UserSessionGUID, _UserGUID, NOW());

    INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
    VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);

    RETURN QUERY SELECT * FROM temp_table;

END
$$;
