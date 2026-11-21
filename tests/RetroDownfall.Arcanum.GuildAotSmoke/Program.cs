using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

return GuildAotSmoke.Run();

internal static class GuildAotSmoke
{
    private static readonly Guid Id = Guid.Parse("a1111111-1111-4111-8111-111111111111");

    private static readonly DateTimeOffset Instant = new(2026, 11, 5, 12, 30, 0, TimeSpan.Zero);

    public static int Run()
    {
        Exercise(SampleAcquireWorkspaceLeaseRequest(), GuildJsonContext.Default.AcquireWorkspaceLeaseRequest);

        Exercise(SampleAdmitCohortMemberRequest(), GuildJsonContext.Default.AdmitCohortMemberRequest);

        Exercise(SampleAppendSharedNoteRequest(), GuildJsonContext.Default.AppendSharedNoteRequest);

        Exercise(SampleAwardWorkRequest(), GuildJsonContext.Default.AwardWorkRequest);

        Exercise(SampleCohortContributionDto(), GuildJsonContext.Default.CohortContributionDto);

        Exercise(SampleCohortContributionPageDto(), GuildJsonContext.Default.CohortContributionPageDto);

        Exercise(SampleCohortConvergenceDto(), GuildJsonContext.Default.CohortConvergenceDto);

        Exercise(SampleCohortDto(), GuildJsonContext.Default.CohortDto);

        Exercise(SampleCohortMemberQuestDto(), GuildJsonContext.Default.CohortMemberQuestDto);

        Exercise(SampleCohortMemberQuestPageDto(), GuildJsonContext.Default.CohortMemberQuestPageDto);

        Exercise(SampleCohortMemberDto(), GuildJsonContext.Default.CohortMemberDto);

        Exercise(SampleCohortMemberPageDto(), GuildJsonContext.Default.CohortMemberPageDto);

        Exercise(SampleCohortPageDto(), GuildJsonContext.Default.CohortPageDto);

        Exercise(SampleCreateCohortRequest(), GuildJsonContext.Default.CreateCohortRequest);

        Exercise(SampleGuildActionRequest(), GuildJsonContext.Default.GuildActionRequest);

        Exercise(SampleGuildAdmissionReceipt(), GuildJsonContext.Default.GuildAdmissionReceipt);

        Exercise(SampleGuildAdmissionRequest(), GuildJsonContext.Default.GuildAdmissionRequest);

        Exercise(SampleGuildAgentCapabilitiesDto(), GuildJsonContext.Default.GuildAgentCapabilitiesDto);

        Exercise(SampleGuildAgentCard(), GuildJsonContext.Default.GuildAgentCard);

        Exercise(SampleGuildAgentCardDto(), GuildJsonContext.Default.GuildAgentCardDto);

        Exercise(SampleGuildAgentCardPageDto(), GuildJsonContext.Default.GuildAgentCardPageDto);

        Exercise(SampleGuildAgentSkillDto(), GuildJsonContext.Default.GuildAgentSkillDto);

        Exercise(SampleGuildAgentSourceDto(), GuildJsonContext.Default.GuildAgentSourceDto);

        Exercise(SampleGuildBudgetGrant(), GuildJsonContext.Default.GuildBudgetGrant);

        Exercise(new GuildUsdBudget(GuildBudgetMode.Finite, decimal.MaxValue), GuildJsonContext.Default.GuildUsdBudget);

        Exercise(new GuildUsdBudget(GuildBudgetMode.Finite, 0.0000000000000000000000000001m), GuildJsonContext.Default.GuildUsdBudget);

        foreach (string amount in new[] { "0.00000000000000000000000000001", "1.00000000000000000000000000001", "79228162514264337593543950335.1" })
        {
            try
            {
                _ = JsonSerializer.Deserialize("{\"mode\":\"finite\",\"value\":" + amount + "}", GuildJsonContext.Default.GuildUsdBudget);
            }
            catch (JsonException)
            {
                continue;
            }

            throw new InvalidOperationException("Unrepresentable Guild USD authority was rounded by Native AOT JSON.");
        }

        Exercise(SampleGuildCancelQuestRequest(), GuildJsonContext.Default.GuildCancelQuestRequest);

        Exercise(SampleGuildCancellationDto(), GuildJsonContext.Default.GuildCancellationDto);

        Exercise(SampleGuildChangeEventDto(), GuildJsonContext.Default.GuildChangeEventDto);

        Exercise(SampleGuildCircleAcknowledgeRequest(), GuildJsonContext.Default.GuildCircleAcknowledgeRequest);

        Exercise(SampleGuildCircleAcknowledgement(), GuildJsonContext.Default.GuildCircleAcknowledgement);

        Exercise(SampleGuildCircleAddress(), GuildJsonContext.Default.GuildCircleAddress);

        Exercise(SampleGuildCircleInboxItem(), GuildJsonContext.Default.GuildCircleInboxItem);

        Exercise(SampleGuildCircleInboxItem() with { Envelope = null, DeliveryState = GuildDeliveryState.Withheld,
            ReasonCode = "OwnerUnavailable", EnvelopeId = Id }, GuildJsonContext.Default.GuildCircleInboxItem);

        Exercise(SampleGuildCircleInboxPage(), GuildJsonContext.Default.GuildCircleInboxPage);

        Exercise(SampleGuildCircleInboxQuery(), GuildJsonContext.Default.GuildCircleInboxQuery);

        Exercise(SampleGuildCohortContributionRequest(), GuildJsonContext.Default.GuildCohortContributionRequest);

        Exercise(SampleGuildCohortSnapshotDto(), GuildJsonContext.Default.GuildCohortSnapshotDto);

        Exercise(SampleGuildQuestDto(), GuildJsonContext.Default.GuildQuestDto);

        Exercise(SampleGuildQuestDto() with { State = QuestState.InputRequired, AwaitingInput = new(Id, Id) }, GuildJsonContext.Default.GuildQuestDto);

        Exercise(new GuildQuestInputDto(Id, 17L, new(Id, Id), "Choose an option"), GuildJsonContext.Default.GuildQuestInputDto);

        Exercise(SampleGuildQuestPageDto(), GuildJsonContext.Default.GuildQuestPageDto);

        Exercise(new GuildQuestInputEvidence(Id, Id), GuildJsonContext.Default.GuildQuestInputEvidence);

        Exercise(new GuildQuestProjectionEvidence(Id, Id, QuestState.Working, GuildWaitReason.Input,
            new(Id, Id), SampleGuildResultReceipt(), true, true, true, true, true), GuildJsonContext.Default.GuildQuestProjectionEvidence);

        Exercise(new GuildQuestProjectedState(QuestState.Working, GuildWaitReason.Reconciliation,
            true, false, true, "Guild.ReconciliationRequired"), GuildJsonContext.Default.GuildQuestProjectedState);

        Exercise(SampleGuildCommitResultRequest(), GuildJsonContext.Default.GuildCommitResultRequest);

        Exercise(SampleGuildContentDto(), GuildJsonContext.Default.GuildContentDto);

        Exercise(new GuildContentResolutionDto([SampleGuildDataRef()]), GuildJsonContext.Default.GuildContentResolutionDto);

        Exercise(new GuildResolveContentRequest("a1111111"), GuildJsonContext.Default.GuildResolveContentRequest);

        Exercise(SampleGuildContinueRequest(), GuildJsonContext.Default.GuildContinueRequest);

        Exercise(SampleGuildConvergencePolicy(), GuildJsonContext.Default.GuildConvergencePolicy);

        Exercise(SampleGuildDataRef(), GuildJsonContext.Default.GuildDataRef);

        Exercise(SampleGuildDispatchObservation(), GuildJsonContext.Default.GuildDispatchObservation);

        Exercise(SampleGuildDispatchRequest(), GuildJsonContext.Default.GuildDispatchRequest);

        Exercise(SampleGuildEnvelope(), GuildJsonContext.Default.GuildEnvelope);

        Exercise(SampleGuildEventPage(), GuildJsonContext.Default.GuildEventPage);

        Exercise(SampleGuildEventQuery(), GuildJsonContext.Default.GuildEventQuery);

        Exercise(SampleGuildGrant(), GuildJsonContext.Default.GuildGrant);

        Exercise(SampleGuildIssuanceIdentity(), GuildJsonContext.Default.GuildIssuanceIdentity);

        Exercise(SampleGuildIssueChildData(), GuildJsonContext.Default.GuildIssueChildData);

        Exercise(SampleGuildIssueChildRequest(), GuildJsonContext.Default.GuildIssueChildRequest);

        Exercise(SampleGuildIssueRootRequest(), GuildJsonContext.Default.GuildIssueRootRequest);

        Exercise(SampleGuildListAgentsQuery(), GuildJsonContext.Default.GuildListAgentsQuery);

        Exercise(SampleGuildListQuestsQuery(), GuildJsonContext.Default.GuildListQuestsQuery);

        Exercise(SampleGuildMemoryGrant(), GuildJsonContext.Default.GuildMemoryGrant);

        Exercise(SampleGuildMemoryScope(), GuildJsonContext.Default.GuildMemoryScope);

        Exercise(SampleGuildMessageReceipt(), GuildJsonContext.Default.GuildMessageReceipt);

        GuildPortalMessageRequest peerMessage = new("task", "context", Id, Id, 1, "text/plain", "bounded peer message", "");

        peerMessage = peerMessage with { IntentDigest = GuildPortalMessageProtocol.ComputeIntentDigest(peerMessage).Value };

        Exercise(peerMessage, GuildJsonContext.Default.GuildPortalMessageRequest);

        Exercise(peerMessage.ReceiptQuery(), GuildJsonContext.Default.GuildPortalMessageReceiptQuery);

        Exercise(new GuildPortalMessageDeliveryReceipt(peerMessage.TaskId, peerMessage.ContextId, peerMessage.MessageId,
            peerMessage.IntentDigest, Id, GuildDeliveryState.Pending, 1, null), GuildJsonContext.Default.GuildPortalMessageDeliveryReceipt);

        GuildPortalPeerProducer peerProducer = new("peer", peerMessage.IntentDigest, Id, Id);

        Exercise(peerProducer, GuildJsonContext.Default.GuildPortalPeerProducer);

        Exercise(new GuildPortalDeliveryEvidence(Id, Id, Id, GuildDeliveryState.Pending,
            GuildPortalTransportEvidence.PeerAccepted, Id), GuildJsonContext.Default.GuildPortalDeliveryEvidence);

        RoundTrip(new GuildProducerIdentity(GuildProducerKind.A2APeer, null, null, null, null, null, null, null, peerProducer),
            GuildJsonContext.Default.GuildProducerIdentity);

        Exercise(SampleGuildMutationIdentity(), GuildJsonContext.Default.GuildMutationIdentity);

        Exercise(SampleGuildMutationPlan(), GuildJsonContext.Default.GuildMutationPlan);

        Exercise(SampleGuildMutationReceiptDto(), GuildJsonContext.Default.GuildMutationReceiptDto);

        Exercise(SampleGuildPageQuery(), GuildJsonContext.Default.GuildPageQuery);

        Exercise(SampleGuildPathScope(), GuildJsonContext.Default.GuildPathScope);

        Exercise(SampleGuildProducerIdentity(), GuildJsonContext.Default.GuildProducerIdentity);

        Exercise(SampleGuildProducerStreamKey(), GuildJsonContext.Default.GuildProducerStreamKey);

        Exercise(SampleGuildPublishDedupeKey(), GuildJsonContext.Default.GuildPublishDedupeKey);

        Exercise(SampleGuildPublishRequest(), GuildJsonContext.Default.GuildPublishRequest);

        Exercise(SampleGuildRefreshAgentsRequest(), GuildJsonContext.Default.GuildRefreshAgentsRequest);

        Exercise(SampleGuildRefreshDiagnosticDto(), GuildJsonContext.Default.GuildRefreshDiagnosticDto);

        Exercise(SampleGuildRefreshDiagnosticPageDto(), GuildJsonContext.Default.GuildRefreshDiagnosticPageDto);

        Exercise(SampleGuildRefreshReceipt(), GuildJsonContext.Default.GuildRefreshReceipt);

        Exercise(SampleGuildRemoteStateProjection(), GuildJsonContext.Default.GuildRemoteStateProjection);

        Exercise(SampleGuildRemoteTransportDescriptor(), GuildJsonContext.Default.GuildRemoteTransportDescriptor);

        Exercise(SampleGuildRemoteInterfaceDescriptor(), GuildJsonContext.Default.GuildRemoteInterfaceDescriptor);

        Exercise(SampleGuildRemoteInvocationOptions(), GuildJsonContext.Default.GuildRemoteInvocationOptions);

        Exercise(new GuildResolveRemoteAgentRequest("https://peer.example/agent-card"), GuildJsonContext.Default.GuildResolveRemoteAgentRequest);

        A2AInboundPeerSettings peerSettings = new()
        {
            PeerId = "sample-peer",
            CredentialEnvironmentVariable = "ARCANUM_SAMPLE_PEER_KEY",
            CampaignId = null,
            Grant = new()
            {
                Workspaces = [],
                Tools = [],
                Memory = [],
                Budget = new() { Usd = new() { Mode = GuildBudgetMode.Unlimited }, Tokens = new() { Mode = GuildBudgetMode.Unlimited } },
                Delegation = [],
                ExpiresAtUtc = null,
            },
        };

        RoundTrip(peerSettings, ConfigurationJsonContext.Default.A2AInboundPeerSettings);

        RoundTrip(new[] { peerSettings }, ConfigurationJsonContext.Default.A2AInboundPeerSettingsArray);

        Exercise(SampleGuildRequestedGrant(), GuildJsonContext.Default.GuildRequestedGrant);

        Exercise(SampleGuildResultApplicationReceipt(), GuildJsonContext.Default.GuildResultApplicationReceipt);

        Exercise(SampleGuildResultApplicationRequest(), GuildJsonContext.Default.GuildResultApplicationRequest);

        Exercise(SampleGuildResultContract(), GuildJsonContext.Default.GuildResultContract);

        Exercise(SampleGuildResultReceipt(), GuildJsonContext.Default.GuildResultReceipt);

        Exercise(SampleGuildResyncEventDto(), GuildJsonContext.Default.GuildResyncEventDto);

        Exercise(SampleGuildRevisionRequest(), GuildJsonContext.Default.GuildRevisionRequest);

        Exercise(SampleGuildRevokeSignetRequest(), GuildJsonContext.Default.GuildRevokeSignetRequest);

        Exercise(SampleGuildSendOperatorMessageRequest(), GuildJsonContext.Default.GuildSendOperatorMessageRequest);

        Exercise(SampleGuildSignetBinding(), GuildJsonContext.Default.GuildSignetBinding);

        Exercise(SampleGuildSignetStatus(), GuildJsonContext.Default.GuildSignetStatus);

        Exercise(SampleGuildSpendDto(), GuildJsonContext.Default.GuildSpendDto);

        Exercise(SampleGuildStatusDto(), GuildJsonContext.Default.GuildStatusDto);

        Exercise(SampleGuildTerminalEventDto(), GuildJsonContext.Default.GuildTerminalEventDto);

        Exercise(SampleGuildTokenBudget(), GuildJsonContext.Default.GuildTokenBudget);

        Exercise(SampleGuildToolGrant(), GuildJsonContext.Default.GuildToolGrant);

        Exercise(SampleGuildTreeSnapshotDto(), GuildJsonContext.Default.GuildTreeSnapshotDto);

        Exercise(SampleGuildUsdBudget(), GuildJsonContext.Default.GuildUsdBudget);

        Exercise(SampleGuildWorkspaceGrant(), GuildJsonContext.Default.GuildWorkspaceGrant);

        Exercise(SamplePostWorkOfferRequest(), GuildJsonContext.Default.PostWorkOfferRequest);

        Exercise(SampleReleaseWorkspaceLeaseRequest(), GuildJsonContext.Default.ReleaseWorkspaceLeaseRequest);

        Exercise(SampleRenewWorkspaceLeaseRequest(), GuildJsonContext.Default.RenewWorkspaceLeaseRequest);

        Exercise(SampleSharedNoteDto(), GuildJsonContext.Default.SharedNoteDto);

        Exercise(new GuildCohortProgress("note.appended", Id, Id, 17L), GuildJsonContext.Default.GuildCohortProgress);

        Exercise(SampleSharedNotePageDto(), GuildJsonContext.Default.SharedNotePageDto);

        Exercise(SampleSignetRevocationDto(), GuildJsonContext.Default.SignetRevocationDto);

        Exercise(SampleSubmitBidRequest(), GuildJsonContext.Default.SubmitBidRequest);

        Exercise(SampleWorkAwardDto(), GuildJsonContext.Default.WorkAwardDto);

        Exercise(SampleWorkBidDto(), GuildJsonContext.Default.WorkBidDto);

        Exercise(SampleWorkBidPageDto(), GuildJsonContext.Default.WorkBidPageDto);

        Exercise(SampleWorkOfferDto(), GuildJsonContext.Default.WorkOfferDto);

        Exercise(SampleWorkOfferPageDto(), GuildJsonContext.Default.WorkOfferPageDto);

        Exercise(SampleWorkspaceLeaseDto(), GuildJsonContext.Default.WorkspaceLeaseDto);

        Exercise(SampleWorkspaceLeasePageDto(), GuildJsonContext.Default.WorkspaceLeasePageDto);

        Exercise(QuestState.Submitted, GuildJsonContext.Default.QuestState);

        Exercise(GuildAccountingState.Settled, GuildJsonContext.Default.GuildAccountingState);

        Exercise(GuildAgentSourceKind.DeclaredGlobal, GuildJsonContext.Default.GuildAgentSourceKind);

        Exercise(GuildAuthorityAction.Issue, GuildJsonContext.Default.GuildAuthorityAction);

        Exercise(GuildAuthorityKind.Operator, GuildJsonContext.Default.GuildAuthorityKind);

        Exercise(GuildAuthorityKind.LegacyUnknown, GuildJsonContext.Default.GuildAuthorityKind);

        RoundTrip(new ApprenticeCheckpoint { GoalAuthority = GuildAuthorityKind.Operator, OperatorGuidance = "operator input" },
            ArcanumCoreJsonContext.Default.ApprenticeCheckpoint);

        RoundTrip(new ApprenticeCheckpoint { GoalAuthority = GuildAuthorityKind.Parent, ParentGuidance = "parent input" },
            ArcanumCoreJsonContext.Default.ApprenticeCheckpoint);

        RoundTrip(new ApprenticeCheckpoint { DmGuidance = "legacy input" }, ArcanumCoreJsonContext.Default.ApprenticeCheckpoint);

        Exercise(GuildBudgetMode.Finite, GuildJsonContext.Default.GuildBudgetMode);

        Exercise(GuildCallerKind.Operator, GuildJsonContext.Default.GuildCallerKind);

        Exercise(GuildCancellationReason.OperatorRequested, GuildJsonContext.Default.GuildCancellationReason);

        Exercise(GuildCircleAddressKind.Parent, GuildJsonContext.Default.GuildCircleAddressKind);

        Exercise(GuildCohortMemberState.Active, GuildJsonContext.Default.GuildCohortMemberState);

        Exercise(GuildCohortState.Open, GuildJsonContext.Default.GuildCohortState);

        Exercise(GuildContentKind.AgentCard, GuildJsonContext.Default.GuildContentKind);

        Exercise(GuildContributionOutcome.Accepted, GuildJsonContext.Default.GuildContributionOutcome);

        Exercise(GuildConvergenceDecision.Success, GuildJsonContext.Default.GuildConvergenceDecision);

        Exercise(GuildConvergenceKind.All, GuildJsonContext.Default.GuildConvergenceKind);

        Exercise(GuildDelegationKind.Local, GuildJsonContext.Default.GuildDelegationKind);

        Exercise(GuildDeliveryState.None, GuildJsonContext.Default.GuildDeliveryState);

        Exercise(GuildDispatchEvidence.Prepared, GuildJsonContext.Default.GuildDispatchEvidence);

        Exercise(GuildEnvelopeKind.QuestIssued, GuildJsonContext.Default.GuildEnvelopeKind);

        Exercise(GuildEventKind.QuestChanged, GuildJsonContext.Default.GuildEventKind);

        Exercise(GuildMemberAssociationKind.Primary, GuildJsonContext.Default.GuildMemberAssociationKind);

        Exercise(GuildMemoryOperation.Read, GuildJsonContext.Default.GuildMemoryOperation);

        Exercise(GuildMemoryScopeKind.Global, GuildJsonContext.Default.GuildMemoryScopeKind);

        Exercise(GuildMemoryStore.Transcript, GuildJsonContext.Default.GuildMemoryStore);

        Exercise(GuildMessageKind.Message, GuildJsonContext.Default.GuildMessageKind);

        Exercise(GuildNoteKind.Observation, GuildJsonContext.Default.GuildNoteKind);

        Exercise(GuildPathScopeKind.File, GuildJsonContext.Default.GuildPathScopeKind);

        Exercise(GuildProducerKind.Run, GuildJsonContext.Default.GuildProducerKind);

        Exercise(GuildReceiptSourceKind.QuestIssue, GuildJsonContext.Default.GuildReceiptSourceKind);

        Exercise(GuildToolSourceKind.BuiltIn, GuildJsonContext.Default.GuildToolSourceKind);

        Exercise(GuildWaitReason.Input, GuildJsonContext.Default.GuildWaitReason);

        Exercise(GuildWorkAwardState.Committed, GuildJsonContext.Default.GuildWorkAwardState);

        Exercise(GuildWorkBidState.Submitted, GuildJsonContext.Default.GuildWorkBidState);

        Exercise(GuildWorkOfferState.Open, GuildJsonContext.Default.GuildWorkOfferState);

        Exercise(GuildWorkspaceAccess.Read, GuildJsonContext.Default.GuildWorkspaceAccess);

        GuildWorkspaceLeaseToolReceipt leaseObservation = new(Id, Id, GuildWorkspaceLeaseOwnerKind.Quest, Id,
            GuildWorkspaceLeaseState.Held, 3, 4, Instant, SampleGuildMutationReceiptDto());

        if (RoundTrip(leaseObservation, GuildWorkspaceLeaseToolJsonContext.Default.GuildWorkspaceLeaseToolReceipt) != leaseObservation)
            throw new InvalidOperationException("The model-facing workspace lease receipt changed during serialization.");

        Exercise(GuildWorkspaceLeaseOwnerKind.Quest, GuildJsonContext.Default.GuildWorkspaceLeaseOwnerKind);

        Exercise(GuildWorkspaceLeaseState.Held, GuildJsonContext.Default.GuildWorkspaceLeaseState);

        ExerciseLiteralsAndBoundaries();

        ExerciseSpendReconciliation();

        ExerciseWorkspaceLeaseInfrastructure();

        ExerciseWorkspaceLeaseKernelAsync().GetAwaiter().GetResult();

        byte[] checkpoint = GuildQuestCheckpoint.Encode(Id);

        var restoredCheckpoint = GuildQuestCheckpoint.Decode(1, checkpoint);

        Require(restoredCheckpoint.IsSuccess && restoredCheckpoint.Value.QuestId == Id
            && restoredCheckpoint.Value.Phase == "admitted", "Quest checkpoints require generated, content-free native round trips.");

        RoundTrip(new GuildCohortSettlementNode
        {
            AccountId = Id, RootAccountId = Id, OwnerId = Id, Kind = GuildBudgetOwnerKind.Cohort,
            Revision = 1, KnownUsd = "0.1234567890123456789012345678", KnownTokens = 37,
            Phase = 1, LastEffect = Id, LastChild = Id, PendingMembers = 2, PendingEffects = 1,
            HoldUsd = null, HoldTokens = null, UnknownUsd = true, UnknownTokens = true,
        }, GuildCohortSettlementJsonContext.Default.GuildCohortSettlementNode);

        RoundTrip(new GuildStoredAgentCardPayload(SampleGuildAgentCard(), "Bounded guidance: λ", ["read_file"]),
            GuildStorageJsonContext.Default.GuildStoredAgentCardPayload);

        RoundTrip(new GuildQuestTreePage(new(1, 1, 0, 0, 1),
            [new(Id, Id, null, 1, 1, QuestState.Working, null, false, false, false, GuildAccountingState.Pending, false)],
            null, 1), GuildTreeJsonContext.Default.GuildQuestTreePage);

        var parsed = GuildDeclaredAgentParser.Parse("---\nname: Auditor\ndescription: Reviews source\nskills: [review]\n---\nKeep evidence.");

        Require(parsed.IsSuccess && parsed.Value.Name == "Auditor" && parsed.Value.Skills.SequenceEqual(["review"])
            && parsed.Value.Guidance == "Keep evidence.", "The declared Card parser failed under Native AOT.");

        Require(GuildDeclaredAgentParser.Parse("---\nname: Auditor\ndescription: Reviews source\nunknown: value\n---\n").IsFailure,
            "The declared Card parser accepted an unknown key under Native AOT.");

        Console.WriteLine("Guild Native AOT graph smoke passed.");

        return 0;
    }

