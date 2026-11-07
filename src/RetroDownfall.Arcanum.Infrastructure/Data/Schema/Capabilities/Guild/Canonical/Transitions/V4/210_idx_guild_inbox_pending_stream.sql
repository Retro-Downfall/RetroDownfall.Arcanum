CREATE INDEX IF NOT EXISTS idx_guild_inbox_pending_stream ON guild_inbox(RecipientKey,StreamId,SenderSequence) WHERE DeliveryStateCode=1;
