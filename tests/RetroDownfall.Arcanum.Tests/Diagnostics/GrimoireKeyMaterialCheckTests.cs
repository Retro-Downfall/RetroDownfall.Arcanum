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

    /// <summary>
    /// A sidecar the check cannot open for permission reasons has unknown content, not known-bad content:
    /// the finding is <c>Unavailable</c> and names no restore, because a restore would replace a file
    /// whose only fault is who may read it.
    /// </summary>
    [SkippableFact]
    public async Task An_unreadable_sidecar_is_unavailable_and_offers_no_restore()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permission bits are what make the sidecar unreadable here.");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string database = CreateDatabaseFile();

        string sidecarPath = GrimoireKdfSidecarFile.GetSidecarPath(database);

        GrimoireKdfSidecarFile.Write(
            database,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        File.SetUnixFileMode(sidecarPath, UnixFileMode.None);

        try
        {
            Skip.If(CanOpenForRead(sidecarPath), "A superuser reads a mode 000 file, so there is no refusal to observe.");

            DoctorFinding finding = await InspectAsync();

            Assert.Equal(DoctorOutcome.Unavailable, finding.Outcome);

            Assert.DoesNotContain("damaged", finding.Detail, StringComparison.OrdinalIgnoreCase);

            Assert.True(finding.Remedies is null or { Count: 0 }, "A permission failure must not point at a restore.");
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
