-- A generation's search index only serves discovery, so it ends with discovery: the generation's own rows leave both indexes the moment it
-- completes. A completed generation also supersedes every earlier completed generation of the same scope kind, Campaign and protected
-- partition, which the existing invalidation purge then removes. Retired generations keep the paid evidence of failed requests and are untouched.
CREATE TRIGGER IF NOT EXISTS long_rest_generation_complete
AFTER UPDATE OF PhaseCode ON long_rest_discovery_generations
WHEN NEW.PhaseCode = 6 AND OLD.PhaseCode <> 6
BEGIN
    DELETE FROM long_rest_clean_fts WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_protected_fts WHERE GenerationId = NEW.GenerationId;
    INSERT INTO long_rest_clean_fts(long_rest_clean_fts) VALUES ('optimize');
    INSERT INTO long_rest_protected_fts(long_rest_protected_fts) VALUES ('optimize');
    UPDATE long_rest_discovery_generations SET PhaseCode = 7, Revision = Revision + 1, Published = 0
    WHERE ScopeKindCode = NEW.ScopeKindCode AND CampaignId IS NEW.CampaignId AND PartitionDigest IS NEW.PartitionDigest
        AND PhaseCode = 6 AND GenerationId <> NEW.GenerationId;
END;
