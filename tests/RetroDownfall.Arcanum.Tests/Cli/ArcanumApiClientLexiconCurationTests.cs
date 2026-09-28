using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumApiClientLexiconCurationTests
{
    [Theory]
    [InlineData("show")]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Every_client_call_posts_the_complete_source_generated_contract(string verb)
    {
        using LexiconCliFixture.Handler handler = new();

        object result = await InvokeAsync(Client(handler), verb, CancellationToken.None);

        Assert.Equal([$"POST /api/memory/lexicon/{verb}"], handler.Requests);

        Assert.Equal("application/json; charset=utf-8", Assert.Single(handler.ContentTypes));

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));

        if (verb == "show")
        {
            Assert.Equal("Operator / exact?", body.RootElement.GetProperty("name").GetString());

            Assert.Equal("Campaign", body.RootElement.GetProperty("scope").GetProperty("kind").GetString());

            Assert.Equal(LexiconCliFixture.Campaign, body.RootElement.GetProperty("scope").GetProperty("campaignId").GetGuid());

            Result<LexiconEntryDetail> detail = Assert.IsType<Result<LexiconEntryDetail>>(result);

            Assert.True(detail.IsSuccess);

            Assert.Equal(LexiconCliFixture.Detail.Target, detail.Value.Target);
        }
        else
        {
            Assert.Equal(LexiconCliFixture.TargetJson, body.RootElement.GetProperty("target").GetRawText());

            Result<LexiconCurationResult> mutation = Assert.IsType<Result<LexiconCurationResult>>(result);

            Assert.True(mutation.IsSuccess);

            Assert.Equal(LexiconCurationOutcomeKind.Applied, mutation.Value.Outcome);

            Assert.Equal(LexiconCliFixture.Detail.Target, mutation.Value.Entry.Target);

            if (verb == "correct")
            {
                Assert.Equal("Preference", body.RootElement.GetProperty("content").GetProperty("type").GetString());

                Assert.Equal("A corrected fact", body.RootElement.GetProperty("content").GetProperty("facts")[0].GetString());
            }
        }
    }

    [Theory]
    [InlineData("show")]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Every_client_call_preserves_network_failures_and_cancellation(string verb)
    {
        using LexiconCliFixture.Handler network = new() { Exception = new HttpRequestException("offline") };

        object result = await InvokeAsync(Client(network), verb, CancellationToken.None);

        Error error = result is Result<LexiconEntryDetail> detail ? detail.Error : ((Result<LexiconCurationResult>)result).Error;

        Assert.StartsWith("Connection.", error.Code, StringComparison.Ordinal);

        using CancellationTokenSource cancellation = new();

        using LexiconCliFixture.Handler cancelled = new()
        {
            BeforeResponse = () => cancellation.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(Client(cancelled), verb, cancellation.Token));
    }

    private static ArcanumApiClient Client(HttpMessageHandler handler) =>
        new(new LexiconCliFixture.Factory(handler), ArcanumApiCredentialLeaseTestFactory.Create(new LexiconCliFixture.Secrets(), LexiconCliFixture.Key));

    // Resolve the public client seam so a missing method is an executable RED, not a compiler error.
    private static async Task<object> InvokeAsync(ArcanumApiClient client, string verb, CancellationToken token)
    {
        object request = verb switch
        {
            "show" => new LexiconShowRequest("Operator / exact?", new(LexiconScopeKind.Campaign, LexiconCliFixture.Campaign)),
            "correct" => new LexiconCorrectRequest(LexiconCliFixture.Detail.Target, new("Preference", ["A corrected fact"])),
            "retire" => new LexiconRetireRequest(LexiconCliFixture.Detail.Target),
            "reinstate" => new LexiconReinstateRequest(LexiconCliFixture.Detail.Target),
            "pin" => new LexiconPinRequest(LexiconCliFixture.Detail.Target),
            "unpin" => new LexiconUnpinRequest(LexiconCliFixture.Detail.Target),
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };

        string methodName = char.ToUpperInvariant(verb[0]) + verb[1..] + "LexiconAsync";

        MethodInfo? method = typeof(ArcanumApiClient).GetMethod(methodName, [request.GetType(), typeof(CancellationToken)]);

        Assert.NotNull(method);

        object pending = method.Invoke(client, [request, token])!;

        if (pending is Task<Result<LexiconEntryDetail>> detail)
        {
            return await detail;
        }

        return await Assert.IsAssignableFrom<Task<Result<LexiconCurationResult>>>(pending);
    }
}