    private static void ExerciseSpendReconciliation()
    {
        Guid questId = Id;

        Guid effectId = Guid.Parse("b2222222-2222-4222-8222-222222222222");

        Guid receiptId = Guid.Parse("c3333333-3333-4333-8333-333333333333");

        Guid requestId = Guid.Parse("d4444444-4444-4444-8444-444444444444");

        Guid operationId = Guid.Parse("e5555555-5555-4555-8555-555555555555");

        Guid sourceId = Guid.Parse("f6666666-6666-4666-8666-666666666666");

        Guid dispatchId = Guid.Parse("a7777777-7777-4777-8777-777777777777");

        Guid runId = Guid.Parse("b8888888-8888-4888-8888-888888888888");

        Guid remoteQuestId = Guid.Parse("c9999999-9999-4999-8999-999999999999");

        Guid providerReceiptId = Guid.Parse("d0000000-0000-4000-8000-000000000001");

        Guid remoteReceiptId = Guid.Parse("e0000000-0000-4000-8000-000000000002");

        Guid refreshId = Guid.Parse("f0000000-0000-4000-8000-000000000003");

        const decimal usd = 0.1234567890123456789012345678m;

        const long inputTokens = long.MaxValue - 100;

        const long outputTokens = 100;

        const long cachedTokens = 25;

        const long reasoningTokens = 7;

        string digest = new('A', 64);

        DateTimeOffset acceptedAt = Instant.AddTicks(1234567);

        GuildSpendAmountsRequest amounts = new(inputTokens, outputTokens, cachedTokens, reasoningTokens, usd);

        Exercise(amounts, GuildJsonContext.Default.GuildSpendAmountsRequest);

        Require(RoundTrip(amounts, GuildJsonContext.Default.GuildSpendAmountsRequest) == amounts,
            "Spend request dimensions and exact USD must survive generated native serialization.");

        GuildSpendFacts facts = new(inputTokens, outputTokens, cachedTokens, reasoningTokens, usd, true, true, false);

        Exercise(facts, GuildJsonContext.Default.GuildSpendFacts);

        Require(RoundTrip(facts, GuildJsonContext.Default.GuildSpendFacts) == facts
            && checked(facts.InputTokens + facts.OutputTokens) == long.MaxValue,
            "Spend facts must retain valid Int64 totals and uncertainty without numeric narrowing.");

        GuildSpendCorrectionRequest correction = new(requestId, 17, GuildSpendOrigin.OperatorAttested, amounts, digest);

        Exercise(correction, GuildJsonContext.Default.GuildSpendCorrectionRequest);

        Require(RoundTrip(correction, GuildJsonContext.Default.GuildSpendCorrectionRequest) == correction,
            "Correction identity, revision, attestation origin, facts and digest must remain distinct.");

        GuildSpendLocalTokensDto localTokens = new(inputTokens, outputTokens, cachedTokens, reasoningTokens);

        Exercise(localTokens, GuildJsonContext.Default.GuildSpendLocalTokensDto);

        Require(RoundTrip(localTokens, GuildJsonContext.Default.GuildSpendLocalTokensDto) == localTokens,
            "Local spend token dimensions must not be reordered or dropped.");

        GuildSpendEvidenceFactsDto localFacts = new(usd, long.MaxValue, true, true, false, localTokens);

        Exercise(localFacts, GuildJsonContext.Default.GuildSpendEvidenceFactsDto);

        Require(RoundTrip(localFacts, GuildJsonContext.Default.GuildSpendEvidenceFactsDto) == localFacts,
            "Known local totals must retain both uncertainty flags and their exact token split.");

        GuildSpendEvidenceFactsDto remoteFacts = localFacts with { LocalTokens = null };

        Exercise(remoteFacts, GuildJsonContext.Default.GuildSpendEvidenceFactsDto);

        Require(RoundTrip(remoteFacts, GuildJsonContext.Default.GuildSpendEvidenceFactsDto) == remoteFacts,
            "Remote cumulative spend must preserve an absent local token split.");

        GuildSpendReceiptDto receipt = new(receiptId, questId, effectId, GuildSpendOrigin.OperatorAttested,
            digest, localFacts, 17, 19, acceptedAt);

        Exercise(receipt, GuildJsonContext.Default.GuildSpendReceiptDto);

        Require(RoundTrip(receipt, GuildJsonContext.Default.GuildSpendReceiptDto) == receipt,
            "Receipt identity, physical source, evidence revision, event sequence and exact timestamp must survive.");

        GuildSpendCorrectionResultDto corrected = new(receipt, false);

        Exercise(corrected, GuildJsonContext.Default.GuildSpendCorrectionResultDto);

        Require(RoundTrip(corrected, GuildJsonContext.Default.GuildSpendCorrectionResultDto) == corrected,
            "A newly accepted correction must retain its immutable receipt and replay disposition.");

        GuildSpendCorrectionResultDto replayed = corrected with { Replayed = true };

        Exercise(replayed, GuildJsonContext.Default.GuildSpendCorrectionResultDto);

        Require(RoundTrip(replayed, GuildJsonContext.Default.GuildSpendCorrectionResultDto) == replayed,
            "A replayed correction must remain distinguishable from a new acceptance.");

        GuildSpendReceiptDto providerReceipt = receipt with
        {
            ReceiptId = providerReceiptId, Origin = GuildSpendOrigin.ProviderReported,
            EvidenceRevision = 18, EventSequence = 21,
        };

        Exercise(providerReceipt, GuildJsonContext.Default.GuildSpendReceiptDto);

        Require(RoundTrip(providerReceipt, GuildJsonContext.Default.GuildSpendReceiptDto) == providerReceipt,
            "Provider-reported receipts must preserve provenance separately from Operator attestations.");

        GuildSpendRefreshRequest refresh = new(refreshId);

        Exercise(refresh, GuildJsonContext.Default.GuildSpendRefreshRequest);

        Require(RoundTrip(refresh, GuildJsonContext.Default.GuildSpendRefreshRequest) == refresh,
            "Refresh idempotency identity must survive generated native serialization.");

        GuildSpendRefreshResultDto[] refreshOutcomes =
        [
            new(operationId, providerReceipt, false, false, false, null, false),
            new(operationId, providerReceipt, true, false, false, null, false),
            new(operationId, null, false, true, false, null, false),
            new(operationId, null, false, false, true, "Guild.ReconciliationRequired", false),
            new(operationId, null, false, false, false, "Guild.RefreshCancelled", true),
        ];

        foreach (GuildSpendRefreshResultDto outcome in refreshOutcomes)
        {
            Exercise(outcome, GuildJsonContext.Default.GuildSpendRefreshResultDto);

            Require(RoundTrip(outcome, GuildJsonContext.Default.GuildSpendRefreshResultDto) == outcome,
                "Refresh acceptance, replay, in-flight, attention and terminal cancellation must retain every field.");
        }

        GuildSpendSourceDto source = new(effectId, receiptId, 17, GuildSpendOrigin.OperatorAttested);

        Exercise(source, GuildJsonContext.Default.GuildSpendSourceDto);

        Require(RoundTrip(source, GuildJsonContext.Default.GuildSpendSourceDto) == source,
            "Physical spend source identity must retain its effect, receipt, revision and origin tuple.");

        GuildSpendEffectDto effect = new(questId, effectId, GuildSpendEffectKind.LocalProvider, localFacts,
            17, receiptId, null, "sourceUnavailable");

        Exercise(effect, GuildJsonContext.Default.GuildSpendEffectDto);

        Require(RoundTrip(effect, GuildJsonContext.Default.GuildSpendEffectDto) == effect,
            "A physical spend effect must retain facts, last receipt and independent eligibility reasons.");

        GuildSpendEffectDto remoteEffect = effect with
        {
            QuestId = remoteQuestId, EffectId = remoteQuestId, Kind = GuildSpendEffectKind.Remote, Facts = remoteFacts,
            EvidenceRevision = 19, LastReceiptId = remoteReceiptId, OperatorIneligibility = "remoteEffect",
            ProviderIneligibility = "remoteEffect",
        };

        Exercise(remoteEffect, GuildJsonContext.Default.GuildSpendEffectDto);

        Require(RoundTrip(remoteEffect, GuildJsonContext.Default.GuildSpendEffectDto) == remoteEffect,
            "Remote physical effects must preserve their kind and lack of local token dimensions.");

        GuildSpendEffectPageDto effects = new([effect], "native-effect-cursor", 23);

        Exercise(effects, GuildJsonContext.Default.GuildSpendEffectPageDto);

        GuildSpendEffectPageDto restoredEffects = RoundTrip(effects, GuildJsonContext.Default.GuildSpendEffectPageDto);

        Require(restoredEffects.Items.Length == 1 && restoredEffects.Items.SequenceEqual(effects.Items)
            && restoredEffects.NextCursor == effects.NextCursor && restoredEffects.SnapshotSequence == effects.SnapshotSequence
            && restoredEffects.StateConsistency == effects.StateConsistency,
            "Effect pages must retain nonempty ordered contents, continuation and snapshot semantics.");

        GuildSpendReceiptDto remoteReceipt = receipt with
        {
            ReceiptId = remoteReceiptId, QuestId = remoteQuestId, EffectId = remoteQuestId, Origin = GuildSpendOrigin.RemoteReport,
            EvidenceDigest = null, Facts = remoteFacts, EvidenceRevision = 19, EventSequence = 23,
        };

        Exercise(remoteReceipt, GuildJsonContext.Default.GuildSpendReceiptDto);

        Require(RoundTrip(remoteReceipt, GuildJsonContext.Default.GuildSpendReceiptDto) == remoteReceipt,
            "Remote receipts must retain their Quest-bound effect, absent digest and cumulative facts without local token dimensions.");

        GuildSpendReceiptPageDto receipts = new([receipt, providerReceipt], "native-receipt-cursor", 23);

        Exercise(receipts, GuildJsonContext.Default.GuildSpendReceiptPageDto);

        GuildSpendReceiptPageDto restoredReceipts = RoundTrip(receipts, GuildJsonContext.Default.GuildSpendReceiptPageDto);

        Require(restoredReceipts.Items.Length == 2 && restoredReceipts.Items.SequenceEqual(receipts.Items)
            && restoredReceipts.NextCursor == receipts.NextCursor && restoredReceipts.SnapshotSequence == receipts.SnapshotSequence
            && restoredReceipts.StateConsistency == receipts.StateConsistency,
            "Receipt pages must retain original ordered receipts and their independent continuation state.");

        foreach (decimal exact in new[] { 0.0000000000000000000000000001m, decimal.MaxValue })
        {
            GuildSpendAmountsRequest boundary = amounts with { Usd = exact };

            Exercise(boundary, GuildJsonContext.Default.GuildSpendAmountsRequest);

            Require(RoundTrip(boundary, GuildJsonContext.Default.GuildSpendAmountsRequest) == boundary,
                "Spend USD must retain the smallest positive decimal and decimal.MaxValue exactly.");
        }

        foreach (string number in new[] { "1e-29", "79228162514264337593543950336", "0.12345678901234567890123456789" })
            RejectJson("{\"usd\":" + number + "}", GuildJsonContext.Default.GuildSpendAmountsRequest);

        GuildSpendAmountsRequest omitted = JsonSerializer.Deserialize("{}", GuildJsonContext.Default.GuildSpendAmountsRequest)!;

        GuildSpendAmountsRequest explicitNull = JsonSerializer.Deserialize(
            "{\"inputTokens\":null,\"outputTokens\":null,\"cachedTokens\":null,\"reasoningTokens\":null,\"usd\":null}",
            GuildJsonContext.Default.GuildSpendAmountsRequest)!;

        Require(omitted.InputTokens is null && omitted.OutputTokens is null && omitted.CachedTokens is null
            && omitted.ReasoningTokens is null && omitted.Usd is null && explicitNull == omitted,
            "Missing nullable amounts must remain missing; wire decoding must not invent complete measurements.");

        (GuildSpendOrigin Origin, string Wire)[] origins =
        [
            (GuildSpendOrigin.LocalResponse, "localResponse"),
            (GuildSpendOrigin.RemoteReport, "remoteReport"),
            (GuildSpendOrigin.OperatorAttested, "operatorAttested"),
            (GuildSpendOrigin.ProviderReported, "providerReported"),
        ];

        foreach ((GuildSpendOrigin origin, string wire) in origins)
        {
            Exercise(origin, GuildJsonContext.Default.GuildSpendOrigin);

            Require(JsonSerializer.Serialize(origin, GuildJsonContext.Default.GuildSpendOrigin) == $"\"{wire}\""
                && RoundTrip(origin, GuildJsonContext.Default.GuildSpendOrigin) == origin,
                "Spend provenance must retain its exact closed wire literal.");
        }

        RejectJson("2", GuildJsonContext.Default.GuildSpendOrigin);

        RejectJson("\"future-origin\"", GuildJsonContext.Default.GuildSpendOrigin);

        RejectJson("{\"origin\":2}", GuildJsonContext.Default.GuildSpendCorrectionRequest);

        RejectJson("{\"origin\":\"future-origin\"}", GuildJsonContext.Default.GuildSpendCorrectionRequest);

        (GuildSpendEffectKind Kind, string Wire)[] kinds =
        [
            (GuildSpendEffectKind.LocalProvider, "localProvider"),
            (GuildSpendEffectKind.Remote, "remote"),
        ];

        foreach ((GuildSpendEffectKind kind, string wire) in kinds)
        {
            Exercise(kind, GuildJsonContext.Default.GuildSpendEffectKind);

            Require(JsonSerializer.Serialize(kind, GuildJsonContext.Default.GuildSpendEffectKind) == $"\"{wire}\""
                && RoundTrip(kind, GuildJsonContext.Default.GuildSpendEffectKind) == kind,
                "Physical effect kind must retain its exact closed wire literal.");
        }

        RejectJson("0", GuildJsonContext.Default.GuildSpendEffectKind);

        RejectJson("\"future-effect\"", GuildJsonContext.Default.GuildSpendEffectKind);

        (GuildSpendSourceDto Source, long Sequence, Guid QuestId, Guid ObjectId, long Revision)[] spendEvents =
        [
            (new(effectId, sourceId, 1, GuildSpendOrigin.LocalResponse), 13, questId, questId, 73),
            (source, receipt.EventSequence, questId, questId, 74),
            (new(effectId, providerReceiptId, 18, GuildSpendOrigin.ProviderReported), providerReceipt.EventSequence, questId, questId, 75),
            (new(remoteQuestId, remoteReceiptId, 19, GuildSpendOrigin.RemoteReport), remoteReceipt.EventSequence,
                remoteQuestId, remoteReceiptId, remoteReceipt.EvidenceRevision),
        ];

        foreach ((GuildSpendSourceDto eventSource, long sequence, Guid eventQuestId, Guid objectId, long revision) in spendEvents)
        {
            GuildChangeEventDto changed = new(sequence, GuildEventKind.SpendChanged, objectId, revision, [], eventQuestId, eventSource);

            Exercise(changed, GuildJsonContext.Default.GuildChangeEventDto);

            GuildChangeEventDto restored = RoundTrip(changed, GuildJsonContext.Default.GuildChangeEventDto);

            Require(restored.EventKind == GuildEventKind.SpendChanged && restored.EventSequence == sequence
                && restored.ObjectId == objectId && restored.QuestId == eventQuestId && restored.Revision == revision
                && restored.ContentRefs.IsEmpty && restored.SpendSource == eventSource,
                "SpendChanged must preserve each origin's ObjectId/revision convention, Quest identity and physical source tuple without content.");
        }

        GuildChangeEventDto legacy = new(29, GuildEventKind.SpendChanged, questId, 91, [], questId);

        Exercise(legacy, GuildJsonContext.Default.GuildChangeEventDto);

        GuildChangeEventDto restoredLegacy = RoundTrip(legacy, GuildJsonContext.Default.GuildChangeEventDto);

        Require(restoredLegacy.SpendSource is null && restoredLegacy.ObjectId == questId && restoredLegacy.Revision == 91
            && restoredLegacy.QuestId == questId && restoredLegacy.EventKind == GuildEventKind.SpendChanged
            && restoredLegacy.EventSequence == 29 && restoredLegacy.ContentRefs.IsEmpty,
            "Legacy spend invalidations must not acquire a fabricated physical source.");

        GuildInitialBillSnapshot snapshot = new(effectId, questId, dispatchId, sourceId, runId,
            BillableOperationType.Chat, "openrouter", "sample/model", "guild", Instant, acceptedAt,
            inputTokens, outputTokens, reasoningTokens, cachedTokens, "{\"source\":\"native-smoke\"}",
            "0.1234567890123456789012345678", BillableOperationStatus.Completed, "gen-native-smoke");

        byte[] snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot);

