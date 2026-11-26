CREATE TABLE IF NOT EXISTS PartyFormCreateIntent (
    CustomerGUID UUID NOT NULL,
    CharacterID INTEGER NOT NULL,
    MutationId UUID NOT NULL,
    RegistrationId UUID NOT NULL,
    PRIMARY KEY (CustomerGUID, CharacterID, MutationId),
    FOREIGN KEY (CustomerGUID, CharacterID, RegistrationId)
        REFERENCES PartyMutationRegistration (CustomerGUID, CharacterID, RegistrationId) ON DELETE CASCADE
);
