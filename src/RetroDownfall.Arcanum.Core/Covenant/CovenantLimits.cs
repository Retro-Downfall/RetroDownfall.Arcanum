namespace RetroDownfall.Arcanum.Core.Covenant;

public static class CovenantLimits
{
    public const int MaxKeyCharacters = 128;

    public const int MaxAuthoredContentBytes = 2_048;

    public const int MaxGlobalConfirmedRenderedBytes = 4_096;

    public const int MaxGlobalConfirmedEntries = 64;

    public const int MaxCampaignConfirmedRenderedBytes = 4_096;

    public const int MaxCampaignConfirmedEntries = 64;

    public const int MaxCampaignProposedRenderedBytes = 4_096;

    public const int MaxCampaignProposedEntries = 32;

    public const int MaxStagedMutationsPerTurn = 4;

    public const int MaxStableEntriesPerScope = 256;

    public const int MaxVersionsPerScope = 8_192;

    public const int MaxSetVersionsPerScope = 7_936;

    public const int MaxCanonicalBytesPerScope = 16 * 1_024 * 1_024;

    public const int MaxAgentVersionsPerCampaign = 4_096;

    public const int MaxAgentBytesPerCampaign = 8 * 1_024 * 1_024;

    public const int MaxVersionsPerEntryLane = 1_024;

    public const int MaxSetVersionsPerEntryLane = 1_023;

    public const int MaxAgentProposedVersionsPerEntryLane = 256;

    public const int MaxMutationReceiptsPerScope = 16_640;

    public const int MaxOrdinaryMutationReceiptsPerScope = 16_384;

    public const int MaxMutationReceiptLogicalBytesPerScope = 8_519_680;

    public const int MaxOrdinaryMutationReceiptLogicalBytesPerScope = 8_388_608;

    public const int MutationReceiptLogicalBytes = 512;

    public const int MaxAttachmentSourcesPerAgentMutation = 64;

    public const int MaxAttachmentProvenanceRowsPerCampaign = 16_384;

    public const int MaxAttachmentProvenanceBytesPerCampaign = 8 * 1_024 * 1_024;

    public const int MaxPendingSearchOutboxRows = 65_536;

    public const int MaxTurnReceiptsPerSession = 1_024;

    public const int MaxTurnReceiptsInstallationWide = 65_536;

    public const int MaxPublicTurnClaimsPerSession = 16_384;

    public const int MaxPublicTurnClaimsInstallationWide = 1_048_576;

    public const int MaxAssistantFinalizationGuardsPerSession = 16_384;

    public const int MaxAssistantFinalizationGuardsInstallationWide = 1_048_576;

    public const int MaxExactGenerationIds = 8;

    public const int GenerationBloomBytes = 32;

    public const int DisclosureEvidenceBloomBytes = 32;

    public const int DigestBytes = 32;

    public const int MaxActiveSnapshotRows = 160;

    public const int ActiveSnapshotProbeRows = 161;

    public const int MaxMaintenanceStepsPerClaim = 4;

    public const int MaxDisclosureAggregateRowsPerSubject = 16;

    public const int MaxDisclosureQueueSize = 128;

    public const int MaxDisclosureBatchSize = 16;

    public const int MaxDetailedDisclosureReceipts = 65_536;

    public const int MaxExactDisclosureTail = 64;

    public const int MaxDisclosureReceiptsPerFold = 256;

    public const int MaxDisclosureSubjectsPerFold = 64;

    public const int MaxProviderToolCallIndexes = 64;

    public const int MaxProviderToolNameBytes = 256;

    public const int MaxProviderToolArgumentBytes = 65_536;

    public const int MaxProviderToolBufferedBytesPerAttempt = 262_144;

    public const int MaxSearchQueryBytes = 512;

    public const int MaxSearchQueryTerms = 32;

    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    public const int MaxVersionSources = 64;

    public const int MaxFallbackCandidates = 2_048;

    public const int MaxEndUserIdentityBytes = 256;

    public const int MaxProposedAdmissionRemovals = 32;

    public const int MaxCursorPlaintextBytes = 1_024;

    public const int MaxPreflightPlaintextBytes = 2_048;

    public const int MaxEnvelopeDecodedBytes = 3_072;

    public const int MaxEnvelopeEncodedBytes = 4_096;

    public const int EnvelopeHeaderBytes = 46;
}
