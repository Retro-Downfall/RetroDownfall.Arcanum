using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// The selective-erasure surface, as the CLI reaches it.
/// </summary>
/// <remarks>
/// Two static POSTs per store with typed bodies: nothing about the target — a memory id, a Lexicon
/// name, a Covenant key — travels in a URL where a proxy or an access log would keep it.
///
/// <para>Every call is sent with <c>retryResponseBodyIOExceptionOnce</c>. A prepare writes nothing but,
/// on a fresh installation, the key, so asking again only measures again. An apply is the case the
/// retry exists for: a response whose headers arrived and whose body was cut off may belong to an
/// erase that committed, and the only safe second attempt is the identical request. The same bytes
/// carry the same <c>mutationId</c>, which the host answers from its receipt as a replay rather than
/// erasing anything again. The body is serialized once, before the first send, so a retry cannot
/// carry a different mutation.</para>
/// </remarks>
public sealed partial class ArcanumApiClient
{

    public Task<Result<MemoryErasurePreflightDto>> PrepareSagaErasureAsync(
        SagaErasePrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/saga/erase/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaErasePrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto,
            cancellationToken);

    public Task<Result<MemoryErasureResultDto>> EraseSagaMemoryAsync(
        SagaEraseRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/saga/erase",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaEraseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto,
            cancellationToken);

    public Task<Result<MemoryErasurePreflightDto>> PrepareLexiconErasureAsync(
        LexiconErasePrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/lexicon/erase/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconErasePrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto,
            cancellationToken);

    public Task<Result<MemoryErasureResultDto>> EraseLexiconEntryAsync(
        LexiconEraseRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/lexicon/erase",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconEraseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto,
            cancellationToken);

    public Task<Result<MemoryErasurePreflightDto>> PrepareCovenantErasureAsync(
        CovenantErasePrepareRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/covenant/erase/prepare",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantErasePrepareRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto,
            cancellationToken);

    public Task<Result<MemoryErasureResultDto>> EraseCovenantEntryAsync(
        CovenantEraseRequest request,
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/covenant/erase",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantEraseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto,
            cancellationToken);

    private Task<Result<T>> PostErasureAsync<T>(
        string relativePath,
        byte[] body,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            HttpMethod.Post,
            relativePath,
            body,
            JsonUtf8ContentType,
            responseTypeInfo,
            static envelope => Result<T>.Success(envelope.Data!),
            cancellationToken,
            retryResponseBodyIOExceptionOnce: true);

}
