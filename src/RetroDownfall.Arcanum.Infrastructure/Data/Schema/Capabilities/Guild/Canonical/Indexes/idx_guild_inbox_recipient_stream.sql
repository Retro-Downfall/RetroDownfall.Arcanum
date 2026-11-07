CREATE INDEX IF NOT EXISTS idx_guild_inbox_recipient_stream ON guild_inbox(RecipientKey,StreamId,SenderSequence);
