CREATE TRIGGER IF NOT EXISTS saga_committed_uses_validate_insert
BEFORE INSERT ON saga_committed_uses
BEGIN
    SELECT RAISE(ABORT, 'Saga use requires a native committed finalization.')
    WHERE NOT EXISTS (SELECT 1 FROM assistant_entry_finalizations final
        WHERE final.AssistantEntryId = NEW.AssistantEntryId AND final.OutcomeCode = 1 AND final.SourceEvidenceDigest IS NULL);
    SELECT RAISE(ABORT, 'Saga use is bounded to fifty coordinates per final turn.')
    WHERE (SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = NEW.AssistantEntryId) >= 50;
    SELECT RAISE(ABORT, 'Saga use requires its exact current source version.')
    WHERE NOT EXISTS (SELECT 1 FROM saga_memories memory
        JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
        JOIN annal_heads head ON head.ClaimId = claim.ClaimId AND head.SubjectStoreCode = 1
        JOIN annal_versions version ON version.ClaimId = claim.ClaimId AND version.VersionId = head.CurrentVersionId
        WHERE memory.Id = NEW.MemoryId AND memory.RetiredAtUtc IS NULL AND claim.ClaimId = NEW.ClaimId
            AND version.VersionId = NEW.VersionId AND version.ContentHashFormatCode = NEW.HashFormat
            AND version.ContentHash = NEW.ContentDigest AND version.OperationCode <> 3 AND version.SensitivityCode = 0
            AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', ''))));
END;
