CREATE TABLE IF NOT EXISTS guild_remote_executions (
    QuestId TEXT NOT NULL CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    InputContentId TEXT NOT NULL CHECK(typeof(InputContentId)='text' AND InputContentId=upper(InputContentId) AND length(InputContentId)=36 AND substr(InputContentId,9,1)='-' AND substr(InputContentId,14,1)='-' AND substr(InputContentId,19,1)='-' AND substr(InputContentId,24,1)='-' AND length(replace(InputContentId,'-',''))=32 AND replace(InputContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND InputContentId<>'00000000-0000-0000-0000-000000000000'),
    InputDigest BLOB NOT NULL CHECK(typeof(InputDigest)='blob' AND length(InputDigest)=32),
    InputByteLength INTEGER NOT NULL CHECK(typeof(InputByteLength)='integer' AND InputByteLength BETWEEN 1 AND 65536),
    RecoveryDiscoveryEndpoint TEXT NOT NULL CHECK(typeof(RecoveryDiscoveryEndpoint)='text' AND length(CAST(RecoveryDiscoveryEndpoint AS BLOB)) BETWEEN 1 AND 65536),
    RecoveryTransport BLOB NOT NULL CHECK(typeof(RecoveryTransport)='blob' AND length(RecoveryTransport) BETWEEN 1 AND 65536),
    RecoveryDigest BLOB NOT NULL CHECK(typeof(RecoveryDigest)='blob' AND length(RecoveryDigest)=32),
    CHECK(length(CAST(RecoveryDiscoveryEndpoint AS BLOB))+length(RecoveryTransport)<=65536),
    PRIMARY KEY(QuestId),
    UNIQUE(InputContentId)
);
