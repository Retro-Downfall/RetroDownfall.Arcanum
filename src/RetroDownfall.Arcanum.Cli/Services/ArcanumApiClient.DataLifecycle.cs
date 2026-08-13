using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{

    public Task<Result<DataRetentionStatus>> GetDataRetentionStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Get,
            "api/data/status",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseDataRetentionStatus,
            cancellationToken);

    public Task<Result<RetentionSettings>> GetDataRetentionSettingsAsync(
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Get,
            "api/data/retention",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings,
            cancellationToken);

    public Task<Result<RetentionSettings>> UpdateDataRetentionRuleAsync(
        RetentionRuleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest);

        return SendRequestAsync(
            HttpMethod.Put,
            "api/data/retention",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings,
            cancellationToken);

    }

    public Task<Result<DataRetentionPlan>> PlanDataPruneAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.DataRetentionRequest);

        return SendRequestAsync(
            HttpMethod.Post,
            "api/data/prune/plan",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan,
            cancellationToken);

    }

    public Task<Result<DataRetentionApplyResult>> ApplyDataPruneAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.DataRetentionApplyRequest);

        return SendRequestAsync(
            HttpMethod.Post,
            "api/data/prune",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult,
            cancellationToken);

    }

    public Task<Result<DataRetentionApplyResult>> DeleteDataSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Delete,
            $"api/data/sessions/{sessionId:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult,
            cancellationToken);

    public Task<Result<DataRetentionApplyResult>> DeleteDataAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Delete,
            $"api/data/attachments/{attachmentId:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult,
            cancellationToken);

    public Task<Result<DataRetentionApplyResult>> ResetDataMemoryAsync(
        MemoryResetRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.MemoryResetRequest);

        return SendRequestAsync(
            HttpMethod.Post,
            "api/data/memory/reset",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult,
            cancellationToken);

    }

    public Task<Result<DataRetentionApplyResult>> FactoryResetDataAsync(
        FactoryResetRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.FactoryResetRequest);

        return SendRequestAsync(
            HttpMethod.Post,
            "api/data/factory-reset",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult,
            cancellationToken);

    }

    public Task<Result<DataRetentionPlan>> PlanFactoryResetDataAsync(
        InstallationResetDataPlanRequest request,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ArcanumJsonContext.Default.InstallationResetDataPlanRequest);

        return SendRequestAsync(
            HttpMethod.Post,
            "api/data/factory-reset/plan",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan,
            cancellationToken);

    }

}
