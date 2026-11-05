CREATE VIEW IF NOT EXISTS guild_utc_instant_columns AS
SELECT 'guild_agent_cards' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_agent_cards' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_agent_card_revisions' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_content' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_content_erasure_receipts' AS TableName, 'ErasedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_owner_cleanup' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_owner_cleanup' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'guild_signets' AS TableName, 'IssuedAtUtc' AS ColumnName;
