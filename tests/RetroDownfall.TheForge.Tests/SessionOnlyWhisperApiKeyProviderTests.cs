using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class SessionOnlyWhisperApiKeyProviderTests
{

    [Fact]
    public async Task GetApiKeyAsync_WarnsOnlyOnceAcrossManyCalls()
    {

        FakeWhispersService whispers = new();

        SessionOnlyWhisperApiKeyProvider provider = NewProvider(whispers);

        for (int i = 0; i < 10; i++)
        {

            Assert.Equal("pasted-session-only", await provider.GetApiKeyAsync(CancellationToken.None));

        }

        (WhisperSeverity Severity, string Message, string? Title) call = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Warning, call.Severity);

        Assert.Equal(TheForgeApiKeyProvider.SessionOnlyPersistWarning, call.Message);

    }

    [Fact]
    public async Task GetApiKeyAsync_WarnsAgainAfterClearPasteDecline()
    {

        FakeWhispersService whispers = new();

        SessionOnlyWhisperApiKeyProvider provider = NewProvider(whispers);

        await provider.GetApiKeyAsync(CancellationToken.None);

        await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Single(whispers.Calls);

        provider.ClearPasteDecline();

        await provider.GetApiKeyAsync(CancellationToken.None);

        await provider.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal(2, whispers.Calls.Count);

    }

    [Fact]
    public async Task GetApiKeyAsync_DoesNotWarnWhenKeyIsPersisted()
    {

        InMemoryOsCredentialStore store = new();

        store.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MasterApiKeyAccount, "key-from-os");

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider inner = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        FakeWhispersService whispers = new();

        SessionOnlyWhisperApiKeyProvider provider = new(inner, new Lazy<IWhispersService>(() => whispers));

        Assert.Equal("key-from-os", await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.Empty(whispers.Calls);

    }

    private static SessionOnlyWhisperApiKeyProvider NewProvider(FakeWhispersService whispers)
    {

        ApiKeyResolver resolver = new(
            new UnavailableStore(),
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider inner = new(
            resolver,
            new StaticOptions(new TheForgeSettings()),
            NullLogger<TheForgeApiKeyProvider>.Instance,
            _ => Task.FromResult<string?>("pasted-session-only"));

        return new SessionOnlyWhisperApiKeyProvider(inner, new Lazy<IWhispersService>(() => whispers));

    }

    private static Task<string?> NoCliShellOut(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

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

    private sealed class StaticOptions(TheForgeSettings current) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue { get; } = current;

        public TheForgeSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

    private sealed class NullSettingsStore : ITheForgeSettingsStore
    {

        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "forge-test-session-only.json");

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SavePatchAsync(Func<TheForgeSettings, TheForgeSettings> patch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

}
