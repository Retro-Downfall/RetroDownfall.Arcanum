using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ModelsProvidersViewModelTests
{
    [Fact]
    public async Task Refresh_PopulatesModelsAndProviders()
    {
        FakeModelsProvidersDataSource dataSource = new()
        {
            Models = [new ModelInfoDto("gpt-4o", "openai", "OpenAICompatible", "***", 128000, false)],

            Providers = [new ProviderInfoDto("openai", "OpenAICompatible", "***", "OPENAI_API_KEY", ["gpt-4o"], 128000)],
        };

        ModelsProvidersViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.Models);

        Assert.Single(viewModel.Providers);

        Assert.Equal("gpt-4o", viewModel.Models[0].Model);

        Assert.Equal("openai", viewModel.Providers[0].Name);

        Assert.Equal(
            "OPENAI_API_KEY",
            viewModel.Providers[0].CredentialEnvironmentVariable);
    }

    [Fact]
    public async Task TestProvider_CallsDataSourceAndSurfacesResult()
    {
        FakeModelsProvidersDataSource dataSource = new()
        {
            TestResult = new ProviderTestResult(true, 42, ["gpt-4o"], null),
        };

        ModelsProvidersViewModel viewModel = NewViewModel(dataSource);

        viewModel.TestEndpoint = "http://localhost:8080";

        viewModel.TestApiKey = "sk-test";

        await viewModel.TestProviderAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastTestRequest);

        Assert.Equal("http://localhost:8080", dataSource.LastTestRequest!.Endpoint);

        Assert.Equal(AiProviderKind.OpenAICompatible, dataSource.LastTestRequest.Type);

        Assert.True(viewModel.TestIsReachable is true);

        Assert.True(viewModel.TestLatencyMs is 42);

        Assert.Contains("Reachable", viewModel.TestResultText);
    }

    [Fact]
    public async Task TestProvider_WhenResultNull_SetsLastError()
    {
        FakeModelsProvidersDataSource dataSource = new() { TestResult = null };

        ModelsProvidersViewModel viewModel = NewViewModel(dataSource);

        viewModel.TestEndpoint = "http://localhost:8080";

        await viewModel.TestProviderAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));
    }

    [Fact]
    public async Task TestProvider_RealDataSource_PreservesFailedEnvelopeDiagnostic()
    {
        const string diagnostic = "InvalidUrl: Use an absolute http:// or https:// endpoint URL.";

        using FailedProviderHandler handler = new(diagnostic);

        ArcanumApiClient api = new(new ProviderClientFactory(handler),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings { BaseUrl = "http://localhost:5001" }),
            new ProviderApiKey(), NullLogger<ArcanumApiClient>.Instance);

        ModelsProvidersViewModel viewModel = new(new ModelsProvidersDataSource(new ModelService(api)), new FoundryFloorViewModel(new NullLogService()));

        viewModel.TestEndpoint = "localhost:11434/v1";

        await viewModel.TestProviderAsync(CancellationToken.None);

        Assert.False(viewModel.TestIsReachable);

        Assert.Contains(diagnostic, viewModel.TestResultText);
    }

    private sealed class ProviderClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FailedProviderHandler(string diagnostic) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiResponse<ProviderTestResult> envelope = ApiResponse<ProviderTestResult>.FromResult(
                Result<ProviderTestResult>.Failure(new Error("Validation.InvalidUrl", diagnostic)), "trace");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(envelope, TheForgeJsonContext.Default.ApiResponseProviderTestResult), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ProviderApiKey : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ClearPasteDecline() { }
    }

    [Fact]
    public async Task TestProvider_WithoutEndpoint_SetsStatusText()
    {
        ModelsProvidersViewModel viewModel = NewViewModel(new FakeModelsProvidersDataSource());

        await viewModel.TestProviderAsync(CancellationToken.None);

        Assert.Equal("Enter an endpoint to test.", viewModel.StatusText);
    }

    [Fact]
    public async Task Refresh_WhenThrows_SetsLastError()
    {
        FakeModelsProvidersDataSource dataSource = new() { ThrowOnList = true };

        ModelsProvidersViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));
    }

    private static ModelsProvidersViewModel NewViewModel(FakeModelsProvidersDataSource dataSource) =>
        new(dataSource, new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeModelsProvidersDataSource : IModelsProvidersDataSource
    {
        public IReadOnlyList<ModelInfoDto> Models { get; init; } = [];

        public IReadOnlyList<ProviderInfoDto> Providers { get; init; } = [];

        public ProviderTestResult? TestResult { get; init; }

        public bool ThrowOnList { get; init; }

        public ProviderTestRequest? LastTestRequest { get; private set; }

        public Task<IReadOnlyList<ModelInfoDto>> ListModelsAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnList)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(Models);
        }

        public Task<IReadOnlyList<ProviderInfoDto>> ListProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ThrowOnList ? throw new InvalidOperationException("boom") : Providers);

        public Task<ProviderTestResult?> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken)
        {
            LastTestRequest = request;

            return Task.FromResult(TestResult);
        }
    }
}
