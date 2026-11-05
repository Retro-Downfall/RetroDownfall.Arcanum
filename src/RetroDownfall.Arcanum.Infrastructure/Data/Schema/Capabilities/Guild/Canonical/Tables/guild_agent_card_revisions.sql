CREATE TABLE IF NOT EXISTS guild_agent_card_revisions (
    CardId TEXT NOT NULL CHECK((CardId=upper(CardId) AND length(CardId)=36 AND substr(CardId,9,1)='-' AND substr(CardId,14,1)='-' AND substr(CardId,19,1)='-' AND substr(CardId,24,1)='-' AND length(replace(CardId,'-',''))=32 AND replace(CardId,'-','') NOT GLOB '*[^0-9A-F]*' AND CardId<>'00000000-0000-0000-0000-000000000000')),
    Revision INTEGER NOT NULL CHECK(Revision > 0),
    ContentId TEXT NOT NULL UNIQUE CHECK((ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000')),
    SourceDigest TEXT NOT NULL CHECK(length(CAST(SourceDigest AS BLOB)) BETWEEN 1 AND 65536),
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY(CardId,Revision)
);
