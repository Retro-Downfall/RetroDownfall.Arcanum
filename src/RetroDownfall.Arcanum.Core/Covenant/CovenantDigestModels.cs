using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed record SectionItemDigestInput(CovenantKey NormalizedKey, Guid EntryId, Guid VersionId, ulong LaneRevision, CovenantDigest FragmentDigest);

public sealed record SectionDigestInput(CovenantPlacement Placement, ImmutableArray<SectionItemDigestInput> Items, ImmutableArray<byte> RenderedBytes);

public sealed record MutationRequestDigestInput(CovenantMutationKind MutationKind, Guid MutationId, CovenantScope Scope, Guid? CampaignId, CovenantKey NormalizedKey, CovenantLane Lane, CovenantOperation Operation, ulong ExpectedRevision, bool Reactivation, CovenantOrigin Origin, CovenantDigest? AuthoredDigest, CovenantDigest? FragmentDigest, uint CompilerPolicy, CovenantDigest? BasePlanDigest, CovenantDigest? AdmissionDigest, ImmutableArray<CovenantDigest> ProvenanceDigests);

public sealed record PreflightBodyDigestInput(CovenantDigest RequestDigest, ulong OperatorAuthorityEpoch, Guid DatasetGeneration, ulong ExpectedTargetRevision, ulong NormalizedKeyDependencyEpoch, ulong KeyReclamationEpoch, ulong? CampaignRegistryEpoch, CovenantDigest? CompiledArtifactDigest, CovenantDigest DependentHeadVectorDigest, CovenantDigest EffectDigest, long IssuedAt, long ExpiresAt);

public sealed record AuthorizationDigestInput(CovenantDigest RequestDigest, Guid DatasetGeneration, ulong? OperatorAuthorityEpoch, ulong? NormalizedKeyDependencyEpoch, ulong? KeyReclamationEpoch, ulong? CampaignRegistryEpoch, CovenantDigest? PreflightBodyDigest, CovenantDigest? WardReceiptDigest, CovenantAuthorizationMode Authorization);

public sealed record MutationDigestInput(CovenantDigest RequestDigest, CovenantDigest AuthorizationDigest);

public sealed record SnapshotCandidateDigestInput(ulong SearchDocumentId, Guid EntryId, Guid VersionId, CovenantScope Scope, Guid? CampaignId, CovenantLane Lane, CovenantOperation Operation, CovenantOrigin Origin, ulong Revision, Guid? PredecessorId, uint CompilerPolicy, uint RendererPolicy, CovenantDigest AuthoredDigest, CovenantDigest FragmentDigest, uint ProvenanceCount, CovenantDigest ProvenanceDigest, uint CompiledBytes);

public sealed record SnapshotDigestInput(Guid DatasetGeneration, Guid? CanonicalCampaignId, ulong CanonicalSearchSequence, ImmutableArray<SnapshotCandidateDigestInput> Candidates);

public sealed record PlanDecisionDigestInput(Guid EntryId, Guid VersionId, CovenantPlanDecision Decision, Guid? ShadowingVersionId, CovenantPlacement Placement, CovenantDigest FragmentDigest, uint ByteCost);

public sealed record PlanDigestInput(CovenantDigest SnapshotDigest, uint LinkerPolicy, uint PlacementPolicy, ImmutableArray<PlanDecisionDigestInput> Decisions, CovenantDigest EligibleGlobalConfirmedSectionDigest, CovenantDigest EligibleCampaignConfirmedSectionDigest, CovenantDigest EligibleCampaignProposedSectionDigest);

public sealed record MaterializationOccurrenceDigestInput(CovenantMaterializationContainer Container, uint? MessageIndex, uint? ContentPartIndex, CovenantMaterializationOccurrence Occurrence, uint? Utf16Start, uint Length);

public sealed record MaterializationSourceDigestInput(Guid AttachmentId, Guid AttachmentVersionId, string LogicalKey, CovenantDigest ContentDigest, CovenantMaterializationSourceRange SourceRange, uint? SourceStart, uint? SourceEnd, ImmutableArray<MaterializationOccurrenceDigestInput> Occurrences);

public sealed record MaterializationDigestInput(bool Unprovenanced, ImmutableArray<MaterializationSourceDigestInput> Sources);

public sealed record SensitivityDigestInput(ContentSensitivity Level, GenerationProvenanceMode ProvenanceMode, ImmutableArray<Guid> ExactGenerationIds, ImmutableArray<byte> GenerationBloom);

public sealed record ArtifactLabelDigestInput(SensitiveArtifactKind ArtifactKind, Guid ArtifactId, Guid? SessionId, Guid? CampaignId, Guid? TurnId, ulong ArtifactRevision, CovenantDigest ArtifactContentDigest, CovenantDigest SensitivityDigest, CovenantDigest? ProducingPlanDigest, CovenantDigest? ProducingAdmissionDigest, CovenantDigest? ProducingMaintenanceReceiptDigest);

