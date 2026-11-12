CREATE TRIGGER IF NOT EXISTS guild_spend_receipts_operator_delete
AFTER DELETE ON guild_spend_receipts
BEGIN
    DELETE FROM guild_operator_sequences WHERE KindCode=5 AND ObjectId=OLD.ReceiptId;
END;
