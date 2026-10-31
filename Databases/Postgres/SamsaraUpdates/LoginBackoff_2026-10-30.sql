-- Per-account backoff for password logins.
--
-- The Public API rate limit is per client IP, so guesses spread over many addresses were never slowed down.
-- PlayerLoginAndCreateSession now counts failed password logins per customer and normalized email (known or not)
-- in LoginFailures. From the 5th failure within an hour the email is locked for 30 seconds, doubling with each
-- further failure up to 15 minutes. While locked the password is not checked. A successful login clears the count.
-- _DontCheckPassword logins (external providers) are neither counted nor blocked.
--
-- Idempotent: safe to run more than once.

CREATE TABLE IF NOT EXISTS LoginFailures
(
    CustomerGUID UUID         NOT NULL,
    Email        VARCHAR(256) NOT NULL,
    FailedCount  INT          NOT NULL,
    LastFailure  TIMESTAMPTZ  NOT NULL,
    LockedUntil  TIMESTAMPTZ  NULL,
    CONSTRAINT PK_LoginFailures PRIMARY KEY (CustomerGUID, Email)
);

CREATE INDEX IF NOT EXISTS IX_LoginFailures_LastFailure ON LoginFailures (LastFailure);

CREATE OR REPLACE FUNCTION RecordLoginFailure(_CustomerGUID UUID, _EmailKey VARCHAR(256))
    RETURNS VOID
    LANGUAGE PLPGSQL
AS
$$
BEGIN
    INSERT INTO LoginFailures AS f (CustomerGUID, Email, FailedCount, LastFailure, LockedUntil)
    VALUES (_CustomerGUID, _EmailKey, 1, NOW(), NULL)
    ON CONFLICT (CustomerGUID, Email) DO UPDATE
        SET FailedCount = CASE WHEN f.LastFailure < NOW() - INTERVAL '1 hour' THEN 1 ELSE f.FailedCount + 1 END,
            LastFailure = NOW(),
            LockedUntil = CASE
                              WHEN f.LastFailure < NOW() - INTERVAL '1 hour' OR f.FailedCount + 1 < 5 THEN NULL
                              ELSE NOW() + LEAST(INTERVAL '15 minutes',
                                                 INTERVAL '30 seconds' * POWER(2, LEAST(f.FailedCount + 1 - 5, 10)))
                          END;

    -- Rows for emails nobody has tried for a day are only noise; prune them now and then.
    IF (random() < 0.01) THEN
        DELETE FROM LoginFailures WHERE LastFailure < NOW() - INTERVAL '1 day';
    END IF;
END
$$;

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

        DELETE FROM UserSessions WHERE UserGUID = _UserGUID;
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
