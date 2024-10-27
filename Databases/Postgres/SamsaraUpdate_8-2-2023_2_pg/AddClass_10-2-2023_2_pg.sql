CREATE OR REPLACE FUNCTION AddClass(
    CustomerGUID UUID,
    ClassName VARCHAR(50),
    Gender SMALLINT,
    Description TEXT,
    StartingMapName VARCHAR(50),
    X FLOAT,
    Y FLOAT,
    Z FLOAT,
    RX FLOAT,
    RY FLOAT,
    RZ FLOAT,
    TeamNumber INT
)
RETURNS INT AS $$
DECLARE
    ClassID INT;
BEGIN
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

    IF NOT EXISTS (
        SELECT 1
        FROM Class
        WHERE CustomerGUID = AddClass.CustomerGUID
        AND ClassName = AddClass.ClassName
    ) THEN

        INSERT INTO Class (
            CustomerGUID,
            ClassName,
            Gender,
            Description,
            StartingMapName,
            X,
            Y,
            Z,
            RX,
            RY,
            RZ,
            TeamNumber
        )
        VALUES (
            AddClass.CustomerGUID,
            AddClass.ClassName,
            AddClass.Gender,
            AddClass.Description,
            AddClass.StartingMapName,
            AddClass.X,
            AddClass.Y,
            AddClass.Z,
            AddClass.RX,
            AddClass.RY,
            AddClass.RZ,
            AddClass.TeamNumber
        )
        RETURNING ClassID INTO ClassID;

        RETURN ClassID;

    ELSE
        RETURN -1;
    END IF;
END;
$$ LANGUAGE plpgsql;
