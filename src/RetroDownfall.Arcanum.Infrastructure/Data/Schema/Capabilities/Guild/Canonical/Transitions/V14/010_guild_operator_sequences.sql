CREATE TABLE IF NOT EXISTS guild_operator_sequences (
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    KindCode INTEGER NOT NULL CHECK(typeof(KindCode)='integer' AND KindCode IN(1,2,3)),
    ObjectId TEXT NOT NULL CHECK(typeof(ObjectId)='text' AND ObjectId=upper(ObjectId) AND length(ObjectId)=36),
    UNIQUE(KindCode,ObjectId)
);
