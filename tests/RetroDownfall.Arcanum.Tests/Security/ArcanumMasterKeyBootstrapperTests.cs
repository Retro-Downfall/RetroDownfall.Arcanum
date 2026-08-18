using Microsoft.AspNetCore.DataProtection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class ArcanumMasterKeyBootstrapperTests : IDisposable
{

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"arcanum-keyboot-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public ArcanumMasterKeyBootstrapperTests()
    {

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _storeDir);

        _ = Directory.CreateDirectory(_storeDir);

        DeleteSecurityDat();

    }

    public void Dispose()
    {

        try
        {

            DeleteSecurityDat();

            if (Directory.Exists(_storeDir))
            {

                Directory.Delete(_storeDir, recursive: true);

            }

        }
        catch (IOException)
        {

            // Best-effort cleanup.

        }
        catch (UnauthorizedAccessException)
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
    public void Corrupt_key_with_existing_grimoire_throws_sanitized_controlled_failure()
    {

        SecretStoreReadResult result = new(
            SecretStoreReadStatus.Corrupted,
            null,
            "secret-canary-from-data-protection");

        MasterApiKeyUnavailableException exception = Assert.Throws<MasterApiKeyUnavailableException>(
            () => ArcanumMasterKeyBootstrapper.ThrowIfCorruptedWithExistingGrimoire(
                result,
                grimoireExists: true));

        Assert.DoesNotContain("secret-canary", exception.Message, StringComparison.Ordinal);

        Assert.Contains("master API key", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Corrupt_key_without_grimoire_allows_safe_regeneration()
    {

        SecretStoreReadResult result = new(
            SecretStoreReadStatus.Corrupted,
            null,
            "secret-canary-from-data-protection");

        ArcanumMasterKeyBootstrapper.ThrowIfCorruptedWithExistingGrimoire(
            result,
            grimoireExists: false);

    }

    /// <summary>
    /// Minting a replacement master key overwrites the OS credential — and when that write fails too,
    /// deletes it. A probe that failed leaves the credential's existence unknown, and one that
    /// succeeded proves a live credential is there, so neither may authorise regeneration. This holds
    /// whether or not a Grimoire database exists: the surviving key is what every client authenticates
    /// with, and it is unrecoverable once overwritten.
    /// </summary>
    [Theory]
    [InlineData(OsCredentialStoreStatus.Failed)]
    [InlineData(OsCredentialStoreStatus.Ok)]
    public void Regeneration_is_refused_while_os_storage_may_hold_the_live_key(
        OsCredentialStoreStatus status)
    {

        OsCredentialStoreResult probe = new(status, "surviving-key", "probe message");

        MasterApiKeyUnavailableException exception =
            Assert.Throws<MasterApiKeyUnavailableException>(
                () => ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(
                    probe,
                    new ReachableFailingStore()));

        Assert.DoesNotContain("surviving-key", exception.Message, StringComparison.Ordinal);

        Assert.Contains("master API key", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(OsCredentialStoreStatus.NotFound)]
    [InlineData(OsCredentialStoreStatus.Unavailable)]
    public void Regeneration_is_allowed_when_os_storage_holds_nothing_of_ours(
        OsCredentialStoreStatus status)
    {

        OsCredentialStoreResult probe = new(status, null, "probe message");

        ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(
            probe,
            new ReachableFailingStore());

    }

    /// <summary>
    /// A failed probe against a backend that is not reachable at all is the headless-Linux case, and it
    /// clears the mint exactly as <see cref="OsCredentialStoreStatus.Unavailable"/> does.
    /// </summary>
    /// <remarks>
    /// libsecret loads, so the platform store is the real one and a read reports
    /// <see cref="OsCredentialStoreStatus.Failed"/> rather than
    /// <see cref="OsCredentialStoreStatus.Unavailable"/> — the transport error is a GError, not an absent
    /// library. Nothing of ours can be living in a backend nothing can talk to, so refusing here would
    /// strand first-run <c>arcanum serve</c> on precisely the hosts the security.dat mirror exists for.
    /// </remarks>
    [Fact]
    public void Regeneration_is_allowed_when_a_failed_probe_comes_from_an_unreachable_backend()
    {

        OsCredentialStoreResult probe = OsCredentialStoreResult.Failed("no secret service answered the bus");

        ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(
            probe,
            new UnreachableFailingStore());

    }

    /// <summary>
    /// The whole first-run sequence on a headless Linux host, through the real read path rather than the
    /// guard in isolation.
    /// </summary>
    /// <remarks>
    /// The read reports Corrupted because a failed OS read leaves the credential's existence unknown, and
    /// that is correct. What must not follow is a refusal to start: with no security.dat and no reachable
    /// backend there is no credential to overwrite, so the bootstrapper has to reach its mint.
    /// </remarks>
    [Fact]
    public async Task Headless_first_run_reaches_the_mint_instead_of_refusing_to_start()
    {

        UnreachableFailingStore os = new();

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        using OsKeychainSecretStore store = new(
            os,
            legacy,
            new ApiKeyDigestCache(new FakeTimeProvider()),
            NullLogger<OsKeychainSecretStore>.Instance);

        SecretStoreReadResult existing = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, existing.Status);

        ArcanumMasterKeyBootstrapper.ThrowIfCorruptedWithExistingGrimoire(existing, grimoireExists: false);

        OsCredentialStoreResult probe = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(probe, os);

        // The mint itself has to land somewhere, and with no reachable backend that somewhere is the
        // encrypted mirror rather than an exception.
        await store.SaveApiKeyAsync("minted-first-run-key");

        SecretStoreReadResult reread = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, reread.Status);

        Assert.Equal("minted-first-run-key", reread.Value);

    }

    private DataProtectionSecretStore CreateDataProtectionStore()
    {

        IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storeDir),
            static _ => { });

        return new DataProtectionSecretStore(dataProtectionProvider, new ApiKeyDigestCache(new FakeTimeProvider()));

    }

    private static void DeleteSecurityDat()
    {

        string path = ArcanumPaths.ApiKeyStoreFile;

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (IOException)
        {

            // Best-effort cleanup.

        }
        catch (UnauthorizedAccessException)
        {

            // Best-effort cleanup.

        }

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    /// <summary>
    /// A backend that answers and refuses: a locked macOS keychain, or an ACL denial after a resign.
    /// </summary>
    private sealed class ReachableFailingStore : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Failed("the keychain is locked");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Failed("the keychain is locked");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Failed("the keychain is locked");

    }

    /// <summary>
    /// Headless Linux: libsecret loaded, so every call reaches it and comes back with a transport
    /// failure, while the reachability probe correctly reports that nothing is on the bus.
    /// </summary>
    private sealed class UnreachableFailingStore : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Failed("no secret service answered the bus");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Failed("no secret service answered the bus");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Failed("no secret service answered the bus");

    }

}