public sealed record SessionTurnRouteValue
{
    private SessionTurnRouteValue(Guid? promptId, string? spellName)
    {
        PromptId = promptId;
        SpellName = spellName;
    }

    public Guid? PromptId { get; }

    public string? SpellName { get; }

    public static SessionTurnRouteValue ForPrompt(Guid promptId) =>
        new(promptId, null);

    public static SessionTurnRouteValue ForSpell(string spellName) =>
        new(null, spellName);
}

public sealed record SessionTurnRequestDigestInput(SessionTurnSurface Surface, CovenantProviderDispatchMode DispatchMode, Guid ClientTurnId, Guid? RequestedSessionId, CovenantContextPolicy ContextPolicy, SessionTurnRouteValue? Route, CovenantDigest AcceptedBodyDigest, Guid? ExplicitCampaignId, CovenantDigest? OpenedWorkingDirectoryIdentityDigest);

public sealed record SessionTurnExecutionCampaignContextDigestInput(Guid CampaignId, long AvailabilityGeneration);

public sealed record SessionTurnExecutionPathDigestInput(long PathRevision, CovenantDigest RootIdentityDigest);

public sealed record SessionTurnExecutionDigestInput(CovenantDigest SessionTurnRequestDigest, Guid ResolvedSessionId, SessionCampaignBindingKind BindingKind, SessionTurnExecutionCampaignContextDigestInput? CampaignContext, SessionTurnExecutionPathDigestInput? Path, long PreRequestHistoryWatermark, Guid? RouteVersionId, long ProviderConfigurationGeneration, string ProviderIdentity, string ModelIdentity, CovenantDigest ProviderOptionsDigest, ImmutableArray<Guid> ResolvedAttachmentVersionIds, CovenantToolPolicyCode ToolPolicy, bool DisableMcpTools, bool DisableAllTools, InvocationAttendance Attendance);

public sealed record ProviderOptionsDigestInput(ulong? MaxOutputTokens, double? Temperature, double? TopP, double? FrequencyPenalty, double? PresencePenalty, long? Seed, string? EndUserIdentity, ImmutableArray<string> Stop, ProviderToolChoice ToolChoice, string? NamedTool, CovenantTriStateBoolean ParallelToolCalls, ProviderResponseFormat ResponseFormat, string? JsonSchemaName, string? JsonSchemaDescription, CovenantDigest? CanonicalJsonSchemaDigest, CovenantTriStateBoolean JsonSchemaStrict, CovenantReasoningEffort? ReasoningEffort, uint? ReasoningBudget, CovenantReasoningOutput? ReasoningOutput, CovenantReasoningWireDialect ReasoningWireDialect, ImmutableArray<FrozenLogitBias> LogitBias);

public sealed record ProviderCallDigestInput(string ProviderIdentity, string ModelIdentity, CovenantProviderDispatchMode DispatchMode, string TokenizerProfile, ulong ContextWindowIdentity, ulong CompressionGeneration, CovenantDigest SensitivityDigest, CovenantDigest ProviderOptionsDigest, ImmutableArray<byte> SystemPromptBytes, ImmutableArray<ProviderPromptSpanDigestInput> PromptSpans, CovenantDigest MaterializationDigest, ImmutableArray<ProviderMessageDigestInput> Messages, ImmutableArray<ProviderToolDefinitionDigestInput> ToolDefinitions, CovenantDigest? StructuredOutputSchemaDigest);

public sealed record AdmissionCandidateDigestInput(Guid EntryId, Guid VersionId, CovenantAdmissionDecision Decision, ulong EstimatedTokens);

public sealed record AdmissionDigestInput(CovenantDigest PlanDigest, ulong GlobalAttemptOrdinal, Guid BranchId, ulong BranchOrdinal, CovenantDigest? ParentAdmissionDigest, CovenantDigest ProviderCallDigest, CovenantDigest MaterializationDigest, CovenantDigest SensitivityDigest, ulong AvailableTokenBudget, ImmutableArray<AdmissionCandidateDigestInput> EligibleCandidates, CovenantDigest AdmittedGlobalConfirmedSectionDigest, CovenantDigest AdmittedCampaignConfirmedSectionDigest, CovenantDigest AdmittedCampaignProposedSectionDigest);

public sealed record WardEvidenceDigestInput(CovenantDigest ToolNameDigest, CovenantDigest FinalArgumentDigest, CovenantToolRiskIdentity EffectiveRisk, CovenantDigest SensitivityDigest, CovenantEgressDestination Destination, CovenantDigest OpaqueDestinationIdentityDigest, ulong OperatorAuthorityEpoch, CovenantWardDecision Decision);

