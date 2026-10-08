using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class CliContextMutationBoundaryTests
{
    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task Refused_model_selection_retains_the_saved_context_and_reports_failure(
        byte dispositionValue)
    {
        ArcanumClientMutationDisposition disposition =
            (ArcanumClientMutationDisposition)dispositionValue;

        FakeContextStore store = new(
            CliContextDocument.Empty with { Model = "retained-model" });

        RecordingArcanumClientMutationBoundary boundary = new(disposition);

        CliTestResult result = await CliTestHarness.RunAsync(
            Services(store, boundary),
            ["use", "model", "replacement-model"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal("retained-model", store.Load().Model);

        Assert.Equal(0, store.UnprotectedSaves);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Contains(
            disposition is ArcanumClientMutationDisposition.Blocked
                ? "maintenance"
                : "safely",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task Refused_context_clear_retains_every_saved_value_and_reports_failure(
        byte dispositionValue)
    {
        ArcanumClientMutationDisposition disposition =
            (ArcanumClientMutationDisposition)dispositionValue;

        CliContextDocument retained = new(
            CliContextDocument.CurrentVersion,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "campaign-retained",
            "workspace-retained",
            "/workspace/retained",
            "model-retained",
            Guid.Parse("23232323-2323-2323-2323-232323232323"));

        FakeContextStore store = new(retained);

        RecordingArcanumClientMutationBoundary boundary = new(disposition);

        CliTestResult result = await CliTestHarness.RunAsync(
            Services(store, boundary),
            ["use", "clear"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(retained, store.Load());

        Assert.Equal(0, store.UnprotectedSaves);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);
    }

    [Fact]
    public async Task Completed_session_selection_uses_one_exclusive_context_write()
    {
        FakeContextStore store = new(
            CliContextDocument.Empty with { Model = "retained-model" });

        RecordingArcanumClientMutationBoundary boundary = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            Services(store, boundary),
            ["use", "session", FakeResourceCatalog.SessionId.ToString("D")]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(FakeResourceCatalog.SessionId, store.Load().SessionId);

        Assert.Equal("retained-model", store.Load().Model);

        Assert.Equal(0, store.UnprotectedSaves);

        Assert.Equal(1, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);
    }

    /// <summary>
    /// A saved context written by a newer Arcanum reads as empty, so a clear (or any other mutation)
    /// would have replaced it with an older build's document without a word. The mutation refuses,
    /// says why, names the file, and leaves it byte for byte as it was.
    /// </summary>
    [Fact]
    public async Task Context_clear_refuses_to_overwrite_a_newer_version_file_and_says_why()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-context-newer-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "cli-context.json");

            const string Newer = "{\"version\":2,\"model\":\"written-by-a-newer-arcanum\"}";

            await File.WriteAllTextAsync(path, Newer);

            CliContextStore store = new(path);

            RecordingArcanumClientMutationBoundary boundary = new();

            CliTestResult result = await CliTestHarness.RunAsync(
                Services(store, boundary),
                ["use", "clear"]);

            Assert.NotEqual((int)CliExitCode.Success, result.ExitCode);

            Assert.Contains("version 2", result.Error, StringComparison.Ordinal);

            Assert.Contains(path, result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("could not be changed safely", result.Error, StringComparison.Ordinal);

            Assert.Equal(Newer, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The selection itself succeeded, then the host went away before the write could revalidate it.
    /// That is still "the host could not be reached", so it exits 3 like every other host call, and the
    /// saved context stays as it was.
    /// </summary>
    [Fact]
    public async Task A_host_that_goes_away_during_revalidation_exits_3_and_keeps_the_saved_context()
    {
        FakeContextStore store = new(
            CliContextDocument.Empty with { Model = "retained-model" });

        RecordingArcanumClientMutationBoundary boundary = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            Services(store, boundary, new UnreachableHandler()),
            ["use", "session", FakeResourceCatalog.SessionId.ToString("D")]);

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        Assert.Equal("retained-model", store.Load().Model);

        Assert.Null(store.Load().SessionId);

        Assert.Equal(0, store.ExclusiveSaves);
    }

    /// <summary>
    /// The same for a campaign, whose revalidation builds its own refusal: it used to drop the
    /// <c>Connection.*</c> code and exit 2, so a wrapper that retries on 3 never saw the retryable case.
    /// </summary>
    [Fact]
    public async Task A_host_that_goes_away_during_campaign_revalidation_exits_3_and_keeps_the_saved_context()
    {
        FakeContextStore store = new(
            CliContextDocument.Empty with { Model = "retained-model" });

        RecordingArcanumClientMutationBoundary boundary = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            Services(store, boundary, new UnreachableHandler()),
            ["use", "campaign", FakeResourceCatalog.CampaignId.ToString("D")]);

        Assert.True(
            result.ExitCode == (int)CliExitCode.NetworkError,
            $"exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error}");

        Assert.Equal("retained-model", store.Load().Model);

        Assert.Null(store.Load().CampaignId);

        Assert.Equal(0, store.ExclusiveSaves);
    }

    private static ServiceCollection Services<TStore>(
        TStore store,
        RecordingArcanumClientMutationBoundary boundary,
        HttpMessageHandler? handler = null)
        where TStore : class, ICliContextStore, ICliContextExclusiveWriter
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(store);

        services.RemoveAll<ICliContextExclusiveWriter>();

        services.AddSingleton<ICliContextExclusiveWriter>(store);

        services.RemoveAll<ICliResourceCatalog>();

        services.AddSingleton<ICliResourceCatalog>(
            new FakeResourceCatalog());

        services.RemoveAll<ArcanumApiClient>();

        services.AddSingleton(
            new ArcanumApiClient(
                new FakeHttpClientFactory(handler ?? new SessionHandler()),
                ArcanumApiCredentialLeaseTestFactory.Create("test-key")));

        services.RemoveAll<IArcanumClientMutationBoundary>();

        services.AddSingleton<IArcanumClientMutationBoundary>(boundary);

        return services;
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.RequestUri!.AbsolutePath
                != $"/api/sessions/{FakeResourceCatalog.SessionId:D}")
            {
                throw new InvalidOperationException(
                    $"Unexpected request to {request.RequestUri.AbsolutePath}.");
            }

            ApiResponse<SessionDetailDto> envelope =
                ApiResponse<SessionDetailDto>.FromResult(
                    Result<SessionDetailDto>.Success(
                        new SessionDetailDto(
                            FakeResourceCatalog.SessionId,
                            null,
                            "Selected",
                            "Active",
                            1,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch,
                            null,
                            0)));

            string json = JsonSerializer.Serialize(
                envelope,
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),
                });
        }
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

    private sealed class FakeContextStore(
        CliContextDocument document) :
        ICliContextStore,
        ICliContextExclusiveWriter
    {
        private CliContextDocument _document = document;

        internal int ExclusiveSaves { get; private set; }

        internal int UnprotectedSaves { get; private set; }

        public string FilePath => "/tmp/arcanum-context-boundary.json";

        public CliContextDocument Load() => _document;

        public void Save(CliContextDocument value)
        {
            UnprotectedSaves++;

            _document = value;
        }

        void ICliContextExclusiveWriter.SaveUnderExclusive(
            CliContextDocument value)
        {
            ExclusiveSaves++;

            _document = value;
        }
    }

    private sealed class FakeResourceCatalog : ICliResourceCatalog
    {
        internal static Guid SessionId { get; } =
            Guid.Parse("24242424-2424-2424-2424-242424242424");

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<ModelInfoDto>.Selected(
                    new ModelInfoDto(
                        "replacement-model",
                        "provider",
                        "OpenAICompatible",
                        "https://provider.invalid/v1",
                        8_192)));

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<SessionSummaryDto>.Selected(
                    new SessionSummaryDto(
                        SessionId,
                        null,
                        "Selected",
                        "Active",
                        1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));

        internal static Guid CampaignId { get; } =
            Guid.Parse("25252525-2525-2525-2525-252525252525");

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<CampaignDto>.Selected(
                    new CampaignDto(
                        CampaignId,
                        "Selected campaign",
                        "/srv/campaign",
                        WorkspaceType.Campaign,
                        null,
                        CampaignSettings.CreateDefault(),
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
            string? identifier,
            string? workspace,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("This context-boundary test did not select that resource kind.");
    }
}
