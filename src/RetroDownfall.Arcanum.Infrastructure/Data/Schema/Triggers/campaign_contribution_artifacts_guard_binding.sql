CREATE TRIGGER IF NOT EXISTS campaign_contribution_artifacts_guard_binding
BEFORE INSERT ON campaign_contribution_artifacts
WHEN NOT EXISTS (SELECT 1 FROM session_campaign_bindings
    WHERE SessionId = NEW.SessionId AND BindingKindCode = 2 AND CampaignId = NEW.CampaignId)
BEGIN
    SELECT RAISE(ABORT, 'A Campaign contribution requires its exact canonical Session binding.');
END;
