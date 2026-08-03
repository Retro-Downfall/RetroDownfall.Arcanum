using System.Net;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliResourceCatalogSpellPagingTests
{

    [Fact]
    public async Task SelectSpellAsync_follows_the_opaque_cursor_to_a_later_page()
    {

        SpellCatalogHandler handler = new();

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        CliResourceCatalog catalog = new(
            client,
            new NonInteractiveEnvironment(),
            new NeverPicker(),
            new RecordingRecentStore());

        ResourceSelectionResult<SpellSummary> result = await catalog
            .SelectSpellAsync(
                "target-spell",
                "/workspace",
                CancellationToken.None);

        Assert.Equal(ResourceSelectionStatus.Selected, result.Status);

        Assert.Equal("target-spell", result.Value?.Name);

        Assert.Equal(2, handler.Requests.Count);

        Assert.DoesNotContain(
            "cursor=",
            handler.Requests[0].RequestUri!.Query,
            StringComparison.Ordinal);

        Assert.Contains(
            "cursor=opaque-page-two",
            handler.Requests[1].RequestUri!.Query,
            StringComparison.Ordinal);

    }

    private sealed class SpellCatalogHandler : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

            bool secondPage = request.RequestUri!.Query.Contains(
                "cursor=opaque-page-two",
                StringComparison.Ordinal);

            SpellCatalogPage page = secondPage
                ? new SpellCatalogPage(
                    [Summary("target-spell")],
                    false,
                    null,
                    null)
                : new SpellCatalogPage(
                    [Summary("first-spell")],
                    true,
                    "opaque-page-two",
                    "continue");

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<SpellCatalogPage>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseSpellCatalogPage);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(payload),

                });

        }

        private static SpellSummary Summary(string name) =>
            new(
                name,
                "description",
                SpellSource.Workspace,
                []);

    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(
            string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class NonInteractiveEnvironment : ICliEnvironment
    {

        public bool IsInteractive => false;

        public bool ColorEnabled => false;

        public bool ShouldShowManaBar => false;

    }

    private sealed class NeverPicker : IResourcePicker
    {

        public Task<ResourcePickerResult<T>> PickAsync<T>(
            ResourcePickerRequest<T> request,
            CancellationToken cancellationToken)
            where T : class =>
            throw new InvalidOperationException(
                "An exact non-interactive selection must not open the picker.");

    }

    private sealed class RecordingRecentStore : IRecentResourceStore
    {

        public IReadOnlyList<string> GetRecentIds(string resourceKind) => [];

        public void Remember(string resourceKind, string id)
        {

            _ = resourceKind;

            _ = id;

        }

    }

}
