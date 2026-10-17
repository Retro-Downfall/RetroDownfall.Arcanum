CREATE TRIGGER IF NOT EXISTS long_rest_generation_invalidate
AFTER UPDATE OF PhaseCode ON long_rest_discovery_generations
WHEN NEW.PhaseCode = 7 AND OLD.PhaseCode <> 7
BEGIN
    DELETE FROM long_rest_clean_fts WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_protected_fts WHERE GenerationId = NEW.GenerationId;
    INSERT INTO long_rest_clean_fts(long_rest_clean_fts) VALUES ('optimize');
    INSERT INTO long_rest_protected_fts(long_rest_protected_fts) VALUES ('optimize');
    DELETE FROM long_rest_discovery_embeddings WHERE NodeId IN (SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId = NEW.GenerationId);
    DELETE FROM long_rest_discovery_edges WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_accepted_summaries WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_adjudications WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = NEW.GenerationId);
    DELETE FROM long_rest_work_inputs WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = NEW.GenerationId);
    DELETE FROM long_rest_provider_effects WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = NEW.GenerationId);
    DELETE FROM long_rest_work_items WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_discovery_nodes WHERE GenerationId = NEW.GenerationId;
    DELETE FROM long_rest_discovery_leaves WHERE GenerationId = NEW.GenerationId;
    UPDATE long_rest_sweeps SET PhaseCode = 7, Revision = Revision + 1 WHERE GenerationId = NEW.GenerationId;
END;
