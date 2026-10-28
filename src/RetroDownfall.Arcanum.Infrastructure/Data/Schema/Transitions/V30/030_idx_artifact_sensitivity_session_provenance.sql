CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_session_provenance ON artifact_sensitivity (SessionId, ProvenanceModeCode, ExactGenerationIds, GenerationBloom);
