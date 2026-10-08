-- The statement text is character for character the one in Tables/tapestry_generations.sql. The installer
-- compares an installed index's stored DDL with the head file's, normalized, so a transition that phrased
-- the same index differently would report DefinitionDrift on every evolved installation and on none of the
-- fresh ones.
CREATE UNIQUE INDEX IF NOT EXISTS ux_tapestry_generations_complete_scope
    ON tapestry_generations(ScopeKind, ScopeId) WHERE Status = 'Complete';
