using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The erasure calls, as the CLI reaches them: one static route each — six erase POSTs, three release
/// POSTs, the status GET, and the scrub and key-reset POSTs — a body the shared context writes and reads
/// back unchanged, and the envelope the route answers with.
/// </summary>
public sealed class ArcanumApiClientMemoryErasureTests
{
    private static readonly Guid MutationId = Guid.Parse("0195a0f0-0000-7000-8000-00000000a001");

    private static readonly string EffectDigest = new('e', 64);

    private static readonly SagaErasePrepareRequest SagaPrepare =
        new("7c9e6679-7425-40de-944b-e07fc1f90ae7", new string('A', 64), "claim-version-7", MutationId);

    private static readonly LexiconErasePrepareRequest LexiconPrepare =
        new(LexiconCliFixture.Detail.Target, MutationId);

    private static readonly CovenantErasePrepareRequest CovenantPrepare = new(
        CovenantScope.Campaign,
        Guid.Parse("5b2e9c41-08d3-4a7f-b6e5-2c1908fa4d77"),
        "preference.builds",
        Guid.Parse("0195a0f0-0000-7000-8000-0000000000e1"),
        new(Guid.Parse("0195a0f0-0000-7000-8000-0000000000c1"), 3),
        new(Guid.Parse("0195a0f0-0000-7000-8000-0000000000b2"), 1),
        MutationId);

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Prepare_posts_its_exact_path_and_decodes_the_preflight(string store)
    {
        RecordingHandler handler = new(_ => Envelope(Preflight(store), ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto));

        ArcanumApiClient client = Client(handler);

        (Result<MemoryErasurePreflightDto> result, string expectedBody, Func<string, string> roundTrip) = store switch
        {
            "saga" => (
                await client.PrepareSagaErasureAsync(SagaPrepare, CancellationToken.None),
                Serialize(SagaPrepare, ArcanumJsonContext.Default.SagaErasePrepareRequest),
                RoundTrip(ArcanumJsonContext.Default.SagaErasePrepareRequest)),
            "lexicon" => (
                await client.PrepareLexiconErasureAsync(LexiconPrepare, CancellationToken.None),
                Serialize(LexiconPrepare, ArcanumJsonContext.Default.LexiconErasePrepareRequest),
                RoundTrip(ArcanumJsonContext.Default.LexiconErasePrepareRequest)),
            _ => (
                await client.PrepareCovenantErasureAsync(CovenantPrepare, CancellationToken.None),
                Serialize(CovenantPrepare, ArcanumJsonContext.Default.CovenantErasePrepareRequest),
                RoundTrip(ArcanumJsonContext.Default.CovenantErasePrepareRequest)),
        };

        Assert.Equal([$"POST /api/memory/{store}/erase/prepare"], handler.Requests);

        string body = Assert.Single(handler.Bodies);

        Assert.Equal(expectedBody, body);

        Assert.Equal(body, roundTrip(body));

        Assert.Equal("application/json; charset=utf-8", Assert.Single(handler.ContentTypes));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(MutationId, result.Value.MutationId);

        Assert.Equal(EffectDigest, result.Value.EffectDigest);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Apply_posts_its_exact_path_and_decodes_the_result(string store)
    {
        RecordingHandler handler = new(_ => Envelope(Result(store), ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto));

        ArcanumApiClient client = Client(handler);

        (Result<MemoryErasureResultDto> result, string expectedBody, Func<string, string> roundTrip) = await ApplyAsync(client, store);

        Assert.Equal([$"POST /api/memory/{store}/erase"], handler.Requests);

        string body = Assert.Single(handler.Bodies);

        Assert.Equal(expectedBody, body);

        Assert.Equal(body, roundTrip(body));

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal("token-1", document.RootElement.GetProperty("preflightToken").GetString());

        Assert.Equal(MutationId, document.RootElement.GetProperty("mutationId").GetGuid());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(MutationId, result.Value.MutationId);

        Assert.Equal(EffectDigest, result.Value.EffectDigest);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Apply_whose_answer_was_cut_off_is_sent_again_with_the_identical_mutation(string store)
    {
        int calls = 0;

        RecordingHandler handler = new(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() }
            : Envelope(Result(store) with { Replayed = true }, ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto));

        (Result<MemoryErasureResultDto> result, _, _) = await ApplyAsync(Client(handler), store);

        // The host may have committed the first one. Only the same mutation identity, sent again
        // byte for byte, lets it answer from its receipt rather than erase a second time.
        Assert.Equal(2, handler.Bodies.Count);

        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.Replayed);

        Assert.Equal(MutationId, result.Value.MutationId);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_refusal_envelope_decodes_to_the_hosts_own_error(string store)
    {
        RecordingHandler handler = new(_ => Envelope(
            new ApiResponse<MemoryErasureResultDto>(null, false, new Error(ErrorCodes.MemoryErasure.SubjectErased, "Already erased.")),
            HttpStatusCode.Gone,
            ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto));

        (Result<MemoryErasureResultDto> result, _, _) = await ApplyAsync(Client(handler), store);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.SubjectErased, result.Error.Code);

        Assert.Equal("Already erased.", result.Error.Message);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Release_posts_its_exact_path_and_decodes_the_result(string store)
    {
        RecordingHandler handler = new(_ => Envelope(
            new MemoryErasureReleaseResultDto(Store(store), MemoryErasureReleaseOutcome.Released, 2),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto));

        ArcanumApiClient client = Client(handler);

        Guid campaign = Guid.Parse("5b2e9c41-08d3-4a7f-b6e5-2c1908fa4d77");

        (Result<MemoryErasureReleaseResultDto> result, string expectedBody, Func<string, string> roundTrip) = store switch
        {
            "saga" => (
                await client.ReleaseSagaErasureAsync(new(SagaMemoryScopeKind.Campaign, campaign, "Rotate the vault key.\n"), CancellationToken.None),
                Serialize(new SagaErasureReleaseRequest(SagaMemoryScopeKind.Campaign, campaign, "Rotate the vault key.\n"), ArcanumJsonContext.Default.SagaErasureReleaseRequest),
                RoundTrip(ArcanumJsonContext.Default.SagaErasureReleaseRequest)),
            "lexicon" => (
                await client.ReleaseLexiconErasureAsync(new(new(LexiconScopeKind.Campaign, campaign), "Vault Keeper"), CancellationToken.None),
                Serialize(new LexiconErasureReleaseRequest(new(LexiconScopeKind.Campaign, campaign), "Vault Keeper"), ArcanumJsonContext.Default.LexiconErasureReleaseRequest),
                RoundTrip(ArcanumJsonContext.Default.LexiconErasureReleaseRequest)),
            _ => (
                await client.ReleaseCovenantErasureAsync(new(CovenantScope.Campaign, campaign, "preference.vault"), CancellationToken.None),
                Serialize(new CovenantErasureReleaseRequest(CovenantScope.Campaign, campaign, "preference.vault"), ArcanumJsonContext.Default.CovenantErasureReleaseRequest),
                RoundTrip(ArcanumJsonContext.Default.CovenantErasureReleaseRequest)),
        };

        Assert.Equal([$"POST /api/memory/{store}/release"], handler.Requests);

        string body = Assert.Single(handler.Bodies);

        Assert.Equal(expectedBody, body);

        Assert.Equal(body, roundTrip(body));

        Assert.Equal("application/json; charset=utf-8", Assert.Single(handler.ContentTypes));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal((Store(store), MemoryErasureReleaseOutcome.Released, 2), (result.Value.Store, result.Value.Outcome, result.Value.ReleasedCount));
    }

    [Fact]
    public async Task Status_is_a_get_with_no_body()
    {
        MemoryErasureStatusDto status = new(
            MemoryErasureKeyStatus.Lost,
            [
                new(MemoryReviewStore.Covenant, 0, 0, 0),
                new(MemoryReviewStore.Saga, 1, 1, 1),
                new(MemoryReviewStore.Lexicon, 0, 0, 0),
            ],
            0);

        RecordingHandler handler = new(_ => Envelope(status, ArcanumJsonContext.Default.ApiResponseMemoryErasureStatusDto));

        Result<MemoryErasureStatusDto> result = await Client(handler).GetMemoryErasureStatusAsync(CancellationToken.None);

        Assert.Equal(["GET /api/memory/erasure"], handler.Requests);

        Assert.Equal("", Assert.Single(handler.Bodies));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(MemoryErasureKeyStatus.Lost, result.Value.KeyStatus);

        Assert.Equal(1, result.Value.Stores[1].Unverifiable);
    }

    [Theory]
    [InlineData("scrub")]
    [InlineData("reset-key/prepare")]
    public async Task Scrub_and_reset_prepare_post_an_empty_body(string suffix)
    {
        RecordingHandler handler = new(_ => suffix == "scrub"
            ? Envelope(
                new MemoryErasureScrubResultDto(MemoryErasureWalCheckpointAttempt.Truncated, 1, 0),
                ArcanumJsonContext.Default.ApiResponseMemoryErasureScrubResultDto)
            : Envelope(
                new MemoryErasureKeyResetPreflightDto(MemoryErasureKeyStatus.Lost, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), "reset-token"),
                ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto));

        ArcanumApiClient client = Client(handler);

        bool succeeded = suffix == "scrub"
            ? (await client.ScrubMemoryErasuresAsync(CancellationToken.None)).IsSuccess
            : (await client.PrepareMemoryErasureKeyResetAsync(CancellationToken.None)) is { IsSuccess: true, Value.PreflightToken: "reset-token" };

        Assert.True(succeeded);

        Assert.Equal([$"POST /api/memory/erasure/{suffix}"], handler.Requests);

        Assert.Equal("", Assert.Single(handler.Bodies));
    }

