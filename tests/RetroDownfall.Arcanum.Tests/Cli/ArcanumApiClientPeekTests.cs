using System.Net;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumApiClientPeekTests
{

    [Fact]
    public async Task Buffered_api_request_uses_non_migrating_peek_authentication()
    {

        RecordingHandler handler = new();

        PeekOnlySecretStore secrets = new();

        ArcanumApiClient client = new(new StubHttpClientFactory(handler), secrets);

        _ = await client.GetBudgetAsync(CancellationToken.None);

        Assert.Equal(1, secrets.PeekCalls);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("peek-only-key", request.Headers.GetValues("X-Arcanum-Key").Single());

    }

    [Theory]
    [InlineData(PeekOutcome.Missing)]
    [InlineData(PeekOutcome.Corrupted)]
    [InlineData(PeekOutcome.Unavailable)]
    public async Task Buffered_api_request_maps_an_unusable_peek_to_missing_key_without_sending(
        PeekOutcome outcome)
    {

        RecordingHandler handler = new();

        ConfigurablePeekSecretStore secrets = new(outcome);

        ArcanumApiClient client = new(new StubHttpClientFactory(handler), secrets);

        Result<BudgetSummaryDto> result = await client.GetBudgetAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Security.MissingApiKey, result.Error.Code);

        Assert.Equal(
            "No API key found. Run 'arcanum serve' once to generate and store a key.",
            result.Error.Message);

        Assert.Equal(1, secrets.PeekCalls);

        Assert.Empty(handler.Requests);

    }

    [Theory]
    [InlineData(DirectStreamPath.Ask, PeekOutcome.Missing)]
    [InlineData(DirectStreamPath.Ask, PeekOutcome.Corrupted)]
    [InlineData(DirectStreamPath.Ask, PeekOutcome.Unavailable)]
    [InlineData(DirectStreamPath.ApprenticeChronicle, PeekOutcome.Missing)]
    [InlineData(DirectStreamPath.ApprenticeChronicle, PeekOutcome.Corrupted)]
    [InlineData(DirectStreamPath.ApprenticeChronicle, PeekOutcome.Unavailable)]
    public async Task Direct_stream_maps_an_unusable_peek_to_missing_key_without_sending(
        DirectStreamPath path,
        PeekOutcome outcome)
    {

        RecordingHandler handler = new();

        ConfigurablePeekSecretStore secrets = new(outcome);

        ArcanumApiClient client = new(new StubHttpClientFactory(handler), secrets);

        string message = path switch
        {

            DirectStreamPath.Ask => await ReadAskErrorAsync(client),

            DirectStreamPath.ApprenticeChronicle => await ReadChronicleErrorAsync(client),

            _ => throw new ArgumentOutOfRangeException(nameof(path)),

        };

        Assert.Equal(
            "No API key found. Run 'arcanum serve' once to generate and store a key.",
            message);

        Assert.Equal(1, secrets.PeekCalls);

        Assert.Empty(handler.Requests);

    }

    [Theory]
    [InlineData(DirectStreamPath.Ask, HttpMethodName.Post, "/api/intelligence/ping-stream")]
    [InlineData(DirectStreamPath.ApprenticeChronicle, HttpMethodName.Get, "/api/apprentices/11111111-1111-1111-1111-111111111111/chronicle")]
    public async Task Direct_stream_sends_normally_with_a_successful_peek(
        DirectStreamPath path,
        HttpMethodName method,
        string expectedPath)
    {

        RecordingHandler handler = new();

        ConfigurablePeekSecretStore secrets = new(PeekOutcome.Ok);

        ArcanumApiClient client = new(new StubHttpClientFactory(handler), secrets);

        if (path == DirectStreamPath.Ask)
        {

            _ = await ReadAskErrorAsync(client);

        }
        else
        {

            _ = await ReadChronicleErrorAsync(client);

        }

        Assert.Equal(1, secrets.PeekCalls);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(method.ToString().ToUpperInvariant(), request.Method.Method);

        Assert.Equal(expectedPath, request.RequestUri?.AbsolutePath);

        Assert.Equal("peek-only-key", request.Headers.GetValues("X-Arcanum-Key").Single());

    }

    [Fact]
    public async Task Buffered_api_request_propagates_cancellation_while_waiting_for_peek()
    {

        RecordingHandler handler = new();

        BlockingPeekSecretStore secrets = new();

        ArcanumApiClient client = new(new StubHttpClientFactory(handler), secrets);

        using CancellationTokenSource cancellation = new();

        Task<Result<BudgetSummaryDto>> request = client.GetBudgetAsync(cancellation.Token);

        await secrets.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Every_Arcanum_api_client_authentication_path_uses_the_shared_peek_helper()
    {

        ProductionSource[] sources =
        [

            .. ProductionSourceInventory.Sources()
                .Where(static candidate =>
                    candidate.RelativePath
                        .Replace('\\', '/')
                        .StartsWith(
                            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient",
                            StringComparison.Ordinal)),

        ];

        Assert.NotEmpty(sources);

        string source = string.Join('\n', sources.Select(static candidate => candidate.Text));

        Assert.DoesNotContain(
            "secretStore.GetApiKeyAsync()",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "secretStore.GetApiKeyReadResultAsync()",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            ".PeekApiKeyReadResultAsync()",
            source,
            StringComparison.Ordinal);

    }

    private static async Task<string> ReadAskErrorAsync(ArcanumApiClient client)
    {

        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent frame in client.AskStreamAsync(
            new PingRequest("test"),
            CancellationToken.None))
        {

            events.Add(frame);

        }

        IntelligenceEvent error = Assert.Single(events);

        Assert.Equal(IntelligenceEventType.Error, error.Type);

        return error.Message;

    }

    private static async Task<string> ReadChronicleErrorAsync(ArcanumApiClient client)
    {

        List<ChronicleFrame> frames = [];

        await foreach (ChronicleFrame frame in client.StreamApprenticeChronicleAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CancellationToken.None))
        {

            frames.Add(frame);

        }

        ChronicleFrame error = Assert.Single(frames);

        Assert.Equal("error", error.Type);

        return error.Message;

    }

    public enum DirectStreamPath
    {

        Ask,

        ApprenticeChronicle,

    }

    public enum PeekOutcome
    {

        Ok,

        Missing,

        Corrupted,

        Unavailable,

    }

    public enum HttpMethodName
    {

        Get,

        Post,

    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class RecordingHandler : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        }

    }

    private sealed class PeekOnlySecretStore : ISecretStore
    {

        public int PeekCalls { get; private set; }

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Thin API clients must use Peek.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Thin API clients must use Peek.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            PeekCalls++;

            return Task.FromResult(SecretStoreReadResult.Ok("peek-only-key"));

        }

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Thin API clients must not persist credentials.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class ConfigurablePeekSecretStore(PeekOutcome outcome) : ISecretStore
    {

        public int PeekCalls { get; private set; }

        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Thin API clients must use Peek.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Thin API clients must use Peek.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            PeekCalls++;

            return outcome switch
            {

                PeekOutcome.Ok => Task.FromResult(SecretStoreReadResult.Ok("peek-only-key")),

                PeekOutcome.Missing => Task.FromResult(SecretStoreReadResult.Missing()),

                PeekOutcome.Corrupted => Task.FromResult(
                    SecretStoreReadResult.Corrupted("credential is unreadable")),

                PeekOutcome.Unavailable => Task.FromException<SecretStoreReadResult>(
                    new IOException("credential backend is unavailable")),

                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),

            };

        }

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Thin API clients must not persist credentials.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class BlockingPeekSecretStore : ISecretStore
    {

        private readonly TaskCompletionSource<SecretStoreReadResult> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetApiKeyAsync()
        {

            Started.TrySetResult();

            return Task.FromException<string?>(
                new InvalidOperationException("Thin API clients must use Peek."));

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Thin API clients must use Peek.");

        public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            Started.TrySetResult();

            return await _release.Task.ConfigureAwait(false);

        }

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Thin API clients must not persist credentials.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
