using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

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
    public void Active_v2_json_context_is_closed_source_generated_and_legacy_is_separate()
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
            typeof(JsonElement),
            typeof(System.Collections.Immutable.ImmutableArray<string>),
            typeof(System.Collections.Immutable.ImmutableArray<InstallationResetActivePreservedBackupV2>),
            typeof(System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2>),
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

    }

}
