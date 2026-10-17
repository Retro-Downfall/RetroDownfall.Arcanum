CREATE TRIGGER IF NOT EXISTS long_rest_sources_mutated
AFTER UPDATE OF MutationEpoch ON long_rest_sources
WHEN NEW.MutationEpoch <> OLD.MutationEpoch
BEGIN
    UPDATE long_rest_discovery_generations SET PhaseCode = 7, Revision = Revision + 1, Published = 0
    WHERE PhaseCode <> 7 AND GenerationId IN (SELECT GenerationId FROM long_rest_discovery_leaves WHERE MemoryId = OLD.MemoryId);
END;
