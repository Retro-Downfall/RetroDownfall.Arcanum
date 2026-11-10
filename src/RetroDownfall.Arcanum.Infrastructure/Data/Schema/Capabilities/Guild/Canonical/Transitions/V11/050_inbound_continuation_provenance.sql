DROP VIEW guild_utc_instant_columns;
DROP TRIGGER guild_continuations_guard_update;
DROP INDEX idx_guild_continuations_content;
ALTER TABLE guild_continuations RENAME TO guild_continuations_v10;
CREATE TABLE IF NOT EXISTS guild_continuations (
    QuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    ContinuationId TEXT NOT NULL CHECK(ContinuationId=upper(ContinuationId) AND length(ContinuationId)=36 AND substr(ContinuationId,9,1)='-' AND substr(ContinuationId,14,1)='-' AND substr(ContinuationId,19,1)='-' AND substr(ContinuationId,24,1)='-' AND length(replace(ContinuationId,'-',''))=32 AND replace(ContinuationId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContinuationId<>'00000000-0000-0000-0000-000000000000'),
    MutationId TEXT NOT NULL CHECK(MutationId=upper(MutationId) AND length(MutationId)=36 AND substr(MutationId,9,1)='-' AND substr(MutationId,14,1)='-' AND substr(MutationId,19,1)='-' AND substr(MutationId,24,1)='-' AND length(replace(MutationId,'-',''))=32 AND replace(MutationId,'-','') NOT GLOB '*[^0-9A-F]*' AND MutationId<>'00000000-0000-0000-0000-000000000000'),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    ContentId TEXT NOT NULL CHECK(ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000'),
    AcceptedRevision INTEGER NOT NULL CHECK(typeof(AcceptedRevision)='integer' AND AcceptedRevision>=1),
    CreatedAtUtc TEXT NOT NULL,
    AuthorityCode INTEGER NOT NULL DEFAULT 0 CHECK(typeof(AuthorityCode)='integer' AND AuthorityCode IN(0,1,2)),
    ProducerAgentId TEXT NULL CHECK(ProducerAgentId IS NULL OR (ProducerAgentId=upper(ProducerAgentId) AND length(ProducerAgentId)=36 AND substr(ProducerAgentId,9,1)='-' AND substr(ProducerAgentId,14,1)='-' AND substr(ProducerAgentId,19,1)='-' AND substr(ProducerAgentId,24,1)='-' AND length(replace(ProducerAgentId,'-',''))=32 AND replace(ProducerAgentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ProducerAgentId<>'00000000-0000-0000-0000-000000000000')),
    ProducerRunId TEXT NULL CHECK(ProducerRunId IS NULL OR (ProducerRunId=upper(ProducerRunId) AND length(ProducerRunId)=36 AND substr(ProducerRunId,9,1)='-' AND substr(ProducerRunId,14,1)='-' AND substr(ProducerRunId,19,1)='-' AND substr(ProducerRunId,24,1)='-' AND length(replace(ProducerRunId,'-',''))=32 AND replace(ProducerRunId,'-','') NOT GLOB '*[^0-9A-F]*' AND ProducerRunId<>'00000000-0000-0000-0000-000000000000')),
    ProducerQuestId TEXT NULL CHECK(ProducerQuestId IS NULL OR (ProducerQuestId=upper(ProducerQuestId) AND length(ProducerQuestId)=36 AND substr(ProducerQuestId,9,1)='-' AND substr(ProducerQuestId,14,1)='-' AND substr(ProducerQuestId,19,1)='-' AND substr(ProducerQuestId,24,1)='-' AND length(replace(ProducerQuestId,'-',''))=32 AND replace(ProducerQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND ProducerQuestId<>'00000000-0000-0000-0000-000000000000')),
    OperatorPrincipalId TEXT NULL CHECK(OperatorPrincipalId IS NULL OR (OperatorPrincipalId=upper(OperatorPrincipalId) AND length(OperatorPrincipalId)=36 AND substr(OperatorPrincipalId,9,1)='-' AND substr(OperatorPrincipalId,14,1)='-' AND substr(OperatorPrincipalId,19,1)='-' AND substr(OperatorPrincipalId,24,1)='-' AND length(replace(OperatorPrincipalId,'-',''))=32 AND replace(OperatorPrincipalId,'-','') NOT GLOB '*[^0-9A-F]*' AND OperatorPrincipalId<>'00000000-0000-0000-0000-000000000000')) CHECK((AuthorityCode=0 AND ProducerAgentId IS NULL AND ProducerRunId IS NULL AND ProducerQuestId IS NULL AND OperatorPrincipalId IS NULL) OR (AuthorityCode=1 AND ProducerAgentId IS NULL AND ProducerRunId IS NULL AND ProducerQuestId IS NULL AND OperatorPrincipalId IS NOT NULL) OR (AuthorityCode=2 AND OperatorPrincipalId IS NULL AND ((ProducerAgentId IS NOT NULL AND ProducerRunId IS NOT NULL AND ProducerQuestId IS NOT NULL AND PeerId IS NULL AND PeerGrantRevision IS NULL) OR (ProducerAgentId IS NULL AND ProducerRunId IS NULL AND ProducerQuestId IS NULL AND PeerId IS NOT NULL AND PeerGrantRevision IS NOT NULL)))),
    PeerId TEXT NULL CHECK(PeerId IS NULL OR (typeof(PeerId)='text' AND length(CAST(PeerId AS BLOB)) BETWEEN 1 AND 65536)),
    PeerGrantRevision TEXT NULL CHECK(PeerGrantRevision IS NULL OR (typeof(PeerGrantRevision)='text' AND length(PeerGrantRevision)=64)),
    RemoteOptionsJson TEXT NULL CHECK(RemoteOptionsJson IS NULL OR (typeof(RemoteOptionsJson)='text' AND length(CAST(RemoteOptionsJson AS BLOB)) BETWEEN 1 AND 65536)),
    CHECK((AuthorityCode=2) OR (PeerId IS NULL AND PeerGrantRevision IS NULL)),
    PRIMARY KEY(QuestId,ContinuationId),
    UNIQUE(QuestId,MutationId)
);
INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc,AuthorityCode,ProducerAgentId,ProducerRunId,ProducerQuestId,OperatorPrincipalId) SELECT QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc,AuthorityCode,ProducerAgentId,ProducerRunId,ProducerQuestId,OperatorPrincipalId FROM guild_continuations_v10;
DROP TABLE guild_continuations_v10;
CREATE TRIGGER IF NOT EXISTS guild_continuations_guard_update
BEFORE UPDATE ON guild_continuations
BEGIN
    SELECT RAISE(ABORT, 'Guild accepted continuation receipts are immutable.');
END;
CREATE INDEX IF NOT EXISTS idx_guild_continuations_content ON guild_continuations(ContentId,QuestId,ContinuationId);
CREATE VIEW IF NOT EXISTS guild_utc_instant_columns AS
SELECT 'guild_agent_cards' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_agent_cards' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_agent_card_revisions' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_content' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_content_erasure_receipts' AS TableName, 'ErasedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_owner_cleanup' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_owner_cleanup' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_signets' AS TableName, 'IssuedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_quests' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_quests' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_dispatches' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_dispatches' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_result_receipts' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_result_outbox' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_result_outbox' AS TableName, 'AcknowledgedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_result_applications' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_result_inputs' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_quest_start_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_continuations' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_envelopes' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_inbox' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_inbox' AS TableName, 'AcknowledgedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_portal_receipts' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_portal_inputs' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_events' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_portal_cursors' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_portal_outbox' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_workspace_leases' AS TableName, 'HeartbeatAtUtc' AS ColumnName
UNION ALL SELECT 'guild_workspace_leases' AS TableName, 'ExpiresAtUtc' AS ColumnName
UNION ALL SELECT 'guild_workspace_lease_mutations' AS TableName, 'AcceptedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_quest_cancellations' AS TableName, 'AcceptedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_remote_effects' AS TableName, 'SettledAtUtc' AS ColumnName
UNION ALL SELECT 'guild_remote_callbacks' AS TableName, 'CallbackReceivedAtUtc' AS ColumnName;
