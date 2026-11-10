CREATE TABLE IF NOT EXISTS guild_remote_callbacks (
    CallbackConfigId TEXT NOT NULL CHECK(typeof(CallbackConfigId)='text' AND length(CAST(CallbackConfigId AS BLOB)) BETWEEN 1 AND 128),
    QuestId TEXT NOT NULL CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    DispatchId TEXT NOT NULL CHECK(typeof(DispatchId)='text' AND DispatchId=upper(DispatchId) AND length(DispatchId)=36 AND substr(DispatchId,9,1)='-' AND substr(DispatchId,14,1)='-' AND substr(DispatchId,19,1)='-' AND substr(DispatchId,24,1)='-' AND length(replace(DispatchId,'-',''))=32 AND replace(DispatchId,'-','') NOT GLOB '*[^0-9A-F]*' AND DispatchId<>'00000000-0000-0000-0000-000000000000'),
    CallbackTokenHash TEXT NOT NULL CHECK(typeof(CallbackTokenHash)='text' AND length(CallbackTokenHash)=64 AND CallbackTokenHash NOT GLOB '*[^0-9A-F]*'),
    CallbackReceivedAtUtc TEXT NULL,
    PRIMARY KEY(CallbackConfigId),
    UNIQUE(QuestId,DispatchId),
    FOREIGN KEY(QuestId) REFERENCES guild_remote_executions(QuestId),
    FOREIGN KEY(DispatchId) REFERENCES guild_dispatches(DispatchId)
);
