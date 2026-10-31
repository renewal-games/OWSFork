-- SteamLoginAndCreateSession takes a per-SteamId advisory lock before it looks the SteamId up, so two concurrent
-- first logins for one SteamId create one Users row and both authenticate, instead of the second failing with
-- 23505 on a unique index. Same body as BcryptPasswordHashes_2026-10-30.sql otherwise.
--
-- Idempotent: safe to run more than once.

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

    -- Two first logins for the same SteamId (a double submit or a retry) both counted 0 and both inserted; the
    -- second failed on a unique index (the synthetic email's AK_User_NormalizedEmail, or AK_User_SteamId) and the
    -- client got a 500. The lock makes the second wait for the first to commit, and its count then sees the new row.
    PERFORM pg_advisory_xact_lock(hashtext('steam_login:' || _CustomerGUID::TEXT || ':' || TRIM(_SteamId)));

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
                crypt(gen_random_uuid()::text, gen_salt('bf', 12)), NOW(), NOW(),
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
