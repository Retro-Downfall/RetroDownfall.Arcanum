CREATE TABLE IF NOT EXISTS guild_operator_sequences (
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    KindCode INTEGER NOT NULL CHECK(typeof(KindCode)='integer' AND KindCode IN(1,2,3,4,5)),
    ObjectId TEXT NOT NULL CHECK(typeof(ObjectId)='text' AND ObjectId=upper(ObjectId) AND length(ObjectId)=36),
    ScopeId TEXT NULL CHECK(ScopeId IS NULL OR (typeof(ScopeId)='text' AND ScopeId=upper(ScopeId) AND length(ScopeId)=36 AND substr(ScopeId,9,1)='-' AND substr(ScopeId,14,1)='-' AND substr(ScopeId,19,1)='-' AND substr(ScopeId,24,1)='-' AND length(replace(ScopeId,'-',''))=32 AND replace(ScopeId,'-','') NOT GLOB '*[^0-9A-F]*' AND ScopeId<>'00000000-0000-0000-0000-000000000000')),
    CHECK((KindCode IN(1,2,3) AND ScopeId IS NULL) OR (KindCode IN(4,5) AND ScopeId IS NOT NULL)),
    UNIQUE(KindCode,ObjectId)
);
