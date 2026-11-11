CREATE TABLE IF NOT EXISTS guild_work_offer_recipients (
    OfferId TEXT NOT NULL REFERENCES guild_work_offers(Id) ON DELETE RESTRICT,
    MemberId TEXT NOT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    AgentId TEXT NOT NULL,
    PrimaryQuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    CardId TEXT NOT NULL,
    CardRevision INTEGER NOT NULL CHECK(typeof(CardRevision)='integer' AND CardRevision>=1),
    PRIMARY KEY(OfferId,MemberId),
    CHECK(typeof(OfferId)='text' AND OfferId=upper(OfferId) AND length(OfferId)=36 AND substr(OfferId,9,1)='-' AND substr(OfferId,14,1)='-' AND substr(OfferId,19,1)='-' AND substr(OfferId,24,1)='-' AND length(replace(OfferId,'-',''))=32 AND replace(OfferId,'-','') NOT GLOB '*[^0-9A-F]*' AND OfferId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(MemberId)='text' AND MemberId=upper(MemberId) AND length(MemberId)=36 AND substr(MemberId,9,1)='-' AND substr(MemberId,14,1)='-' AND substr(MemberId,19,1)='-' AND substr(MemberId,24,1)='-' AND length(replace(MemberId,'-',''))=32 AND replace(MemberId,'-','') NOT GLOB '*[^0-9A-F]*' AND MemberId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(AgentId)='text' AND AgentId=upper(AgentId) AND length(AgentId)=36 AND substr(AgentId,9,1)='-' AND substr(AgentId,14,1)='-' AND substr(AgentId,19,1)='-' AND substr(AgentId,24,1)='-' AND length(replace(AgentId,'-',''))=32 AND replace(AgentId,'-','') NOT GLOB '*[^0-9A-F]*' AND AgentId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(PrimaryQuestId)='text' AND PrimaryQuestId=upper(PrimaryQuestId) AND length(PrimaryQuestId)=36 AND substr(PrimaryQuestId,9,1)='-' AND substr(PrimaryQuestId,14,1)='-' AND substr(PrimaryQuestId,19,1)='-' AND substr(PrimaryQuestId,24,1)='-' AND length(replace(PrimaryQuestId,'-',''))=32 AND replace(PrimaryQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND PrimaryQuestId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(CardId)='text' AND CardId=upper(CardId) AND length(CardId)=36 AND substr(CardId,9,1)='-' AND substr(CardId,14,1)='-' AND substr(CardId,19,1)='-' AND substr(CardId,24,1)='-' AND length(replace(CardId,'-',''))=32 AND replace(CardId,'-','') NOT GLOB '*[^0-9A-F]*' AND CardId<>'00000000-0000-0000-0000-000000000000')
);
