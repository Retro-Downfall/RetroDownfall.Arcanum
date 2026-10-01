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
/// Two static POSTs per store with typed bodies, plus one release POST per store: nothing about the
/// target — a memory id, Saga content, a Lexicon name, a Covenant key — travels in a URL where a proxy
/// or an access log would keep it. The administration routes belong to no one store: a status GET, and
/// bodiless POSTs for the scrub and the key-reset prepare, whose apply carries only its token.
///
/// <para>Every call is sent with <c>retryResponseBodyIOExceptionOnce</c>. A prepare writes nothing but,
/// on a fresh installation, the key, so asking again only measures again. An apply is the case the
/// retry exists for: a response whose headers arrived and whose body was cut off may belong to an
/// erase that committed, and the only safe second attempt is the identical request. The same bytes
/// carry the same <c>mutationId</c>, which the host answers from its receipt as a replay rather than
/// erasing anything again. The body is serialized once, before the first send, so a retry cannot
/// carry a different mutation. Release, scrub and key reset are naturally idempotent, and the status
/// read writes nothing, so the same single retry is safe on each. It is not a faithful answer, though:
/// if the first attempt took effect, the resend finds nothing left to do and says so. Those three calls
/// therefore take an <c>onResent</c> callback, so the caller can tell the operator the reported outcome
/// describes only the resend.</para>
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

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseSagaErasureAsync(
        SagaErasureReleaseRequest request,
        CancellationToken cancellationToken = default,
        Action? onResent = null) =>
        PostErasureAsync(
            "api/memory/saga/release",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaErasureReleaseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto,
            cancellationToken,
            onResent);

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseLexiconErasureAsync(
        LexiconErasureReleaseRequest request,
        CancellationToken cancellationToken = default,
        Action? onResent = null) =>
        PostErasureAsync(
            "api/memory/lexicon/release",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconErasureReleaseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto,
            cancellationToken,
            onResent);

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseCovenantErasureAsync(
        CovenantErasureReleaseRequest request,
        CancellationToken cancellationToken = default,
        Action? onResent = null) =>
        PostErasureAsync(
            "api/memory/covenant/release",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CovenantErasureReleaseRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto,
            cancellationToken,
            onResent);

    public Task<Result<MemoryErasureStatusDto>> GetMemoryErasureStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendErasureAsync(
            HttpMethod.Get,
            "api/memory/erasure",
            body: null,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureStatusDto,
            cancellationToken);

    public Task<Result<MemoryErasureScrubResultDto>> ScrubMemoryErasuresAsync(
        CancellationToken cancellationToken = default,
        Action? onResent = null) =>
        PostErasureAsync(
            "api/memory/erasure/scrub",
            body: null,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureScrubResultDto,
            cancellationToken,
            onResent);

    public Task<Result<MemoryErasureKeyResetPreflightDto>> PrepareMemoryErasureKeyResetAsync(
        CancellationToken cancellationToken = default) =>
        PostErasureAsync(
            "api/memory/erasure/reset-key/prepare",
            body: null,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto,
            cancellationToken);

    public Task<Result<MemoryErasureKeyResetResultDto>> ResetMemoryErasureKeyAsync(
        MemoryErasureKeyResetRequest request,
        CancellationToken cancellationToken = default,
        Action? onResent = null) =>
        PostErasureAsync(
            "api/memory/erasure/reset-key",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.MemoryErasureKeyResetRequest),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto,
            cancellationToken,
            onResent);

    /// <summary>
    /// Whether a failed apply leaves the erase's outcome unknown, so the operator has to be told it may
    /// have been applied.
    /// </summary>
    /// <remarks>
    /// A typed refusal from an erase route proves the erase rolled back: the host settles an uncertain
    /// <c>COMMIT</c> by reading its receipt back before it answers, and reports a commit it finds as the
    /// committed result. Three kinds of failure prove nothing:
    /// <list type="bullet">
    /// <item><description><c>Covenant.ManualRecoveryRequired</c>, which is the host saying exactly that
    /// the commit's outcome could not be read back.</description></item>
    /// <item><description><c>Hub.Unhandled</c>, the host's catch-all for an exception nothing
    /// classified. A Saga or Lexicon <c>COMMIT</c> that failed and whose receipt could not be read back
    /// rethrows its failure, and this is how it reaches the wire.</description></item>
    /// <item><description>Any failure in which the host's answer was never read as a typed refusal: the
    /// connection failed or timed out, the answer was not an envelope or carried no result, it was too
    /// large to read, it was an error status with no envelope, or the client failed unexpectedly after
    /// sending.</description></item>
    /// </list>
    /// </remarks>
    internal static bool ErasureOutcomeUnknown(Error error) =>
        error.Code is ErrorCodes.Connection.Timeout
            or ErrorCodes.Connection.Unreachable
            or ErrorCodes.Covenant.ManualRecoveryRequired
            or ErrorCodes.Hub.Unhandled
            or UnreadableHttpErrorCode
        || error.Code == InvalidResponseError.Code
        || error.Code == ResponseTooLargeError.Code
        || error.Code == RequestUnexpectedError.Code;

    /// <summary>The code an error status without an envelope is reported under.</summary>
    private const string UnreadableHttpErrorCode = "Api.HttpError";

    private Task<Result<T>> PostErasureAsync<T>(
        string relativePath,
        byte[]? body,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken,
        Action? onResent = null) =>
        SendErasureAsync(HttpMethod.Post, relativePath, body, responseTypeInfo, cancellationToken, onResent);

    private Task<Result<T>> SendErasureAsync<T>(
        HttpMethod method,
        string relativePath,
        byte[]? body,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken,
        Action? onResent = null) =>
        SendRequestAsync(
            method,
            relativePath,
            body,
            body is null ? null : JsonUtf8ContentType,
            responseTypeInfo,
            // A success envelope with no result is an answer the client cannot read, not a result.
            static envelope => envelope.Data is { } data
                ? Result<T>.Success(data)
                : Result<T>.Failure(InvalidResponseError),
            cancellationToken,
            retryResponseBodyIOExceptionOnce: true,
            onResponseBodyRetry: onResent);

}
