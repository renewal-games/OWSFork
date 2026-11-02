-- Two changes to the login functions.
--
-- 1. SteamLoginAndCreateSession takes a per-SteamId advisory lock before it looks the SteamId up. Two concurrent
--    first logins for one SteamId now create one Users row and both authenticate. Before, the second failed with
--    23505 on a unique index.
--
-- 2. PlayerLoginAndCreateSession and SteamLoginAndCreateSession no longer delete the account's earlier sessions.
--    The Public API runs LoginGate (ban, maintenance) after the function returns. The function used to delete the
--    earlier sessions first, so a refused login still kicked the account's live session. The API now ends earlier
--    sessions itself (IUsersRepository.EndEarlierSessions) once the gate admits the new one, and logs out a refused one.
--
-- Bodies otherwise as in LoginBackoff_2026-10-30.sql (password) and BcryptPasswordHashes_2026-10-30.sql (Steam).
-- OWSDatabaseMigrations --baseline leaves this script pending on a database whose functions predate it.
--
-- Idempotent: safe to run more than once.

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
    _PasswordHash    VARCHAR(128);
    _UserGUID        UUID;
    _UserSessionGUID UUID;
    _ErrorMessage    VARCHAR(100) = '';
    _MatchingUsers   INT = 0;
    _EmailKey        VARCHAR(256) = LOWER(TRIM(_Email));
    _LockedUntil     TIMESTAMPTZ;
BEGIN

    CREATE TEMP TABLE IF NOT EXISTS temp_table
    (
        Authenticated   BOOLEAN,
        UserSessionGUID UUID,
        ErrorMessage    VARCHAR(100)
    ) ON COMMIT DROP;

    -- Per-account backoff for password logins. Logins for one email run one at a time, so parallel guesses cannot
    -- all slip past the check below before the first failure is counted. Unknown emails are counted the same way.
    IF (_DontCheckPassword = FALSE) THEN
        PERFORM pg_advisory_xact_lock(hashtextextended(_CustomerGUID::TEXT || ':' || _EmailKey, 0));

        SELECT LockedUntil
        INTO _LockedUntil
        FROM LoginFailures
        WHERE CustomerGUID = _CustomerGUID
          AND Email = _EmailKey;

        IF (_LockedUntil > NOW()) THEN
            -- The password is not checked while locked, so a guess made now tells the caller nothing.
            _ErrorMessage := 'Too many failed logins. Try again later';
            INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
            VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
            RETURN QUERY SELECT * FROM temp_table;
            RETURN;
        END IF;
    END IF;

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
        IF (_DontCheckPassword = FALSE) THEN
            PERFORM RecordLoginFailure(_CustomerGUID, _EmailKey);
        END IF;
        INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
        VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);
        RETURN QUERY SELECT * FROM temp_table;
        RETURN;
    END IF;

    SELECT (PasswordHash = crypt(_Password, PasswordHash)), PasswordHash, UserGUID
    INTO _PasswordCheck, _PasswordHash, _UserGUID
    FROM Users
    WHERE CustomerGUID = _CustomerGUID
      AND LOWER(TRIM(Email)) = LOWER(TRIM(_Email));

    IF (_PasswordCheck = TRUE OR _DontCheckPassword = TRUE) THEN
        _Authenticated := TRUE;

        -- Upgrade a legacy md5-crypt hash now that the plaintext is known to be right. Only a verified password
        -- is rehashed, never a _DontCheckPassword login.
        IF (_PasswordCheck = TRUE AND _PasswordHash LIKE '$1$%') THEN
            UPDATE Users
            SET PasswordHash = crypt(_Password, gen_salt('bf', 12))
            WHERE UserGUID = _UserGUID
              AND PasswordHash = _PasswordHash;
        END IF;

        IF (_DontCheckPassword = FALSE) THEN
            DELETE FROM LoginFailures WHERE CustomerGUID = _CustomerGUID AND Email = _EmailKey;
        END IF;

        -- The account's earlier sessions stay until the API's LoginGate admits this one (EndEarlierSessions).
        _UserSessionGUID := gen_random_uuid();
        INSERT INTO UserSessions (CustomerGUID, UserSessionGUID, UserGUID, LoginDate)
        VALUES (_CustomerGUID, _UserSessionGUID, _UserGUID, NOW());
    ELSE
        _ErrorMessage := 'Invalid email or password';
        PERFORM RecordLoginFailure(_CustomerGUID, _EmailKey);
    END IF;
    INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
    VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);

    RETURN QUERY SELECT * FROM temp_table;

END
$$;

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
    -- The account's earlier sessions stay until the API's LoginGate admits this one (EndEarlierSessions).
    _UserSessionGUID := gen_random_uuid();
    INSERT INTO UserSessions (CustomerGUID, UserSessionGUID, UserGUID, LoginDate)
    VALUES (_CustomerGUID, _UserSessionGUID, _UserGUID, NOW());

    INSERT INTO temp_table (Authenticated, UserSessionGUID, ErrorMessage)
    VALUES (_Authenticated, _UserSessionGUID, _ErrorMessage);

    RETURN QUERY SELECT * FROM temp_table;

END
$$;
