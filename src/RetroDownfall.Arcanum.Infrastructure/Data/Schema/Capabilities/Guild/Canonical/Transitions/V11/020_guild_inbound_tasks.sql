CREATE TABLE IF NOT EXISTS guild_inbound_tasks (
    TaskId TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK(typeof(TaskId)='text' AND length(CAST(TaskId AS BLOB)) BETWEEN 1 AND 4096),
    QuestId TEXT NOT NULL UNIQUE REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    PeerId TEXT NOT NULL COLLATE BINARY REFERENCES guild_inbound_peer_state(PeerId) ON DELETE RESTRICT,
    GrantRevision TEXT NOT NULL CHECK(typeof(GrantRevision)='text' AND length(GrantRevision)=64),
    AuthorityEpoch TEXT NOT NULL CHECK(typeof(AuthorityEpoch)='text' AND length(AuthorityEpoch)=36),
    RemoteMessageId TEXT NOT NULL COLLATE BINARY CHECK(typeof(RemoteMessageId)='text' AND length(CAST(RemoteMessageId AS BLOB)) BETWEEN 1 AND 4096),
    ContextId TEXT NOT NULL COLLATE BINARY CHECK(typeof(ContextId)='text' AND length(CAST(ContextId AS BLOB)) BETWEEN 1 AND 4096),
    RequestDigest BLOB NOT NULL CHECK(typeof(RequestDigest)='blob' AND length(RequestDigest)=32),
    UNIQUE(PeerId,RemoteMessageId)
);
