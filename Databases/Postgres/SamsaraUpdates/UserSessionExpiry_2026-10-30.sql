-- User sessions expire UserSessionMaxAge() after LoginDate.
--
-- Until now a session lived until the user logged in again or logged out, so a leaked UserSessionGUID (logs, a
-- crash dump, a shared PC) stayed valid indefinitely. GetUserSession now ignores a session older than
-- UserSessionMaxAge() and returns ExpiresAt, so the API's session cache cannot serve it past that point either.
-- The API's inline session lookups (roster, character removal, selected character) use the same rule.
-- PurgeExpiredUserSessions deletes expired rows; ows-pie-purge.timer runs it nightly.
--
-- The age is absolute, not idle: a client that stays logged in longer than UserSessionMaxAge() has to log in again.
-- LoginDate is a TIMESTAMP written from NOW(), so it is compared with LOCALTIMESTAMP.
--
-- Idempotent: safe to run more than once.

CREATE OR REPLACE FUNCTION UserSessionMaxAge()
    RETURNS INTERVAL
    LANGUAGE SQL
    IMMUTABLE
AS
$$
SELECT INTERVAL '72 hours';
$$;

-- The returned columns change (ExpiresAt), which CREATE OR REPLACE cannot do.
DROP FUNCTION IF EXISTS GetUserSession(UUID, UUID);

CREATE FUNCTION GetUserSession(_CustomerGUID UUID,
                               _UserSessionGUID UUID)
    RETURNS TABLE
            (
                CustomerGUID          UUID,
                UserGUID              UUID,
                UserSessionGUID       UUID,
                LoginDate             TIMESTAMP,
                ExpiresAt             TIMESTAMPTZ,
                SelectedCharacterName VARCHAR(50),
                Email                 VARCHAR(255),
                FirstName             VARCHAR(50),
                LastName              VARCHAR(50),
                CreateDate            TIMESTAMP,
                LastAccess            TIMESTAMP,
                Role                  VARCHAR(10),
                CharacterID           INT,
                CharName              VARCHAR(50),
                X                     FLOAT,
                Y                     FLOAT,
                Z                     FLOAT,
                RX                    FLOAT,
                RY                    FLOAT,
                RZ                    FLOAT,
                ZoneName              VARCHAR(50)
            )
    LANGUAGE SQL
AS
$$
SELECT US.CustomerGUID,
       US.UserGUID,
       US.UserSessionGUID,
       US.LoginDate,
       (US.LoginDate + UserSessionMaxAge())::TIMESTAMPTZ AS ExpiresAt,
       US.SelectedCharacterName,
       U.Email,
       U.FirstName,
       U.LastName,
       U.CreateDate,
       U.LastAccess,
       U.Role,
       C.CharacterID,
       C.CharName,
       C.X,
       C.Y,
       C.Z,
       C.RX,
       C.RY,
       C.RZ,
       C.MapName AS ZoneName
FROM UserSessions US
         INNER JOIN Users U
                    ON U.UserGUID = US.UserGUID
         LEFT JOIN Characters C
                   ON C.CustomerGUID = US.CustomerGUID
                       AND C.CharName = US.SelectedCharacterName
                       AND C.UserGUID = US.UserGUID
WHERE US.CustomerGUID = _CustomerGUID
  AND US.UserSessionGUID = _UserSessionGUID
  AND US.LoginDate > LOCALTIMESTAMP - UserSessionMaxAge();
$$;

CREATE OR REPLACE FUNCTION PurgeExpiredUserSessions()
    RETURNS BIGINT
    LANGUAGE plpgsql
AS
$$
DECLARE
    _Count BIGINT;
BEGIN
    DELETE FROM UserSessions WHERE LoginDate <= LOCALTIMESTAMP - UserSessionMaxAge();
    GET DIAGNOSTICS _Count = ROW_COUNT;
    RETURN _Count;
END
$$;
