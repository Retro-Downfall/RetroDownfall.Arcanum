using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;

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

        Exercise(SampleCohortMemberCommissionDto(), GuildJsonContext.Default.CohortMemberCommissionDto);

        Exercise(SampleCohortMemberCommissionPageDto(), GuildJsonContext.Default.CohortMemberCommissionPageDto);

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

        Exercise(SampleGuildCancelCommissionRequest(), GuildJsonContext.Default.GuildCancelCommissionRequest);

        Exercise(SampleGuildCancellationDto(), GuildJsonContext.Default.GuildCancellationDto);

        Exercise(SampleGuildChangeEventDto(), GuildJsonContext.Default.GuildChangeEventDto);

        Exercise(SampleGuildCircleAcknowledgeRequest(), GuildJsonContext.Default.GuildCircleAcknowledgeRequest);

        Exercise(SampleGuildCircleAcknowledgement(), GuildJsonContext.Default.GuildCircleAcknowledgement);

        Exercise(SampleGuildCircleAddress(), GuildJsonContext.Default.GuildCircleAddress);

        Exercise(SampleGuildCircleInboxItem(), GuildJsonContext.Default.GuildCircleInboxItem);

        Exercise(SampleGuildCircleInboxPage(), GuildJsonContext.Default.GuildCircleInboxPage);

        Exercise(SampleGuildCircleInboxQuery(), GuildJsonContext.Default.GuildCircleInboxQuery);

        Exercise(SampleGuildCohortContributionRequest(), GuildJsonContext.Default.GuildCohortContributionRequest);

        Exercise(SampleGuildCohortSnapshotDto(), GuildJsonContext.Default.GuildCohortSnapshotDto);

        Exercise(SampleGuildCommissionDto(), GuildJsonContext.Default.GuildCommissionDto);

        Exercise(SampleGuildCommissionPageDto(), GuildJsonContext.Default.GuildCommissionPageDto);

        Exercise(SampleGuildCommitResultRequest(), GuildJsonContext.Default.GuildCommitResultRequest);

        Exercise(SampleGuildContentDto(), GuildJsonContext.Default.GuildContentDto);

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

        Exercise(SampleGuildListCommissionsQuery(), GuildJsonContext.Default.GuildListCommissionsQuery);

        Exercise(SampleGuildMemoryGrant(), GuildJsonContext.Default.GuildMemoryGrant);

        Exercise(SampleGuildMemoryScope(), GuildJsonContext.Default.GuildMemoryScope);

        Exercise(SampleGuildMessageReceipt(), GuildJsonContext.Default.GuildMessageReceipt);

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

        Exercise(CommissionState.Submitted, GuildJsonContext.Default.CommissionState);

        Exercise(GuildAccountingState.Settled, GuildJsonContext.Default.GuildAccountingState);

        Exercise(GuildAgentSourceKind.DeclaredGlobal, GuildJsonContext.Default.GuildAgentSourceKind);

        Exercise(GuildAuthorityAction.Issue, GuildJsonContext.Default.GuildAuthorityAction);

        Exercise(GuildAuthorityKind.Operator, GuildJsonContext.Default.GuildAuthorityKind);

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

        Exercise(GuildEnvelopeKind.CommissionIssued, GuildJsonContext.Default.GuildEnvelopeKind);

        Exercise(GuildEventKind.CommissionChanged, GuildJsonContext.Default.GuildEventKind);

        Exercise(GuildMemberAssociationKind.Primary, GuildJsonContext.Default.GuildMemberAssociationKind);

        Exercise(GuildMemoryOperation.Read, GuildJsonContext.Default.GuildMemoryOperation);

        Exercise(GuildMemoryScopeKind.Global, GuildJsonContext.Default.GuildMemoryScopeKind);

        Exercise(GuildMemoryStore.Transcript, GuildJsonContext.Default.GuildMemoryStore);

        Exercise(GuildMessageKind.Message, GuildJsonContext.Default.GuildMessageKind);

        Exercise(GuildNoteKind.Observation, GuildJsonContext.Default.GuildNoteKind);

        Exercise(GuildPathScopeKind.File, GuildJsonContext.Default.GuildPathScopeKind);

        Exercise(GuildProducerKind.Run, GuildJsonContext.Default.GuildProducerKind);

        Exercise(GuildReceiptSourceKind.CommissionIssue, GuildJsonContext.Default.GuildReceiptSourceKind);

        Exercise(GuildToolSourceKind.BuiltIn, GuildJsonContext.Default.GuildToolSourceKind);

        Exercise(GuildWaitReason.Input, GuildJsonContext.Default.GuildWaitReason);

        Exercise(GuildWorkAwardState.Committed, GuildJsonContext.Default.GuildWorkAwardState);

        Exercise(GuildWorkBidState.Submitted, GuildJsonContext.Default.GuildWorkBidState);

        Exercise(GuildWorkOfferState.Open, GuildJsonContext.Default.GuildWorkOfferState);

        Exercise(GuildWorkspaceAccess.Read, GuildJsonContext.Default.GuildWorkspaceAccess);

        Exercise(GuildWorkspaceLeaseOwnerKind.Commission, GuildJsonContext.Default.GuildWorkspaceLeaseOwnerKind);

        Exercise(GuildWorkspaceLeaseState.Held, GuildJsonContext.Default.GuildWorkspaceLeaseState);

        ExerciseLiteralsAndBoundaries();

        Console.WriteLine("Guild Native AOT graph smoke passed.");

        return 0;
    }

    private static void Exercise<T>(T value, JsonTypeInfo<T> metadata)
    {
        RoundTrip(value, metadata);

        // typeof(T) here supplies static generated metadata; no reflection serializer is enabled.
        RoundTrip(new[] { value }, (JsonTypeInfo<T[]>)GuildJsonContext.Default.GetTypeInfo(typeof(T[]))!);

        RoundTrip(ImmutableArray.Create(value), (JsonTypeInfo<ImmutableArray<T>>)GuildJsonContext.Default.GetTypeInfo(typeof(ImmutableArray<T>))!);
    }

    private static void RoundTrip<T>(T value, JsonTypeInfo<T> metadata)
    {
        string json = JsonSerializer.Serialize(value, metadata);

        T? restored = JsonSerializer.Deserialize(json, metadata);

        if (restored is null)
        {
            throw new InvalidOperationException("A generated Guild graph unexpectedly deserialized to null.");
        }

        Require(json == JsonSerializer.Serialize(restored, metadata), "A generated Guild graph failed to round-trip.");
    }

    private static void ExerciseLiteralsAndBoundaries()
    {
        (CommissionState State, string Wire)[] states =
        [
            (CommissionState.Submitted, "submitted"),
            (CommissionState.Working, "working"),
            (CommissionState.InputRequired, "input-required"),
            (CommissionState.Completed, "completed"),
            (CommissionState.Failed, "failed"),
            (CommissionState.Canceled, "canceled"),
            (CommissionState.Rejected, "rejected")
        ];

        foreach ((CommissionState state, string wire) in states)
            Require(JsonSerializer.Serialize(state, GuildJsonContext.Default.CommissionState) == $"\"{wire}\"", "Commission wire state changed.");

        RejectJson("42", GuildJsonContext.Default.CommissionState);

        RejectJson("\"future-success-state\"", GuildJsonContext.Default.CommissionState);

        GuildRemoteStateProjection unknown = GuildRemoteStateProjection.Observe(new string('x', 4096));

        Require(unknown.State == CommissionState.Working && unknown.ReconciliationRequired && unknown.RemoteStateDiagnostic!.Length <= 128, "Unknown peer states must remain reconciliable nonterminal observations.");

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

        throw new InvalidOperationException("A closed Guild enum accepted an unsupported literal.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static AcquireWorkspaceLeaseRequest SampleAcquireWorkspaceLeaseRequest() => new(IdempotencyKey: Id, WorkspaceId: Id, Scopes: [SampleGuildPathScope()]);

    private static AdmitCohortMemberRequest SampleAdmitCohortMemberRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Commission: SampleGuildIssueChildData());

    private static AppendSharedNoteRequest SampleAppendSharedNoteRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Kind: GuildNoteKind.Correction, SubjectId: Id, Body: "A bounded Guild result.\nSecond line: λ", CorrectsNoteId: Id);

    private static AwardWorkRequest SampleAwardWorkRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ExpectedCohortRevision: 17L, BidId: Id);

    private static CohortContributionDto SampleCohortContributionDto() => new(CohortId: Id, MemberId: Id, PrimaryVotingCommissionId: Id, ResultSequence: 17L, Outcome: GuildContributionOutcome.Accepted, ResultRef: SampleGuildDataRef(), AgreementDigest: "sample-AgreementDigest", Selected: true);

    private static CohortContributionPageDto SampleCohortContributionPageDto() => new(Items: [SampleCohortContributionDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortConvergenceDto SampleCohortConvergenceDto() => new(CohortId: Id, Revision: 17L, State: GuildCohortState.Completed, Decision: GuildConvergenceDecision.Success, ReasonCode: "sample-ReasonCode", SelectedResultRef: SampleGuildDataRef(), FrozenMemberCount: 17L, PendingMemberCount: 17L, PendingDescendantCount: 17L, CancelRequestedCount: 17L, Spend: SampleGuildSpendDto(), EventWatermark: 17L);

    private static CohortDto SampleCohortDto() => new(Id: Id, Name: "sample-Name", RootCommissionId: Id, IssuerCommissionId: Id, CampaignId: Id, Policy: SampleGuildConvergencePolicy(), ResultContract: SampleGuildResultContract(), State: GuildCohortState.Completed, Revision: 17L, MembershipVersion: 17L, ClosedMembershipVersion: 17L, CohortGrant: SampleGuildGrant(), AllocationAccountId: Id, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static CohortMemberCommissionDto SampleCohortMemberCommissionDto() => new(MemberId: Id, CommissionId: Id, AssociationKind: GuildMemberAssociationKind.Primary, AssociationSequence: 17L);

    private static CohortMemberCommissionPageDto SampleCohortMemberCommissionPageDto() => new(Items: [SampleCohortMemberCommissionDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortMemberDto SampleCohortMemberDto() => new(Id: Id, CohortId: Id, PrimaryVotingCommissionId: Id, AgentId: Id, AdmissionSequence: 17L, State: GuildCohortMemberState.Active, InClosedSnapshot: true, RemovalSequence: null, AssociatedCommissionCount: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static CohortMemberPageDto SampleCohortMemberPageDto() => new(Items: [SampleCohortMemberDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CohortPageDto SampleCohortPageDto() => new(Items: [SampleCohortDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static CreateCohortRequest SampleCreateCohortRequest() => new(IdempotencyKey: Id, IssuerCommissionId: Id, Name: "sample-Name", Policy: SampleGuildConvergencePolicy(), ResultContract: SampleGuildResultContract(), RequestedGrant: SampleGuildRequestedGrant());

    private static GuildActionRequest SampleGuildActionRequest() => new(Action: GuildAuthorityAction.Issue, ResourceId: Id);

    private static GuildAdmissionReceipt SampleGuildAdmissionReceipt() => new(CommissionId: Id, OperationId: Id, SignetId: Id, AllocationAccountId: Id, Receipt: SampleGuildMutationReceiptDto());

    private static GuildAdmissionRequest SampleGuildAdmissionRequest() => new(Mutation: SampleGuildMutationIdentity(), Issuance: SampleGuildIssuanceIdentity(), Commission: SampleGuildCommissionDto(), Goal: SampleGuildDataRef(), AcceptedResolutionDigest: "sample-AcceptedResolutionDigest", ExpectedIssuerRevision: 17L);

    private static GuildAgentCapabilitiesDto SampleGuildAgentCapabilitiesDto() => new(Streaming: true, PushNotifications: true, StateTransitionHistory: true);

    private static GuildAgentCard SampleGuildAgentCard() => new(AgentId: Id, CardId: Id, Revision: 17L, Name: "sample-Name", Description: "sample-Description", Version: "sample-Version", Skills: [SampleGuildAgentSkillDto()], DefaultInputModes: ["text/plain"], DefaultOutputModes: ["text/plain"], Capabilities: SampleGuildAgentCapabilitiesDto(), Source: SampleGuildAgentSourceDto());

    private static GuildAgentCardDto SampleGuildAgentCardDto() => new(AgentId: Id, CardId: Id, Revision: 17L, Name: "sample-Name", Description: "sample-Description", Version: "sample-Version", Skills: [SampleGuildAgentSkillDto()], DefaultInputModes: ["text/plain"], DefaultOutputModes: ["text/plain"], Capabilities: SampleGuildAgentCapabilitiesDto(), Source: SampleGuildAgentSourceDto());

    private static GuildAgentCardPageDto SampleGuildAgentCardPageDto() => new(Items: [SampleGuildAgentCardDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildAgentSkillDto SampleGuildAgentSkillDto() => new(Id: "sample-Id", Name: "sample-Name", Description: "sample-Description", Tags: ["sample-value"], InputModes: ["text/plain"], OutputModes: ["text/plain"]);

    private static GuildAgentSourceDto SampleGuildAgentSourceDto() => new(Kind: GuildAgentSourceKind.Remote, WorkspaceId: null, PeerId: "configured-peer", Available: true, ReasonCode: "sample-ReasonCode");

    private static GuildBudgetGrant SampleGuildBudgetGrant() => new(Usd: SampleGuildUsdBudget(), Tokens: SampleGuildTokenBudget());

    private static GuildCancelCommissionRequest SampleGuildCancelCommissionRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ReasonCode: GuildCancellationReason.OperatorRequested);

    private static GuildCancellationDto SampleGuildCancellationDto() => new(CommissionId: Id, RootCommissionId: Id, Revision: 17L, CancellationRequested: true, CancellationConverged: true, PendingExecutionCount: 0L, UnresolvedExternalEffectCount: 0L, AccountingState: GuildAccountingState.Settled, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildChangeEventDto SampleGuildChangeEventDto() => new(EventSequence: 17L, EventKind: GuildEventKind.CommissionChanged, ObjectId: Id, Revision: 17L, ContentRefs: [SampleGuildDataRef()]);

    private static GuildCircleAcknowledgeRequest SampleGuildCircleAcknowledgeRequest() => new(Mutation: SampleGuildMutationIdentity(), ApplicationReceiptId: Id, ApplicationDigest: "sample-ApplicationDigest", ExpectedInboxRevision: 17L);

    private static GuildCircleAcknowledgement SampleGuildCircleAcknowledgement() => new(InboxId: Id, ApplicationReceiptId: Id, Revision: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildCircleAddress SampleGuildCircleAddress() => new(GuildCircleAddressKind.Child, Id, null, null);

    private static GuildCircleInboxItem SampleGuildCircleInboxItem() => new(InboxId: Id, Envelope: SampleGuildEnvelope(), Revision: 17L);

    private static GuildCircleInboxPage SampleGuildCircleInboxPage() => new(Items: [SampleGuildCircleInboxItem()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildCircleInboxQuery SampleGuildCircleInboxQuery() => new(RecipientCommissionId: Id, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildCohortContributionRequest SampleGuildCohortContributionRequest() => new(Mutation: SampleGuildMutationIdentity(), CohortId: Id, MemberId: Id, ExpectedRevision: 17L, Result: SampleGuildResultReceipt());

    private static GuildCohortSnapshotDto SampleGuildCohortSnapshotDto() => new(Summary: SampleCohortDto(), Members: SampleCohortMemberPageDto(), Watermark: 17L, ResumeCursor: "sample-ResumeCursor");

    private static GuildCommissionDto SampleGuildCommissionDto() => new(Id: Id, Name: "sample-Name", OperationId: Id, AgentId: Id, CardId: Id, CardRevision: 17L, RunId: Id, ParentCommissionId: Id, RootCommissionId: Id, SessionId: Id, CampaignId: Id, WorkspaceId: Id, SignetId: Id, SignetRevision: 17L, Revision: 17L, IssuedSequence: 17L, State: CommissionState.Completed, WaitReason: GuildWaitReason.Input, CancellationRequested: true, CancellationConverged: true, ReconciliationRequired: true, DeliveryState: GuildDeliveryState.None, AccountingState: GuildAccountingState.Settled, GoalRef: SampleGuildDataRef(), ResultRef: SampleGuildDataRef(), ResultReceiptId: Id, ResolvedGrant: SampleGuildGrant(), CreatedAt: Instant, UpdatedAt: Instant, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildCommissionPageDto SampleGuildCommissionPageDto() => new(Items: [SampleGuildCommissionDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildCommitResultRequest SampleGuildCommitResultRequest() => new(Mutation: SampleGuildMutationIdentity(), ExpectedCommissionRevision: 17L, ExpectedOperationRevision: 17L, Result: SampleGuildResultReceipt());

    private static GuildContentDto SampleGuildContentDto() => new(Reference: SampleGuildDataRef(), Body: "A bounded Guild result.\nSecond line: λ");

    private static GuildContinueRequest SampleGuildContinueRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ContinuationId: Id, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ");

    private static GuildConvergencePolicy SampleGuildConvergencePolicy() => new(GuildConvergenceKind.Quorum, 2);

    private static GuildDataRef SampleGuildDataRef() => new(ContentId: Id, Kind: GuildContentKind.AgentCard, Revision: 17L, Digest: "0000000000000000000000000000000000000000000000000000000000000000", MimeType: "text/plain", ByteLength: 17L, ContentErased: true);

    private static GuildDispatchObservation SampleGuildDispatchObservation() => new(CommissionId: Id, DispatchId: Id, Evidence: GuildDispatchEvidence.Prepared, State: CommissionState.Completed, ReconciliationRequired: true, ReasonCode: "sample-ReasonCode", LocalChildId: Id, RemoteTaskId: "sample-RemoteTaskId", RemoteContextId: "sample-RemoteContextId");

    private static GuildDispatchRequest SampleGuildDispatchRequest() => new(Mutation: SampleGuildMutationIdentity(), CommissionId: Id, OperationId: Id, RunId: Id, DispatchId: Id, ExpectedOperationRevision: 17L, InvocationDigest: "sample-InvocationDigest");

    private static GuildEnvelope SampleGuildEnvelope() => new(Id: Id, Producer: SampleGuildProducerIdentity(), CommissionId: Id, RootCommissionId: Id, AuthorityGeneration: 17L, SessionId: Id, CampaignId: Id, Authority: GuildAuthorityKind.Parent, Kind: GuildEnvelopeKind.Progress, ContentRef: SampleGuildDataRef(), SenderSequence: 17L, CreatedAt: Instant, Address: SampleGuildCircleAddress());

    private static GuildEventPage SampleGuildEventPage() => new(Items: [SampleGuildChangeEventDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildEventQuery SampleGuildEventQuery() => new(RootCommissionId: Id, SubtreeCommissionId: Id, CohortId: Id, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildGrant SampleGuildGrant() => new(Workspaces: [SampleGuildWorkspaceGrant()], Tools: [SampleGuildToolGrant()], Memory: [SampleGuildMemoryGrant()], Budget: SampleGuildBudgetGrant(), Delegation: [GuildDelegationKind.Local], ExpiresAtUtc: Instant);

    private static GuildIssuanceIdentity SampleGuildIssuanceIdentity() => new(Id, null, Id, Id, Id);

    private static GuildIssueChildData SampleGuildIssueChildData() => new(AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant());

    private static GuildIssueChildRequest SampleGuildIssueChildRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant());

    private static GuildIssueRootRequest SampleGuildIssueRootRequest() => new(IdempotencyKey: Id, AgentId: Id, Name: "sample-Name", Goal: "sample-Goal", Grant: SampleGuildGrant(), SessionId: Id, CampaignId: Id, WorkspaceId: Id, PredecessorCommissionId: Id);

    private static GuildListAgentsQuery SampleGuildListAgentsQuery() => new(Source: GuildAgentSourceKind.DeclaredGlobal, Skill: "sample-Skill", Cursor: "sample-Cursor", PageSize: 3);

    private static GuildListCommissionsQuery SampleGuildListCommissionsQuery() => new(RootId: Id, ParentId: Id, State: CommissionState.Completed, Cursor: "sample-Cursor", PageSize: 3);

    private static GuildMemoryGrant SampleGuildMemoryGrant() => new(Store: GuildMemoryStore.Transcript, Operation: GuildMemoryOperation.Read, Scope: SampleGuildMemoryScope());

    private static GuildMemoryScope SampleGuildMemoryScope() => new(GuildMemoryScopeKind.Campaign, Id);

    private static GuildMessageReceipt SampleGuildMessageReceipt() => new(CommissionId: Id, MessageId: Id, Revision: 17L, DeliveryState: GuildDeliveryState.None, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildMutationIdentity SampleGuildMutationIdentity() => new(IdempotencyKey: Id, CanonicalRequestDigest: "1111111111111111111111111111111111111111111111111111111111111111");

    private static GuildMutationPlan SampleGuildMutationPlan() => new(WorkspaceId: Id, Scopes: [SampleGuildPathScope()], EffectPlanDigest: "sample-EffectPlanDigest");

    private static GuildMutationReceiptDto SampleGuildMutationReceiptDto() => new(Id: Id, IdempotencyKey: Id, Replayed: true, AcceptedAt: Instant, AcceptedRevision: 17L);

    private static GuildPageQuery SampleGuildPageQuery() => new(Cursor: "sample-Cursor", PageSize: 3);

    private static GuildPathScope SampleGuildPathScope() => new(RelativePath: "src", Kind: GuildPathScopeKind.File);

    private static GuildProducerIdentity SampleGuildProducerIdentity() => new(GuildProducerKind.Run, Id, Id, Id, null, null, null, null);

    private static GuildProducerStreamKey SampleGuildProducerStreamKey() => SampleGuildProducerIdentity().StreamKey(Id);

    private static GuildPublishDedupeKey SampleGuildPublishDedupeKey() => SampleGuildProducerIdentity().DedupeKey(Id, Id);

    private static GuildPublishRequest SampleGuildPublishRequest() => new(IdempotencyKey: Id, Address: SampleGuildCircleAddress(), Kind: GuildEnvelopeKind.CommissionIssued, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ", ExpectedRevision: 17L);

    private static GuildRefreshAgentsRequest SampleGuildRefreshAgentsRequest() => new(IdempotencyKey: Id, AgentId: Id, Cursor: "sample-Cursor");

    private static GuildRefreshDiagnosticDto SampleGuildRefreshDiagnosticDto() => new(AgentId: Id, SourceKind: GuildAgentSourceKind.DeclaredGlobal, ReasonCode: "sample-ReasonCode", Available: true, CardRevision: 17L);

    private static GuildRefreshDiagnosticPageDto SampleGuildRefreshDiagnosticPageDto() => new(Items: [SampleGuildRefreshDiagnosticDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static GuildRefreshReceipt SampleGuildRefreshReceipt() => new(Receipt: SampleGuildMutationReceiptDto(), Diagnostics: SampleGuildRefreshDiagnosticPageDto(), Completed: true);

    private static GuildRemoteStateProjection SampleGuildRemoteStateProjection() => GuildRemoteStateProjection.Observe("future-state");

    private static GuildRemoteTransportDescriptor SampleGuildRemoteTransportDescriptor() => new(PeerId: "sample-PeerId", Endpoint: "https://peer.example/a2a", ProtocolVersion: "sample-ProtocolVersion", Interfaces: ["sample-value"], AuthenticationSchemes: ["sample-value"], CredentialReference: "sample-CredentialReference", ProviderLink: "sample-ProviderLink", DocumentationLink: "sample-DocumentationLink");

    private static GuildRequestedGrant SampleGuildRequestedGrant() => new(Budget: new(true, SampleGuildBudgetGrant()), ExpiresAtUtc: new(true, Instant));

    private static GuildResultApplicationReceipt SampleGuildResultApplicationReceipt() => new(RecipientCommissionId: Id, CommissionId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", RecipientRevision: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static GuildResultApplicationRequest SampleGuildResultApplicationRequest() => new(Mutation: SampleGuildMutationIdentity(), RecipientCommissionId: Id, CommissionId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", ExpectedRecipientRevision: 17L);

    private static GuildResultContract SampleGuildResultContract() => new(MimeType: "text/plain");

    private static GuildResultReceipt SampleGuildResultReceipt() => new(CommissionId: Id, ResultId: Id, CanonicalContentDigest: "sample-CanonicalContentDigest", Content: SampleGuildDataRef(), Outcome: CommissionState.Completed, ReasonCode: "sample-ReasonCode");

    private static GuildResyncEventDto SampleGuildResyncEventDto() => new(ReasonCode: "sample-ReasonCode", SnapshotRoute: "/api/guild/commissions/a1111111-1111-4111-8111-111111111111", ResumeCursor: "sample-ResumeCursor");

    private static GuildRevisionRequest SampleGuildRevisionRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L);

    private static GuildRevokeSignetRequest SampleGuildRevokeSignetRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, ReasonCode: GuildCancellationReason.OperatorRequested);

    private static GuildSendOperatorMessageRequest SampleGuildSendOperatorMessageRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Kind: GuildMessageKind.Message, MimeType: "text/plain", Body: "A bounded Guild result.\nSecond line: λ");

    private static GuildSignetBinding SampleGuildSignetBinding() => new(SignetId: Id, AgentId: Id, RunId: Id, CommissionId: Id, RootCommissionId: Id, IssuerSignetId: Id, Grant: SampleGuildGrant(), IssuedAtUtc: Instant, OriginPrincipalId: Id);

    private static GuildSignetStatus SampleGuildSignetStatus() => new(SignetId: Id, Revision: 17L, CredentialGeneration: 17L, RevocationGeneration: 17L, Revoked: true, RestoredInert: true);

    private static GuildSpendDto SampleGuildSpendDto() => new(CommissionId: Id, KnownUsd: 1.25m, KnownTokens: 17L, UnknownUsd: true, UnknownTokens: true, PricedCount: 17L, UnpricedCount: 17L, UncertainCount: 17L, OutstandingAllocation: SampleGuildBudgetGrant(), UnresolvedHolds: SampleGuildBudgetGrant(), Remaining: SampleGuildBudgetGrant(), EventWatermark: 17L);

    private static GuildStatusDto SampleGuildStatusDto() => new(Enabled: true, Available: true, ReasonCodes: ["sample-value"], SchemaVersion: 17L, LegacyPendingCount: 17L);

    private static GuildTerminalEventDto SampleGuildTerminalEventDto() => new(ObjectId: Id, Revision: 17L, State: CommissionState.Completed, AccountingState: GuildAccountingState.Settled, EventWatermark: 17L);

    private static GuildTokenBudget SampleGuildTokenBudget() => new(Mode: GuildBudgetMode.Finite, Value: 17L);

    private static GuildToolGrant SampleGuildToolGrant() => new(SourceKind: GuildToolSourceKind.BuiltIn, SourceRegistrationId: "arcanum", Name: "sample-Name");

    private static GuildTreeSnapshotDto SampleGuildTreeSnapshotDto() => new(Summary: SampleGuildCommissionDto(), Members: SampleGuildCommissionPageDto(), Watermark: 17L, ResumeCursor: "sample-ResumeCursor");

    private static GuildUsdBudget SampleGuildUsdBudget() => new(Mode: GuildBudgetMode.Finite, Value: 1.25m);

    private static GuildWorkspaceGrant SampleGuildWorkspaceGrant() => new(WorkspaceId: Id, RelativeRoot: "src", Access: GuildWorkspaceAccess.Read);

    private static PostWorkOfferRequest SamplePostWorkOfferRequest() => new(IdempotencyKey: Id, ExpectedCohortRevision: 17L, SkillId: "apprentice-goal-execution", Goal: "sample-Goal", RequestedGrant: SampleGuildRequestedGrant(), ResultContract: SampleGuildResultContract());

    private static ReleaseWorkspaceLeaseRequest SampleReleaseWorkspaceLeaseRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Epoch: 17L);

    private static RenewWorkspaceLeaseRequest SampleRenewWorkspaceLeaseRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Epoch: 17L);

    private static SharedNoteDto SampleSharedNoteDto() => new(Id: Id, CohortId: Id, Sequence: 17L, AuthorCommissionId: Id, AuthorMemberId: Id, Authority: GuildAuthorityKind.Operator, Kind: GuildNoteKind.Correction, SubjectId: Id, BodyRef: SampleGuildDataRef(), CorrectsNoteId: Id, CreatedAt: Instant, Receipt: SampleGuildMutationReceiptDto());

    private static SharedNotePageDto SampleSharedNotePageDto() => new(Items: [SampleSharedNoteDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static SignetRevocationDto SampleSignetRevocationDto() => new(SignetId: Id, Revision: 17L, Generation: 17L, Revoked: true, CancellationRequested: true, CancellationConverged: true, EventWatermark: 17L, Receipt: SampleGuildMutationReceiptDto());

    private static SubmitBidRequest SampleSubmitBidRequest() => new(IdempotencyKey: Id, ExpectedRevision: 17L, Note: "sample-Note", ClaimedCostUsd: 1.25m);

    private static WorkAwardDto SampleWorkAwardDto() => new(Id: Id, OfferId: Id, BidId: Id, CommissionId: Id, MemberId: Id, State: GuildWorkAwardState.Committed, Receipt: SampleGuildMutationReceiptDto());

    private static WorkBidDto SampleWorkBidDto() => new(Id: Id, OfferId: Id, MemberId: Id, AgentId: Id, CardRevision: 17L, BidSequence: 17L, Revision: 17L, State: GuildWorkBidState.Submitted, NoteRef: SampleGuildDataRef(), ClaimedCostUsd: 1.25m, Receipt: SampleGuildMutationReceiptDto());

    private static WorkBidPageDto SampleWorkBidPageDto() => new(Items: [SampleWorkBidDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static WorkOfferDto SampleWorkOfferDto() => new(Id: Id, CohortId: Id, IssuerCommissionId: Id, OfferSequence: 17L, Revision: 17L, SkillId: "apprentice-goal-execution", ResultContract: SampleGuildResultContract(), GoalRef: SampleGuildDataRef(), State: GuildWorkOfferState.Awarded, AwardId: Id, CommissionId: Id, Receipt: SampleGuildMutationReceiptDto());

    private static WorkOfferPageDto SampleWorkOfferPageDto() => new(Items: [SampleWorkOfferDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");

    private static WorkspaceLeaseDto SampleWorkspaceLeaseDto() => new(Id: Id, WorkspaceId: Id, OwnerKind: GuildWorkspaceLeaseOwnerKind.Commission, CommissionId: Id, RootCommissionId: Id, Scopes: [SampleGuildPathScope()], State: GuildWorkspaceLeaseState.Held, Epoch: 17L, Revision: 17L, ExpiresAt: Instant, InFlightMutationCount: 17L, ReasonCode: "sample-ReasonCode", Receipt: SampleGuildMutationReceiptDto());

    private static WorkspaceLeasePageDto SampleWorkspaceLeasePageDto() => new(Items: [SampleWorkspaceLeaseDto()], NextCursor: "sample-NextCursor", SnapshotSequence: 17L, StateConsistency: "livePerPage");
}
