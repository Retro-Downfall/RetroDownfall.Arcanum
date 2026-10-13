-- Deleting a label is deleting the only record that an artifact is tainted, so it is allowed only
-- from a scope that is also removing or replacing the artifact itself: artifact replacement,
-- sensitivity retention purge, Session retention, owner cleanup, or Covenant family maintenance.
-- Every one of these begins FALSE on each connection, so ordinary application work reaches this
-- guard and aborts rather than quietly leaving a tainted artifact with no label.
-- Kind16 cleanup may retire an orphan label only after the node and every matching content/reuse
-- projection are absent. This grants no scope and never permits removal of a live node's label.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_guard_delete
BEFORE DELETE ON artifact_sensitivity
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND NOT (OLD.ArtifactKindCode = 16
        AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_nodes
            WHERE lower(replace(NodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', '')))
        AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_embeddings
            WHERE lower(replace(NodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', '')))
        AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_edges
            WHERE lower(replace(ParentNodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''))
                OR lower(replace(ChildNodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', '')))
        AND NOT EXISTS (SELECT 1 FROM long_rest_clean_fts
            WHERE lower(replace(NodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', '')))
        AND NOT EXISTS (SELECT 1 FROM long_rest_protected_fts
            WHERE lower(replace(NodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', '')))
        AND NOT EXISTS (SELECT 1 FROM long_rest_accepted_summaries
            WHERE lower(replace(NodeId, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''))))
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity delete requires an authorized replacement, purge, retention, or maintenance scope.');
END;
