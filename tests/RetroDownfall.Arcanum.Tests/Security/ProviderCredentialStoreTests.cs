using System.Text;

using Microsoft.AspNetCore.DataProtection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Issue #47 — inference-provider credentials must be storable in the OS credential manager with an
/// owner-only Data Protection mirror, must never appear in plaintext on disk, and must expose
/// presence/status and deletion without disclosing the credential.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ProviderCredentialStoreTests : IDisposable
{

    private readonly string _testHome =
        Path.Combine(
            Path.GetTempPath(),
            $"arcanum-provider-credential-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private readonly IDataProtectionProvider _dataProtectionProvider;

    public ProviderCredentialStoreTests()
    {

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

        Directory.CreateDirectory(_testHome);

        _dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_testHome),
            _ => { });

    }

    public void Dispose()
    {

        try
        {

            if (Directory.Exists(_testHome))
            {

                Directory.Delete(_testHome, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup.

        }
        finally
        {

            foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
            {

                global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            }

        }

    }

    [Fact]
    public void Credential_identity_is_stable_and_distinct_from_the_web_research_account()
    {

        string openAi = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI");

        string spaced = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("open ai");

        Assert.Equal("inference-provider-OPENAI-api-key", openAi);

        Assert.Equal("inference-provider-OPEN_AI-api-key", spaced);

        Assert.NotEqual(ArcanumCredentialIdentity.PerplexityApiKeyAccount, openAi);

        Assert.NotEqual(
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("perplexity"));

        Assert.True(
            ArcanumCredentialIdentity.IsInferenceProviderApiKeyAccount(openAi));

        Assert.False(
            ArcanumCredentialIdentity.IsInferenceProviderApiKeyAccount(
                ArcanumCredentialIdentity.PerplexityApiKeyAccount));

        Assert.False(
            ArcanumCredentialIdentity.IsInferenceProviderApiKeyAccount(
                ArcanumCredentialIdentity.MasterApiKeyAccount));

    }

    [Fact]
    public async Task Save_and_get_use_the_os_store_and_an_encrypted_mirror()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "sk-provider-secret-value");

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync("OpenAI");

        OsCredentialStoreResult direct = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"));

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);

        Assert.Equal("sk-provider-secret-value", result.Value);

        Assert.Equal("sk-provider-secret-value", direct.Value);

        string mirror = ArcanumPaths.InferenceProviderApiKeyStoreFile("OpenAI");

        Assert.True(File.Exists(mirror));

        Assert.DoesNotContain(
            "sk-provider-secret-value",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(mirror)),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Providers_are_isolated_from_each_other()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "sk-openai");

        await store.SaveApiKeyAsync("Ollama", "sk-ollama");

        Assert.Equal("sk-openai", (await store.GetApiKeyReadResultAsync("OpenAI")).Value);

        Assert.Equal("sk-ollama", (await store.GetApiKeyReadResultAsync("Ollama")).Value);

        await store.DeleteApiKeyAsync("OpenAI");

        Assert.Equal(
            SecretStoreReadStatus.Missing,
            (await store.GetApiKeyReadResultAsync("OpenAI")).Status);

        Assert.Equal("sk-ollama", (await store.GetApiKeyReadResultAsync("Ollama")).Value);

    }

    [Fact]
    public async Task Unavailable_os_store_round_trips_through_the_encrypted_mirror()
    {

        using ProviderCredentialStore writer = CreateStore(new UnavailableOsCredentialStore());

        await writer.SaveApiKeyAsync("OpenAI", "mirror-secret");

        using ProviderCredentialStore reader = CreateStore(new UnavailableOsCredentialStore());

        SecretStoreReadResult result = await reader.GetApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);

        Assert.Equal("mirror-secret", result.Value);

    }

    [Fact]
    public async Task Legacy_mirror_is_promoted_into_the_os_store_on_first_read()
    {

        using (ProviderCredentialStore legacyWriter = CreateStore(new UnavailableOsCredentialStore()))
        {

            await legacyWriter.SaveApiKeyAsync("OpenAI", "legacy-secret");

        }

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync("OpenAI");

        OsCredentialStoreResult promoted = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"));

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);

        Assert.Equal("legacy-secret", result.Value);

        Assert.Equal(OsCredentialStoreStatus.Ok, promoted.Status);

        Assert.Equal("legacy-secret", promoted.Value);

    }

    [Fact]
    public async Task Peek_returns_a_legacy_mirror_without_promoting_or_changing_files()
    {

        using (ProviderCredentialStore writer = CreateStore(new UnavailableOsCredentialStore()))
        {

            await writer.SaveApiKeyAsync("OpenAI", "peek-provider-secret");

        }

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using ProviderCredentialStore store = CreateStore(os);

        IProviderCredentialStore contract = store;

        SecretStoreReadResult first = await contract.PeekApiKeyReadResultAsync("OpenAI");

        SecretStoreReadResult second = await contract.PeekApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Ok, first.Status);

        Assert.Equal("peek-provider-secret", first.Value);

        Assert.Equal(first, second);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task Peek_fails_closed_when_the_os_read_is_ambiguous_even_with_a_valid_mirror()
    {

        using (ProviderCredentialStore writer = CreateStore(new UnavailableOsCredentialStore()))
        {

            await writer.SaveApiKeyAsync("OpenAI", "possibly-superseded-provider-secret");

        }

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(
            OsCredentialStoreResult.Failed("test ambiguous read"));

        using ProviderCredentialStore store = CreateStore(os);

        SecretStoreReadResult result = await ((IProviderCredentialStore)store)
            .PeekApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task Peek_reports_missing_and_corrupt_mirrors_without_creating_or_repairing_state()
    {

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using ProviderCredentialStore store = CreateStore(os);

        string[] missingBefore = SnapshotFileTree();

        SecretStoreReadResult missing = await ((IProviderCredentialStore)store)
            .PeekApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Missing, missing.Status);

        Assert.Equal(missingBefore, SnapshotFileTree());

        string path = ArcanumPaths.InferenceProviderApiKeyStoreFile("OpenAI");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        string[] corruptBefore = SnapshotFileTree();

        SecretStoreReadResult corrupt = await ((IProviderCredentialStore)store)
            .PeekApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Corrupted, corrupt.Status);

        Assert.Equal(corruptBefore, SnapshotFileTree());

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

    }

    [Fact]
    public async Task A_corrupt_mirror_fails_closed_without_generating_a_replacement()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "sk-openai");

        _ = os.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"));

        await File.WriteAllBytesAsync(
            ArcanumPaths.InferenceProviderApiKeyStoreFile("OpenAI"),
            [1, 2, 3, 4]);

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync("OpenAI");

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);

        Assert.NotNull(result.Message);

        Assert.DoesNotContain("sk-openai", result.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Repeated_saves_are_idempotent_and_replace_the_prior_value()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "first");

        await store.SaveApiKeyAsync("OpenAI", "second");

        await store.SaveApiKeyAsync("OpenAI", "second");

        Assert.Equal("second", (await store.GetApiKeyReadResultAsync("OpenAI")).Value);

    }

    [Fact]
    public async Task A_failed_os_write_removes_the_superseded_os_credential()
    {

        InMemoryOsCredentialStore backing = new();

        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"),
            "sk-old");

        using ProviderCredentialStore store = CreateStore(new WriteFailingStore(backing));

        await store.SaveApiKeyAsync("OpenAI", "sk-new");

        Assert.Equal("sk-new", (await store.GetApiKeyReadResultAsync("OpenAI")).Value);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            backing.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI")).Status);

    }

    [Fact]
    public async Task A_failed_os_write_fails_closed_when_the_superseded_credential_survives()
    {

        InMemoryOsCredentialStore backing = new();

        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"),
            "sk-old");

        using ProviderCredentialStore store =
            CreateStore(new WriteFailingStore(backing, deleteFails: true));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveApiKeyAsync("OpenAI", "sk-new"));

        Assert.Equal("sk-old", (await store.GetApiKeyReadResultAsync("OpenAI")).Value);

        Assert.False(File.Exists(ArcanumPaths.InferenceProviderApiKeyStoreFile("OpenAI")));

    }

    /// <summary>
    /// A backend that no longer answers is the headless-Linux case §11.2 item 4 promises still
    /// works: the encrypted mirror is the operating mode there, not a thrown save.
    /// </summary>
    /// <remarks>
    /// Goes through the real <see cref="OsCredentialStore"/> rather than a bare fake, because the
    /// defect lived in the wrapper: it reported a backend as reachable on the strength of the
    /// platform being supported, so the purge's fail-closed branch fired on a host with nothing to
    /// fail closed against and the credential was stored nowhere at all.
    /// </remarks>
    [Fact]
    public async Task A_backend_that_stops_answering_degrades_to_the_mirror_instead_of_throwing()
    {

        using ProviderCredentialStore store =
            CreateStore(new OsCredentialStore(new SilentBackendStore()));

        await store.SaveApiKeyAsync("OpenAI", "sk-new");

        Assert.Equal("sk-new", (await store.GetApiKeyReadResultAsync("OpenAI")).Value);

    }

    [Fact]
    public async Task Delete_is_safe_when_nothing_is_stored()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.DeleteApiKeyAsync("OpenAI");

        Assert.Equal(
            SecretStoreReadStatus.Missing,
            (await store.GetApiKeyReadResultAsync("OpenAI")).Status);

    }

    [Fact]
    public async Task Status_reports_presence_without_returning_the_credential()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        Assert.False(await store.HasApiKeyAsync("OpenAI"));

        await store.SaveApiKeyAsync("OpenAI", "sk-openai");

        Assert.True(await store.HasApiKeyAsync("OpenAI"));

    }

    [Fact]
    public async Task Empty_or_whitespace_credentials_are_rejected()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveApiKeyAsync("OpenAI", "   "));

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveApiKeyAsync("   ", "sk-openai"));

    }

    [Fact]
    public async Task Resolver_peek_uses_only_the_non_mutating_credential_contract()
    {

        RecordingProviderCredentialStore store = new();

        ProviderApiKeyResolver resolver = new(store);

        ProviderSettings provider = new()
        {

            Name = "OpenAI",

            Endpoint = "https://api.openai.invalid/v1",

        };

        Assert.Equal(
            "peek-secret",
            await resolver.PeekAsync(provider, CancellationToken.None));

        Assert.Equal(0, store.OrdinaryReadCount);

        Assert.Equal(1, store.PeekReadCount);

        Assert.Equal(0, store.WriteCount);

    }

    [Fact]
    public async Task Resolver_prefers_an_environment_reference_over_the_secure_store()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "stored-secret");

        ProviderSettings provider = new()
        {

            Name = "OpenAI",

            Endpoint = "https://api.openai.invalid/v1",

            CredentialEnvironmentVariable = "ARCANUM_TEST_PROVIDER_KEY",

        };

        SetEnvironment("ARCANUM_TEST_PROVIDER_KEY", "environment-secret");

        ProviderApiKeyResolver resolver = new(store);

        Assert.Equal(
            "environment-secret",
            await resolver.ResolveAsync(provider, CancellationToken.None));

        SetEnvironment("ARCANUM_TEST_PROVIDER_KEY", null);

        Assert.Equal(
            "stored-secret",
            await resolver.ResolveAsync(provider, CancellationToken.None));

    }

    [Fact]
    public async Task Resolver_returns_null_when_no_credential_is_configured()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        ProviderSettings provider = new()
        {

            Name = "Ollama",

            Endpoint = "http://127.0.0.1:11434/v1",

        };

        ProviderApiKeyResolver resolver = new(store);

        Assert.Null(await resolver.ResolveAsync(provider, CancellationToken.None));

    }

    [Fact]
    public async Task Resolver_treats_a_corrupt_stored_credential_as_absent()
    {

        InMemoryOsCredentialStore os = new();

        using ProviderCredentialStore store = CreateStore(os);

        await store.SaveApiKeyAsync("OpenAI", "sk-openai");

        _ = os.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("OpenAI"));

        await File.WriteAllBytesAsync(
            ArcanumPaths.InferenceProviderApiKeyStoreFile("OpenAI"),
            [9, 9, 9]);

        ProviderSettings provider = new()
        {

            Name = "OpenAI",

            Endpoint = "https://api.openai.invalid/v1",

        };

        ProviderApiKeyResolver resolver = new(store);

        Assert.Null(await resolver.ResolveAsync(provider, CancellationToken.None));

    }

    private ProviderCredentialStore CreateStore(IOsCredentialStore osStore) =>
        new(osStore, _dataProtectionProvider);

    private void SetEnvironment(string name, string? value)
    {

        if (!_originalEnvironment.ContainsKey(name))
        {

            _originalEnvironment[name] =
                global::System.Environment.GetEnvironmentVariable(name);

        }

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private string[] SnapshotFileTree() => Directory
        .EnumerateFiles(_testHome, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path =>
            Path.GetRelativePath(_testHome, path)
            + "|"
            + File.GetLastWriteTimeUtc(path).Ticks
            + "|"
            + Convert.ToBase64String(File.ReadAllBytes(path)))
        .ToArray();

    private sealed class RecordingProviderCredentialStore : IProviderCredentialStore
    {

        public int OrdinaryReadCount { get; private set; }

        public int PeekReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            OrdinaryReadCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("ordinary-secret"));

        }

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            PeekReadCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("peek-secret"));

        }

        public Task SaveApiKeyAsync(
            string providerName,
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            WriteCount++;

            return Task.CompletedTask;

        }

        public Task DeleteApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            WriteCount++;

            return Task.CompletedTask;

        }

    }

    private sealed class RecordingOsCredentialStore(OsCredentialStoreResult readResult)
        : IOsCredentialStore
    {

        public bool IsAvailable => readResult.Status != OsCredentialStoreStatus.Unavailable;

        public int SetCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account) => readResult;

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            SetCallCount++;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            DeleteCallCount++;

            return OsCredentialStoreResult.Ok(string.Empty);

        }

    }

    /// <summary>
    /// A reachable OS credential backend that refuses writes (locked keychain, transient Secret
    /// Service error) while reads and deletes still work.
    /// </summary>
    private sealed class WriteFailingStore(IOsCredentialStore inner, bool deleteFails = false)
        : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Failed("test write failure");

        public OsCredentialStoreResult Delete(string service, string account) =>
            deleteFails
                ? OsCredentialStoreResult.Failed("test delete failure")
                : inner.Delete(service, account);

    }

    /// <summary>
    /// A backend whose client library is present — so the platform-support answer is optimistic —
    /// while nothing on the far side answers anything. The headless Linux shape: libsecret loads and
    /// no Secret Service is on the bus.
    /// </summary>
    private sealed class SilentBackendStore : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("no secret service answered");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable("no secret service answered");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("no secret service answered");

    }

    private sealed class UnavailableOsCredentialStore : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("unavailable");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable("unavailable");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("unavailable");

    }

}

