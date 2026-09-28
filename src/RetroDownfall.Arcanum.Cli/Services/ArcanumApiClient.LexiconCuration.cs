using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>Exact Lexicon curation uses static POST routes and the complete server-issued target.</summary>
public sealed partial class ArcanumApiClient
{
    public Task<Result<LexiconEntryDetail>> ShowLexiconAsync(
        LexiconShowRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/show",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconShowRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail,
            cancellationToken);

    public Task<Result<LexiconCurationResult>> CorrectLexiconAsync(
        LexiconCorrectRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/correct",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconCorrectRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult,
            cancellationToken);

    public Task<Result<LexiconCurationResult>> RetireLexiconAsync(
        LexiconRetireRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/retire",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconRetireRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult,
            cancellationToken);

    public Task<Result<LexiconCurationResult>> ReinstateLexiconAsync(
        LexiconReinstateRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/reinstate",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconReinstateRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult,
            cancellationToken);

    public Task<Result<LexiconCurationResult>> PinLexiconAsync(
        LexiconPinRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/pin",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconPinRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult,
            cancellationToken);

    public Task<Result<LexiconCurationResult>> UnpinLexiconAsync(
        LexiconUnpinRequest request,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync(
            HttpMethod.Post,
            "api/memory/lexicon/unpin",
            JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.LexiconUnpinRequest),
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLexiconCurationResult,
            cancellationToken);
}
