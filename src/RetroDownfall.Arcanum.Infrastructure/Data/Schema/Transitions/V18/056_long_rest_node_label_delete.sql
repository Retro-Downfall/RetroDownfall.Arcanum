-- BEFORE node deletion invalidates and purges the owning generation. Only after the node has
-- gone may its orphan label be retired; the label guard independently refuses surviving projections.
CREATE TRIGGER IF NOT EXISTS long_rest_node_label_delete
AFTER DELETE ON long_rest_discovery_nodes
BEGIN
    DELETE FROM artifact_sensitivity
    WHERE ArtifactKindCode = 16
        AND lower(replace(ArtifactId, '-', '')) = lower(replace(OLD.NodeId, '-', ''));
END;
