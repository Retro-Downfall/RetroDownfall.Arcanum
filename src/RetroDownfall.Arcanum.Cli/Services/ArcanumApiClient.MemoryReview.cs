using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{
    public Task<Result<SagaReviewPageDto>> ListSagaReviewAsync(
        SagaReviewListRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/saga/review/list",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaReviewListRequest),
            ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkPlanDto>> PrepareSagaReviewAsync(
        SagaReviewBulkPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/saga/review/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkResultDto>> ApplySagaReviewAsync(
        SagaReviewBulkApplyRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/saga/review/apply",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaReviewBulkApplyRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto,
            cancellationToken);

    public Task<Result<LexiconReviewPageDto>> ListLexiconReviewAsync(
        LexiconReviewListRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/lexicon/review/list",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconReviewListRequest),
            ArcanumJsonContext.Default.ApiResponseLexiconReviewPageDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkPlanDto>> PrepareLexiconReviewAsync(
        LexiconReviewBulkPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/lexicon/review/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconReviewBulkPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkResultDto>> ApplyLexiconReviewAsync(
        LexiconReviewBulkApplyRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/lexicon/review/apply",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconReviewBulkApplyRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto,
            cancellationToken);

    public Task<Result<CovenantReviewPageDto>> ListCovenantReviewAsync(
        CovenantReviewListRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/covenant/review/list",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantReviewListRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantReviewPageDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkPlanDto>> PrepareCovenantReviewAsync(
        CovenantReviewBulkPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/covenant/review/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto,
            cancellationToken);

    public Task<Result<MemoryReviewBulkResultDto>> ApplyCovenantReviewAsync(
        CovenantReviewBulkApplyRequest request,
        CancellationToken cancellationToken = default) =>
        PostReviewAsync(
            "api/memory/covenant/review/apply",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantReviewBulkApplyRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto,
            cancellationToken);

    private Task<Result<T>> PostReviewAsync<T>(
        string path,
        byte[] body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            HttpMethod.Post,
            path,
            body,
            JsonUtf8ContentType,
            responseTypeInfo,
            cancellationToken);
}
