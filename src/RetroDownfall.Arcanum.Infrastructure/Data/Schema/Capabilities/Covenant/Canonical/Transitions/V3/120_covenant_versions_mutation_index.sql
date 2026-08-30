CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_mutation
    ON covenant_versions(MutationId);
