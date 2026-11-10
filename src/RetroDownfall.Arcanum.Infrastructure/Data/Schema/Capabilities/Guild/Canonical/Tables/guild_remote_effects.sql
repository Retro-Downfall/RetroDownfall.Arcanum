CREATE TABLE IF NOT EXISTS guild_remote_effects (
    EffectId TEXT NOT NULL CHECK(typeof(EffectId)='text' AND EffectId=upper(EffectId) AND length(EffectId)=36 AND substr(EffectId,9,1)='-' AND substr(EffectId,14,1)='-' AND substr(EffectId,19,1)='-' AND substr(EffectId,24,1)='-' AND length(replace(EffectId,'-',''))=32 AND replace(EffectId,'-','') NOT GLOB '*[^0-9A-F]*' AND EffectId<>'00000000-0000-0000-0000-000000000000'),
    QuestId TEXT NOT NULL CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    DispatchId TEXT NOT NULL CHECK(typeof(DispatchId)='text' AND DispatchId=upper(DispatchId) AND length(DispatchId)=36 AND substr(DispatchId,9,1)='-' AND substr(DispatchId,14,1)='-' AND substr(DispatchId,19,1)='-' AND substr(DispatchId,24,1)='-' AND length(replace(DispatchId,'-',''))=32 AND replace(DispatchId,'-','') NOT GLOB '*[^0-9A-F]*' AND DispatchId<>'00000000-0000-0000-0000-000000000000'),
    SettledAtUtc TEXT NULL,
    StopReasonCode TEXT NULL CHECK(StopReasonCode IS NULL OR StopReasonCode='Sending.TaskRejected'),
    PRIMARY KEY(EffectId),
    UNIQUE(DispatchId)
);
