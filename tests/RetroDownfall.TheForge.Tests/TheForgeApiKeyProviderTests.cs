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
    public async Task ResolveAsync_keeps_a_legacy_json_key_session_only_without_os_or_settings_writes()
    {

        RecordingOsCredentialStore store = new();

        RecordingSettingsStore settingsStore = new();

        ApiKeyResolver resolver = new(
            store,
            settingsStore,
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeSettings settings = new() { ApiKey = "legacy-plaintext" };

        ApiKeyResolution resolution = await resolver.ResolveAsync(
            settings,
            CancellationToken.None);

        Assert.Equal("legacy-plaintext", resolution.Key);

        Assert.True(resolution.IsSessionOnly);

        Assert.Equal(0, store.SetCallCount);

        Assert.Equal(0, store.DeleteCallCount);

        Assert.Equal(0, settingsStore.SavePatchCallCount);

    }

    [Fact]
    public async Task GetApiKeyAsync_keeps_a_pasted_key_session_only_without_credential_mutation()
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

        Assert.True(provider.IsSessionOnlyKey);

        OsCredentialStoreResult stored = store.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal(OsCredentialStoreStatus.NotFound, stored.Status);

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
    public async Task ResolveAsync_DoesNotPersistShellOutOutputThatIsNotAPlausibleKey()
    {

        InMemoryOsCredentialStore store = new();

        ApiKeyResolver resolver = new(
            store,
            new NullSettingsStore(),
            NullLogger<ApiKeyResolver>.Instance,
            _ => null,
            _ => Task.FromResult<string?>("fatal: could not open the credential store"));

        ApiKeyResolution resolution = await resolver.ResolveAsync(new TheForgeSettings(), CancellationToken.None);

        Assert.Null(resolution.Key);

        OsCredentialStoreResult stored = store.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.NotEqual(OsCredentialStoreStatus.Ok, stored.Status);

    }

    [Fact]
    public async Task ResolveAsync_keeps_a_plausible_shell_key_session_only_without_os_or_settings_writes()
    {

        RecordingOsCredentialStore store = new();

        RecordingSettingsStore settingsStore = new();

        ApiKeyResolver resolver = new(
            store,
            settingsStore,
            NullLogger<ApiKeyResolver>.Instance,
            _ => null,
            _ => Task.FromResult<string?>("shell-recovered-key"));

        ApiKeyResolution resolution = await resolver.ResolveAsync(
            new TheForgeSettings(),
            CancellationToken.None);

        Assert.Equal("shell-recovered-key", resolution.Key);

        Assert.True(resolution.IsSessionOnly);

        Assert.Equal(0, store.SetCallCount);

        Assert.Equal(0, store.DeleteCallCount);

        Assert.Equal(0, settingsStore.SavePatchCallCount);

    }

    [Fact]
    public async Task PersistPastedKeyAsync_keeps_the_key_session_only_without_credential_mutation()
    {

        RecordingOsCredentialStore store = new();

        RecordingSettingsStore settingsStore = new();

        ApiKeyResolver resolver = new(
            store,
            settingsStore,
            NullLogger<ApiKeyResolver>.Instance,
            shellOut: NoCliShellOut);

        TheForgeApiKeyProvider provider = new(
            resolver,
            new StaticOptions(new TheForgeSettings { ApiKey = "legacy-plaintext" }),
            NullLogger<TheForgeApiKeyProvider>.Instance);

        await provider.PersistPastedKeyAsync("process-only-key", CancellationToken.None);

        Assert.Equal("process-only-key", await provider.GetApiKeyAsync(CancellationToken.None));

        Assert.True(provider.IsSessionOnlyKey);

        Assert.Equal(0, store.SetCallCount);

        Assert.Equal(0, store.DeleteCallCount);

        Assert.Equal(0, settingsStore.SavePatchCallCount);

        Assert.Null(store.StoredValue);

    }

    [Fact]
    public void ResolveCliExecutablePath_ReturnsAnAbsolutePathFromPath()
    {

        string directory = OperatingSystem.IsWindows() ? @"C:\tools\arcanum" : "/opt/arcanum/bin";

        string expected = Path.Combine(directory, OperatingSystem.IsWindows() ? "arcanum.exe" : "arcanum");

        string? resolved = ApiKeyResolver.ResolveCliExecutablePath(
            _ => directory,
            candidate => string.Equals(candidate, expected, StringComparison.Ordinal));

        Assert.Equal(expected, resolved);

    }

    [Fact]
    public void ResolveCliExecutablePath_RejectsRelativePathEntriesSoAPlantedBinaryInTheWorkingDirectoryIsNeverLaunched()
    {

        string relativeEntry = "." + Path.DirectorySeparatorChar + "hostile";

        string? resolved = ApiKeyResolver.ResolveCliExecutablePath(
            _ => string.Join(Path.PathSeparator, ["", ".", relativeEntry]),
            _ => true);

        Assert.Null(resolved);

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

    private sealed class RecordingOsCredentialStore : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public int SetCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public string? StoredValue { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account) =>
            StoredValue is null
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Ok(StoredValue);

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            SetCallCount++;

            StoredValue = secret;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            DeleteCallCount++;

            StoredValue = null;

            return OsCredentialStoreResult.Ok(string.Empty);

        }

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

    private sealed class RecordingSettingsStore : ITheForgeSettingsStore
    {

        public string SettingsPath { get; } =
            Path.Combine(Path.GetTempPath(), "forge-test-recording.json");

        public int SavePatchCallCount { get; private set; }

        public Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TheForgeSettings());

        public Task SaveAsync(
            TheForgeSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ApiKeyResolver uses patch writes only.");

        public Task SavePatchAsync(
            Func<TheForgeSettings, TheForgeSettings> patch,
            CancellationToken cancellationToken = default)
        {

            SavePatchCallCount++;

            _ = patch(new TheForgeSettings { ApiKey = "legacy-plaintext" });

            return Task.CompletedTask;

        }

    }

}
