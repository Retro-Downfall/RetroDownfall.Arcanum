using System.Text.Json;

using System.Text.Json.Serialization;

using System.Collections.Immutable;

using System.Reflection;

using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class InstallationResetContractTests
{

    [Fact]
    public void Data_retention_exposes_the_exact_workspace_reset_binding()
    {

        Guid campaignId = Guid.NewGuid();

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetWorkspace,
            Workspace: new DataRetentionWorkspaceBinding(campaignId, "/workspace"));

        Assert.Equal(DataRetentionOperation.ResetWorkspace, request.Operation);

        Assert.Equal(campaignId, request.Workspace!.CampaignId);

        Assert.Equal("/workspace", request.Workspace.WorkspaceRoot);

    }

    [Fact]
    public void Legacy_data_retention_request_omits_the_new_null_workspace_binding()
    {

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        string json = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.DataRetentionRequest);

        Assert.Equal("{\"operation\":\"Prune\",\"targetId\":null,\"memoryScope\":null}", json);

    }

    [Theory]
    [InlineData(typeof(InstallationResetPlan))]
    [InlineData(typeof(InstallationResetResult))]
    [InlineData(typeof(InstallationResetDataPlanRequest))]
    [InlineData(typeof(ApiResponse<InstallationResetPlan>))]
    [InlineData(typeof(ApiResponse<InstallationResetResult>))]
    public void Api_json_context_contains_each_installation_reset_wire_closure(Type type)
    {

        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type));

    }

    [Fact]
    public void Installation_reset_enums_reject_numeric_json()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "0",
            ArcanumJsonContext.Default.InstallationResetScope));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "0",
            ArcanumJsonContext.Default.InstallationResetPhase));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "0",
            ArcanumJsonContext.Default.InstallationResetItemStatus));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "0",
            ArcanumJsonContext.Default.InstallationResetDataHandoff));

    }

    [Fact]
    public void Factory_reset_request_omits_an_absent_installation_handoff_byte_for_byte()
    {

        FactoryResetRequest request = new("factory-reset");

        string json = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.FactoryResetRequest);

        Assert.Equal("{\"confirmation\":\"factory-reset\"}", json);

    }

    [Fact]
    public void Installation_reset_host_handoff_is_required_typed_and_public_api_only()
    {

        Guid requestedOperationId = Guid.Parse(
            "10213243-5465-4687-98a9-bacbdcedfe0f");

        InstallationResetHostHandoff handoff = new(
            requestedOperationId,
            "installation-plan",
            InstallationResetScope.Global,
            Workspace: null,
            new InstallationResetAcceptedBinding(
                "binding",
                ["/selected"],
                ["/excluded"],
                [],
                ["credential"],
                ["data-plan"]));

        FactoryResetRequest request = new(
            "factory-reset",
            ExpectedPlanId: "data-plan",
            RequestedOperationId: requestedOperationId,
            InstallationResetHandoff: handoff);

        string json = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.FactoryResetRequest);

        FactoryResetRequest? roundTrip = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.FactoryResetRequest);

        Assert.NotNull(roundTrip?.InstallationResetHandoff);

        Assert.Equal(
            handoff.RequestedOperationId,
            roundTrip.InstallationResetHandoff.RequestedOperationId);

        Assert.Equal(
            handoff.InstallationPlanId,
            roundTrip.InstallationResetHandoff.InstallationPlanId);

        Assert.Equal(handoff.Scope, roundTrip.InstallationResetHandoff.Scope);

        Assert.Equal(
            handoff.AcceptedBinding.DataPlanIds,
            roundTrip.InstallationResetHandoff.AcceptedBinding.DataPlanIds);

        Assert.NotNull(
            ArcanumJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetHostHandoff)));

        Assert.Null(
            InstallationResetActiveJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetHostHandoff)));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"requestedOperationId\":\"10213243-5465-4687-98a9-bacbdcedfe0f\",\"installationPlanId\":\"installation-plan\",\"scope\":0,\"acceptedBinding\":{}}",
            ArcanumJsonContext.Default.InstallationResetHostHandoff));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.InstallationResetHostHandoff));

    }

    [Fact]
    public void Installation_reset_error_codes_are_wire_stable()
    {

        Assert.Equal("Data.InventoryUnavailable", ErrorCodes.Data.InventoryUnavailable);

        Assert.Equal(
            "Data.CredentialInventoryUnavailable",
            ErrorCodes.Data.CredentialInventoryUnavailable);

        Assert.Equal("Data.ResetInProgress", ErrorCodes.Data.ResetInProgress);

        Assert.Equal("Data.RecoveryRequired", ErrorCodes.Data.RecoveryRequired);

        Assert.Equal("Data.FileLocked", ErrorCodes.Data.FileLocked);

        Assert.Equal("Data.WorkspaceOverlap", ErrorCodes.Data.WorkspaceOverlap);

        Assert.Equal("Data.ControlPathUnavailable", ErrorCodes.Data.ControlPathUnavailable);

    }

    [Fact]
    public void Active_v2_projection_deep_copies_domain_arrays_in_both_directions()
    {

        // Mutation caught: wrapping a mutable domain array instead of copying it, or returning an
        // immutable array's backing storage on open, lets later service mutation rewrite evidence.
        string[] selectedRoots = ["/selected"];

        string[] excludedRoots = ["/excluded"];

        InstallationResetPreservedBackup[] backups =
        [
            new(
                "/backup",
                new InstallationResetFileIdentity("identity", 7, 1)),
        ];

        string[] accounts = ["account"];

        string[] dataPlans = ["data-plan"];

        InstallationResetCredentialResult[] credentials =
        [
            new("account", InstallationResetItemStatus.Pending),
        ];

        InstallationResetActiveRecord domain = new(
            Version: 2,
            OperationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            PlanId: "plan",
            Scope: InstallationResetScope.Workspace,
            Workspace: new DataRetentionWorkspaceBinding(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                "/workspace"),
            AcceptedBinding: new InstallationResetAcceptedBinding(
                "binding",
                selectedRoots,
                excludedRoots,
                backups,
                accounts,
                dataPlans),
            Phase: InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: credentials,
            LastErrorCode: null);

        InstallationResetActivePayloadV2 payload =
            InstallationResetActivePayloadV2.FromRecord(domain);

        selectedRoots[0] = "/mutated";

        excludedRoots[0] = "/mutated";

        backups[0] = new InstallationResetPreservedBackup(
            "/mutated",
            new InstallationResetFileIdentity("mutated", 8, 2));

        accounts[0] = "mutated";

        dataPlans[0] = "mutated";

        credentials[0] = new InstallationResetCredentialResult(
            "mutated",
            InstallationResetItemStatus.Failed,
            "error");

        Assert.Equal("/selected", payload.AcceptedBinding.SelectedRoots[0]);

        Assert.Equal("/excluded", payload.AcceptedBinding.ExcludedRoots[0]);

        Assert.Equal("/backup", payload.AcceptedBinding.PreservedBackups[0].CanonicalPath);

        Assert.Equal("account", payload.AcceptedBinding.CredentialAccounts[0]);

        Assert.Equal("data-plan", payload.AcceptedBinding.DataPlanIds[0]);

        Assert.Equal("account", payload.CredentialResults[0].Account);

        InstallationResetActiveRecord first = payload.ToRecord();

        InstallationResetActiveRecord second = payload.ToRecord();

        Assert.NotSame(first.AcceptedBinding.SelectedRoots, second.AcceptedBinding.SelectedRoots);

        Assert.NotSame(first.AcceptedBinding.ExcludedRoots, second.AcceptedBinding.ExcludedRoots);

        Assert.NotSame(first.AcceptedBinding.PreservedBackups, second.AcceptedBinding.PreservedBackups);

        Assert.NotSame(first.AcceptedBinding.CredentialAccounts, second.AcceptedBinding.CredentialAccounts);

        Assert.NotSame(first.AcceptedBinding.DataPlanIds, second.AcceptedBinding.DataPlanIds);

        Assert.NotSame(first.CredentialResults, second.CredentialResults);

        first.AcceptedBinding.SelectedRoots[0] = "/changed-after-open";

        first.CredentialResults[0] = new InstallationResetCredentialResult(
            "changed-after-open",
            InstallationResetItemStatus.Failed);

        Assert.Equal("/selected", second.AcceptedBinding.SelectedRoots[0]);

        Assert.Equal("account", second.CredentialResults[0].Account);

        Assert.Equal("/selected", payload.AcceptedBinding.SelectedRoots[0]);

        Assert.Equal("account", payload.CredentialResults[0].Account);

    }

    [Fact]
    public void V2_projection_deep_copies_inventory_and_intent_vectors_in_both_directions()
    {

        // Mutation caught: retaining either ImmutableArray backing store or any nested restart
        // projection reference lets mutable runtime state rewrite authenticated checkpoint evidence.
        Guid campaignId = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");

        CampaignMarkerInventoryEntryV1 inventoryEntry = new(
            campaignId,
            PriorPathRevision: 1,
            TestDigest(0x10),
            TestDigest(0x20),
            TestDigest(0x30),
            TestDigest(0x40));

        CampaignMarkerInventoryEntryV1[] inventoryBacking = [inventoryEntry];

        Guid intentId = Guid.Parse("22334455-6677-8899-aabb-ccddeeff0011");

        Guid[] intentBacking = [intentId];

        HostProcessToolsDatabaseMarkerEvidence database = new(
            "00112233-4455-6677-8899-aabbccddeeff",
            RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
            1,
            TestDigest(0x50));

        HostProcessToolsOsMarkerEvidence marker = new(
            database.InstallationIdentity,
            database.TransitionId!.Value,
            database.TaintMasterKeyVersion!.Value,
            database.TaintFingerprint!.Value,
            TestDigest(0x60),
            TestDigest(0x70));

        FullInstallationResetSignedAttestationProjectionV1 signed = new(
            Version: 1,
            OperationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            InstallationId: Guid.Parse(database.InstallationIdentity),
            database.TransitionId.Value,
            database.TaintMasterKeyVersion.Value,
            database.TaintFingerprint.Value,
            database.DatabaseMarkerDigest,
            marker.MarkerBytesDigest,
            TestDigest(0x80),
            "nonce",
            "issuer",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            "signature");

        HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
            Version: 1,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            new FullInstallationResetRestartProofV1(
                Version: 1,
                signed,
                DateTimeOffset.UnixEpoch,
                TestDigest(0x90),
                database,
                marker,
                TestDigest(0xA0)),
            ImmutableCollectionsMarshal.AsImmutableArray(inventoryBacking),
            TestDigest(0xB0),
            TestDigest(0xC0),
            MarkerIntentCount: 1,
            ImmutableCollectionsMarshal.AsImmutableArray(intentBacking),
            TestDigest(0xD0),
            DeletedCount: 0,
            OrphanCount: 0);

        InstallationResetActiveRecord domain = new(
            Version: 2,
            signed.OperationId,
            "plan",
            InstallationResetScope.All,
            Workspace: null,
            new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: ErrorCodes.Data.RecoveryRequired,
            HostToolsMarkerPairReset: checkpoint);

        InstallationResetActivePayloadV2 payload =
            InstallationResetActivePayloadV2.FromRecord(domain);

        inventoryBacking[0] = inventoryEntry with { CampaignId = Guid.NewGuid() };

        intentBacking[0] = Guid.NewGuid();

        Assert.Equal(campaignId, payload.HostToolsMarkerPairReset!.CampaignInventory[0].CampaignId);

        Assert.Equal(intentId, payload.HostToolsMarkerPairReset.OrderedMarkerIntentIds!.Value[0]);

        Assert.NotSame(
            checkpoint.RestartProof,
            payload.HostToolsMarkerPairReset.RestartProof);

        Assert.NotSame(
            checkpoint.RestartProof.DatabaseMarkerEvidence,
            payload.HostToolsMarkerPairReset.RestartProof.DatabaseMarkerEvidence);

        InstallationResetActiveRecord first = payload.ToRecord();

        InstallationResetActiveRecord second = payload.ToRecord();

        CampaignMarkerInventoryEntryV1[] firstInventory =
            ImmutableCollectionsMarshal.AsArray(
                first.HostToolsMarkerPairReset!.CampaignInventory)!;

        Guid[] firstIntents = ImmutableCollectionsMarshal.AsArray(
            first.HostToolsMarkerPairReset.OrderedMarkerIntentIds!.Value)!;

        firstInventory[0] = inventoryEntry with { CampaignId = Guid.NewGuid() };

        firstIntents[0] = Guid.NewGuid();

        Assert.Equal(
            campaignId,
            second.HostToolsMarkerPairReset!.CampaignInventory[0].CampaignId);

        Assert.Equal(
            intentId,
            second.HostToolsMarkerPairReset.OrderedMarkerIntentIds!.Value[0]);

        Assert.Equal(campaignId, payload.HostToolsMarkerPairReset.CampaignInventory[0].CampaignId);

        Assert.Equal(intentId, payload.HostToolsMarkerPairReset.OrderedMarkerIntentIds.Value[0]);

        Assert.NotSame(
            first.HostToolsMarkerPairReset.RestartProof,
            second.HostToolsMarkerPairReset.RestartProof);

    }

    [Fact]
    public void V2_projection_checks_checkpoint_vector_bounds_before_copy()
    {

        // Mutation caught: checking bounds only after CreateRange or builder allocation lets an
        // authenticated-record projection allocate and enumerate attacker-sized vectors first.
        InstallationResetActiveRecord exact = ProjectionRecordWithCheckpointVectorCounts(
            inventoryCount: 4096,
            intentCount: 4096);

        InstallationResetActivePayloadV2 exactPayload =
            InstallationResetActivePayloadV2.FromRecord(exact);

        Assert.Equal(4096, exactPayload.HostToolsMarkerPairReset!.CampaignInventory.Length);

        Assert.Equal(
            4096,
            exactPayload.HostToolsMarkerPairReset.OrderedMarkerIntentIds!.Value.Length);

        InstallationResetActiveRecord oversizedInventory =
            ProjectionRecordWithCheckpointVectorCounts(
                inventoryCount: 4097,
                intentCount: 4096);

        InstallationResetActiveRecord oversizedIntents =
            ProjectionRecordWithCheckpointVectorCounts(
                inventoryCount: 4096,
                intentCount: 4097);

        Assert.Throws<ArgumentException>(() =>
            InstallationResetActivePayloadV2.FromRecord(oversizedInventory));

        Assert.Throws<ArgumentException>(() =>
            InstallationResetActivePayloadV2.FromRecord(oversizedIntents));

        InstallationResetActivePayloadV2 payloadWithOversizedCheckpoint =
            exactPayload with
            {
                HostToolsMarkerPairReset = oversizedInventory.HostToolsMarkerPairReset,
            };

        Assert.Throws<ArgumentException>(payloadWithOversizedCheckpoint.ToRecord);

    }

    [Fact]
    public void V2_projection_rejects_default_checkpoint_vector_shapes_before_copy()
    {

        // Mutation caught: treating default ImmutableArray values as empty lets the copy path
        // preserve an unauthenticatable vector shape instead of rejecting it before enumeration.
        InstallationResetActiveRecord initialized =
            ProjectionRecordWithCheckpointVectorCounts(
                inventoryCount: 0,
                intentCount: 0);

        InstallationResetActiveRecord defaultInventory = initialized with
        {
            HostToolsMarkerPairReset = initialized.HostToolsMarkerPairReset! with
            {
                CampaignInventory = default,
            },
        };

        InstallationResetActiveRecord defaultIntents = initialized with
        {
            HostToolsMarkerPairReset = initialized.HostToolsMarkerPairReset! with
            {
                OrderedMarkerIntentIds = default(ImmutableArray<Guid>),
            },
        };

        Assert.Throws<ArgumentException>(() =>
            InstallationResetActivePayloadV2.FromRecord(defaultInventory));

        Assert.Throws<ArgumentException>(() =>
            InstallationResetActivePayloadV2.FromRecord(defaultIntents));

    }

    [Fact]
    public void V2_context_owns_the_complete_closed_checkpoint_graph_and_no_live_authority_type()
    {

        // Mutation caught: relying on a transitive reflection fallback, registering a key lease, or
        // decoding V1 through the V2 context opens the pre-database recovery graph beyond this shape.
        Type[] closure =
        [
            typeof(InstallationResetActiveEnvelopeV2),
            typeof(InstallationResetActiveAnchorV1),
            typeof(InstallationResetActivePayloadV2),
            typeof(InstallationResetActiveWorkspaceV2),
            typeof(InstallationResetActiveFileIdentityV2),
            typeof(InstallationResetActivePreservedBackupV2),
            typeof(InstallationResetActiveAcceptedBindingV2),
            typeof(InstallationResetActiveCredentialResultV2),
            typeof(InstallationResetActiveOnlineCompletionV2),
            typeof(InstallationResetActiveAnchorState),
            typeof(InstallationResetScope),
            typeof(InstallationResetPhase),
            typeof(InstallationResetItemStatus),
            typeof(InstallationResetDataHandoff),
            typeof(CovenantDigest),
            typeof(FullInstallationResetRemediationClaimV1),
            typeof(HostToolsMarkerPairResetCheckpointV1),
            typeof(HostToolsMarkerPairResetPhase),
            typeof(FullInstallationResetRestartProofV1),
            typeof(FullInstallationResetSignedAttestationProjectionV1),
            typeof(CampaignMarkerInventoryEntryV1),
            typeof(HostProcessToolsDatabaseMarkerEvidence),
            typeof(HostProcessToolsOsMarkerEvidence),
            typeof(CovenantHostToolsState),
            typeof(System.Collections.Immutable.ImmutableArray<string>),
            typeof(System.Collections.Immutable.ImmutableArray<InstallationResetActivePreservedBackupV2>),
            typeof(System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2>),
            typeof(ImmutableArray<CampaignMarkerInventoryEntryV1>),
            typeof(ImmutableArray<Guid>),
        ];

        Assert.All(
            closure,
            type => Assert.NotNull(
                InstallationResetActiveJsonContext.Default.GetTypeInfo(type)));

        Assert.Null(
            InstallationResetActiveJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetActiveRecordKeyLease)));

        Assert.Null(
            InstallationResetActiveJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetActiveRecord)));

        Assert.NotNull(
            InstallationResetActiveLegacyJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetActiveRecord)));

        Type[] liveAuthorityTypes =
        [
            typeof(FullInstallationResetExternalRemediationAttestation),
            typeof(CovenantArtifactErasureAuthority),
            typeof(OperatorAuthorityContext),
        ];

        Assert.All(
            liveAuthorityTypes,
            type => Assert.Null(
                InstallationResetActiveJsonContext.Default.GetTypeInfo(type)));

        Type[] checkpointOnlyTypes =
        [
            typeof(HostToolsMarkerPairResetCheckpointV1),
            typeof(HostToolsMarkerPairResetPhase),
            typeof(FullInstallationResetRestartProofV1),
            typeof(FullInstallationResetSignedAttestationProjectionV1),
            typeof(CampaignMarkerInventoryEntryV1),
            typeof(HostProcessToolsDatabaseMarkerEvidence),
            typeof(HostProcessToolsOsMarkerEvidence),
            typeof(CovenantHostToolsState),
            typeof(ImmutableArray<CampaignMarkerInventoryEntryV1>),
            typeof(ImmutableArray<Guid>),
        ];

        Assert.All(
            checkpointOnlyTypes,
            type => Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type)));

        Type[] activeRegistrations = DeclaredJsonTypes(
            typeof(InstallationResetActiveJsonContext));

        Type[] apiRegistrations = DeclaredJsonTypes(typeof(ArcanumJsonContext));

        Type[] legacyRegistrations = DeclaredJsonTypes(
            typeof(InstallationResetActiveLegacyJsonContext));

        Assert.All(
            checkpointOnlyTypes,
            type =>
            {

                Assert.Contains(type, activeRegistrations);

                Assert.DoesNotContain(type, apiRegistrations);

                Assert.DoesNotContain(type, legacyRegistrations);

            });

    }

    [Fact]
    public void Legacy_v1_never_adopts_the_ignored_checkpoint_member()
    {

        // Mutation caught: removing JsonIgnore lets legacy V1 input manufacture a V2 restart
        // checkpoint without AEAD authentication or the V2 canonical checkpoint validation.
        InstallationResetActiveRecord record = new(
            Version: 1,
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            "plan",
            InstallationResetScope.All,
            Workspace: null,
            new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            HostToolsMarkerPairReset: new HostToolsMarkerPairResetCheckpointV1(
                Version: 1,
                HostToolsMarkerPairResetPhase.PairJournaled,
                RestartProof: null!,
                CampaignInventory: [],
                CampaignMarkerInventoryDigest: default,
                OwnerEffectDigest: default,
                MarkerIntentCount: null,
                OrderedMarkerIntentIds: null,
                MarkerIntentVectorDigest: null,
                DeletedCount: null,
                OrphanCount: null));

        string legacyJson = JsonSerializer.Serialize(
            record,
            InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

        Assert.DoesNotContain("hostToolsMarkerPairReset", legacyJson, StringComparison.Ordinal);

        string suppliedMember = legacyJson[..^1]
            + ",\"hostToolsMarkerPairReset\":{\"version\":1}}";

        InstallationResetActiveRecord? decoded = JsonSerializer.Deserialize(
            suppliedMember,
            InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

        Assert.NotNull(decoded);

        Assert.Null(decoded.HostToolsMarkerPairReset);

    }

    private static CovenantDigest TestDigest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    private static InstallationResetActiveRecord ProjectionRecordWithCheckpointVectorCounts(
        int inventoryCount,
        int intentCount)
    {

        Guid installationId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        Guid operationId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

        Guid transitionId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

        CovenantDigest fingerprint = TestDigest(0x10);

        CovenantDigest databaseDigest = TestDigest(0x20);

        CovenantDigest osDigest = TestDigest(0x30);

        CampaignMarkerInventoryEntryV1 inventoryEntry = new(
            Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00"),
            PriorPathRevision: 1,
            TestDigest(0x40),
            TestDigest(0x50),
            TestDigest(0x60),
            TestDigest(0x70));

        FullInstallationResetSignedAttestationProjectionV1 signed = new(
            Version: 1,
            operationId,
            installationId,
            transitionId,
            TaintMasterKeyVersion: 1,
            fingerprint,
            databaseDigest,
            osDigest,
            TestDigest(0x80),
            "nonce",
            "issuer",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            "signature");

        HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
            Version: 1,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            new FullInstallationResetRestartProofV1(
                Version: 1,
                signed,
                DateTimeOffset.UnixEpoch,
                TestDigest(0x90),
                new HostProcessToolsDatabaseMarkerEvidence(
                    installationId.ToString("D"),
                    CovenantHostToolsState.HostToolsTainted,
                    transitionId,
                    taintMasterKeyVersion: 1,
                    fingerprint),
                new HostProcessToolsOsMarkerEvidence(
                    installationId.ToString("D"),
                    transitionId,
                    taintMasterKeyVersion: 1,
                    fingerprint,
                    osDigest,
                    TestDigest(0xA0)),
                TestDigest(0xB0)),
            Enumerable.Repeat(inventoryEntry, inventoryCount).ToImmutableArray(),
            TestDigest(0xC0),
            TestDigest(0xD0),
            checked((ulong)intentCount),
            Enumerable.Range(0, intentCount)
                .Select(static index => new Guid(index, 0, 0, new byte[8]))
                .ToImmutableArray(),
            TestDigest(0xE0),
            DeletedCount: 0,
            OrphanCount: 0);

        return new InstallationResetActiveRecord(
            Version: 2,
            operationId,
            "plan",
            InstallationResetScope.All,
            Workspace: null,
            new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: ErrorCodes.Data.RecoveryRequired,
            HostToolsMarkerPairReset: checkpoint);

    }

    private static Type[] DeclaredJsonTypes(Type contextType) =>
        CustomAttributeData.GetCustomAttributes(contextType)
            .Where(static attribute =>
                attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(static attribute => (Type)attribute.ConstructorArguments[0].Value!)
            .ToArray();

}
