CREATE TRIGGER IF NOT EXISTS guild_spend_receipts_operator_insert
AFTER INSERT ON guild_spend_receipts
BEGIN
    INSERT INTO guild_operator_sequences(KindCode,ObjectId,ScopeId) VALUES(5,NEW.ReceiptId,NEW.EffectId);
END;
