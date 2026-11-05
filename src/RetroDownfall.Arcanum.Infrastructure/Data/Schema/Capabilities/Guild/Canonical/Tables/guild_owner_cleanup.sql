CREATE TABLE IF NOT EXISTS guild_owner_cleanup (
    OwnerKindCode INTEGER NOT NULL CHECK(OwnerKindCode IN(1,2)),
    OwnerId TEXT NOT NULL CHECK((OwnerId=upper(OwnerId) AND length(OwnerId)=36 AND substr(OwnerId,9,1)='-' AND substr(OwnerId,14,1)='-' AND substr(OwnerId,19,1)='-' AND substr(OwnerId,24,1)='-' AND length(replace(OwnerId,'-',''))=32 AND replace(OwnerId,'-','') NOT GLOB '*[^0-9A-F]*' AND OwnerId<>'00000000-0000-0000-0000-000000000000')),
    EventSequence INTEGER NULL CHECK(EventSequence IS NULL OR EventSequence>0),
    PhaseCode INTEGER NOT NULL DEFAULT 0 CHECK(PhaseCode BETWEEN 0 AND 6),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY(OwnerKindCode,OwnerId)
);
