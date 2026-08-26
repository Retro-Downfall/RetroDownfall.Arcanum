using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// The Covenant management surface, as the CLI reaches it.
/// </summary>
/// <remarks>
/// Every call is a <c>POST</c> or <c>PUT</c> with a typed body, mirroring the routes: nothing an
/// operator names — a scope, a Campaign, a key, a cursor — travels in a URL where a proxy or an access
/// log would keep it.
/// </remarks>
public sealed partial class ArcanumApiClient
{

    public Task<Result<CovenantMutationPreflightDto>> PrepareCovenantSetAsync(
        CovenantSetPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/set/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantSetPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto,
            cancellationToken);

    public Task<Result<CovenantMutationPreflightDto>> PrepareCovenantRetireAsync(
        CovenantRetirePrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/retire/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantRetirePrepareRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto,
            cancellationToken);

    public Task<Result<CovenantMutationPreflightDto>> PrepareCovenantCorrectAsync(
        CovenantCorrectPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/correct/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantCorrectPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto,
            cancellationToken);

    public Task<Result<CovenantMutationResultDto>> CorrectCovenantAsync(
        CovenantCorrectRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/correct",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantCorrectRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto,
            cancellationToken);

    public Task<Result<CovenantCurationPreflightDto>> PrepareCovenantCurationAsync(
        CovenantCurationPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/curate/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantCurationPrepareRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantCurationPreflightDto,
            cancellationToken);

    public Task<Result<CovenantCurationResultDto>> CurateCovenantAsync(
        CovenantCurationRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/curate",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantCurationRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantCurationResultDto,
            cancellationToken);

    public Task<Result<CovenantMutationResultDto>> SetCovenantAsync(
        CovenantSetRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Put,
            "api/memory/covenant",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantSetRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto,
            cancellationToken);

    public Task<Result<CovenantMutationResultDto>> RetireCovenantAsync(
        CovenantRetireRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/retire",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantRetireRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto,
            cancellationToken);

    public Task<Result<CovenantPageDto>> ListCovenantAsync(
        CovenantListRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/list",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantListRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantPageDto,
            cancellationToken);

    public Task<Result<CovenantDetailDto>> ShowCovenantAsync(
        CovenantDetailRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/detail",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantDetailRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantDetailDto,
            cancellationToken);

    public Task<Result<CovenantVersionPageDto>> ListCovenantVersionsAsync(
        CovenantVersionsRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/versions",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantVersionsRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantVersionPageDto,
            cancellationToken);

    public Task<Result<CovenantExplainDto>> ExplainCovenantAsync(
        CovenantExplainRequest request,
        CancellationToken cancellationToken = default) =>
        PostCovenantAsync(
            "api/memory/covenant/explain",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantExplainRequest),
            ArcanumJsonContext.Default.ApiResponseCovenantExplainDto,
            cancellationToken);

    private Task<Result<T>> PostCovenantAsync<T>(
        string relativePath,
        byte[] body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            HttpMethod.Post,
            relativePath,
            body,
            JsonUtf8ContentType,
            responseTypeInfo,
            cancellationToken);

}
