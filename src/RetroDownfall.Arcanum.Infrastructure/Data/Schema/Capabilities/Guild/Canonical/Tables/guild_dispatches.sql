CREATE TABLE IF NOT EXISTS guild_dispatches (
    DispatchId TEXT NOT NULL PRIMARY KEY CHECK(DispatchId=upper(DispatchId) AND length(DispatchId)=36 AND substr(DispatchId,9,1)='-' AND substr(DispatchId,14,1)='-' AND substr(DispatchId,19,1)='-' AND substr(DispatchId,24,1)='-' AND length(replace(DispatchId,'-',''))=32 AND replace(DispatchId,'-','') NOT GLOB '*[^0-9A-F]*' AND DispatchId<>'00000000-0000-0000-0000-000000000000'),
    QuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    InvocationDigest BLOB NOT NULL CHECK(typeof(InvocationDigest)='blob' AND length(InvocationDigest)=32),
    IsContinuation INTEGER NOT NULL CHECK(typeof(IsContinuation)='integer' AND IsContinuation IN(0,1)),
    AccountingAssociationId TEXT NOT NULL CHECK(AccountingAssociationId=upper(AccountingAssociationId) AND length(AccountingAssociationId)=36 AND substr(AccountingAssociationId,9,1)='-' AND substr(AccountingAssociationId,14,1)='-' AND substr(AccountingAssociationId,19,1)='-' AND substr(AccountingAssociationId,24,1)='-' AND length(replace(AccountingAssociationId,'-',''))=32 AND replace(AccountingAssociationId,'-','') NOT GLOB '*[^0-9A-F]*' AND AccountingAssociationId<>'00000000-0000-0000-0000-000000000000'),
    EvidenceCode INTEGER NOT NULL CHECK(EvidenceCode IN(1,2,3,4)),
    LocalChildId TEXT NULL CHECK(LocalChildId IS NULL OR (LocalChildId=upper(LocalChildId) AND length(LocalChildId)=36 AND substr(LocalChildId,9,1)='-' AND substr(LocalChildId,14,1)='-' AND substr(LocalChildId,19,1)='-' AND substr(LocalChildId,24,1)='-' AND length(replace(LocalChildId,'-',''))=32 AND replace(LocalChildId,'-','') NOT GLOB '*[^0-9A-F]*' AND LocalChildId<>'00000000-0000-0000-0000-000000000000')),
    RemoteTaskId TEXT NULL CHECK(RemoteTaskId IS NULL OR length(CAST(RemoteTaskId AS BLOB)) BETWEEN 1 AND 65536),
    RemoteContextId TEXT NULL CHECK(RemoteContextId IS NULL OR length(CAST(RemoteContextId AS BLOB)) BETWEEN 1 AND 65536),
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
