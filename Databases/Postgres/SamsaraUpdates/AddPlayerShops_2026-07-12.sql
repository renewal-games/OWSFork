-- Player Shops (offline vending) + economy-revision concurrency guard.
-- Idempotent: safe to run more than once.

-- 1. Economy revision on Characters (lost-update guard for gold/inventory writers).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'characters' AND column_name = 'economyrevision'
    ) THEN
        ALTER TABLE Characters ADD COLUMN EconomyRevision BIGINT NOT NULL DEFAULT 0;
    END IF;
END $$;

-- 2. PlayerShops
CREATE TABLE IF NOT EXISTS PlayerShops (
    PlayerShopID        BIGSERIAL       PRIMARY KEY,
    CustomerGUID        UUID            NOT NULL,
    OwnerCharacterID    INT             NOT NULL,
    OwnerUserGUID       UUID            NOT NULL,
    OwnerCharName       VARCHAR(50)     NOT NULL,
    ShopName            VARCHAR(24)     NOT NULL,
    ZoneName            VARCHAR(50)     NOT NULL,
    X                   DOUBLE PRECISION NOT NULL DEFAULT 0,
    Y                   DOUBLE PRECISION NOT NULL DEFAULT 0,
    Z                   DOUBLE PRECISION NOT NULL DEFAULT 0,
    RX                  DOUBLE PRECISION NOT NULL DEFAULT 0,
    RY                  DOUBLE PRECISION NOT NULL DEFAULT 0,
    RZ                  DOUBLE PRECISION NOT NULL DEFAULT 0,
    SaleStatus          VARCHAR(16)     NOT NULL DEFAULT 'open',
    SettlementStatus    VARCHAR(20)     NOT NULL DEFAULT 'pending',
    ProceedsGoldPending INT             NOT NULL DEFAULT 0,
    ProceedsGoldClaimed INT             NOT NULL DEFAULT 0,
    MerchantXpPending   INT             NOT NULL DEFAULT 0,
    MerchantXpClaimed   INT             NOT NULL DEFAULT 0,
    AppearanceJson      TEXT            NOT NULL DEFAULT '',
    CreatedAt           TIMESTAMPTZ     NOT NULL DEFAULT now(),
    ClosedAt            TIMESTAMPTZ     NULL,
    SettledAt           TIMESTAMPTZ     NULL,
    CONSTRAINT ck_playershops_salestatus       CHECK (SaleStatus IN ('open','closed','sold_out')),
    CONSTRAINT ck_playershops_settlementstatus CHECK (SettlementStatus IN ('pending','partially_claimed','claimed')),
    CONSTRAINT ck_playershops_shopname_len     CHECK (char_length(ShopName) BETWEEN 3 AND 24),
    CONSTRAINT ck_playershops_proceeds_nonneg  CHECK (ProceedsGoldPending >= 0 AND ProceedsGoldClaimed >= 0),
    CONSTRAINT ck_playershops_xp_nonneg        CHECK (MerchantXpPending >= 0 AND MerchantXpClaimed >= 0)
);

-- One OPEN shop per account (hard cap).
CREATE UNIQUE INDEX IF NOT EXISTS ux_playershops_open_per_account
    ON PlayerShops (CustomerGUID, OwnerUserGUID)
    WHERE SaleStatus = 'open';

CREATE INDEX IF NOT EXISTS ix_playershops_zone
    ON PlayerShops (CustomerGUID, ZoneName, SaleStatus);

CREATE INDEX IF NOT EXISTS ix_playershops_owner_settlement
    ON PlayerShops (CustomerGUID, OwnerCharacterID, SettlementStatus);

-- 3. PlayerShopItems
CREATE TABLE IF NOT EXISTS PlayerShopItems (
    PlayerShopItemID        BIGSERIAL   PRIMARY KEY,
    PlayerShopID            BIGINT      NOT NULL REFERENCES PlayerShops(PlayerShopID) ON DELETE CASCADE,
    ListingIndex            SMALLINT    NOT NULL,
    ItemIDTag               VARCHAR(255) NOT NULL,
    QuantityListed          INT         NOT NULL,
    QuantityRemainingForSale INT        NOT NULL,
    QuantityPendingReturn   INT         NOT NULL DEFAULT 0,
    QuantityReturned        INT         NOT NULL DEFAULT 0,
    UnitPriceGold           INT         NOT NULL,
    TaxRateBps              INT         NOT NULL DEFAULT 500,
    CustomData              TEXT        NULL,
    CONSTRAINT ck_playershopitems_listingindex CHECK (ListingIndex BETWEEN 0 AND 9),
    CONSTRAINT ck_playershopitems_qtylisted    CHECK (QuantityListed > 0),
    CONSTRAINT ck_playershopitems_qtyremaining CHECK (QuantityRemainingForSale >= 0),
    CONSTRAINT ck_playershopitems_qtypending   CHECK (QuantityPendingReturn >= 0),
    CONSTRAINT ck_playershopitems_qtyreturned  CHECK (QuantityReturned >= 0),
    CONSTRAINT ck_playershopitems_price        CHECK (UnitPriceGold > 0),
    CONSTRAINT ck_playershopitems_tax          CHECK (TaxRateBps BETWEEN 0 AND 10000),
    CONSTRAINT ux_playershopitems_shop_slot    UNIQUE (PlayerShopID, ListingIndex)
);

-- 4. PlayerShopPurchases (buyer payment/delivery state machine + purchase idempotency)
CREATE TABLE IF NOT EXISTS PlayerShopPurchases (
    PurchaseID          BIGSERIAL   PRIMARY KEY,
    OperationId         UUID        NOT NULL UNIQUE,
    CustomerGUID        UUID        NOT NULL,
    PlayerShopID        BIGINT      NOT NULL,
    PlayerShopItemID    BIGINT      NOT NULL,
    BuyerCharacterID    INT         NOT NULL,
    Quantity            INT         NOT NULL,
    UnitPriceGold       INT         NOT NULL,
    TaxPaid             INT         NOT NULL,
    ProceedsCredited    INT         NOT NULL,
    XpCredited          INT         NOT NULL,
    ItemIDTag           VARCHAR(255) NOT NULL,
    CustomData          TEXT        NULL,
    State               VARCHAR(16) NOT NULL DEFAULT 'paid',
    CreatedAt           TIMESTAMPTZ NOT NULL DEFAULT now(),
    ResolvedAt          TIMESTAMPTZ NULL,
    CONSTRAINT ck_playershoppurchases_state CHECK (State IN ('paid','delivered','restocked'))
);

CREATE INDEX IF NOT EXISTS ix_playershoppurchases_buyer_state
    ON PlayerShopPurchases (CustomerGUID, BuyerCharacterID, State);

-- 5. PlayerShopOperations (idempotency replay store for non-purchase mutations)
CREATE TABLE IF NOT EXISTS PlayerShopOperations (
    OperationId     UUID        PRIMARY KEY,
    CustomerGUID    UUID        NOT NULL,
    OpType          VARCHAR(32) NOT NULL,
    State           VARCHAR(16) NOT NULL DEFAULT 'committed',
    ResultJson      TEXT        NOT NULL DEFAULT '',
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT now()
);
