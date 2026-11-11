using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Serialization;
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

        Exercise(GuildWorkspaceLeaseOwnerKind.Quest, GuildJsonContext.Default.GuildWorkspaceLeaseOwnerKind);

        Exercise(GuildWorkspaceLeaseState.Held, GuildJsonContext.Default.GuildWorkspaceLeaseState);

        ExerciseLiteralsAndBoundaries();

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

        throw new InvalidOperationException("A closed Guild enum accepted an unsupported literal.");
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
