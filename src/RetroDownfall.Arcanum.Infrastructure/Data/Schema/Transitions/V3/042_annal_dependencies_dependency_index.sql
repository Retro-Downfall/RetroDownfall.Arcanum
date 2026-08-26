-- An erasure removes an edge when either end goes, so the incoming direction needs its own index.
CREATE INDEX IF NOT EXISTS idx_annal_dependencies_dependency
ON annal_dependencies(DependencyVersionId);
