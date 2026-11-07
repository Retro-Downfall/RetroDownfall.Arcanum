CREATE TABLE IF NOT EXISTS guild_event_streams (
    RootQuestId TEXT NOT NULL PRIMARY KEY CHECK(RootQuestId=upper(RootQuestId) AND length(RootQuestId)=36 AND substr(RootQuestId,9,1)='-' AND substr(RootQuestId,14,1)='-' AND substr(RootQuestId,19,1)='-' AND substr(RootQuestId,24,1)='-' AND length(replace(RootQuestId,'-',''))=32 AND replace(RootQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RootQuestId<>'00000000-0000-0000-0000-000000000000'),
    LastSequence INTEGER NOT NULL CHECK(typeof(LastSequence)='integer' AND LastSequence>=0),
    RetainedAfterSequence INTEGER NOT NULL DEFAULT 0 CHECK(typeof(RetainedAfterSequence)='integer' AND RetainedAfterSequence>=0),
    CHECK(RetainedAfterSequence<=LastSequence)
);
