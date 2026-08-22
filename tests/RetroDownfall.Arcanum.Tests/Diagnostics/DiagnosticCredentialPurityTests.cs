using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

[Collection("ProcessEnvironment")]
public sealed class DiagnosticCredentialPurityTests : IDisposable
{

    private readonly string _testHome = Path.Combine(
        Path.GetTempPath(),
        "arcanum-tests",
        "diagnostic-purity-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DiagnosticCredentialPurityTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    public void Dispose()
    {

        foreach ((string name, string? value) in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    [Fact]
    public async Task File_encryption_diagnostics_use_the_non_mutating_secret_read()
    {

        MigrationSensitiveSecretStore secrets = new();

        EncryptedBlobDiagnostics diagnostics = new(secrets, new UnexpectedBlobStore());

        FileEncryptionDiagnostics result = await diagnostics.InspectAsync();

        Assert.Equal(FileEncryptionSecretStatus.Available, result.SecretStatus);

        Assert.Equal(0, secrets.FileCredentialMutationCount);

        Assert.Equal(1, secrets.FileCredentialPeekCount);

    }

    [Fact]
    public async Task Grimoire_probe_derives_legacy_passphrases_from_a_non_mutating_master_key_read()
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllBytesAsync(ArcanumPaths.GrimoireDatabaseFile, []);

        MigrationSensitiveSecretStore secrets = new();

        await using GrimoireProbe.OpenResult result = await GrimoireProbe.OpenReadOnlyAsync(
            secrets,
            CancellationToken.None);

        Assert.NotEqual(GrimoireProbe.OpenState.NoDatabase, result.State);

        Assert.Equal(0, secrets.MasterCredentialMutationCount);

        Assert.Equal(1, secrets.MasterCredentialPeekCount);

    }

    private void SetEnvironment(string name, string? value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed class MigrationSensitiveSecretStore : ISecretStore
    {

        public int FileCredentialMutationCount { get; private set; }

        public int FileCredentialPeekCount { get; private set; }

        public int MasterCredentialMutationCount { get; private set; }

        public int MasterCredentialPeekCount { get; private set; }

        public Task<string?> GetApiKeyAsync()
        {

            MasterCredentialMutationCount++;

            return Task.FromResult<string?>("legacy-master-key");

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync()
        {

            MasterCredentialMutationCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("legacy-master-key"));

        }

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            MasterCredentialPeekCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("legacy-master-key"));

        }

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>("grimoire-secret");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
        {

            FileCredentialMutationCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("file-encryption-secret"));

        }

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
        {

            FileCredentialPeekCount++;

            return Task.FromResult(SecretStoreReadResult.Ok("file-encryption-secret"));

        }

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class UnexpectedBlobStore : IEncryptedBlobStore
    {

        public Task<EncryptedBlobDescriptor> WriteAsync(
            string destinationPath,
            Stream plaintext,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            long? plaintextLength = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No diagnostic blob candidate was expected.");

        public Task<Stream> OpenReadAsync(
            string path,
            EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No diagnostic blob candidate was expected.");

        public Task<EncryptedBlobWriter> CreateWriterAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No diagnostic blob candidate was expected.");

        public Task<EncryptedBlobDescriptor> InspectAsync(
            string path,
            EncryptedBlobPurpose purpose,
            bool verifyAllChunks,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No diagnostic blob candidate was expected.");

        public bool HasEnvelope(string path) =>
            throw new InvalidOperationException("No diagnostic blob candidate was expected.");

    }

}
