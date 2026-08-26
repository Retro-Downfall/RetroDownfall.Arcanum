-- Bounded, deterministic, cycle-safe edges that target exact retained versions.
--
-- Cycle safety is structural rather than procedural. Each edge carries both endpoints' allocation
-- sequences, each bound to its version by a composite foreign key so neither can be misstated, and the
-- ordering check refuses any edge that does not point strictly backwards. A cycle needs at least one
-- edge that does not, so this table cannot hold one. There is no traversal, no recursive query, and no
-- detector to get wrong -- and no way for a future writer to bypass the rule by taking another code
-- path, because the rule is in the database rather than in a writer.
--
-- Sequence reuse after a deletion does not weaken that. Edges go with the version they name, so at
-- every instant every live edge satisfies the strict ordering, which is a directed acyclic graph by
-- construction.
CREATE TABLE IF NOT EXISTS annal_dependencies (
    DependentVersionId TEXT NOT NULL,
    DependentSequence INTEGER NOT NULL,
    DependencyVersionId TEXT NOT NULL,
    DependencySequence INTEGER NOT NULL,
    RelationCode INTEGER NOT NULL CHECK (RelationCode IN (1, 2, 3)),
    -- The ceiling lives here rather than in a writer, so the seventeenth edge is refused whatever
    -- produced it. AnnalLimits.MaxDependenciesPerVersion restates it and is not the authority.
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (DependentVersionId, DependencyVersionId),
    FOREIGN KEY (DependentVersionId, DependentSequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    FOREIGN KEY (DependencyVersionId, DependencySequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    CHECK (DependencySequence < DependentSequence)
);
