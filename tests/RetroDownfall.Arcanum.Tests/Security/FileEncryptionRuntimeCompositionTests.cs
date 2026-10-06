using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class FileEncryptionRuntimeCompositionTests
{
    /// <summary>
    /// The Data Protection key ring wraps every encrypted mirror. A stack that lets the framework
    /// create its directory on first use gets the umask's permissions (0755), so the ring is listable
    /// and readable by other local users; the host's bootstrap path already makes it owner-only.
    /// </summary>
    [SkippableTheory]
    [InlineData("cli")]
    [InlineData("configuration-presets")]
    public void CliClientStack_CreatesOwnerOnlyKeyRingDirectory(string stack)
    {
        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If has run; kept so the platform analyzer sees the guard.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using ArcanumTestHomeScope home = new("arcanum-keyring-composition");

        ServiceCollection services = [];
        services.AddLogging();

        if (stack == "cli")
        {
            services.AddArcanumCliClientStack();
        }
        else
        {
            services.AddArcanumConfigurationPresets();
        }

        using ServiceProvider provider = services.BuildServiceProvider();

        _ = provider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Arcanum.Tests.KeyRingComposition")
            .Protect([1, 2, 3]);

        string keyRing = DataProtectionKeyPaths.Directory;

        Assert.StartsWith(home.Root, keyRing, StringComparison.Ordinal);
        Assert.True(Directory.Exists(keyRing));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(keyRing));
    }

    /// <summary>
    /// Windows lane for the key-ring posture gate: the ring a stack creates on first use carries a
    /// protected ACL owned by, and granting only, the current user. The Unix lane above asserts the
    /// mode bits.
    /// </summary>
    [SkippableTheory]
    [InlineData("cli")]
    [InlineData("configuration-presets")]
    public void Windows_key_ring_directory_is_created_owner_only(string stack)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Asserts a real Windows access-control list.");

        using ArcanumTestHomeScope home = new("arcanum-keyring-composition-windows");

        ServiceCollection services = [];
        services.AddLogging();

        if (stack == "cli")
        {
            services.AddArcanumCliClientStack();
        }
        else
        {
            services.AddArcanumConfigurationPresets();
        }

        using ServiceProvider provider = services.BuildServiceProvider();

        _ = provider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Arcanum.Tests.KeyRingComposition")
            .Protect([1, 2, 3]);

        Assert.True(SecureFilePermissions.HasOwnerOnlyPosture(DataProtectionKeyPaths.Directory, isDirectory: true));
    }

    /// <summary>
    /// The behavioural tests above build two of the stacks. The host's own registration and the host's
    /// key bootstrap build theirs inside larger compositions (one reads real OS key storage), so this
    /// pins every Data Protection registration in the product to the one owner-only key-ring call.
    /// </summary>
    [Fact]
    public void Every_data_protection_registration_persists_to_the_owner_only_key_ring()
    {
        List<string> registrations = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            string compact = string.Concat(source.Text.Where(static character => !char.IsWhiteSpace(character)));

            int index = 0;

            while ((index = compact.IndexOf(".AddDataProtection()", index, StringComparison.Ordinal)) >= 0)
            {
                int statementEnd = compact.IndexOf(';', index);

                string statement = compact[index..statementEnd];

                Assert.True(
                    statement.Contains(".PersistKeysToOwnerOnlyKeyRing()", StringComparison.Ordinal),
                    $"{source.RelativePath}: {statement}");

                Assert.DoesNotContain(".PersistKeysToFileSystem(", statement, StringComparison.Ordinal);

                registrations.Add(source.RelativePath.Replace(Path.DirectorySeparatorChar, '/'));

                index = statementEnd;
            }
        }

        Assert.Contains("src/RetroDownfall.Arcanum.Infrastructure/Security/ArcanumMasterKeyBootstrapper.cs", registrations);

        // The configuration-preset stack, the CLI client stack, and the host's infrastructure.
        Assert.Equal(
            3,
            registrations.Count(static path => path.EndsWith(
                "DependencyInjection/ServiceCollectionExtensions.cs",
                StringComparison.Ordinal)));
    }

    /// <summary>
    /// A key ring whose owner-only posture cannot be established must fail every secret operation
    /// closed — but not the composition itself. Building the ring eagerly made resolving the secret
    /// store throw, so <c>arcanum doctor</c> (whose checks take the secret store) could not run in
    /// exactly the state its key-ring and permission checks exist to report. The ring is now
    /// established on first use, a read reports <see cref="SecretStoreReadStatus.Unreadable"/> (never
    /// <see cref="SecretStoreReadStatus.Corrupted"/>, whose guidance deletes things), and a save throws
    /// the posture failure without writing.
    /// </summary>
    [Fact]
    public async Task A_key_ring_that_cannot_be_made_owner_only_fails_secrets_closed_without_breaking_composition()
    {
        using ArcanumTestHomeScope home = new("arcanum-keyring-posture");

        using (ServiceProvider earlier = BuildCliStack())
        {
            await earlier.GetRequiredService<ISecretStore>().SaveGrimoireEncryptionSecretAsync("grimoire-secret");
        }

        byte[] sealedBefore = await File.ReadAllBytesAsync(ArcanumPaths.GrimoireKeyStoreFile);

        string keyRing = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataProtectionKeyPaths.Directory));

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = (path, isDirectory) =>
            isDirectory
            && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                keyRing,
                StringComparison.Ordinal)
                ? false
                : null;

        try
        {
            using ServiceProvider provider = BuildCliStack();

            ISecretStore store = provider.GetRequiredService<ISecretStore>();

            SecretStoreReadResult read = await store.GetGrimoireEncryptionSecretReadResultAsync();

            Assert.Equal(SecretStoreReadStatus.Unreadable, read.Status);

            Assert.Null(read.Value);

            Assert.Contains("key ring", read.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("remove", read.Message, StringComparison.OrdinalIgnoreCase);

            _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => store.SaveGrimoireEncryptionSecretAsync("replacement-secret"));

            Assert.Equal(sealedBefore, await File.ReadAllBytesAsync(ArcanumPaths.GrimoireKeyStoreFile));
        }
        finally
        {
            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;
        }
    }

    private static ServiceProvider BuildCliStack()
    {
        ServiceCollection services = [];
        services.AddLogging();

        // Registered first so the stack's TryAdd keeps it: nothing here may reach real OS key storage.
        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());
        services.AddArcanumCliClientStack();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task File_encryption_interfaces_share_one_disposed_singleton_and_one_runtime_status()
    {
        ServiceCollection services = [];
        services.AddDataProtection();
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddArcanumSecretStore();
        services.RemoveAll<ISecretStore>();
        services.AddSingleton<ISecretStore>(
            new MissingFileEncryptionSecretStore());
        services.RemoveAll<IEncryptedBlobPresenceInspector>();
        services.AddSingleton<IEncryptedBlobPresenceInspector>(
            new FixedPresenceInspector(EncryptedBlobPresence.Absent));

        ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        FileEncryptionKeyProvider concrete =
            provider.GetRequiredService<FileEncryptionKeyProvider>();
        IFileEncryptionKeyProvider keyProvider =
            provider.GetRequiredService<IFileEncryptionKeyProvider>();
        IFileEncryptionKeyStartupValidator validator =
            provider.GetRequiredService<IFileEncryptionKeyStartupValidator>();
        IFileEncryptionKeyRing keyRing =
            provider.GetRequiredService<IFileEncryptionKeyRing>();
        FileEncryptionRuntimeStatus concreteStatus =
            provider.GetRequiredService<FileEncryptionRuntimeStatus>();
        IFileEncryptionRuntimeStatus publicStatus =
            provider.GetRequiredService<IFileEncryptionRuntimeStatus>();

        Assert.Same(concrete, keyProvider);
        Assert.Same(concrete, validator);
        Assert.Same(concrete, keyRing);
        Assert.Same(concreteStatus, publicStatus);

        FileEncryptionKeyMaterial material = await keyProvider.GetForWriteAsync();
        ReadOnlyMemory<byte> retainedView = material.MasterKey;

        await provider.DisposeAsync();

        Assert.All(retainedView.ToArray(), static value => Assert.Equal(0, value));

        concrete.Dispose();
    }

    private sealed class FixedPresenceInspector(EncryptedBlobPresence presence) :
        IEncryptedBlobPresenceInspector
    {
        public EncryptedBlobPresence Inspect(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return presence;
        }
    }

    private sealed class MissingFileEncryptionSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;
    }
}
