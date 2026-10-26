INSERT INTO tapestry_scope_revisions (ScopeKind, ScopeId)
SELECT DISTINCT ScopeKind, ScopeId FROM tapestry_generations;
