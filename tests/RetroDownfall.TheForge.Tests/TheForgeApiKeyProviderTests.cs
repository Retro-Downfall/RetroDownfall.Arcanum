using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class TheForgeApiKeyProviderTests
{

    [Fact]
    public async Task GetApiKeyAsync_ReadsFromOsCredentialStore()
    {

        InMemoryOsCredentialStore store = new();

        store.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MasterApiKeyAccount, "key-from-os");

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("key-from-os", key);

    }

    [Fact]
    public async Task GetApiKeyAsync_MigratesLegacyForgeJsonApiKeyIntoOsStore()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeSettings settings = new() { ApiKey = "legacy-plaintext" };

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(settings),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("legacy-plaintext", key);

        OsCredentialStoreResult stored = store.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        Assert.Equal("legacy-plaintext", stored.Value);

    }

    [Fact]
    public async Task GetApiKeyAsync_PromptsAndPersistsWhenMissing()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ => Task.FromResult<string?>("pasted-key"));

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("pasted-key", key);

        OsCredentialStoreResult stored = store.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal("pasted-key", stored.Value);

    }

    [Fact]
    public async Task GetApiKeyAsync_ReturnsNullWhenNothingAvailable()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Null(key);

    }

    [Fact]
    public async Task GetApiKeyAsync_DoesNotCacheNullWhenPromptUnavailable()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        int promptCalls = 0;

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ =>
            {
                promptCalls++;

                throw new InvalidOperationException("Main window is not ready");
            });

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(2, promptCalls);

    }

    [Fact]
    public async Task GetApiKeyAsync_DoesNotRepromptAfterUserDecline()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        int promptCalls = 0;

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ =>
            {
                promptCalls++;

                return Task.FromResult<string?>(null);
            });

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(1, promptCalls);

    }

    [Fact]
    public async Task ClearPasteDecline_AllowsRepromptAfterUserDecline()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        int promptCalls = 0;

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ =>
            {
                promptCalls++;

                return Task.FromResult<string?>(null);
            });

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(1, promptCalls);

        provider.ClearPasteDecline();

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(2, promptCalls);

    }

    [Fact]
    public async Task ResolveAsync_UsesEnvironmentVariableBeforeCliShellOut()
    {

        InMemoryOsCredentialStore store = new();

        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            [ApiKeyResolver.EnvironmentVariableName] = "  env-key-value  ",
        };

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            name => env.TryGetValue(name, out string? value) ? value : null);

        ApiKeyResolution resolution = await resolver.ResolveAsync(new TheForgeSettings(), CancellationToken.None);

        Assert.Equal("env-key-value", resolution.Key);

        Assert.True(resolution.IsSessionOnly);

        OsCredentialStoreResult stored = store.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal(OsCredentialStoreStatus.NotFound, stored.Status);

    }

    [Fact]
    public async Task ResolveAsync_TreatsWhitespaceEnvironmentVariableAsAbsent()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            _ => "   ",
            NoCliShellOut);

        ApiKeyResolution resolution = await resolver.ResolveAsync(new TheForgeSettings(), CancellationToken.None);

        Assert.Null(resolution.Key);

    }

    [Fact]
    public async Task GetApiKeyAsync_KeepsSessionOnlyKeyWhenOsPersistFails()
    {

        UnavailableStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ => Task.FromResult<string?>("pasted-session-only"));

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("pasted-session-only", key);

        Assert.True(provider.IsSessionOnlyKey);

    }

    [Fact]
    public async Task ClearPasteDecline_ClearsCachedKeySoPasteCanOverride()
    {

        InMemoryOsCredentialStore store = new();

        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            [ApiKeyResolver.EnvironmentVariableName] = "bad-env-key",
        };

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            name => env.TryGetValue(name, out string? value) ? value : null,
            NoCliShellOut);

        int promptCalls = 0;

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ =>
            {
                promptCalls++;

                return Task.FromResult<string?>("replacement-key");
            });

        Assert.Equal("bad-env-key", await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(0, promptCalls);

        provider.ClearPasteDecline();

        env.Remove(ApiKeyResolver.EnvironmentVariableName);

        Assert.Equal("replacement-key", await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Equal(1, promptCalls);

    }

    private sealed class UnavailableStore : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

    }

    private static Task<string?> NoCliShellOut(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    private sealed class StaticOptions(TheForgeSettings current) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue { get; } = current;

        public TheForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

    private sealed class NullSettingsStore : ITheForgeSettingsStore
    {

        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "forge-test-null.json");

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SavePatchAsync(Func<TheForgeSettings, TheForgeSettings> patch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

}
