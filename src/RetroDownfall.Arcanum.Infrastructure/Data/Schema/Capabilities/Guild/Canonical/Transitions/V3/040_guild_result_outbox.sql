CREATE TABLE IF NOT EXISTS guild_result_outbox (
    QuestId TEXT NOT NULL PRIMARY KEY REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    ResultId TEXT NOT NULL UNIQUE CHECK(ResultId=upper(ResultId) AND length(ResultId)=36 AND substr(ResultId,9,1)='-' AND substr(ResultId,14,1)='-' AND substr(ResultId,19,1)='-' AND substr(ResultId,24,1)='-' AND length(replace(ResultId,'-',''))=32 AND replace(ResultId,'-','') NOT GLOB '*[^0-9A-F]*' AND ResultId<>'00000000-0000-0000-0000-000000000000'),
    RecipientQuestId TEXT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(RecipientQuestId IS NULL OR (RecipientQuestId=upper(RecipientQuestId) AND length(RecipientQuestId)=36 AND substr(RecipientQuestId,9,1)='-' AND substr(RecipientQuestId,14,1)='-' AND substr(RecipientQuestId,19,1)='-' AND substr(RecipientQuestId,24,1)='-' AND length(replace(RecipientQuestId,'-',''))=32 AND replace(RecipientQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RecipientQuestId<>'00000000-0000-0000-0000-000000000000')),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    DeliveryStateCode INTEGER NOT NULL CHECK(DeliveryStateCode IN(1,2,3)),
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1),
    CreatedAtUtc TEXT NOT NULL,
    AcknowledgedAtUtc TEXT NULL,
    FOREIGN KEY(QuestId,ResultId,Digest) REFERENCES guild_result_receipts(QuestId,ResultId,Digest) ON DELETE RESTRICT
);
