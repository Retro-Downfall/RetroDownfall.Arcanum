-- One stable total order over a version's edges, so two readers of one claim see the same dependency
-- list in the same order. The unique constraint is also the bound: sixteen legal ordinals, one row each.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_dependencies_dependent_ordinal
ON annal_dependencies(DependentVersionId, Ordinal);
