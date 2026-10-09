-- One writer cannot bypass the exact-version, scope, lifecycle, or no-chain boundary of another.
CREATE TRIGGER IF NOT EXISTS long_rest_suppressions_validate_insert
BEFORE INSERT ON long_rest_suppressions
WHEN NOT EXISTS (
    SELECT 1
    FROM annal_versions source
    JOIN annal_claims source_claim ON source_claim.ClaimId = source.ClaimId
    JOIN annal_heads source_head ON source_head.ClaimId = source.ClaimId
        AND source_head.CurrentVersionId = source.VersionId
    JOIN saga_memories source_memory ON source_memory.Id = source_claim.SubjectId
    JOIN annal_versions survivor ON survivor.VersionId = NEW.SurvivorVersionId
    JOIN annal_claims survivor_claim ON survivor_claim.ClaimId = survivor.ClaimId
    JOIN annal_heads survivor_head ON survivor_head.ClaimId = survivor.ClaimId
        AND survivor_head.CurrentVersionId = survivor.VersionId
    JOIN saga_memories survivor_memory ON survivor_memory.Id = survivor_claim.SubjectId
    JOIN long_rest_receipts receipt ON receipt.ReceiptId = NEW.ReceiptId
    WHERE source.VersionId = NEW.SourceVersionId
      AND source_claim.SubjectStoreCode = 1
      AND survivor_claim.SubjectStoreCode = 1
      AND source.OperationCode <> 3 AND survivor.OperationCode <> 3
      AND source.ScopeKindCode IN (1, 2)
      AND source.ScopeKindCode = survivor.ScopeKindCode
      AND source.CampaignId IS survivor.CampaignId
      AND source_memory.ScopeKindCode = source.ScopeKindCode
      AND source_memory.CampaignId IS source.CampaignId
      AND survivor_memory.ScopeKindCode = survivor.ScopeKindCode
      AND survivor_memory.CampaignId IS survivor.CampaignId
      AND source.SensitivityCode = 0 AND survivor.SensitivityCode = 0
      AND EXISTS (SELECT 1 FROM saga_memory_embeddings embedding WHERE embedding.MemoryId = survivor_memory.Id)
      AND source_memory.PinnedAtUtc IS NULL AND survivor_memory.PinnedAtUtc IS NULL
      AND source_memory.RetiredAtUtc IS NULL AND survivor_memory.RetiredAtUtc IS NULL
      AND receipt.OutcomeCode = 1
      AND (receipt.KindCode <> 1 OR (source.ContentHashFormatCode = survivor.ContentHashFormatCode
          AND source.ContentHash = survivor.ContentHash))
      AND receipt.SurvivorVersionId = survivor.VersionId
      AND receipt.SurvivorMemoryId = survivor_claim.SubjectId
      AND EXISTS (SELECT 1 FROM long_rest_receipt_inputs input
          WHERE input.ReceiptId = receipt.ReceiptId
            AND input.VersionId = source.VersionId AND input.MemoryId = source_claim.SubjectId)
      AND EXISTS (SELECT 1 FROM long_rest_receipt_inputs input
          WHERE input.ReceiptId = receipt.ReceiptId
            AND input.VersionId = survivor.VersionId AND input.MemoryId = survivor_claim.SubjectId)
      AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label
          WHERE label.ArtifactKindCode = 6
            AND lower(replace(label.ArtifactId, '-', '')) IN (
                lower(replace(source_memory.Id, '-', '')), lower(replace(survivor_memory.Id, '-', ''))))
)
OR EXISTS (SELECT 1 FROM long_rest_suppressions WHERE SourceVersionId = NEW.SurvivorVersionId)
OR EXISTS (SELECT 1 FROM long_rest_receipts
    WHERE OutcomeCode = 1 AND SurvivorVersionId = NEW.SourceVersionId)
BEGIN
    SELECT RAISE(ABORT, 'Long Rest suppression requires compatible, current, unpinned Saga inputs and cannot form a chain.');
END;
