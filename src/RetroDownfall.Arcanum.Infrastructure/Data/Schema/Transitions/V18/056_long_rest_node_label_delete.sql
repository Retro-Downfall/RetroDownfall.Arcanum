-- BEFORE node deletion invalidates and purges the owning generation. Only after the node has
-- gone may its orphan label be retired; the label guard independently refuses surviving projections.
-- The label ids are found by a SELECT and deleted by id because SQLite plans a bare DELETE with this predicate
-- against the kind-only unique index, which would read every kind-16 label for every node deleted, while the
-- SELECT is answered from idx_artifact_sensitivity_artifact_norm. A trigger body cannot carry INDEXED BY, so the
-- shape of the statement is what steers the plan.
CREATE TRIGGER IF NOT EXISTS long_rest_node_label_delete
AFTER DELETE ON long_rest_discovery_nodes
BEGIN
    DELETE FROM artifact_sensitivity
    WHERE LabelId IN (
        SELECT LabelId FROM artifact_sensitivity
        WHERE ArtifactKindCode = 16
            AND lower(replace(ArtifactId, '-', '')) = lower(replace(OLD.NodeId, '-', '')));
END;
