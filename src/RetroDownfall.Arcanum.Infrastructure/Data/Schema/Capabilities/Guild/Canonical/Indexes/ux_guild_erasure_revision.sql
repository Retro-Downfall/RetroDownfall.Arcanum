CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_erasure_revision ON guild_content_erasure_receipts(ContentKindCode,SourceRecordId,Revision);
