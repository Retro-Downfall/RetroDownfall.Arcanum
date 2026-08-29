using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// The Saga curation surface, as the CLI reaches it.
/// </summary>
/// <remarks>
/// One method per route, each naming the memory it acts on in the path exactly as the routes do. The
/// four write verbs answer <see cref="SagaCurationResult"/> rather than the projection alone, because
/// what the call did is not always readable from the memory it left behind: retiring what is already
/// retired and reinstating what is not retired write nothing and are still successes, and only
/// <see cref="SagaCurationResult.Outcome"/> tells those apart from the call that did the work.
///
/// <para>Pin and unpin send no body at all — a pin is not a statement about what a memory says, so the
/// routes ask for no proof of its text.</para>
/// </remarks>
public sealed partial class ArcanumApiClient
{

    public Task<Result<SagaMemoryDetail>> ShowSagaMemoryAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Get,
            SagaCurationPath(id),
            body: null,
            contentType: null,
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail,
            cancellationToken);

    public Task<Result<SagaCurationResult>> CorrectSagaMemoryAsync(
        string id,
        SagaCorrectRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            $"{SagaCurationPath(id)}/correct",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaCorrectRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            cancellationToken);

    public Task<Result<SagaCurationResult>> RetireSagaMemoryAsync(
        string id,
        SagaRetireRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            $"{SagaCurationPath(id)}/retire",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaRetireRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            cancellationToken);

    public Task<Result<SagaCurationResult>> ReinstateSagaMemoryAsync(
        string id,
        SagaReinstateRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            $"{SagaCurationPath(id)}/reinstate",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SagaReinstateRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            cancellationToken);

    public Task<Result<SagaCurationResult>> PinSagaMemoryAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            $"{SagaCurationPath(id)}/pin",
            body: null,
            contentType: null,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            cancellationToken);

    public Task<Result<SagaCurationResult>> UnpinSagaMemoryAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            $"{SagaCurationPath(id)}/unpin",
            body: null,
            contentType: null,
            ArcanumJsonContext.Default.ApiResponseSagaCurationResult,
            cancellationToken);

    /// <summary>
    /// The route prefix for one memory, with the identity escaped as a path segment.
    /// </summary>
    /// <remarks>
    /// Escaped rather than interpolated raw: a Saga identity is store-assigned, but nothing in this
    /// client's contract says it can never contain a character that would otherwise change which route
    /// the request reaches.
    /// </remarks>
    private static string SagaCurationPath(string id) =>
        $"api/memory/saga/{Uri.EscapeDataString(id)}";

}
