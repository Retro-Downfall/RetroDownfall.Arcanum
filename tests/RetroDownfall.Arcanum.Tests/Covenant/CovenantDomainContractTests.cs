using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantDomainContractTests
{

    [Fact]
    public void Policy_v1_enum_codes_are_immutable()
    {

        Assert.Equal((byte)1, (byte)CovenantScope.Global);
        Assert.Equal((byte)2, (byte)CovenantScope.Campaign);

        Assert.Equal((byte)1, (byte)CovenantLane.Confirmed);
        Assert.Equal((byte)2, (byte)CovenantLane.Proposed);

        Assert.Equal((byte)1, (byte)CovenantOperation.Set);
        Assert.Equal((byte)2, (byte)CovenantOperation.Retire);

        Assert.Equal((byte)1, (byte)CovenantOrigin.Operator);
        Assert.Equal((byte)2, (byte)CovenantOrigin.AgentProposed);
        Assert.Equal((byte)3, (byte)CovenantOrigin.AgentApproved);

        Assert.Equal((byte)1, (byte)CovenantMutationKind.OperatorSet);
        Assert.Equal((byte)2, (byte)CovenantMutationKind.OperatorRetire);
        Assert.Equal((byte)3, (byte)CovenantMutationKind.AgentPropose);
        Assert.Equal((byte)4, (byte)CovenantMutationKind.AgentRetire);

        Assert.Equal((byte)1, (byte)CovenantPlacement.GlobalConfirmed);
        Assert.Equal((byte)2, (byte)CovenantPlacement.CampaignConfirmed);
        Assert.Equal((byte)3, (byte)CovenantPlacement.CampaignProposed);

        Assert.Equal((byte)1, (byte)CovenantPlanDecision.EligibleConfirmed);
        Assert.Equal((byte)2, (byte)CovenantPlanDecision.EligibleProposed);
        Assert.Equal((byte)3, (byte)CovenantPlanDecision.Shadowed);
        Assert.Equal((byte)4, (byte)CovenantPlanDecision.ReviewOnly);
        Assert.Equal((byte)5, (byte)CovenantPlanDecision.Quarantined);
        Assert.Equal((byte)6, (byte)CovenantPlanDecision.Invalid);

        Assert.Equal((byte)1, (byte)CovenantAdmissionDecision.Admitted);
        Assert.Equal((byte)2, (byte)CovenantAdmissionDecision.Pressured);
        Assert.Equal((byte)3, (byte)CovenantAdmissionDecision.RequiredNoFit);

        Assert.Equal((byte)0, (byte)CovenantAuthorizationMode.None);
        Assert.Equal((byte)1, (byte)CovenantAuthorizationMode.ApiMasterKey);
        Assert.Equal((byte)2, (byte)CovenantAuthorizationMode.WardInteractive);
        Assert.Equal((byte)3, (byte)CovenantAuthorizationMode.WardConfiguredAutoApproval);

        Assert.Equal((byte)1, (byte)CovenantMutationOutcome.Applied);
        Assert.Equal((byte)2, (byte)CovenantMutationOutcome.NoChange);

        Assert.Equal((byte)1, (byte)CovenantFinalOutcome.Completed);
        Assert.Equal((byte)2, (byte)CovenantFinalOutcome.Failed);
        Assert.Equal((byte)3, (byte)CovenantFinalOutcome.Cancelled);
        Assert.Equal((byte)4, (byte)CovenantFinalOutcome.Interrupted);

        Assert.Equal((byte)1, (byte)AssistantFinalizationOrigin.Committed);
        Assert.Equal((byte)2, (byte)AssistantFinalizationOrigin.Discarded);
        Assert.Equal((byte)3, (byte)AssistantFinalizationOrigin.CommittedImported);
        Assert.Equal((byte)4, (byte)AssistantFinalizationOrigin.CommittedForked);

        Assert.Equal((byte)1, (byte)CovenantWardDecision.Approved);
        Assert.Equal((byte)2, (byte)CovenantWardDecision.Denied);
        Assert.Equal((byte)3, (byte)CovenantWardDecision.Cancelled);

        Assert.Equal((byte)1, (byte)CovenantProviderRole.System);
        Assert.Equal((byte)2, (byte)CovenantProviderRole.User);
        Assert.Equal((byte)3, (byte)CovenantProviderRole.Assistant);
        Assert.Equal((byte)4, (byte)CovenantProviderRole.Tool);

        Assert.Equal((byte)1, (byte)CovenantProviderDispatchMode.Buffered);
        Assert.Equal((byte)2, (byte)CovenantProviderDispatchMode.Streaming);

        Assert.Equal((byte)1, (byte)CovenantProviderContentPart.Text);
        Assert.Equal((byte)2, (byte)CovenantProviderContentPart.Binary);
        Assert.Equal((byte)3, (byte)CovenantProviderContentPart.ToolCall);
        Assert.Equal((byte)4, (byte)CovenantProviderContentPart.ToolResult);
        Assert.Equal((byte)5, (byte)CovenantProviderContentPart.Json);
        Assert.Equal((byte)6, (byte)CovenantProviderContentPart.Uri);
        Assert.Equal((byte)7, (byte)CovenantProviderContentPart.TextReasoning);

        Assert.Equal((byte)1, (byte)CovenantToolRiskIdentity.Ordinary);
        Assert.Equal((byte)2, (byte)CovenantToolRiskIdentity.ConfiguredForbiddenArt);
        Assert.Equal((byte)3, (byte)CovenantToolRiskIdentity.IntrinsicForbiddenArt);
        Assert.Equal((byte)4, (byte)CovenantToolRiskIdentity.CovenantSensitiveEgress);

        Assert.Equal((byte)0, (byte)ContentSensitivity.None);
        Assert.Equal((byte)1, (byte)ContentSensitivity.CovenantDerived);

        Assert.Equal((byte)1, (byte)GenerationProvenanceMode.Exact);
        Assert.Equal((byte)2, (byte)GenerationProvenanceMode.BloomOverflow);

        Assert.Equal((byte)1, (byte)SensitiveArtifactKind.AssistantEntry);
        Assert.Equal((byte)2, (byte)SensitiveArtifactKind.TurnEvidence);
        Assert.Equal((byte)3, (byte)SensitiveArtifactKind.Summary);
        Assert.Equal((byte)4, (byte)SensitiveArtifactKind.ToolArtifact);
        Assert.Equal((byte)5, (byte)SensitiveArtifactKind.SessionTitle);
        Assert.Equal((byte)6, (byte)SensitiveArtifactKind.Saga);
        Assert.Equal((byte)7, (byte)SensitiveArtifactKind.Lexicon);
        Assert.Equal((byte)8, (byte)SensitiveArtifactKind.Embedding);
        Assert.Equal((byte)9, (byte)SensitiveArtifactKind.SearchProjection);
        Assert.Equal((byte)10, (byte)SensitiveArtifactKind.AuditProjection);
        Assert.Equal((byte)11, (byte)SensitiveArtifactKind.Notification);
        Assert.Equal((byte)12, (byte)SensitiveArtifactKind.ManagedWorkspaceFile);
        Assert.Equal((byte)13, (byte)SensitiveArtifactKind.IdempotencyClaim);

        Assert.Equal((byte)1, (byte)CovenantEgressDestination.Provider);
        Assert.Equal((byte)2, (byte)CovenantEgressDestination.ManagedWorkspaceFile);
        Assert.Equal((byte)3, (byte)CovenantEgressDestination.UnmanagedWorkspaceFile);
        Assert.Equal((byte)4, (byte)CovenantEgressDestination.Process);
        Assert.Equal((byte)5, (byte)CovenantEgressDestination.Network);
        Assert.Equal((byte)6, (byte)CovenantEgressDestination.ExternalMcp);
        Assert.Equal((byte)7, (byte)CovenantEgressDestination.Message);
        Assert.Equal((byte)8, (byte)CovenantEgressDestination.EncryptedBackup);

        Assert.Equal((byte)1, (byte)CovenantDisclosureSubjectKind.Turn);
        Assert.Equal((byte)2, (byte)CovenantDisclosureSubjectKind.Operation);

        Assert.Equal((byte)1, (byte)CovenantDisclosureRevocability.LocallyRevocable);
        Assert.Equal((byte)2, (byte)CovenantDisclosureRevocability.Nonrevocable);

        Assert.Equal((byte)1, (byte)CovenantDisclosureCountKind.Exact);
        Assert.Equal((byte)2, (byte)CovenantDisclosureCountKind.LowerBound);

        Assert.Equal((byte)1, (byte)SessionTurnSurface.Intelligence);
        Assert.Equal((byte)2, (byte)SessionTurnSurface.PromptExecute);
        Assert.Equal((byte)3, (byte)SessionTurnSurface.SpellExecute);

        Assert.Equal((byte)1, (byte)CovenantContextPolicy.Default);
        Assert.Equal((byte)2, (byte)CovenantContextPolicy.None);

        Assert.Equal((byte)1, (byte)CovenantToolPolicyCode.AllTools);
        Assert.Equal((byte)2, (byte)CovenantToolPolicyCode.NoTools);
        Assert.Equal((byte)3, (byte)CovenantToolPolicyCode.ReadOnlyTools);
        Assert.Equal((byte)4, (byte)CovenantToolPolicyCode.NoForbiddenArts);

        Assert.Equal((byte)1, (byte)InvocationAttendance.Attended);
        Assert.Equal((byte)2, (byte)InvocationAttendance.Unattended);

        Assert.Equal((byte)1, (byte)CovenantMaintenanceStep.Summary);
        Assert.Equal((byte)2, (byte)CovenantMaintenanceStep.Title);
        Assert.Equal((byte)3, (byte)CovenantMaintenanceStep.Saga);
        Assert.Equal((byte)4, (byte)CovenantMaintenanceStep.Lexicon);

        Assert.Equal((byte)1, (byte)CovenantMaintenanceCheckpoint.Prepared);
        Assert.Equal((byte)2, (byte)CovenantMaintenanceCheckpoint.Committed);
        Assert.Equal((byte)3, (byte)CovenantMaintenanceCheckpoint.Failed);

        Assert.Equal((byte)1, (byte)SessionTurnClaimState.PendingMaintenance);
        Assert.Equal((byte)2, (byte)SessionTurnClaimState.Begun);
        Assert.Equal((byte)3, (byte)SessionTurnClaimState.Committed);
        Assert.Equal((byte)4, (byte)SessionTurnClaimState.Discarded);
        Assert.Equal((byte)5, (byte)SessionTurnClaimState.Erased);
        Assert.Equal((byte)6, (byte)SessionTurnClaimState.RestoredInterrupted);

        Assert.Equal((byte)1, (byte)BackupDisclosurePhase.SnapshotRead);
        Assert.Equal((byte)2, (byte)BackupDisclosurePhase.EncryptedArchiveWrite);

        Assert.Equal((byte)1, (byte)ProviderToolChoice.Auto);
        Assert.Equal((byte)2, (byte)ProviderToolChoice.None);
        Assert.Equal((byte)3, (byte)ProviderToolChoice.Required);
        Assert.Equal((byte)4, (byte)ProviderToolChoice.Named);

        Assert.Equal((byte)1, (byte)ProviderResponseFormat.Text);
        Assert.Equal((byte)2, (byte)ProviderResponseFormat.JsonObject);
        Assert.Equal((byte)3, (byte)ProviderResponseFormat.JsonSchema);

        Assert.Equal((byte)1, (byte)CovenantReasoningEffort.None);
        Assert.Equal((byte)2, (byte)CovenantReasoningEffort.Minimal);
        Assert.Equal((byte)3, (byte)CovenantReasoningEffort.Low);
        Assert.Equal((byte)4, (byte)CovenantReasoningEffort.Medium);
        Assert.Equal((byte)5, (byte)CovenantReasoningEffort.High);
        Assert.Equal((byte)6, (byte)CovenantReasoningEffort.ExtraHigh);

        Assert.Equal((byte)1, (byte)CovenantReasoningOutput.None);
        Assert.Equal((byte)2, (byte)CovenantReasoningOutput.Summary);
        Assert.Equal((byte)3, (byte)CovenantReasoningOutput.Full);

        Assert.Equal((byte)1, (byte)CovenantReasoningWireDialect.Standard);
        Assert.Equal((byte)2, (byte)CovenantReasoningWireDialect.OpenRouter);
        Assert.Equal((byte)3, (byte)CovenantReasoningWireDialect.TopLevelReasoningBudget);
        Assert.Equal((byte)4, (byte)CovenantReasoningWireDialect.AnthropicThinking);

        Assert.Equal((byte)0, (byte)CovenantTriStateBoolean.Absent);
        Assert.Equal((byte)1, (byte)CovenantTriStateBoolean.False);
        Assert.Equal((byte)2, (byte)CovenantTriStateBoolean.True);

        Assert.Equal((byte)1, (byte)CovenantImageDetail.Auto);
        Assert.Equal((byte)2, (byte)CovenantImageDetail.Low);
        Assert.Equal((byte)3, (byte)CovenantImageDetail.High);

        Assert.Equal((byte)1, (byte)CovenantPromptAttribution.DataHeader);
        Assert.Equal((byte)2, (byte)CovenantPromptAttribution.CovenantProposed);
        Assert.Equal((byte)3, (byte)CovenantPromptAttribution.DataBody);
        Assert.Equal((byte)4, (byte)CovenantPromptAttribution.WorkspaceContext);
        Assert.Equal((byte)5, (byte)CovenantPromptAttribution.CovenantConfirmed);
        Assert.Equal((byte)6, (byte)CovenantPromptAttribution.ContextBody);
        Assert.Equal((byte)7, (byte)CovenantPromptAttribution.SpecialOrUncovered);
        Assert.Equal((byte)8, (byte)CovenantPromptAttribution.Preamble);
        Assert.Equal((byte)9, (byte)CovenantPromptAttribution.Instructions);

        Assert.Equal((byte)1, (byte)CovenantMaterializationContainer.SystemPrompt);
        Assert.Equal((byte)2, (byte)CovenantMaterializationContainer.MessagePart);

        Assert.Equal((byte)1, (byte)CovenantMaterializationOccurrence.Utf16TextRange);
        Assert.Equal((byte)2, (byte)CovenantMaterializationOccurrence.WholeBinaryPart);

        Assert.Equal((byte)1, (byte)CovenantMaterializationSourceRange.WholeSource);
        Assert.Equal((byte)2, (byte)CovenantMaterializationSourceRange.Utf16Range);
        Assert.Equal((byte)3, (byte)CovenantMaterializationSourceRange.ByteRange);

        Assert.Equal((byte)1, (byte)CovenantCursorEndpoint.List);
        Assert.Equal((byte)2, (byte)CovenantCursorEndpoint.FtsQuery);
        Assert.Equal((byte)3, (byte)CovenantCursorEndpoint.FallbackQuery);
        Assert.Equal((byte)4, (byte)CovenantCursorEndpoint.Versions);

        Assert.Equal((byte)1, (byte)CovenantCursorScopeSelection.Global);
        Assert.Equal((byte)2, (byte)CovenantCursorScopeSelection.Campaign);
        Assert.Equal((byte)3, (byte)CovenantCursorScopeSelection.AllScopes);

        Assert.Equal((byte)1, (byte)CovenantLifecycle.Set);
        Assert.Equal((byte)2, (byte)CovenantLifecycle.Retired);
        Assert.Equal((byte)3, (byte)CovenantLifecycle.Any);

        Assert.Equal((byte)1, (byte)CovenantCursorSort.CanonicalHeads);
        Assert.Equal((byte)2, (byte)CovenantCursorSort.FtsRank);
        Assert.Equal((byte)3, (byte)CovenantCursorSort.FallbackHeads);
        Assert.Equal((byte)4, (byte)CovenantCursorSort.VersionDescending);

    }

    [Fact]
    public void Campaign_path_and_session_binding_codes_are_immutable()
    {

        Assert.Equal((byte)1, (byte)CampaignPathIdentityOperation.Register);
        Assert.Equal((byte)2, (byte)CampaignPathIdentityOperation.Update);
        Assert.Equal((byte)3, (byte)CampaignPathIdentityOperation.RepairMoved);
        Assert.Equal((byte)4, (byte)CampaignPathIdentityOperation.Deregister);
        Assert.Equal((byte)5, (byte)CampaignPathIdentityOperation.TakeoverOrphan);

        Assert.Equal((byte)1, (byte)SessionCampaignBindingKind.GlobalOnly);
        Assert.Equal((byte)2, (byte)SessionCampaignBindingKind.Campaign);
        Assert.Equal((byte)3, (byte)SessionCampaignBindingKind.LegacyUnresolved);

    }

    /// <summary>
    /// The status surface reports one rendered-byte ceiling for all three prompt sections, and that
    /// projection is honest only while the three placement ceilings are the same number.
    /// </summary>
    /// <remarks>
    /// Asserted against each other rather than against a literal, so revising the contract to give one
    /// section a ceiling of its own fails here — where the single-field projection lives — instead of
    /// leaving an operator comparing a Campaign Proposed total against a Global bound.
    /// </remarks>
    [Fact]
    public void The_three_placement_ceilings_agree_so_one_reported_ceiling_can_stand_for_all_of_them()
    {

        Assert.Equal(
            CovenantLimits.MaxGlobalConfirmedRenderedBytes,
            CovenantLimits.MaxCampaignConfirmedRenderedBytes);

        Assert.Equal(
            CovenantLimits.MaxGlobalConfirmedRenderedBytes,
            CovenantLimits.MaxCampaignProposedRenderedBytes);

    }

    [Fact]
    public void Hard_limits_match_the_approved_contract()
    {

        Assert.Equal(128, CovenantLimits.MaxKeyCharacters);
        Assert.Equal(2_048, CovenantLimits.MaxAuthoredContentBytes);
        Assert.Equal(4_096, CovenantLimits.MaxGlobalConfirmedRenderedBytes);
        Assert.Equal(64, CovenantLimits.MaxGlobalConfirmedEntries);
        Assert.Equal(4_096, CovenantLimits.MaxCampaignConfirmedRenderedBytes);
        Assert.Equal(64, CovenantLimits.MaxCampaignConfirmedEntries);
        Assert.Equal(4_096, CovenantLimits.MaxCampaignProposedRenderedBytes);
        Assert.Equal(32, CovenantLimits.MaxCampaignProposedEntries);
        Assert.Equal(4, CovenantLimits.MaxStagedMutationsPerTurn);
        Assert.Equal(256, CovenantLimits.MaxStableEntriesPerScope);
        Assert.Equal(8_192, CovenantLimits.MaxVersionsPerScope);
        Assert.Equal(7_936, CovenantLimits.MaxSetVersionsPerScope);
        Assert.Equal(16 * 1_024 * 1_024, CovenantLimits.MaxCanonicalBytesPerScope);
        Assert.Equal(4_096, CovenantLimits.MaxAgentVersionsPerCampaign);
        Assert.Equal(8 * 1_024 * 1_024, CovenantLimits.MaxAgentBytesPerCampaign);
        Assert.Equal(1_024, CovenantLimits.MaxVersionsPerEntryLane);
        Assert.Equal(1_023, CovenantLimits.MaxSetVersionsPerEntryLane);
        Assert.Equal(256, CovenantLimits.MaxAgentProposedVersionsPerEntryLane);
        Assert.Equal(16_640, CovenantLimits.MaxMutationReceiptsPerScope);
        Assert.Equal(16_384, CovenantLimits.MaxOrdinaryMutationReceiptsPerScope);
        Assert.Equal(8_519_680, CovenantLimits.MaxMutationReceiptLogicalBytesPerScope);
        Assert.Equal(8_388_608, CovenantLimits.MaxOrdinaryMutationReceiptLogicalBytesPerScope);
        Assert.Equal(512, CovenantLimits.MutationReceiptLogicalBytes);
        Assert.Equal(64, CovenantLimits.MaxAttachmentSourcesPerAgentMutation);
        Assert.Equal(16_384, CovenantLimits.MaxAttachmentProvenanceRowsPerCampaign);
        Assert.Equal(8 * 1_024 * 1_024, CovenantLimits.MaxAttachmentProvenanceBytesPerCampaign);
        Assert.Equal(65_536, CovenantLimits.MaxPendingSearchOutboxRows);
        Assert.Equal(1_024, CovenantLimits.MaxTurnReceiptsPerSession);
        Assert.Equal(65_536, CovenantLimits.MaxTurnReceiptsInstallationWide);
        Assert.Equal(16_384, CovenantLimits.MaxPublicTurnClaimsPerSession);
        Assert.Equal(1_048_576, CovenantLimits.MaxPublicTurnClaimsInstallationWide);
        Assert.Equal(16_384, CovenantLimits.MaxAssistantFinalizationGuardsPerSession);
        Assert.Equal(1_048_576, CovenantLimits.MaxAssistantFinalizationGuardsInstallationWide);
        Assert.Equal(8, CovenantLimits.MaxExactGenerationIds);
        Assert.Equal(32, CovenantLimits.GenerationBloomBytes);
        Assert.Equal(32, CovenantLimits.DisclosureEvidenceBloomBytes);
        Assert.Equal(32, CovenantLimits.DigestBytes);
        Assert.Equal(160, CovenantLimits.MaxActiveSnapshotRows);
        Assert.Equal(161, CovenantLimits.ActiveSnapshotProbeRows);
        Assert.Equal(4, CovenantLimits.MaxMaintenanceStepsPerClaim);
        Assert.Equal(16, CovenantLimits.MaxDisclosureAggregateRowsPerSubject);
        Assert.Equal(128, CovenantLimits.MaxDisclosureQueueSize);
        Assert.Equal(16, CovenantLimits.MaxDisclosureBatchSize);
        Assert.Equal(65_536, CovenantLimits.MaxDetailedDisclosureReceipts);
        Assert.Equal(64, CovenantLimits.MaxExactDisclosureTail);
        Assert.Equal(256, CovenantLimits.MaxDisclosureReceiptsPerFold);
        Assert.Equal(64, CovenantLimits.MaxDisclosureSubjectsPerFold);
        Assert.Equal(64, CovenantLimits.MaxProviderToolCallIndexes);
        Assert.Equal(256, CovenantLimits.MaxProviderToolNameBytes);
        Assert.Equal(65_536, CovenantLimits.MaxProviderToolArgumentBytes);
        Assert.Equal(262_144, CovenantLimits.MaxProviderToolBufferedBytesPerAttempt);
        Assert.Equal(512, CovenantLimits.MaxSearchQueryBytes);
        Assert.Equal(32, CovenantLimits.MaxSearchQueryTerms);
        Assert.Equal(50, CovenantLimits.DefaultPageSize);
        Assert.Equal(200, CovenantLimits.MaxPageSize);
        Assert.Equal(64, CovenantLimits.MaxVersionSources);
        Assert.Equal(2_048, CovenantLimits.MaxFallbackCandidates);
        Assert.Equal(256, CovenantLimits.MaxEndUserIdentityBytes);
        Assert.Equal(32, CovenantLimits.MaxProposedAdmissionRemovals);
        Assert.Equal(1_024, CovenantLimits.MaxCursorPlaintextBytes);
        Assert.Equal(2_048, CovenantLimits.MaxPreflightPlaintextBytes);
        Assert.Equal(3_072, CovenantLimits.MaxEnvelopeDecodedBytes);
        Assert.Equal(4_096, CovenantLimits.MaxEnvelopeEncodedBytes);
        Assert.Equal(46, CovenantLimits.EnvelopeHeaderBytes);

    }

    [Fact]
    public void Invalid_cross_field_models_are_rejected()
    {

        Guid campaignId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new CovenantKey("Invalid Key"));
        Assert.Throws<ArgumentException>(() => new CovenantDigest(new byte[CovenantLimits.DigestBytes - 1]));
        Assert.Throws<ArgumentException>(() => SessionCampaignBinding.Create(SessionCampaignBindingKind.GlobalOnly, campaignId));
        Assert.Throws<ArgumentException>(() => SessionCampaignBinding.Create(SessionCampaignBindingKind.Campaign, null));
        Assert.Throws<ArgumentException>(() => SessionCampaignBinding.Create(SessionCampaignBindingKind.LegacyUnresolved, campaignId));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.CreateExact([Guid.Empty]));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.CreateBloom(new byte[CovenantLimits.GenerationBloomBytes - 1]));

        SessionCampaignBinding globalBinding = SessionCampaignBinding.GlobalOnly;

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            globalBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 1,
            rootIdentityDigest: new CovenantDigest(new byte[CovenantLimits.DigestBytes])));

        GenerationProvenance emptyProvenance = GenerationProvenance.CreateExact([]);

        Assert.Throws<ArgumentException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            campaignId: null,
            turnId: Guid.NewGuid(),
            artifactRevision: 1,
            new CovenantDigest(new byte[CovenantLimits.DigestBytes]),
            ContentSensitivity.CovenantDerived,
            emptyProvenance,
            new CovenantDigest(new byte[CovenantLimits.DigestBytes]),
            producingEvidenceDigest: null,
            new CovenantDigest(new byte[CovenantLimits.DigestBytes]),
            DateTimeOffset.UtcNow));

    }

    [Fact]
    public void Default_binding_and_context_fail_closed_at_their_public_boundary()
    {

        SessionCampaignBinding binding = default;

        Assert.Throws<InvalidOperationException>(() => _ = binding.Kind);
        Assert.Throws<InvalidOperationException>(() => _ = binding.CampaignId);

        CanonicalCampaignContext context = default;

        Assert.Throws<InvalidOperationException>(() => _ = context.Binding);
        Assert.Throws<InvalidOperationException>(() => _ = context.CampaignId);
        Assert.Throws<InvalidOperationException>(() => _ = context.CampaignAvailabilityGeneration);
        Assert.Throws<InvalidOperationException>(() => _ = context.PathIdentityPolicyVersion);
        Assert.Throws<InvalidOperationException>(() => _ = context.PathIdentityRevision);
        Assert.Throws<InvalidOperationException>(() => _ = context.RootIdentityDigest);
        Assert.Throws<InvalidOperationException>(() => _ = context.IsCampaignBound);

    }

    [Fact]
    public void Canonical_context_rejects_unresolved_or_incomplete_campaign_facts()
    {

        CovenantDigest rootIdentityDigest = new(new byte[CovenantLimits.DigestBytes]);
        SessionCampaignBinding campaignBinding = SessionCampaignBinding.ForCampaign(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            SessionCampaignBinding.LegacyUnresolved,
            campaignAvailabilityGeneration: null,
            pathIdentityPolicyVersion: null,
            pathIdentityRevision: null,
            rootIdentityDigest: null));

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: null,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 1,
            rootIdentityDigest));

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: null,
            pathIdentityRevision: 1,
            rootIdentityDigest));

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest));

        Assert.Throws<ArgumentException>(() => CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 1,
            rootIdentityDigest: null));

    }

    [Fact]
    public void Canonical_context_accepts_campaign_with_or_without_paired_path_facts()
    {
        CovenantDigest rootIdentityDigest = new(Enumerable.Repeat((byte)1, CovenantLimits.DigestBytes).ToArray());
        SessionCampaignBinding campaignBinding = SessionCampaignBinding.ForCampaign(Guid.NewGuid());

        CanonicalCampaignContext withoutPath = CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);
        CanonicalCampaignContext withPath = CanonicalCampaignContext.Create(
            campaignBinding,
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 2,
            rootIdentityDigest);

        Assert.Null(withoutPath.PathIdentityRevision);
        Assert.Null(withoutPath.RootIdentityDigest);
        Assert.Equal(2, withPath.PathIdentityRevision);
        Assert.Equal(rootIdentityDigest, withPath.RootIdentityDigest);
    }

}
