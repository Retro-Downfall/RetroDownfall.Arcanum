using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ForgeApiKeyProviderTests
{

    [Fact]
    public async Task GetApiKeyAsync_ReadsFromOsCredentialStore()
    {

        InMemoryOsCredentialStore store = new();

        store.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MasterApiKeyAccount, "key-from-os");

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

        ForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new ForgeSettings()),
            NullLogger<ForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("key-from-os", key);

    }

    [Fact]
    public async Task GetApiKeyAsync_MigratesLegacyForgeJsonApiKeyIntoOsStore()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(store, NullLogger<ApiKeyResolver>.Instance);

        ForgeSettings settings = new() { ApiKey = "legacy-plaintext" };

        ForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(settings),
            NullLogger<ForgeApiKeyProvider>.Instance);

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

        ForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new ForgeSettings()),
            NullLogger<ForgeApiKeyProvider>.Instance,
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

        ForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new ForgeSettings()),
            NullLogger<ForgeApiKeyProvider>.Instance);

        string? key = await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Null(key);

    }

    private sealed class StaticOptions(ForgeSettings current) : IOptionsMonitor<ForgeSettings>
    {

        public ForgeSettings CurrentValue { get; } = current;

        public ForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ForgeSettings, string?> listener) => null;

    }

}
