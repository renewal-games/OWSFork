CREATE TABLE IF NOT EXISTS PartyMutationRegistration (
    CustomerGUID UUID NOT NULL,
    CharacterID INTEGER NOT NULL,
    RegistrationId UUID NOT NULL,
    RegistrationOrder BIGINT GENERATED ALWAYS AS IDENTITY,
    PRIMARY KEY (CustomerGUID, CharacterID, RegistrationId),
    FOREIGN KEY (CustomerGUID, CharacterID) REFERENCES Characters (CustomerGUID, CharacterID) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_PartyMutationRegistration_Current
    ON PartyMutationRegistration (CustomerGUID, CharacterID, RegistrationOrder DESC);