/// <summary>
/// The credential account and the <c>ARCANUM_PROVIDER_{NAME}_API_KEY</c> reference must always name
/// the same provider. Secrets cannot reference Core, so the shared normalization rule is duplicated
/// and pinned here instead of shared through a type.
/// </summary>
public sealed class ProviderCredentialIdentityParityTests
{

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("open ai")]
    [InlineData("  Ollama  ")]
    [InlineData("gpt-4o::local")]
    [InlineData("provider 1")]
    [InlineData("ünïcødé")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Secure_store_and_environment_reference_normalize_provider_names_identically(
        string providerName)
    {

        Assert.Equal(
            EnvironmentCredentialResolver.NormalizeProviderName(providerName),
            ArcanumCredentialIdentity.NormalizeProviderName(providerName));

    }

    [Fact]
    public void Null_provider_names_normalize_identically()
    {

        Assert.Equal(
            EnvironmentCredentialResolver.NormalizeProviderName(null),
            ArcanumCredentialIdentity.NormalizeProviderName(null));

    }

    [Fact]
    public void Mirror_file_names_stay_inside_the_secret_store_directory()
    {

        string traversal = ArcanumPaths.InferenceProviderApiKeyStoreFile("../../escape");

        Assert.Equal(
            Path.GetFullPath(ArcanumPaths.SecretStoreDirectory),
            Path.GetFullPath(Path.GetDirectoryName(traversal)!));

    }

}