    [Fact]
    public async Task Reset_key_posts_the_preflight_token_and_decodes_the_result()
    {
        RecordingHandler handler = new(_ => Envelope(
            new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: true),
            ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto));

        Result<MemoryErasureKeyResetResultDto> result = await Client(handler)
            .ResetMemoryErasureKeyAsync(new MemoryErasureKeyResetRequest("reset-token"), CancellationToken.None);

        Assert.Equal(["POST /api/memory/erasure/reset-key"], handler.Requests);

        string body = Assert.Single(handler.Bodies);

        Assert.Equal(Serialize(new MemoryErasureKeyResetRequest("reset-token"), ArcanumJsonContext.Default.MemoryErasureKeyResetRequest), body);

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal("reset-token", document.RootElement.GetProperty("preflightToken").GetString());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal((2L, 2L, true), (result.Value.FingerprintsDiscarded, result.Value.ReceiptsDiscarded, result.Value.KeyCreated));
    }

    private static async Task<(Result<MemoryErasureResultDto> Result, string Body, Func<string, string> RoundTrip)> ApplyAsync(
        ArcanumApiClient client,
        string store)
    {
        switch (store)
        {
            case "saga":
            {
                SagaEraseRequest request = new(SagaPrepare.MemoryId, SagaPrepare.ExpectedContentHash, SagaPrepare.ExpectedClaimVersionId, MutationId, "token-1");

                return (
                    await client.EraseSagaMemoryAsync(request, CancellationToken.None),
                    Serialize(request, ArcanumJsonContext.Default.SagaEraseRequest),
                    RoundTrip(ArcanumJsonContext.Default.SagaEraseRequest));
            }

            case "lexicon":
            {
                LexiconEraseRequest request = new(LexiconPrepare.Target, MutationId, "token-1");

                return (
                    await client.EraseLexiconEntryAsync(request, CancellationToken.None),
                    Serialize(request, ArcanumJsonContext.Default.LexiconEraseRequest),
                    RoundTrip(ArcanumJsonContext.Default.LexiconEraseRequest));
            }

            default:
            {
                CovenantEraseRequest request = new(
                    CovenantPrepare.Scope,
                    CovenantPrepare.CampaignId,
                    CovenantPrepare.Key,
                    CovenantPrepare.EntryId,
                    CovenantPrepare.Confirmed,
                    CovenantPrepare.Proposed,
                    MutationId,
                    "token-1");

                return (
                    await client.EraseCovenantEntryAsync(request, CancellationToken.None),
                    Serialize(request, ArcanumJsonContext.Default.CovenantEraseRequest),
                    RoundTrip(ArcanumJsonContext.Default.CovenantEraseRequest));
            }
        }
    }

    private static MemoryReviewStore Store(string store) => store switch
    {
        "saga" => MemoryReviewStore.Saga,
        "lexicon" => MemoryReviewStore.Lexicon,
        _ => MemoryReviewStore.Covenant,
    };

    private static MemoryErasurePreflightDto Preflight(string store) => new(
        Store(store),
        MutationId,
        new string('d', 64),
        EffectDigest,
        new MemoryErasurePlanDto(1, 4, 0, 0, false, null, null),
        new MemoryErasureExternalExposureDto(MemoryExternalRevocation.NotPerformed, []),
        [MemoryRetainedLocalCopy.OtherLocalState],
        [MemoryErasureNote.OtherScopesUnaffected],
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(5),
        "token-1");

    private static MemoryErasureResultDto Result(string store) => new(
        Store(store),
        MutationId,
        false,
        EffectDigest,
        new MemoryErasureLocalResultDto(MemoryLocalErasureOutcome.Verified, [], MemoryErasureWalCheckpointAttempt.Truncated, 1, 4, 0, 0, true),
        new MemoryErasureExternalExposureDto(MemoryExternalRevocation.NotPerformed, []),
        [MemoryRetainedLocalCopy.OtherLocalState],
        [MemoryErasureNote.OtherScopesUnaffected]);

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    private static Func<string, string> RoundTrip<T>(JsonTypeInfo<T> typeInfo) =>
        body => JsonSerializer.Serialize(JsonSerializer.Deserialize(body, typeInfo)!, typeInfo);

    private static HttpResponseMessage Envelope<T>(T value, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        Envelope(new ApiResponse<T>(value, true, null), HttpStatusCode.OK, typeInfo);

    private static HttpResponseMessage Envelope<T>(ApiResponse<T> envelope, HttpStatusCode status, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(envelope, typeInfo), Encoding.UTF8, "application/json") };

    private static ArcanumApiClient Client(HttpMessageHandler handler) =>
        new(new LexiconCliFixture.Factory(handler), ArcanumApiCredentialLeaseTestFactory.Create(new LexiconCliFixture.Secrets(), LexiconCliFixture.Key));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        internal List<string> Bodies { get; } = [];

        internal List<string?> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());

            return respond(request);
        }
    }

    /// <summary>A response whose headers arrived and whose body the connection lost.</summary>
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("The connection dropped mid-body.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BrokenStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection dropped mid-body.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The connection dropped mid-body."));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
