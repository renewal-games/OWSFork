CREATE TABLE IF NOT EXISTS PartyMutationReceipt (
    CustomerGUID UUID NOT NULL,
    MutationId UUID NOT NULL,
    CharacterID INTEGER NOT NULL,
    Committed BOOLEAN NOT NULL,
    PRIMARY KEY (CustomerGUID, MutationId, CharacterID)
);
