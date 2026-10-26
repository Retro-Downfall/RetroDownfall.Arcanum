CREATE TRIGGER IF NOT EXISTS tapestry_generations_guard_update
BEFORE UPDATE ON tapestry_generations
WHEN NEW.GenerationId IS NOT OLD.GenerationId OR NEW.ScopeKind IS NOT OLD.ScopeKind OR NEW.ScopeId IS NOT OLD.ScopeId OR NEW.AlgorithmVersion IS NOT OLD.AlgorithmVersion OR NEW.SettingsFingerprint IS NOT OLD.SettingsFingerprint OR NEW.SummaryModel IS NOT OLD.SummaryModel OR NEW.SummaryRecipeVersion IS NOT OLD.SummaryRecipeVersion OR NEW.EmbeddingDimension IS NOT OLD.EmbeddingDimension OR NEW.CorpusFingerprint IS NOT OLD.CorpusFingerprint OR NEW.SourceRevision IS NOT OLD.SourceRevision OR NEW.EmbeddingIdentity IS NOT OLD.EmbeddingIdentity OR NEW.StartedAt IS NOT OLD.StartedAt OR (OLD.Status <> 'Building' AND NEW.Status IS NOT OLD.Status AND NOT (OLD.Status = 'Complete' AND NEW.Status = 'Superseded')) OR (OLD.Status <> 'Building' AND (NEW.LayerCount IS NOT OLD.LayerCount OR NEW.NodeCount IS NOT OLD.NodeCount OR NEW.RootNodeCount IS NOT OLD.RootNodeCount OR NEW.TerminalReason IS NOT OLD.TerminalReason OR NEW.CompletedAt IS NOT OLD.CompletedAt))
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
