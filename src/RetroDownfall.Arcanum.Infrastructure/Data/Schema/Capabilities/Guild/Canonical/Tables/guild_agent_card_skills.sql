CREATE TABLE IF NOT EXISTS guild_agent_card_skills (
    CardId TEXT NOT NULL CHECK((CardId=upper(CardId) AND length(CardId)=36 AND substr(CardId,9,1)='-' AND substr(CardId,14,1)='-' AND substr(CardId,19,1)='-' AND substr(CardId,24,1)='-' AND length(replace(CardId,'-',''))=32 AND replace(CardId,'-','') NOT GLOB '*[^0-9A-F]*' AND CardId<>'00000000-0000-0000-0000-000000000000')),
    Revision INTEGER NOT NULL,
    SkillId TEXT NOT NULL CHECK(length(CAST(SkillId AS BLOB)) BETWEEN 1 AND 65536),
    Ordinal INTEGER NOT NULL CHECK(Ordinal >= 0),
    PRIMARY KEY(CardId,Revision,Ordinal)
);
