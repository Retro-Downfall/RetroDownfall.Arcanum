CREATE TABLE IF NOT EXISTS guild_work_awards (
    Id TEXT NOT NULL PRIMARY KEY,
    OfferId TEXT NOT NULL UNIQUE REFERENCES guild_work_offers(Id) ON DELETE RESTRICT,
    BidId TEXT NOT NULL UNIQUE REFERENCES guild_work_bids(Id) ON DELETE RESTRICT,
    QuestId TEXT NOT NULL UNIQUE REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    MemberId TEXT NOT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    PrimaryQuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    PrimarySignetId TEXT NOT NULL REFERENCES guild_signets(SignetId) ON DELETE RESTRICT,
    PrimaryRevocationGeneration INTEGER NOT NULL CHECK(typeof(PrimaryRevocationGeneration)='integer' AND PrimaryRevocationGeneration>=0),
    CHECK(typeof(Id)='text' AND Id=upper(Id) AND length(Id)=36 AND substr(Id,9,1)='-' AND substr(Id,14,1)='-' AND substr(Id,19,1)='-' AND substr(Id,24,1)='-' AND length(replace(Id,'-',''))=32 AND replace(Id,'-','') NOT GLOB '*[^0-9A-F]*' AND Id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(OfferId)='text' AND OfferId=upper(OfferId) AND length(OfferId)=36 AND substr(OfferId,9,1)='-' AND substr(OfferId,14,1)='-' AND substr(OfferId,19,1)='-' AND substr(OfferId,24,1)='-' AND length(replace(OfferId,'-',''))=32 AND replace(OfferId,'-','') NOT GLOB '*[^0-9A-F]*' AND OfferId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(BidId)='text' AND BidId=upper(BidId) AND length(BidId)=36 AND substr(BidId,9,1)='-' AND substr(BidId,14,1)='-' AND substr(BidId,19,1)='-' AND substr(BidId,24,1)='-' AND length(replace(BidId,'-',''))=32 AND replace(BidId,'-','') NOT GLOB '*[^0-9A-F]*' AND BidId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(MemberId)='text' AND MemberId=upper(MemberId) AND length(MemberId)=36 AND substr(MemberId,9,1)='-' AND substr(MemberId,14,1)='-' AND substr(MemberId,19,1)='-' AND substr(MemberId,24,1)='-' AND length(replace(MemberId,'-',''))=32 AND replace(MemberId,'-','') NOT GLOB '*[^0-9A-F]*' AND MemberId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(PrimaryQuestId)='text' AND PrimaryQuestId=upper(PrimaryQuestId) AND length(PrimaryQuestId)=36 AND substr(PrimaryQuestId,9,1)='-' AND substr(PrimaryQuestId,14,1)='-' AND substr(PrimaryQuestId,19,1)='-' AND substr(PrimaryQuestId,24,1)='-' AND length(replace(PrimaryQuestId,'-',''))=32 AND replace(PrimaryQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND PrimaryQuestId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(PrimarySignetId)='text' AND PrimarySignetId=upper(PrimarySignetId) AND length(PrimarySignetId)=36 AND substr(PrimarySignetId,9,1)='-' AND substr(PrimarySignetId,14,1)='-' AND substr(PrimarySignetId,19,1)='-' AND substr(PrimarySignetId,24,1)='-' AND length(replace(PrimarySignetId,'-',''))=32 AND replace(PrimarySignetId,'-','') NOT GLOB '*[^0-9A-F]*' AND PrimarySignetId<>'00000000-0000-0000-0000-000000000000')
);
