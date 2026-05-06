DO $$
DECLARE
    v_has_legacy_itemid BOOLEAN;
BEGIN
    SELECT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'charinventoryitems'
          AND column_name = 'itemid'
    ) INTO v_has_legacy_itemid;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name = 'charinventoryitems'
    ) THEN
        CREATE TABLE CharInventoryItems (
            CustomerGUID UUID NOT NULL,
            CharInventoryID INT NOT NULL,
            ItemIDTag VARCHAR(50) NOT NULL,
            Quantity INT NOT NULL,
            InSlotNumber INT NOT NULL,
            CustomData TEXT,
            CONSTRAINT PK_CharInventoryItems
                PRIMARY KEY (CustomerGUID, CharInventoryID, InSlotNumber)
        );

        RAISE NOTICE 'Created CharInventoryItems with ItemIDTag schema.';
        RETURN;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'charinventoryitems'
          AND column_name = 'itemidtag'
    ) THEN
        ALTER TABLE CharInventoryItems
            ADD COLUMN ItemIDTag VARCHAR(50);
    END IF;

    IF v_has_legacy_itemid THEN
        UPDATE CharInventoryItems
        SET ItemIDTag = COALESCE(ItemIDTag, CONCAT('legacy-item-', ItemID::TEXT))
        WHERE ItemIDTag IS NULL;
    END IF;

    ALTER TABLE CharInventoryItems
        ALTER COLUMN ItemIDTag SET NOT NULL;

    ALTER TABLE CharInventoryItems
        DROP CONSTRAINT IF EXISTS "PK_CharInventoryItems";

    ALTER TABLE CharInventoryItems
        DROP CONSTRAINT IF EXISTS pk_charinventoryitems;

    ALTER TABLE CharInventoryItems
        DROP CONSTRAINT IF EXISTS charinventoryitems_pkey;

    ALTER TABLE CharInventoryItems
        DROP COLUMN IF EXISTS CharInventoryItemID,
        DROP COLUMN IF EXISTS ItemID,
        DROP COLUMN IF EXISTS NumberOfUsesLeft,
        DROP COLUMN IF EXISTS Condition,
        DROP COLUMN IF EXISTS CharInventoryItemGUID;

    ALTER TABLE CharInventoryItems
        ADD CONSTRAINT PK_CharInventoryItems
            PRIMARY KEY (CustomerGUID, CharInventoryID, InSlotNumber);

    RAISE NOTICE 'Updated CharInventoryItems to ItemIDTag schema.';
END $$;
