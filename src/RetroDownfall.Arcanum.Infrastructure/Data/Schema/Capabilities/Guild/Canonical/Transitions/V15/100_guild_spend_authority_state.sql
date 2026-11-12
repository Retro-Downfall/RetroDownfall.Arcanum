CREATE TABLE IF NOT EXISTS guild_spend_authority_state (
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK(StateKey=1),
    KeyId TEXT NOT NULL CHECK(typeof(KeyId)='text' AND KeyId=upper(KeyId) AND length(KeyId)=36 AND substr(KeyId,9,1)='-' AND substr(KeyId,14,1)='-' AND substr(KeyId,19,1)='-' AND substr(KeyId,24,1)='-' AND length(replace(KeyId,'-',''))=32 AND replace(KeyId,'-','') NOT GLOB '*[^0-9A-F]*' AND KeyId<>'00000000-0000-0000-0000-000000000000'),
    AuthorityEpoch TEXT NOT NULL CHECK(typeof(AuthorityEpoch)='text' AND AuthorityEpoch=upper(AuthorityEpoch) AND length(AuthorityEpoch)=36 AND substr(AuthorityEpoch,9,1)='-' AND substr(AuthorityEpoch,14,1)='-' AND substr(AuthorityEpoch,19,1)='-' AND substr(AuthorityEpoch,24,1)='-' AND length(replace(AuthorityEpoch,'-',''))=32 AND replace(AuthorityEpoch,'-','') NOT GLOB '*[^0-9A-F]*' AND AuthorityEpoch<>'00000000-0000-0000-0000-000000000000'),
    CredentialKey BLOB NOT NULL CHECK(typeof(CredentialKey)='blob' AND length(CredentialKey)=32)
);
