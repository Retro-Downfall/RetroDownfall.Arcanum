CREATE VIEW IF NOT EXISTS covenant_utc_instant_columns AS
SELECT 'covenant_curation_heads' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_curation_receipts' AS TableName, 'CommittedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_curation_versions' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_entries' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_heads' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_key_epochs' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_mutation_receipts' AS TableName, 'CommittedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_turn_receipt_aggregate' AS TableName, 'EarliestCoveredAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_turn_receipt_aggregate' AS TableName, 'LatestCoveredAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_turn_receipt_aggregate' AS TableName, 'UpdatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_turn_receipts' AS TableName, 'CreatedAtUtc' AS ColumnName
UNION ALL SELECT 'covenant_versions' AS TableName, 'CreatedAtUtc' AS ColumnName
;
