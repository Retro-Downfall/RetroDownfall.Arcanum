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

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

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

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

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

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

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

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Null(key);

    }

    private sealed class StaticOptions(TheForgeSettings current) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue { get; } = current;

        public TheForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

}
