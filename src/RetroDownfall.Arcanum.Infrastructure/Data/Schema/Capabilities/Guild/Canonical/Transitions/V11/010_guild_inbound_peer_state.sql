CREATE TABLE IF NOT EXISTS guild_inbound_peer_state (
    PeerId TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK(typeof(PeerId)='text' AND length(CAST(PeerId AS BLOB)) BETWEEN 1 AND 65536),
    GrantRevision TEXT NOT NULL CHECK(typeof(GrantRevision)='text' AND length(GrantRevision)=64),
    AuthorityEpoch TEXT NOT NULL CHECK(typeof(AuthorityEpoch)='text' AND length(AuthorityEpoch)=36),
    HostInstanceId TEXT NOT NULL CHECK(typeof(HostInstanceId)='text' AND length(HostInstanceId)=36),
    AuthorityVersion INTEGER NOT NULL CHECK(typeof(AuthorityVersion)='integer' AND AuthorityVersion>=0)
);
