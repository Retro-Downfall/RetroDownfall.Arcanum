using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class WebResearchApiKeyResolverTests : IDisposable
{
    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public WebResearchApiKeyResolverTests()
    {
        SetEnvironment(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            null);
        SetEnvironment("ARCANUM_TEST_WEB_RESEARCH_KEY", null);
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {
            global::System.Environment.SetEnvironmentVariable(
                entry.Key,
                entry.Value);
        }
    }

    [Fact]
    public async Task Configured_environment_reference_precedes_secure_store()
    {
        SetEnvironment("ARCANUM_TEST_WEB_RESEARCH_KEY", "environment-secret");
        FakeCredentialStore store = new("stored-secret");
        WebResearchApiKeyResolver resolver = CreateResolver(
            store,
            "ARCANUM_TEST_WEB_RESEARCH_KEY");

        string? resolved = await resolver.ResolveApiKeyAsync("perplexity");

        Assert.Equal("environment-secret", resolved);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Omitted_reference_uses_default_environment_then_secure_store()
    {
        SetEnvironment(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            "default-environment-secret");
        FakeCredentialStore store = new("stored-secret");
        WebResearchApiKeyResolver resolver = CreateResolver(store);

        string? fromEnvironment =
            await resolver.ResolveApiKeyAsync("Perplexity");

        Assert.Equal("default-environment-secret", fromEnvironment);
        Assert.Equal(0, store.ReadCount);

        SetEnvironment(
            EnvironmentCredentialResolver.DefaultPerplexityApiKeyEnvironmentVariable,
            null);

        string? fromStore = await resolver.ResolveApiKeyAsync("perplexity");

        Assert.Equal("stored-secret", fromStore);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task Unknown_provider_does_not_read_perplexity_credential()
    {
        FakeCredentialStore store = new("stored-secret");
        WebResearchApiKeyResolver resolver = CreateResolver(store);

        string? resolved = await resolver.ResolveApiKeyAsync("other-provider");

        Assert.Null(resolved);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Corrupted_or_missing_secure_value_resolves_as_unconfigured()
    {
        FakeCredentialStore corrupted = new(
            SecretStoreReadResult.Corrupted("test corruption"));
        WebResearchApiKeyResolver resolver = CreateResolver(corrupted);

        string? resolved = await resolver.ResolveApiKeyAsync("perplexity");

        Assert.Null(resolved);
    }

    private static WebResearchApiKeyResolver CreateResolver(
        FakeCredentialStore store,
        string? environmentVariable = null)
    {
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                WebResearch = new WebResearchIntegrationSettings
                {
                    CredentialEnvironmentVariable = environmentVariable,
                },
            },
        };

        return new WebResearchApiKeyResolver(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            store);
    }

    private void SetEnvironment(string name, string? value)
    {
        if (!_originalEnvironment.ContainsKey(name))
        {
            _originalEnvironment[name] =
                global::System.Environment.GetEnvironmentVariable(name);
        }

        global::System.Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class FakeCredentialStore : IWebResearchCredentialStore
    {
        private readonly SecretStoreReadResult _result;

        public FakeCredentialStore(string value)
            : this(SecretStoreReadResult.Ok(value))
        {
        }

        public FakeCredentialStore(SecretStoreReadResult result)
        {
            _result = result;
        }

        public int ReadCount { get; private set; }

        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(_result);
        }

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeletePerplexityApiKeyAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
