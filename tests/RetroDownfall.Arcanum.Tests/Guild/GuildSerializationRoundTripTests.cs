using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSerializationRoundTripTests
{
    private static readonly Guid SampleId = Guid.Parse("a1111111-1111-4111-8111-111111111111");

    // Explicit reviewed inventory: adding a field or public data contract requires fixture review.
    private static readonly string[] ReviewedRecords =
    [
        "AcquireWorkspaceLeaseRequest",
        "AdmitCohortMemberRequest",
        "AppendSharedNoteRequest",
        "AwardWorkRequest",
        "CohortContributionDto",
        "CohortContributionPageDto",
        "CohortConvergenceDto",
        "CohortDto",
        "CohortMemberQuestDto",
        "CohortMemberQuestPageDto",
        "CohortMemberDto",
        "CohortMemberPageDto",
        "CohortPageDto",
        "CreateCohortRequest",
        "GuildActionRequest",
        "GuildAdmissionReceipt",
        "GuildAdmissionRequest",
        "GuildAgentCapabilitiesDto",
        "GuildAgentCard",
        "GuildAgentCardDto",
        "GuildAgentCardPageDto",
        "GuildAgentSkillDto",
        "GuildAgentSourceDto",
        "GuildBudgetGrant",
        "GuildCancelQuestRequest",
        "GuildCancellationDto",
        "GuildChangeEventDto",
        "GuildCircleAcknowledgeRequest",
        "GuildCircleAcknowledgement",
        "GuildCircleAddress",
        "GuildCircleInboxItem",
        "GuildCircleInboxPage",
        "GuildCircleInboxQuery",
        "GuildCohortContributionRequest",
        "GuildCohortSnapshotDto",
        "GuildQuestDto",
        "GuildQuestInputDto",
        "GuildQuestPageDto",
        "GuildQuestInputEvidence",
        "GuildQuestProjectedState",
        "GuildQuestProjectionEvidence",
        "GuildCommitResultRequest",
        "GuildContentDto",
        "GuildContinueRequest",
        "GuildConvergencePolicy",
        "GuildDataRef",
        "GuildDispatchObservation",
        "GuildDispatchRequest",
        "GuildEnvelope",
        "GuildEventPage",
        "GuildEventQuery",
        "GuildGrant",
        "GuildIssuanceIdentity",
        "GuildIssueChildData",
        "GuildIssueChildRequest",
        "GuildIssueRootRequest",
        "GuildListAgentsQuery",
        "GuildListQuestsQuery",
        "GuildMemoryGrant",
        "GuildMemoryScope",
        "GuildMessageReceipt",
        "GuildMutationIdentity",
        "GuildMutationPlan",
        "GuildMutationReceiptDto",
        "GuildPageQuery",
        "GuildPathScope",
        "GuildProducerIdentity",
        "GuildProducerStreamKey",
        "GuildPublishDedupeKey",
        "GuildPublishRequest",
        "GuildRefreshAgentsRequest",
        "GuildRefreshDiagnosticDto",
        "GuildRefreshDiagnosticPageDto",
        "GuildRefreshReceipt",
        "GuildRemoteInterfaceDescriptor",
        "GuildRemoteInvocationOptions",
        "GuildRemoteStateProjection",
        "GuildRemoteTransportDescriptor",
        "GuildResolveRemoteAgentRequest",
        "GuildRequestedGrant",
        "GuildResultApplicationReceipt",
        "GuildResultApplicationRequest",
        "GuildResultContract",
        "GuildResultReceipt",
        "GuildResyncEventDto",
        "GuildRevisionRequest",
        "GuildRevokeSignetRequest",
        "GuildSendOperatorMessageRequest",
        "GuildSignetBinding",
        "GuildSignetStatus",
        "GuildSpendDto",
        "GuildStatusDto",
        "GuildTerminalEventDto",
        "GuildTokenBudget",
        "GuildToolGrant",
        "GuildTreeSnapshotDto",
        "GuildUsdBudget",
        "GuildWorkspaceGrant",
        "PostWorkOfferRequest",
        "ReleaseWorkspaceLeaseRequest",
        "RenewWorkspaceLeaseRequest",
        "SharedNoteDto",
        "SharedNotePageDto",
        "SignetRevocationDto",
        "SubmitBidRequest",
        "WorkAwardDto",
        "WorkBidDto",
        "WorkBidPageDto",
        "WorkOfferDto",
        "WorkOfferPageDto",
        "WorkspaceLeaseDto",
        "WorkspaceLeasePageDto",
    ];

    private static readonly IReadOnlyDictionary<string, string> ReviewedParameters = new Dictionary<string, string>
    {
        ["AcquireWorkspaceLeaseRequest"] = "IdempotencyKey,WorkspaceId,Scopes",
        ["AdmitCohortMemberRequest"] = "IdempotencyKey,ExpectedRevision,Quest",
        ["AppendSharedNoteRequest"] = "IdempotencyKey,ExpectedRevision,Kind,SubjectId,Body,CorrectsNoteId",
        ["AwardWorkRequest"] = "IdempotencyKey,ExpectedRevision,ExpectedCohortRevision,BidId",
        ["CohortContributionDto"] = "CohortId,MemberId,PrimaryVotingQuestId,ResultSequence,Outcome,ResultRef,AgreementDigest,Selected",
        ["CohortContributionPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["CohortConvergenceDto"] = "CohortId,Revision,State,Decision,ReasonCode,SelectedResultRef,FrozenMemberCount,PendingMemberCount,PendingDescendantCount,CancelRequestedCount,Spend,EventWatermark",
        ["CohortDto"] = "Id,Name,RootQuestId,IssuerQuestId,CampaignId,Policy,ResultContract,State,Revision,MembershipVersion,ClosedMembershipVersion,CohortGrant,AllocationAccountId,EventWatermark,Receipt",
        ["CohortMemberQuestDto"] = "MemberId,QuestId,AssociationKind,AssociationSequence",
        ["CohortMemberQuestPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["CohortMemberDto"] = "Id,CohortId,PrimaryVotingQuestId,AgentId,AdmissionSequence,State,InClosedSnapshot,RemovalSequence,AssociatedQuestCount,Receipt",
        ["CohortMemberPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["CohortPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["CreateCohortRequest"] = "IdempotencyKey,IssuerQuestId,Name,Policy,ResultContract,RequestedGrant",
        ["GuildActionRequest"] = "Action,ResourceId",
        ["GuildAdmissionReceipt"] = "QuestId,OperationId,SignetId,AllocationAccountId,Receipt",
        ["GuildAdmissionRequest"] = "Mutation,Issuance,Quest,Goal,AcceptedResolutionDigest,ExpectedIssuerRevision",
        ["GuildAgentCapabilitiesDto"] = "Streaming,PushNotifications,StateTransitionHistory",
        ["GuildAgentCard"] = "AgentId,CardId,Revision,Name,Description,Version,Skills,DefaultInputModes,DefaultOutputModes,Capabilities,Source",
        ["GuildAgentCardDto"] = "AgentId,CardId,Revision,Name,Description,Version,Skills,DefaultInputModes,DefaultOutputModes,Capabilities,Source",
        ["GuildAgentCardPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["GuildAgentSkillDto"] = "Id,Name,Description,Tags,InputModes,OutputModes",
        ["GuildAgentSourceDto"] = "Kind,WorkspaceId,PeerId,Available,ReasonCode",
        ["GuildBudgetGrant"] = "Usd,Tokens",
        ["GuildCancelQuestRequest"] = "IdempotencyKey,ExpectedRevision,ReasonCode",
        ["GuildCancellationDto"] = "QuestId,RootQuestId,Revision,CancellationRequested,CancellationConverged,PendingExecutionCount,UnresolvedExternalEffectCount,AccountingState,EventWatermark,Receipt",
        ["GuildChangeEventDto"] = "EventSequence,EventKind,ObjectId,Revision,ContentRefs,QuestId",
        ["GuildCircleAcknowledgeRequest"] = "Mutation,ApplicationReceiptId,ApplicationDigest,ExpectedInboxRevision",
        ["GuildCircleAcknowledgement"] = "InboxId,ApplicationReceiptId,Revision,Receipt",
        ["GuildCircleAddress"] = "Kind,QuestId,CohortId,MembershipVersion",
        ["GuildCircleInboxItem"] = "InboxId,Envelope,Revision,DeliveryState,ReasonCode,EnvelopeId",
        ["GuildCircleInboxPage"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["GuildCircleInboxQuery"] = "RecipientQuestId,Cursor,PageSize",
        ["GuildCohortContributionRequest"] = "Mutation,CohortId,MemberId,ExpectedRevision,Result",
        ["GuildCohortSnapshotDto"] = "Summary,Members,Watermark,ResumeCursor",
        ["GuildQuestDto"] = "Id,Name,OperationId,AgentId,CardId,CardRevision,RunId,ParentQuestId,RootQuestId,SessionId,CampaignId,WorkspaceId,SignetId,SignetRevision,Revision,IssuedSequence,State,WaitReason,CancellationRequested,CancellationConverged,ReconciliationRequired,DeliveryState,AccountingState,GoalRef,ResultRef,ResultReceiptId,ResolvedGrant,CreatedAt,UpdatedAt,EventWatermark,Receipt,AwaitingInput",
        ["GuildQuestInputDto"] = "QuestId,Revision,AwaitingInput,Question",
        ["GuildQuestPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["GuildQuestInputEvidence"] = "QuestionId,ContinuationId",
        ["GuildQuestProjectedState"] = "State,WaitReason,CancellationRequested,CancellationConverged,ReconciliationRequired,ReasonCode",
        ["GuildQuestProjectionEvidence"] = "QuestId,OperationId,LastNonterminalState,WaitReason,AwaitingInput,ResultReceipt,NoDispatchProven,WorkStoppedProven,SafelyAbandoned,CancellationRequested,CancellationConverged",
        ["GuildCommitResultRequest"] = "Mutation,ExpectedQuestRevision,ExpectedOperationRevision,Result",
        ["GuildContentDto"] = "Reference,Body",
        ["GuildContinueRequest"] = "IdempotencyKey,ExpectedRevision,ContinuationId,MimeType,Body,RemoteOptions",
        ["GuildConvergencePolicy"] = "Kind,Quorum",
        ["GuildDataRef"] = "ContentId,Kind,Revision,Digest,MimeType,ByteLength,ContentErased",
        ["GuildDispatchObservation"] = "QuestId,DispatchId,Evidence,State,ReconciliationRequired,ReasonCode,LocalChildId,RemoteTaskId,RemoteContextId",
        ["GuildDispatchRequest"] = "Mutation,QuestId,OperationId,RunId,DispatchId,ExpectedOperationRevision,InvocationDigest",
        ["GuildEnvelope"] = "Id,Producer,QuestId,RootQuestId,AuthorityGeneration,SessionId,CampaignId,Authority,Kind,ContentRef,SenderSequence,CreatedAt,Address",
        ["GuildEventPage"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["GuildEventQuery"] = "RootQuestId,SubtreeQuestId,CohortId,Cursor,PageSize",
        ["GuildGrant"] = "Workspaces,Tools,Memory,Budget,Delegation,ExpiresAtUtc",
        ["GuildIssuanceIdentity"] = "InstallationGeneration,IssuerQuestId,RootInvocationId,IssuingStepId,LogicalCallId",
        ["GuildIssueChildData"] = "AgentId,Name,Goal,RequestedGrant",
        ["GuildIssueChildRequest"] = "IdempotencyKey,ExpectedRevision,AgentId,Name,Goal,RequestedGrant,RemoteOptions",
        ["GuildIssueRootRequest"] = "IdempotencyKey,AgentId,Name,Goal,Grant,SessionId,CampaignId,WorkspaceId,PredecessorQuestId,RemoteOptions",
        ["GuildListAgentsQuery"] = "Source,Skill,Cursor,PageSize",
        ["GuildListQuestsQuery"] = "RootId,ParentId,State,Cursor,PageSize",
        ["GuildMemoryGrant"] = "Store,Operation,Scope",
        ["GuildMemoryScope"] = "Kind,Id",
        ["GuildMessageReceipt"] = "QuestId,MessageId,Revision,DeliveryState,EventWatermark,Receipt",
        ["GuildMutationIdentity"] = "IdempotencyKey,CanonicalRequestDigest",
        ["GuildMutationPlan"] = "WorkspaceId,Scopes,EffectPlanDigest",
        ["GuildMutationReceiptDto"] = "Id,IdempotencyKey,Replayed,AcceptedAt,AcceptedRevision",
        ["GuildPageQuery"] = "Cursor,PageSize",
        ["GuildPathScope"] = "RelativePath,Kind",
        ["GuildProducerIdentity"] = "Kind,AgentId,RunId,QuestId,OperatorPrincipalId,DomainOwnerId,SourceKind,ReceiptId",
        ["GuildProducerStreamKey"] = "Kind,AgentId,RunId,OperatorPrincipalId,DomainOwnerId,SourceKind,QuestId",
        ["GuildPublishDedupeKey"] = "Stream,ProducerRequestId",
        ["GuildPublishRequest"] = "IdempotencyKey,Address,Kind,MimeType,Body,ExpectedRevision",
        ["GuildRefreshAgentsRequest"] = "IdempotencyKey,AgentId,Cursor",
        ["GuildRefreshDiagnosticDto"] = "AgentId,SourceKind,ReasonCode,Available,CardRevision",
        ["GuildRefreshDiagnosticPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["GuildRefreshReceipt"] = "Receipt,Diagnostics,Completed",
        ["GuildRemoteStateProjection"] = "State,ReconciliationRequired,ReasonCode,RemoteStateDiagnostic",
        ["GuildRemoteTransportDescriptor"] = "PeerId,Endpoint,ProtocolVersion,Interfaces,AuthenticationSchemes,CredentialReference,ProviderLink,DocumentationLink,InterfaceObservations",
        ["GuildRemoteInterfaceDescriptor"] = "Endpoint,ProtocolBinding,ProtocolVersion",
        ["GuildRemoteInvocationOptions"] = "Mode,AcceptedOutputModes,SkillId",
        ["GuildResolveRemoteAgentRequest"] = "Agent",
        ["GuildRequestedGrant"] = "Workspaces,Tools,Memory,Budget,Delegation,ExpiresAtUtc",
        ["GuildResultApplicationReceipt"] = "RecipientQuestId,QuestId,ResultId,CanonicalContentDigest,RecipientRevision,Receipt",
        ["GuildResultApplicationRequest"] = "Mutation,RecipientQuestId,QuestId,ResultId,CanonicalContentDigest,ExpectedRecipientRevision",
        ["GuildResultContract"] = "MimeType",
        ["GuildResultReceipt"] = "QuestId,ResultId,CanonicalContentDigest,Content,Outcome,ReasonCode",
        ["GuildResyncEventDto"] = "ReasonCode,SnapshotRoute,ResumeCursor",
        ["GuildRevisionRequest"] = "IdempotencyKey,ExpectedRevision",
        ["GuildRevokeSignetRequest"] = "IdempotencyKey,ExpectedRevision,ReasonCode",
        ["GuildSendOperatorMessageRequest"] = "IdempotencyKey,ExpectedRevision,Kind,MimeType,Body",
        ["GuildSignetBinding"] = "SignetId,AgentId,RunId,QuestId,RootQuestId,IssuerSignetId,Grant,IssuedAtUtc,OriginPrincipalId",
        ["GuildSignetStatus"] = "SignetId,Revision,CredentialGeneration,RevocationGeneration,Revoked,RestoredInert",
        ["GuildSpendDto"] = "QuestId,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,PricedCount,UnpricedCount,UncertainCount,OutstandingAllocation,UnresolvedHolds,Remaining,EventWatermark",
        ["GuildStatusDto"] = "Enabled,Available,ReasonCodes,SchemaVersion,LegacyPendingCount",
        ["GuildTerminalEventDto"] = "ObjectId,Revision,State,AccountingState,EventWatermark",
        ["GuildTokenBudget"] = "Mode,Value",
        ["GuildToolGrant"] = "SourceKind,SourceRegistrationId,Name",
        ["GuildTreeSnapshotDto"] = "Summary,Members,Watermark,ResumeCursor",
        ["GuildUsdBudget"] = "Mode,Value",
        ["GuildWorkspaceGrant"] = "WorkspaceId,RelativeRoot,Access",
        ["PostWorkOfferRequest"] = "IdempotencyKey,ExpectedCohortRevision,SkillId,Goal,RequestedGrant,ResultContract",
        ["ReleaseWorkspaceLeaseRequest"] = "IdempotencyKey,ExpectedRevision,Epoch",
        ["RenewWorkspaceLeaseRequest"] = "IdempotencyKey,ExpectedRevision,Epoch",
        ["SharedNoteDto"] = "Id,CohortId,Sequence,AuthorQuestId,AuthorMemberId,Authority,Kind,SubjectId,BodyRef,CorrectsNoteId,CreatedAt,Receipt",
        ["SharedNotePageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["SignetRevocationDto"] = "SignetId,Revision,Generation,Revoked,CancellationRequested,CancellationConverged,EventWatermark,Receipt",
        ["SubmitBidRequest"] = "IdempotencyKey,ExpectedRevision,Note,ClaimedCostUsd",
        ["WorkAwardDto"] = "Id,OfferId,BidId,QuestId,MemberId,State,Receipt",
        ["WorkBidDto"] = "Id,OfferId,MemberId,AgentId,CardRevision,BidSequence,Revision,State,NoteRef,ClaimedCostUsd,Receipt",
        ["WorkBidPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["WorkOfferDto"] = "Id,CohortId,IssuerQuestId,OfferSequence,Revision,SkillId,ResultContract,GoalRef,State,AwardId,QuestId,Receipt",
        ["WorkOfferPageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
        ["WorkspaceLeaseDto"] = "Id,WorkspaceId,OwnerKind,QuestId,RootQuestId,Scopes,State,Epoch,Revision,ExpiresAt,InFlightMutationCount,ReasonCode,Receipt",
        ["WorkspaceLeasePageDto"] = "Items,NextCursor,SnapshotSequence,StateConsistency",
    };

    public static IEnumerable<object[]> PublicDataGraphs() => ReviewedRecords.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(PublicDataGraphs))]
    public void Every_reviewed_data_graph_round_trips_populated_values_and_collections(string name)
    {
        Type type = typeof(GuildGrant).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}")!;

        Assert.Equal(ReviewedParameters[name], string.Join(",", Assert.Single(type.GetConstructors()).GetParameters().Select(parameter => parameter.Name)));

        object value = Fixture(type);

        RoundTrip(type, value);

        Array array = Array.CreateInstance(type, 1);

        array.SetValue(value, 0);

        RoundTrip(type.MakeArrayType(), array);

        RoundTrip(typeof(ImmutableArray<>).MakeGenericType(type), ImmutableCollection(type, value));
    }

    [Fact]
    public void Reviewed_inventory_covers_all_public_records_and_each_enum_has_named_metadata()
    {
        Type[] types = typeof(GuildGrant).Assembly.GetExportedTypes().Where(type => type.Namespace == "RetroDownfall.Arcanum.Core.Guild").ToArray();

        string[] records = types.Where(type => !type.ContainsGenericParameters && type.GetMethod("<Clone>$") is not null).Select(type => type.Name).Order().ToArray();

        Assert.Equal(ReviewedRecords.Order(), records);

        foreach (Type type in types.Where(type => type.IsEnum))
        {
            JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(GuildJsonContext.Default.GetTypeInfo(type));

            foreach (object value in Enum.GetValues(type))
                Assert.Equal(value, JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata));

            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("999", metadata));

            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"unsupported-future-value\"", metadata));
        }
    }

    [Fact]
    public void Circle_receipts_share_stream_order_but_keep_individual_deduplication()
    {
        Guid quest = SampleId;

        Guid firstReceipt = Guid.Parse("b2222222-2222-4222-8222-222222222222");

        Guid secondReceipt = Guid.Parse("c3333333-3333-4333-8333-333333333333");

        GuildProducerIdentity first = new(GuildProducerKind.HostReceipt, null, null, null, null, SampleId, GuildReceiptSourceKind.Result, firstReceipt);

        GuildProducerIdentity second = new(GuildProducerKind.HostReceipt, null, null, null, null, SampleId, GuildReceiptSourceKind.Result, secondReceipt);

        Assert.Equal(first.StreamKey(quest), second.StreamKey(quest));

        Assert.NotEqual(first.DedupeKey(quest, firstReceipt), second.DedupeKey(quest, secondReceipt));

        Assert.Throws<ArgumentException>(() => first.DedupeKey(quest, secondReceipt));

        Assert.Throws<ArgumentException>(() => first.StreamKey(Guid.Empty));
    }

    [Fact]
    public void Producer_variants_round_trip_and_never_borrow_Run_identity()
    {
        GuildProducerIdentity[] producers =
        [
            new(GuildProducerKind.Run, SampleId, SampleId, SampleId, null, null, null, null),
            new(GuildProducerKind.Operator, null, null, null, "native-operator", null, null, null),
            new(GuildProducerKind.HostReceipt, null, null, null, null, SampleId, GuildReceiptSourceKind.Settlement, SampleId)
        ];

        foreach (GuildProducerIdentity producer in producers)
        {
            string json = JsonSerializer.Serialize(producer, GuildJsonContext.Default.GuildProducerIdentity);

            Assert.Equal(producer, JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildProducerIdentity));
        }

        Assert.Throws<ArgumentException>(() => new GuildProducerIdentity(GuildProducerKind.Run, SampleId, SampleId, null, null, null, null, null));

        Assert.Throws<ArgumentException>(() => new GuildProducerIdentity(GuildProducerKind.Operator, null, null, null, "native-operator", SampleId, null, null));

        Assert.Throws<ArgumentException>(() => producers[0].StreamKey(Guid.NewGuid()));
    }

    private static void RoundTrip(Type type, object value)
    {
        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(GuildJsonContext.Default.GetTypeInfo(type));

        string json = JsonSerializer.Serialize(value, metadata);

        object result = Assert.IsAssignableFrom<object>(JsonSerializer.Deserialize(json, metadata));

        Assert.Equal(json, JsonSerializer.Serialize(result, metadata));

        Assert.NotEqual("{}", json);

        Assert.NotEqual("[]", json);
    }

    private static object Fixture(Type type, string? name = null)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);

        if (nullable is not null)
            return Fixture(nullable, name);

        if (type == typeof(string))
            return name switch
            {
                "StateConsistency" => "livePerPage",
                "MimeType" => "text/plain",
                "Body" => "A bounded Guild result.\nSecond line: \u03bb",
                "Question" => "Which option?\nSecond question: \u03bb",
                "Endpoint" => "https://peer.example/a2a",
                "SnapshotRoute" => "/api/guild/quests/a1111111-1111-4111-8111-111111111111",
                "RelativePath" or "RelativeRoot" => "src",
                _ => $"sample-{name ?? "value"}"
            };

        if (type == typeof(Guid))
            return SampleId;

        if (type == typeof(long))
            return 17L;

        if (type == typeof(int))
            return 3;

        if (type == typeof(decimal))
            return 1.25m;

        if (type == typeof(bool))
            return true;

        if (type == typeof(DateTimeOffset))
            return new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero);

        if (type.IsEnum)
            return Enum.GetValues(type).GetValue(0)!;

        if (type == typeof(GuildProducerIdentity))
            return new GuildProducerIdentity(GuildProducerKind.Run, SampleId, SampleId, SampleId, null, null, null, null);

        if (type == typeof(GuildMemoryScope))
            return new GuildMemoryScope(GuildMemoryScopeKind.Campaign, SampleId);

        if (type == typeof(GuildConvergencePolicy))
            return new GuildConvergencePolicy(GuildConvergenceKind.Quorum, 2);

        if (type == typeof(GuildRequestedGrant))
            return new GuildRequestedGrant(Budget: new(true, new GuildBudgetGrant(new(GuildBudgetMode.Finite, 1.25m), new(GuildBudgetMode.Finite, 17))), ExpiresAtUtc: new(true, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)));

        if (type == typeof(GuildCircleAddress))
            return new GuildCircleAddress(GuildCircleAddressKind.Child, SampleId, null, null);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            Type element = type.GenericTypeArguments[0];

            return ImmutableCollection(element, Fixture(element));
        }

        ConstructorInfo constructor = Assert.Single(type.GetConstructors());

        return constructor.Invoke(constructor.GetParameters().Select(parameter => Fixture(parameter.ParameterType, parameter.Name)).ToArray());
    }

    private static object ImmutableCollection(Type element, object value)
    {
        MethodInfo factory = typeof(ImmutableArray).GetMethods().Single(method => method.Name == "Create" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsGenericParameter);

        return factory.MakeGenericMethod(element).Invoke(null, [value])!;
    }
}
