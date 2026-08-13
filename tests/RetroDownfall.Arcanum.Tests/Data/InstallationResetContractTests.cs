using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

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

}
