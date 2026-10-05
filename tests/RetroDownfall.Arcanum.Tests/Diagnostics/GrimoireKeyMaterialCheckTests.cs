using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

/// <summary>
/// <c>grimoire.key_material</c> must read the committed KDF sidecar rather than only noting that a file
/// with that name exists: a torn or unsupported sidecar is a database that cannot be opened.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class GrimoireKeyMaterialCheckTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-key-material-check");

    public void Dispose()
    {
        _home.Dispose();
    }

    [Fact]
    public async Task A_valid_sidecar_is_healthy_and_current()
    {
        string database = CreateDatabaseFile();

        GrimoireKdfSidecarFile.Write(
            database,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        DoctorFinding finding = await InspectAsync();

        Assert.Equal(DoctorOutcome.Healthy, finding.Outcome);

        Assert.Contains("sidecar is current", finding.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"v\":2,\"salt\":\"!!!not-base64!!!\"}")]
    [InlineData("{\"v\":2,\"salt\":\"AAAA\"}")]
    [InlineData("{\"v\":99,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}")]
    public async Task A_malformed_sidecar_is_unhealthy_with_a_sidecar_remedy(string sidecarContent)
    {
        string database = CreateDatabaseFile();

        await File.WriteAllTextAsync(GrimoireKdfSidecarFile.GetSidecarPath(database), sidecarContent);

        DoctorFinding finding = await InspectAsync();

        Assert.Equal(DoctorOutcome.Unhealthy, finding.Outcome);

        Assert.Contains("sidecar", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("current", finding.Detail, StringComparison.OrdinalIgnoreCase);

        DoctorRemedy remedy = Assert.Single(finding.Remedies!);

        Assert.Equal("grimoire.restore_kdf_sidecar", remedy.Id);

        Assert.Equal(DoctorRemedyCommands.BackupRestore, remedy.Command);
    }

    [Fact]
    public async Task A_stranded_pending_upgrade_beside_a_valid_sidecar_stays_degraded()
    {
        string database = CreateDatabaseFile();

        GrimoireKdfSidecarFile.Write(
            database,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        GrimoireKdfSidecarFile.WritePending(
            database,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        DoctorFinding finding = await InspectAsync();

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);
    }

    private static string CreateDatabaseFile()
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllBytes(ArcanumPaths.GrimoireDatabaseFile, []);

        return ArcanumPaths.GrimoireDatabaseFile;
    }

    private static Task<DoctorFinding> InspectAsync() =>
        new GrimoireKeyMaterialCheck(new SecretBearingStore()).InspectAsync(CancellationToken.None);

    private sealed class SecretBearingStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("The key-material check must not read the master key.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("The key-material check must not read the master key.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("The key-material check must not persist anything.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>("test-secret");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            throw new InvalidOperationException("The key-material check must not persist anything.");
    }
}
