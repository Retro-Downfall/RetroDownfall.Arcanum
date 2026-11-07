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
UNION ALL SELECT 'guild_portal_outbox' AS TableName, 'CreatedAtUtc' AS ColumnName;