        GuildInitialBillSnapshot restoredSnapshot = RoundTrip(snapshot, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot);

        Require(restoredSnapshot == snapshot && snapshotBytes.AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(restoredSnapshot, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot)),
            "The original physical bill snapshot must preserve every identity, token dimension, exact cost spelling and canonical byte.");

        GuildBudgetEffect savedEffect = new(new(effectId, questId, dispatchId, runId, GuildBudgetEffectKind.LocalProvider,
            sourceId, Guid.Parse("a0000000-0000-4000-8000-000000000004"), decimal.MaxValue, long.MaxValue),
            usd, long.MaxValue, true, true, GuildBudgetEffectState.Complete, 1);

        byte[] effectBytes = JsonSerializer.SerializeToUtf8Bytes(savedEffect, GuildSpendStorageJsonContext.Default.GuildBudgetEffect);

        GuildBudgetEffect restoredSavedEffect = RoundTrip(savedEffect, GuildSpendStorageJsonContext.Default.GuildBudgetEffect);

        Require(restoredSavedEffect == savedEffect && effectBytes.AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(restoredSavedEffect, GuildSpendStorageJsonContext.Default.GuildBudgetEffect)),
            "The saved physical effect must retain its original request, reservation, totals, uncertainty and canonical bytes.");

        byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto);

        GuildSpendReceiptDto restoredStoredReceipt = RoundTrip(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto);

        Require(restoredStoredReceipt == receipt && receiptBytes.AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(restoredStoredReceipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto)),
            "An immutable correction receipt must retain its originally accepted result and canonical stored bytes.");

        GuildSpendRefreshCheckpoint checkpoint = new(1, refreshId, questId,
            Guid.Parse("b0000000-0000-4000-8000-000000000005"), effectId, sourceId, operationId,
            Guid.Parse("c0000000-0000-4000-8000-000000000006"), Guid.Parse("d0000000-0000-4000-8000-000000000007"),
            Guid.Parse("e0000000-0000-4000-8000-000000000008"), Guid.Parse("f0000000-0000-4000-8000-000000000009"),
            Guid.Parse("a0000000-0000-4000-8000-000000000010"), "admitted");

        byte[] checkpointBytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint);

        GuildSpendRefreshCheckpoint restoredCheckpoint = RoundTrip(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint);

        Require(restoredCheckpoint == checkpoint && restoredCheckpoint.Version == 1 && restoredCheckpoint.Phase == "admitted"
            && checkpointBytes.Length <= 2048 && checkpointBytes.AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(restoredCheckpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint)),
            "The version-1 refresh checkpoint must retain every data identity, admitted phase and canonical recovery bytes.");
    }

    private static async Task ExerciseWorkspaceLeaseKernelAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-native-lease-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var locked = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(root, "host.lock"));

            Require(locked.IsSuccess, "Native workspace host lock was unavailable.");

            using var host = locked.Value;

            Require(host.Validate().IsSuccess, "Native workspace host lock could not prove its identity.");

            await GuildWorkspaceReadSmoke.VerifyAsync(root);

            WorkspaceLeaseIdentityResolver resolver = new();

            var before = await resolver.ResolveAsync(root, [new("child", GuildPathScopeKind.Subtree)], default);

            Require(before.IsSuccess, "Native workspace identity could not be resolved.");

            var prepared = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(root, "child"));

            Require(prepared.IsSuccess, "Native directory creation could not be prepared.");

            WorkspaceLeaseIdentitySet createdIdentity;

            using (var preparation = prepared.Value)
            {
                Require((await resolver.ValidateDirectoryCreationAsync(before.Value, preparation, default)).IsSuccess,
                    "Native directory creation was not covered by the admitted scope.");

                var created = preparation.CreateExclusive(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                Require(created.IsSuccess, "Native exclusive directory creation failed.");

                using var proof = created.Value;

                var adopted = await resolver.AdoptCreatedDirectoryAsync(before.Value, proof, default);

                Require(adopted.IsSuccess, "Native directory creation could not be adopted.");

                createdIdentity = adopted.Value;
            }

            var removal = WorkspaceLeaseDirectoryRemoval.Prepare(Path.Combine(root, "child"));

            Require(removal.IsSuccess, "Native directory removal could not be prepared.");

            using var preparedRemoval = removal.Value;

            Require((await resolver.ValidateDirectoryRemovalAsync(createdIdentity, preparedRemoval, default)).IsSuccess,
                "Native directory removal was not covered by the admitted scope.");

            Directory.Delete(Path.Combine(root, "child"));

            var removed = preparedRemoval.Complete();

            Require(removed.IsSuccess, "Native directory removal could not prove absence.");

            using var removedProof = removed.Value;

            var final = await resolver.AdoptRemovedDirectoryAsync(createdIdentity, removedProof, default);

            Require(final.IsSuccess && (await resolver.ValidateAsync(final.Value, default)).IsSuccess,
                "Native workspace directory identity did not converge after removal.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExerciseWorkspaceLeaseInfrastructure()
    {
        GuildWorkspaceLeaseOwner owner = new(GuildWorkspaceLeaseOwnerKind.Operator, Id, null, null, null, null, Id, Id);

        string cursor = GuildWorkspaceLeaseCursor.Encode(owner, null, GuildWorkspaceLeaseState.Held, Id);

        Require(GuildWorkspaceLeaseCursor.TryDecode(cursor, owner, null, GuildWorkspaceLeaseState.Held, out Guid? afterId)
            && afterId == Id, "The workspace lease continuation failed its native round trip.");

        Require(!GuildWorkspaceLeaseCursor.TryDecode(cursor, owner, null, GuildWorkspaceLeaseState.Released, out _),
            "The workspace lease continuation accepted a different filter under Native AOT.");

        GuildWorkspaceLeaseMutationSnapshot snapshot = new(Id, 1, 1, GuildWorkspaceLeaseState.Held,
            Instant, Instant.AddMinutes(15), 0, null);

        RoundTrip(snapshot, GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

        RoundTrip(snapshot with { State = GuildWorkspaceLeaseState.ReconciliationRequired, InFlightMutationCount = 1,
            ReasonCode = "Guild.ReconciliationRequired" }, GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

        string serialized = JsonSerializer.Serialize(snapshot, GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

        foreach (string missing in new[] { "heartbeatAt", "inFlightMutationCount" })
        {
            using JsonDocument document = JsonDocument.Parse(serialized);

            using MemoryStream incomplete = new();

            using (Utf8JsonWriter writer = new(incomplete))
            {
                writer.WriteStartObject();

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Name != missing) property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            bool refused = false;

            try
            {
                _ = JsonSerializer.Deserialize(incomplete.ToArray(), GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);
            }
            catch (JsonException)
            {
                refused = true;
            }

            Require(refused, "A persisted workspace mutation snapshot accepted a missing member under Native AOT.");
        }

        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;

        FileHandleIdentity physicalRoot = new(7, 11);

        WorkspaceLeaseIdentitySet identity = new(root, physicalRoot,
            [new("probe.txt", GuildPathScopeKind.File, ["probe.txt"], [new(root, physicalRoot, true, 0)], null, false)]);

        RoundTrip(identity, WorkspaceLeaseIdentityJsonContext.Default.WorkspaceLeaseIdentitySet);

        var restored = WorkspaceLeaseIdentitySet.FromJson(identity.ToJson());

        Require(restored.IsSuccess && restored.Value.Digest == identity.Digest && restored.Value.Covers(identity)
            && identity.Covers(restored.Value), "Physical workspace lease identity failed its generated native round trip.");
    }

    private static void Exercise<T>(T value, JsonTypeInfo<T> metadata)
    {
        RoundTrip(value, metadata);

        // typeof(T) here supplies static generated metadata; no reflection serializer is enabled.
        RoundTrip(new[] { value }, (JsonTypeInfo<T[]>)GuildJsonContext.Default.GetTypeInfo(typeof(T[]))!);

        RoundTrip(ImmutableArray.Create(value), (JsonTypeInfo<ImmutableArray<T>>)GuildJsonContext.Default.GetTypeInfo(typeof(ImmutableArray<T>))!);
    }

    private static T RoundTrip<T>(T value, JsonTypeInfo<T> metadata)
    {
        string json = JsonSerializer.Serialize(value, metadata);

        T? restored = JsonSerializer.Deserialize(json, metadata);

        if (restored is null)
        {
            throw new InvalidOperationException("A generated Guild graph unexpectedly deserialized to null.");
        }

        Require(json == JsonSerializer.Serialize(restored, metadata), "A generated Guild graph failed to round-trip.");

        return restored;
    }

    private static void ExerciseLiteralsAndBoundaries()
    {
        GuildIssueRootRequest firstIntent = new(Id, null, "Smith", "cafe\u0301\r\nnext", SampleGuildGrant());

        var firstDigest = GuildQuestIntent.Compute(firstIntent);

        Require(firstDigest.IsSuccess && firstDigest.Value.Length == 64, "Quest intent must have a bounded SHA256 identity.");

        var repeatedDigest = GuildQuestIntent.Compute(firstIntent with { IdempotencyKey = Guid.NewGuid(), Goal = "café\nnext" });

        Require(repeatedDigest.IsSuccess && repeatedDigest.Value == firstDigest.Value, "Canonical intent must ignore retry keys and normalize goal text.");

        var childDigest = GuildQuestIntent.Compute(Id, new GuildIssueChildRequest(Id, 17, null, "Smith", "goal", new()));

        Require(childDigest.IsSuccess && childDigest.Value != firstDigest.Value, "Child caller intent must retain its parent and requested-grant identity.");

        (QuestState State, string Wire)[] states =
        [
            (QuestState.Submitted, "submitted"),
            (QuestState.Working, "working"),
            (QuestState.InputRequired, "input-required"),
            (QuestState.Completed, "completed"),
            (QuestState.Failed, "failed"),
            (QuestState.Canceled, "canceled"),
            (QuestState.Rejected, "rejected")
        ];

        foreach ((QuestState state, string wire) in states)
            Require(JsonSerializer.Serialize(state, GuildJsonContext.Default.QuestState) == $"\"{wire}\"", "Quest wire state changed.");

        RejectJson("42", GuildJsonContext.Default.QuestState);

        RejectJson("\"future-success-state\"", GuildJsonContext.Default.QuestState);

        GuildRemoteStateProjection unknown = GuildRemoteStateProjection.Observe(new string('x', 4096));

        Require(unknown.State == QuestState.Working && unknown.ReconciliationRequired && unknown.RemoteStateDiagnostic!.Length <= 128, "Unknown peer states must remain reconciliable nonterminal observations.");

        GuildRequestedGrant omitted = JsonSerializer.Deserialize("{}", GuildJsonContext.Default.GuildRequestedGrant)!;

        GuildRequestedGrant explicitNull = JsonSerializer.Deserialize("{\"expiresAtUtc\":null}", GuildJsonContext.Default.GuildRequestedGrant)!;

        Require(!omitted.ExpiresAtUtc.IsSpecified && explicitNull.ExpiresAtUtc.IsSpecified && explicitNull.ExpiresAtUtc.Value is null, "Missing and explicit null grant intent collapsed.");

        RoundTrip(new GuildProducerIdentity(GuildProducerKind.Operator, null, null, null, "native-operator", null, null, null), GuildJsonContext.Default.GuildProducerIdentity);

        GuildProducerIdentity first = new(GuildProducerKind.HostReceipt, null, null, null, null, Id, GuildReceiptSourceKind.Result, Id);

        Guid secondReceipt = Guid.Parse("b2222222-2222-4222-8222-222222222222");

        GuildProducerIdentity second = new(GuildProducerKind.HostReceipt, null, null, null, null, Id, GuildReceiptSourceKind.Result, secondReceipt);

        RoundTrip(first, GuildJsonContext.Default.GuildProducerIdentity);

        Require(first.StreamKey(Id) == second.StreamKey(Id) && first.DedupeKey(Id, Id) != second.DedupeKey(Id, secondReceipt), "Receipt ordering and deduplication identities collapsed.");
    }

    private static void RejectJson<T>(string json, JsonTypeInfo<T> metadata)
    {
        try
        {
            JsonSerializer.Deserialize(json, metadata);
        }
        catch (JsonException)
        {
            return;
        }

        throw new InvalidOperationException("A generated Guild contract accepted an unsupported JSON value.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static AcquireWorkspaceLeaseRequest SampleAcquireWorkspaceLeaseRequest() => new(IdempotencyKey: Id, WorkspaceId: Id, Scopes: [SampleGuildPathScope()]);

    private static AdmitCohortMemberRequest SampleAdmitCohortMemberRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Quest: SampleGuildIssueChildData());

    private static AppendSharedNoteRequest SampleAppendSharedNoteRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Kind: GuildNoteKind.Correction, SubjectId: Id, Body: "A bounded Guild result.\nSecond line: λ", CorrectsNoteId: Id);

    private static AwardWorkRequest SampleAwardWorkRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ExpectedCohortRevision: 17L, BidId: Id);

    private static CohortContributionDto SampleCohortContributionDto() => new(CohortId: Id, MemberId: Id, PrimaryVotingQuestId: Id, ResultSequence: 17L, Outcome: GuildContributionOutcome.Accepted, ResultRef: SampleGuildDataRef(), AgreementDigest: "sample-AgreementDigest", Selected: true);

    private static CohortContributionPageDto SampleCohortContributionPageDto() => new(Items: [SampleCohortContributionDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortConvergenceDto SampleCohortConvergenceDto() => new(CohortId: Id, Revision: 17L, State: GuildCohortState.Completed, Decision: GuildConvergenceDecision.Success, ReasonCode: "sample-ReasonCode", SelectedResultRef: SampleGuildDataRef(), FrozenMemberCount: 17L, PendingMemberCount: 17L, PendingDescendantCount: 17L, CancelRequestedCount: 17L, Spend: SampleGuildSpendDto(), EventWatermark: 17L);

    private static CohortDto SampleCohortDto() => new(Id: Id, Name: "sample-Name", RootQuestId: Id, IssuerQuestId: Id, CampaignId: Id, Policy: SampleGuildConvergencePolicy(), ResultContract: SampleGuildResultContract(), State: GuildCohortState.Completed, Revision: 17L, MembershipVersion: 17L, ClosedMembershipVersion: 17L, CohortGrant: SampleGuildGrant(), AllocationAccountId: Id, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static CohortMemberQuestDto SampleCohortMemberQuestDto() => new(MemberId: Id, QuestId: Id, AssociationKind: GuildMemberAssociationKind.Primary, AssociationSequence: 17L);

    private static CohortMemberQuestPageDto SampleCohortMemberQuestPageDto() => new(Items: [SampleCohortMemberQuestDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortMemberDto SampleCohortMemberDto() => new(Id: Id, CohortId: Id, PrimaryVotingQuestId: Id, AgentId: Id, AdmissionSequence: 17L, State: GuildCohortMemberState.Active, InClosedSnapshot: true, RemovalSequence: null, AssociatedQuestCount: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static CohortMemberPageDto SampleCohortMemberPageDto() => new(Items: [SampleCohortMemberDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortPageDto SampleCohortPageDto() => new(Items: [SampleCohortDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CreateCohortRequest SampleCreateCohortRequest() => new(IdempotencyKey: Id, IssuerQuestId: Id, Name: "sample-Name", Policy: SampleGuildConvergencePolicy(), ResultContract: SampleGuildResultContract(), RequestedGrant: SampleGuildRequestedGrant());

    private static GuildActionRequest SampleGuildActionRequest() => new(Action: GuildAuthorityAction.Issue, ResourceId: Id);

    private static GuildAdmissionReceipt SampleGuildAdmissionReceipt() => new(QuestId: Id, OperationId: Id, SignetId: Id, AllocationAccountId: Id, Receipt: SampleGuildMutationReceiptDto());

    private static GuildAdmissionRequest SampleGuildAdmissionRequest() => new(Mutation: SampleGuildMutationIdentity(), Issuance: SampleGuildIssuanceIdentity(), Quest: SampleGuildQuestDto(), Goal: SampleGuildDataRef(), AcceptedResolutionDigest: "sample-AcceptedResolutionDigest", ExpectedIssuerRevision: 17L);

    private static GuildAgentCapabilitiesDto SampleGuildAgentCapabilitiesDto() => new(Streaming: true, PushNotifications: true, StateTransitionHistory: true);

    private static GuildAgentCard SampleGuildAgentCard() => new(AgentId: Id, CardId: Id, Revision: 17L, Name: "sample-Name", Description: "sample-Description", Version: "sample-Version", Skills: [SampleGuildAgentSkillDto()], DefaultInputModes: ["text/plain"], DefaultOutputModes: ["text/plain"], Capabilities: SampleGuildAgentCapabilitiesDto(), Source: SampleGuildAgentSourceDto());

    private static GuildAgentCardDto SampleGuildAgentCardDto() => new(AgentId: Id, CardId: Id, Revision: 17L, Name: "sample-Name", Description: "sample-Description", Version: "sample-Version", Skills: [SampleGuildAgentSkillDto()], DefaultInputModes: ["text/plain"], DefaultOutputModes: ["text/plain"], Capabilities: SampleGuildAgentCapabilitiesDto(), Source: SampleGuildAgentSourceDto());

    private static GuildAgentCardPageDto SampleGuildAgentCardPageDto() => new(Items: [SampleGuildAgentCardDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildAgentSkillDto SampleGuildAgentSkillDto() => new(Id: "sample-Id", Name: "sample-Name", Description: "sample-Description", Tags: ["sample-value"], InputModes: ["text/plain"], OutputModes: ["text/plain"]);

    private static GuildAgentSourceDto SampleGuildAgentSourceDto() => new(Kind: GuildAgentSourceKind.Remote, WorkspaceId: null, PeerId: "configured-peer", Available: true, ReasonCode: "sample-ReasonCode");

    private static GuildBudgetGrant SampleGuildBudgetGrant() => new(Usd: SampleGuildUsdBudget(), Tokens: SampleGuildTokenBudget());

    private static GuildCancelQuestRequest SampleGuildCancelQuestRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ReasonCode: GuildCancellationReason.OperatorRequested);

    private static GuildCancellationDto SampleGuildCancellationDto() => new(QuestId: Id, RootQuestId: Id, Revision: 17L, CancellationRequested: true, CancellationConverged: true, PendingExecutionCount: 0L, UnresolvedExternalEffectCount: 0L, AccountingState: GuildAccountingState.Settled, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildChangeEventDto SampleGuildChangeEventDto() => new(EventSequence: 17L, EventKind: GuildEventKind.QuestChanged, ObjectId: Id, Revision: 17L, ContentRefs: [SampleGuildDataRef()], QuestId: Id);

    private static GuildCircleAcknowledgeRequest SampleGuildCircleAcknowledgeRequest() => new(Mutation: SampleGuildMutationIdentity(), ApplicationReceiptId: Id, ApplicationDigest: "sample-ApplicationDigest", ExpectedInboxRevision: 17L);

    private static GuildCircleAcknowledgement SampleGuildCircleAcknowledgement() => new(InboxId: Id, ApplicationReceiptId: Id, Revision: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildCircleAddress SampleGuildCircleAddress() => new(GuildCircleAddressKind.Child, Id, null, null);

    private static GuildCircleInboxItem SampleGuildCircleInboxItem() => new(InboxId: Id, Envelope: SampleGuildEnvelope(), Revision: 17L);

    private static GuildCircleInboxPage SampleGuildCircleInboxPage() => new(Items: [SampleGuildCircleInboxItem()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildCircleInboxQuery SampleGuildCircleInboxQuery() => new(RecipientQuestId: Id, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildCohortContributionRequest SampleGuildCohortContributionRequest() => new(Mutation: SampleGuildMutationIdentity(), CohortId: Id, MemberId: Id, ExpectedRevision: 17L, Result: SampleGuildResultReceipt());

    private static GuildCohortSnapshotDto SampleGuildCohortSnapshotDto() => new(Summary: SampleCohortDto(), Members: SampleCohortMemberPageDto(), Watermark: 17L, ResumeCursor: "sample-ResumeCursor");

    private static GuildQuestDto SampleGuildQuestDto() => new(Id: Id, Name: "sample-Name", OperationId: Id, AgentId: Id, CardId: Id, CardRevision: 17L, RunId: Id, ParentQuestId: Id, RootQuestId: Id, SessionId: Id, CampaignId: Id, WorkspaceId: Id, SignetId: Id, SignetRevision: 17L, Revision: 17L, IssuedSequence: 17L, State: QuestState.Completed, WaitReason: GuildWaitReason.Input, CancellationRequested: true, CancellationConverged: true, ReconciliationRequired: true, DeliveryState: GuildDeliveryState.None, AccountingState: GuildAccountingState.Settled, GoalRef: SampleGuildDataRef(), ResultRef: SampleGuildDataRef(), ResultReceiptId: Id, ResolvedGrant: SampleGuildGrant(), CreatedAt: Instant, UpdatedAt: Instant, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildQuestPageDto SampleGuildQuestPageDto() => new(Items: [SampleGuildQuestDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildCommitResultRequest SampleGuildCommitResultRequest() => new(Mutation: SampleGuildMutationIdentity(), ExpectedQuestRevision: 17L, ExpectedOperationRevision: 17L, Result: SampleGuildResultReceipt());

    private static GuildContentDto SampleGuildContentDto() => new(Reference: SampleGuildDataRef(), Body: "A bounded Guild result.\nSecond line: λ");

    private static GuildContinueRequest SampleGuildContinueRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ContinuationId: Id, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ", RemoteOptions: SampleGuildRemoteInvocationOptions());

    private static GuildConvergencePolicy SampleGuildConvergencePolicy() => new(GuildConvergenceKind.Quorum, 2);

    private static GuildDataRef SampleGuildDataRef() => new(ContentId: Id, Kind: GuildContentKind.AgentCard, Revision: 17L, Digest: "0000000000000000000000000000000000000000000000000000000000000000", MimeType: "text/plain", ByteLength: 17L, ContentErased: true);

    private static GuildDispatchObservation SampleGuildDispatchObservation() => new(QuestId: Id, DispatchId: Id, Evidence: GuildDispatchEvidence.Prepared, State: QuestState.Completed, ReconciliationRequired: true, ReasonCode: "sample-ReasonCode", LocalChildId: Id, RemoteTaskId: "sample-RemoteTaskId", RemoteContextId: "sample-RemoteContextId");

    private static GuildDispatchRequest SampleGuildDispatchRequest() => new(Mutation: SampleGuildMutationIdentity(), QuestId: Id, OperationId: Id, RunId: Id, DispatchId: Id, ExpectedOperationRevision: 17L, InvocationDigest: "sample-InvocationDigest");

    private static GuildEnvelope SampleGuildEnvelope() => new(Id: Id, Producer: SampleGuildProducerIdentity(), QuestId: Id, RootQuestId: Id, AuthorityGeneration: 17L, SessionId: Id, CampaignId: Id, Authority: GuildAuthorityKind.Parent, Kind: GuildEnvelopeKind.Progress, ContentRef: SampleGuildDataRef(), SenderSequence: 17L, CreatedAt: Instant, Address: SampleGuildCircleAddress());

    private static GuildEventPage SampleGuildEventPage() => new(Items: [SampleGuildChangeEventDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildEventQuery SampleGuildEventQuery() => new(RootQuestId: Id, SubtreeQuestId: Id, CohortId: Id, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildGrant SampleGuildGrant() => new(Workspaces: [SampleGuildWorkspaceGrant()], Tools: [SampleGuildToolGrant()], Memory: [SampleGuildMemoryGrant()], Budget: SampleGuildBudgetGrant(), Delegation: [GuildDelegationKind.Local], ExpiresAtUtc: Instant);

    private static GuildIssuanceIdentity SampleGuildIssuanceIdentity() => new(Id, null, Id, Id, Id);

    private static GuildIssueChildData SampleGuildIssueChildData() => new(AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant());

    private static GuildIssueChildRequest SampleGuildIssueChildRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant(), RemoteOptions: SampleGuildRemoteInvocationOptions());

    private static GuildIssueRootRequest SampleGuildIssueRootRequest() => new(IdempotencyKey: Id, AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", Grant: SampleGuildGrant(), SessionId: Id, CampaignId: Id, WorkspaceId: Id, PredecessorQuestId: Id, RemoteOptions: SampleGuildRemoteInvocationOptions());

    private static GuildListAgentsQuery SampleGuildListAgentsQuery() => new(Source: GuildAgentSourceKind.DeclaredGlobal, Skill: "sample-Skill", Cursor: "sample-Cursor", PageSize: 3);

    private static GuildListQuestsQuery SampleGuildListQuestsQuery() => new(RootId: Id, ParentId: Id, State: QuestState.Completed, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildMemoryGrant SampleGuildMemoryGrant() => new(Store: GuildMemoryStore.Transcript, Operation: GuildMemoryOperation.Read, Scope: SampleGuildMemoryScope());

    private static GuildMemoryScope SampleGuildMemoryScope() => new(GuildMemoryScopeKind.Campaign, Id);

    private static GuildMessageReceipt SampleGuildMessageReceipt() => new(QuestId: Id, MessageId: Id, Revision: 17L, DeliveryState: GuildDeliveryState.None, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildMutationIdentity SampleGuildMutationIdentity() => new(IdempotencyKey: Id, CanonicalRequestDigest: "1111111111111111111111111111111111111111111111111111111111111111");

    private static GuildMutationPlan SampleGuildMutationPlan() => new(WorkspaceId: Id, Scopes: [SampleGuildPathScope()], EffectPlanDigest: "sample-EffectPlanDigest");

    private static GuildMutationReceiptDto SampleGuildMutationReceiptDto() => new(Id: Id, IdempotencyKey: Id, Replayed: true, AcceptedAt: Instant, AcceptedRevision: 17L);

    private static GuildPageQuery SampleGuildPageQuery() => new(Cursor: "sample-Cursor", PageSize: 3);

    private static GuildPathScope SampleGuildPathScope() => new(RelativePath: "src", Kind: GuildPathScopeKind.File);

    private static GuildProducerIdentity SampleGuildProducerIdentity() => new(GuildProducerKind.Run, Id, Id, Id, null, null, null, null);

    private static GuildProducerStreamKey SampleGuildProducerStreamKey() => SampleGuildProducerIdentity().StreamKey(Id);

    private static GuildPublishDedupeKey SampleGuildPublishDedupeKey() => SampleGuildProducerIdentity().DedupeKey(Id, Id);

    private static GuildPublishRequest SampleGuildPublishRequest() => new(IdempotencyKey: Id, Address: SampleGuildCircleAddress(), Kind: GuildEnvelopeKind.QuestIssued, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ", ExpectedRevision: 17L);

    private static GuildRefreshAgentsRequest SampleGuildRefreshAgentsRequest() => new(IdempotencyKey: Id, AgentId: Id, Cursor: "sample-Cursor");

    private static GuildRefreshDiagnosticDto SampleGuildRefreshDiagnosticDto() => new(AgentId: Id, SourceKind: GuildAgentSourceKind.DeclaredGlobal, ReasonCode: "sample-ReasonCode", Available: true, CardRevision: 17L);

    private static GuildRefreshDiagnosticPageDto SampleGuildRefreshDiagnosticPageDto() => new(Items: [SampleGuildRefreshDiagnosticDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildRefreshReceipt SampleGuildRefreshReceipt() => new(Receipt: SampleGuildMutationReceiptDto(), Diagnostics: SampleGuildRefreshDiagnosticPageDto(), Completed: true);

    private static GuildRemoteStateProjection SampleGuildRemoteStateProjection() => GuildRemoteStateProjection.Observe("future-state");

    private static GuildRemoteTransportDescriptor SampleGuildRemoteTransportDescriptor() => new(PeerId: "sample-PeerId", Endpoint: "https://peer.example/a2a", ProtocolVersion: "sample-ProtocolVersion", Interfaces: ["sample-value"], AuthenticationSchemes: ["sample-value"], CredentialReference: "sample-CredentialReference", ProviderLink: "sample-ProviderLink", DocumentationLink: "sample-DocumentationLink", InterfaceObservations: [SampleGuildRemoteInterfaceDescriptor()]);

    private static GuildRemoteInterfaceDescriptor SampleGuildRemoteInterfaceDescriptor() => new("https://peer.example/a2a", "JSONRPC", "1.0");

    private static GuildRemoteInvocationOptions SampleGuildRemoteInvocationOptions() => new(GuildRemoteDispatchMode.Continuable, ["text/plain"], "sample-skill");

    private static GuildRequestedGrant SampleGuildRequestedGrant() => new(Budget: new(true, SampleGuildBudgetGrant()), ExpiresAtUtc: new(true, Instant));

    private static GuildResultApplicationReceipt SampleGuildResultApplicationReceipt() => new(RecipientQuestId: Id, QuestId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", RecipientRevision: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildResultApplicationRequest SampleGuildResultApplicationRequest() => new(Mutation: SampleGuildMutationIdentity(), RecipientQuestId: Id, QuestId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", ExpectedRecipientRevision: 17L);

    private static GuildResultContract SampleGuildResultContract() => new(MimeType: "text/plain");

    private static GuildResultReceipt SampleGuildResultReceipt() => new(QuestId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", Content: SampleGuildDataRef(), Outcome: QuestState.Completed, ReasonCode: "sample-ReasonCode");

    private static GuildResyncEventDto SampleGuildResyncEventDto() => new(ReasonCode: "sample-ReasonCode", SnapshotRoute: "/api/guild/quests/a1111111-1111-4111-8111-111111111111", ResumeCursor: "sample-ResumeCursor");

    private static GuildRevisionRequest SampleGuildRevisionRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L);

    private static GuildRevokeSignetRequest SampleGuildRevokeSignetRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ReasonCode: GuildCancellationReason.OperatorRequested);

    private static GuildSendOperatorMessageRequest SampleGuildSendOperatorMessageRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Kind: GuildMessageKind.Message, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ");

    private static GuildSignetBinding SampleGuildSignetBinding() => new(SignetId: Id, AgentId: Id, RunId: Id, QuestId: Id, RootQuestId: Id, IssuerSignetId: Id, Grant: SampleGuildGrant(), IssuedAtUtc: Instant, OriginPrincipalId: Id);

    private static GuildSignetStatus SampleGuildSignetStatus() => new(SignetId: Id, Revision: 17L, CredentialGeneration: 17L, RevocationGeneration: 17L, Revoked: true, RestoredInert: true);

    private static GuildSpendDto SampleGuildSpendDto() => new(QuestId: Id, KnownUsd: 1.25m, KnownTokens: 17L, UnknownUsd: true, UnknownTokens: true, PricedCount: 17L, UnpricedCount: 17L, UncertainCount: 17L, OutstandingAllocation: SampleGuildBudgetGrant(), UnresolvedHolds: SampleGuildBudgetGrant(), Remaining: SampleGuildBudgetGrant(), EventWatermark: 17L);

    private static GuildStatusDto SampleGuildStatusDto() => new(Enabled: true, Available: true, ReasonCodes: ["sample-value"], SchemaVersion: 17L, LegacyPendingCount: 17L);

    private static GuildTerminalEventDto SampleGuildTerminalEventDto() => new(ObjectId: Id, Revision: 17L, State: QuestState.Completed, AccountingState: GuildAccountingState.Settled, EventWatermark: 17L);

    private static GuildTokenBudget SampleGuildTokenBudget() => new(Mode: GuildBudgetMode.Finite, Value: 17L);

    private static GuildToolGrant SampleGuildToolGrant() => new(SourceKind: GuildToolSourceKind.BuiltIn, SourceRegistrationId: "arcanum", Name: "sample-Name");

    private static GuildTreeSnapshotDto SampleGuildTreeSnapshotDto() => new(Summary: SampleGuildQuestDto(), Members: SampleGuildQuestPageDto(), Watermark: 17L, ResumeCursor: "sample-ResumeCursor");

    private static GuildUsdBudget SampleGuildUsdBudget() => new(Mode: GuildBudgetMode.Finite, Value: 1.25m);

    private static GuildWorkspaceGrant SampleGuildWorkspaceGrant() => new(WorkspaceId: Id, RelativeRoot: "src", Access: GuildWorkspaceAccess.Read);

    private static PostWorkOfferRequest SamplePostWorkOfferRequest() => new(IdempotencyKey: Id, ExpectedCohortRevision: 17L, SkillId: "apprentice-goal-execution", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant(), ResultContract: SampleGuildResultContract());

    private static ReleaseWorkspaceLeaseRequest SampleReleaseWorkspaceLeaseRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Epoch: 17L);

    private static RenewWorkspaceLeaseRequest SampleRenewWorkspaceLeaseRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Epoch: 17L);

    private static SharedNoteDto SampleSharedNoteDto() => new(Id: Id, CohortId: Id, Sequence: 17L, AuthorQuestId: null, AuthorMemberId: null, Authority: GuildAuthorityKind.Operator, Kind: GuildNoteKind.Correction, SubjectId: Id, BodyRef: SampleGuildDataRef(), CorrectsNoteId: Id, CreatedAt: Instant, Receipt: SampleGuildMutationReceiptDto(), OperatorPrincipalId: Id, Body: "sample-untrusted-data");

    private static SharedNotePageDto SampleSharedNotePageDto() => new(Items: [SampleSharedNoteDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static SignetRevocationDto SampleSignetRevocationDto() => new(SignetId: Id, Revision: 17L, Generation: 17L, Revoked: true, CancellationRequested: true, CancellationConverged: true, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static SubmitBidRequest SampleSubmitBidRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Note: "sample-Note", ClaimedCostUsd: 1.25m);

    private static WorkAwardDto SampleWorkAwardDto() => new(Id: Id, OfferId: Id, BidId: Id, QuestId: Id, MemberId: Id, State: GuildWorkAwardState.Committed, Receipt: SampleGuildMutationReceiptDto());

    private static WorkBidDto SampleWorkBidDto() => new(Id: Id, OfferId: Id, MemberId: Id, AgentId: Id, CardRevision: 17L, BidSequence: 17L, Revision: 17L, State: GuildWorkBidState.Submitted, NoteRef: SampleGuildDataRef(), ClaimedCostUsd: 1.25m, Receipt: SampleGuildMutationReceiptDto());

    private static WorkBidPageDto SampleWorkBidPageDto() => new(Items: [SampleWorkBidDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static WorkOfferDto SampleWorkOfferDto() => new(Id: Id, CohortId: Id, IssuerQuestId: Id, OfferSequence: 17L, Revision: 17L, SkillId: "apprentice-goal-execution", ResultContract: SampleGuildResultContract(), GoalRef: SampleGuildDataRef(), State: GuildWorkOfferState.Awarded, AwardId: Id, QuestId: Id, Receipt: SampleGuildMutationReceiptDto());

    private static WorkOfferPageDto SampleWorkOfferPageDto() => new(Items: [SampleWorkOfferDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static WorkspaceLeaseDto SampleWorkspaceLeaseDto() => new(Id: Id, WorkspaceId: Id, OwnerKind: GuildWorkspaceLeaseOwnerKind.Quest, QuestId: Id, RootQuestId: Id, Scopes: [SampleGuildPathScope()], State: GuildWorkspaceLeaseState.Held, Epoch: 17L, Revision: 17L, ExpiresAt: Instant, InFlightMutationCount: 17L, ReasonCode: "sample-ReasonCode", Receipt: SampleGuildMutationReceiptDto());

    private static WorkspaceLeasePageDto SampleWorkspaceLeasePageDto() => new(Items: [SampleWorkspaceLeaseDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");
}
