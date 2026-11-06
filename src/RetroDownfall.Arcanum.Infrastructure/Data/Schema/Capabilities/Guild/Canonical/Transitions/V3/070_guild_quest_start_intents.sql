CREATE TABLE IF NOT EXISTS guild_quest_start_intents (
    QuestId TEXT NOT NULL PRIMARY KEY REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    CreatedAtUtc TEXT NOT NULL
);
