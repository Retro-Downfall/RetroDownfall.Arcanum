CREATE INDEX IF NOT EXISTS idx_guild_inbox_pending_recipient_id ON guild_inbox(RecipientKey,Id) WHERE DeliveryStateCode=1;