internal static class LexiconCliFixture
{
    internal const string Key = "arc_test_0123456789abcdef0123456789abcdef";

    internal static readonly Guid Campaign = Guid.Parse("11111111-2222-3333-4444-555555555555");

    internal static readonly LexiconEntryDetail Detail = CreateDetail();

    internal static string TargetJson => JsonSerializer.Serialize(Detail.Target, ArcanumJsonContext.Default.LexiconCurationTarget);

    private static LexiconEntryDetail CreateDetail()
    {
        LexiconCurationScope scope = new(LexiconScopeKind.Campaign, Campaign);

        LexiconEntryLifecycle lifecycle = new(null, DateTimeOffset.UnixEpoch);

        Guid id = Guid.Parse("22222222-3333-4444-5555-666666666666");

        LexiconCurationTarget target = new(scope, "SERVER NORMALIZED", id, 73, new string('A', 64), lifecycle,
            new(true, "claim-server", "version-server", 9, AnnalOperation.Correct, AnnalContentHashFormat.LexiconStructuredSnapshot, new string('B', 64)),
            new(true, Guid.Parse("33333333-4444-5555-6666-777777777777"), 17, new string('C', 64),
                new GenerationProvenance(GenerationProvenanceMode.Exact, [Guid.Parse("44444444-5555-6666-7777-888888888888")], [])));

        return new(new(id, "Server Name", "Person", ["Stored fact"], DateTimeOffset.UnixEpoch, [], Campaign, null, DateTimeOffset.UnixEpoch, 73),
            scope, AnnalOrigin.OperatorStated, lifecycle, LexiconRetrievalEligibility.Eligible, 73, new string('A', 64), target, [], []);
    }

    internal static ServiceCollection Services(Handler handler, IConfirmationPrompt? prompt = null)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IHttpClientFactory>(new Factory(handler));

        services.AddSingleton<ISecretStore>(new Secrets());

        CliTestHarness.AddKeyedArcanumResponder(services, Key);

        if (prompt is not null)
        {
            services.AddSingleton(prompt);
        }

        return services;
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    internal sealed class Secrets : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Key);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok(Key));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string secret) => Task.CompletedTask;
    }

    internal sealed class Handler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        internal List<string> Bodies { get; } = [];

        internal List<string?> ContentTypes { get; } = [];

        internal List<string> Events { get; } = [];

        internal LexiconCurationOutcomeKind Outcome { get; init; } = LexiconCurationOutcomeKind.Applied;

        internal Error? Failure { get; init; }

        internal bool FailMutationOnly { get; init; }

        internal Exception? Exception { get; init; }

        internal Action? BeforeResponse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            Requests.Add($"{request.Method} {path}");

            Events.Add(path.EndsWith("/show", StringComparison.Ordinal) ? "show" : "submit");

            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());

            BeforeResponse?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            if (Exception is not null)
            {
                throw Exception;
            }

            bool show = path.EndsWith("/show", StringComparison.Ordinal);

            Error? error = !show || !FailMutationOnly ? Failure : null;

            byte[] bytes = show
                ? JsonSerializer.SerializeToUtf8Bytes(new ApiResponse<LexiconEntryDetail>(error is null ? Detail : null, error is null, error), ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail)
                : JsonSerializer.SerializeToUtf8Bytes(new ApiResponse<LexiconCurationResult>(error is null ? new(Outcome, Detail) : null, error is null, error), ArcanumJsonContext.Default.ApiResponseLexiconCurationResult);

            return new(error is null ? HttpStatusCode.OK : HttpStatusCode.Conflict) { Content = new ByteArrayContent(bytes) };
        }
    }
}
