CREATE TRIGGER IF NOT EXISTS campaign_maintenance_checkpoints_guard_insert
BEFORE INSERT ON campaign_maintenance_checkpoints
WHEN NOT EXISTS (SELECT 1 FROM session_turn_claims WHERE ClaimId = NEW.ClaimId AND SessionId = NEW.SessionId AND StateCode = 1 AND ExecutorId = NEW.ExecutorId)
    OR NOT EXISTS (SELECT 1 FROM session_campaign_bindings
    WHERE SessionId = NEW.SessionId AND BindingKindCode = 2 AND CampaignId = NEW.CampaignId)
BEGIN
    SELECT RAISE(ABORT, 'Campaign maintenance must bind the requesting claim and canonical Campaign.');
END;