public sealed record ProviderDispatchEffectDigestInput(Guid TurnSubjectId, ulong PhysicalProviderAttemptOrdinal, CovenantDigest AdmissionDigest, CovenantDigest ProviderCallDigest, CovenantDigest ProviderDestinationIdentityDigest);

public sealed record MaintenanceDispatchEffectDigestInput(Guid PendingClaimSubjectId, CovenantMaintenanceStep MaintenanceStep, ulong PhysicalProviderAttemptOrdinal, CovenantDigest ProviderCallDigest, CovenantDigest ProviderDestinationIdentityDigest);

public sealed record ToolEgressEffectDigestInput(Guid TurnSubjectId, ulong PhysicalEffectAttemptOrdinal, CovenantDigest ProducingAdmissionDigest, CovenantDigest CapabilityNonceDigest, string ToolCallId, CovenantDigest FrozenToolNameDigest, CovenantDigest CanonicalArgumentDigest, CovenantEgressDestination Destination, CovenantDigest OpaqueDestinationIdentityDigest);

public sealed record ManagedFileEffectDigestInput(Guid TurnSubjectId, ulong PhysicalEffectAttemptOrdinal, string ToolCallId, CovenantDigest NoFollowTargetCapabilityDigest, CovenantDigest ContentDigest);

public sealed record BackupDisclosureEffectDigestInput(Guid BackupOperationId, ulong PhysicalPhaseAttemptOrdinal, Guid BackupIdentity, CovenantDigest OpaqueDestinationIdentityDigest, BackupDisclosurePhase Phase);

public sealed record ExternalDisclosureDigestInput(Guid OriginInstallationId, CovenantDisclosureSubjectKind SubjectKind, Guid SubjectId, CovenantDigest EffectIdentityDigest, ulong AllocatedSubjectOrdinal, CovenantEgressDestination Destination, CovenantDisclosureRevocability Revocability, CovenantDigest OpaqueDestinationIdentityDigest, CovenantDigest SensitivityDigest, CovenantDigest? WardEvidenceDigest, CovenantDigest? AdmissionDigest, CovenantDigest? BackupEvidenceDigest, long Timestamp);

public sealed record ExternalDisclosureStateDigestInput(CovenantEgressDestination Destination, CovenantDisclosureRevocability Revocability, CovenantDisclosureCountKind CountKind, bool EverOccurred, ulong Count, long MaximumTimestamp, ImmutableArray<byte> EvidenceBloom);

public sealed record CampaignPathApplyRequestDigestInput(Guid OperationId, Guid CampaignId, CampaignPathIdentityOperation Operation, CovenantDigest EffectDigest);

public sealed record SessionBindingApplyRequestDigestInput(Guid OperationId, Guid SessionId, SessionCampaignBindingKind BindingKind, Guid? CampaignId, CovenantDigest PriorBindingRowDigest, CovenantDigest EffectDigest);

public sealed record FamilyReinitializeApplyRequestDigestInput(Guid OperationId, CovenantDigest InspectedCatalogFingerprint, CovenantDigest DatabaseFileIdentityDigest, CovenantDigest EffectDigest);

public sealed record FinalReceiptDigestInput(CovenantDigest SnapshotDigest, CovenantDigest PlanDigest, ulong DispatchedAdmissionCount, CovenantDigest AttemptChainDigest, Guid CommittedBranchId, ulong CommittedBranchOrdinal, CovenantDigest CommittedLineageHeadDigest, CovenantDigest CommittedBranchChainDigest, CovenantDigest FinalSensitivityDigest, ulong ExternalDisclosureCount, CovenantDigest DisclosureChainDigest, ulong ConfirmedTokens, ulong ProposedTokens, uint MutationCount, CovenantFinalOutcome FinalOutcome);

public sealed record TurnAggregateDigestInput(CovenantDigest FinalReceiptDigest, ulong AttemptedAdmissionCount, CovenantDigest AttemptChainDigest, Guid CommittedBranchId, CovenantDigest CommittedLineageHeadDigest, CovenantDigest FinalSensitivityDigest, ulong ExternalDisclosureCount, CovenantDigest DisclosureChainDigest, ulong ConfirmedTokens, ulong ProposedTokens, uint MutationCount, CovenantFinalOutcome FinalOutcome);

public sealed record CursorFilterDigestInput(CovenantCursorEndpoint Endpoint, CovenantCursorScopeSelection ScopeSelection, Guid? CampaignId, Guid? EvaluationCampaignId, CovenantLane? Lane, CovenantLifecycle Lifecycle, CovenantDigest? QueryDigest, uint PageSize, CovenantCursorSort SortPolicy);
