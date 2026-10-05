-- The index the next statement creates admits one Complete generation per scope, so any scope that already
-- holds two has to be settled first or the index could not be built and the whole step would abort. The
-- switch has only ever promoted a generation after superseding the scope's previous one, but nothing
-- checked that it had, so a pair of Complete rows is a state an installation can already be in.
--
-- The newest completion is the current generation: it is the row retrieval reads, because
-- GetCurrentGenerationAsync orders a scope's Complete rows by CompletedAt descending. A tie is settled by
-- the larger generation id so the outcome is deterministic. The older rows become Superseded rather than
-- being deleted: the sweep's reconciliation removes every non-Complete generation, with its nodes and
-- embeddings, so the data goes the way a superseded generation's always does. Scopes that hold a single
-- Complete generation, and every Building or Superseded row, are not touched.
UPDATE tapestry_generations
SET Status = 'Superseded'
WHERE Status = 'Complete'
  AND GenerationId <> (
      SELECT newest.GenerationId
      FROM tapestry_generations AS newest
      WHERE newest.ScopeKind = tapestry_generations.ScopeKind
        AND newest.ScopeId = tapestry_generations.ScopeId
        AND newest.Status = 'Complete'
      ORDER BY newest.CompletedAt DESC, newest.GenerationId DESC
      LIMIT 1);
