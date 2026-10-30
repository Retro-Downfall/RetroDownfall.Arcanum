using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

[Collection("ProcessEnvironment")]
public sealed class BackupSnapshotGuardCompositionTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("backup-snapshot-guards");

    public void Dispose() => _home.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composed_backup_normalizes_only_its_operation_without_granting_snapshot_write_authority(bool guardedCascade)
    {
        SyntheticSecrets secrets = new();

        string passphrase;

        await using (ServiceProvider seed = CreateServices(secrets))
        {
            IGrimoireDbPassphraseSource source = seed.GetRequiredService<IGrimoireDbPassphraseSource>();

            await GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
                secrets, source, seed.GetRequiredService<IServiceScopeFactory>(),
                ArcanumPaths.GrimoireDatabaseFile, ArcanumPaths.GrimoireDirectory, CancellationToken.None);

            passphrase = source.Passphrase;
        }

        await using (SqliteConnection seed = await OpenAsync(ArcanumPaths.GrimoireDatabaseFile, passphrase))
        {
            CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions(seed);

            await ExecuteAsync(seed, """
                INSERT INTO Sessions (Id, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO session_summary_artifacts
                    (ArtifactId, SessionId, Revision, ContentDigest, SensitivityCode, SensitivityDigest, CreatedAtUtc)
                VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 1,
                    zeroblob(32), 0, zeroblob(32), '2026-01-01T00:00:00.0000000Z');
                """);

            if (guardedCascade)
            {
                // The copied schema may demand a guarded write while normalizing the backup row.
                // Registering functions must not grant that write authority or disable its trigger.
                await ExecuteAsync(seed, """
                    CREATE TRIGGER backup_probe_guarded_cascade AFTER DELETE ON LongRunningOperations
                    BEGIN
                        DELETE FROM session_summary_artifacts;
                    END;
                    """);
            }
        }

        await using ServiceProvider provider = CreateServices(secrets);

        IGrimoireDbPassphraseSource current = provider.GetRequiredService<IGrimoireDbPassphraseSource>();

        GrimoireCliInitialization initialization = new(secrets, current,
            provider.GetRequiredService<IServiceScopeFactory>(), new ClearStartupProbe(),
            new ArcanumClientMutationBoundary(ArcanumPaths.GrimoireDirectory, new ClearClientMutationEvidenceProbe()));

        string archive = Path.Combine(_home.Root, "portable.arcbackup");

        BackupPlanRequest plan = new(BackupScope.Full, null, [], []);

        BackupPlan dryRun = await initialization.RunExclusiveAsync(
            (services, token) => services.GetRequiredService<IBackupService>().PlanAsync(plan, token), CancellationToken.None);

        Assert.Contains(dryRun.Components, item => item.Component == BackupComponent.GrimoireDatabase
            && item.Status == BackupComponentStatus.Complete);

        Task<BackupCreateResult> CreateAsync() => initialization.RunExclusiveAsync(
            (services, token) => services.GetRequiredService<IBackupService>().CreateAsync(
                new BackupCreateRequest(plan, archive, false), "synthetic-recovery-passphrase-12345".AsMemory(), token), CancellationToken.None);

        if (guardedCascade)
        {
            SqliteException refused = await Assert.ThrowsAsync<SqliteException>(CreateAsync);

            Assert.Equal(19, refused.SqliteErrorCode);

            Assert.Contains("session_summary_artifacts delete requires an authorized", refused.Message, StringComparison.Ordinal);

            Assert.False(File.Exists(archive));
        }
        else
        {
            BackupCreateResult created = await CreateAsync();

            Assert.True(created.Status == BackupCreateStatus.Complete, string.Join(", ", created.Issues.Select(issue => issue.Code)));

            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            BackupVerifyResult verified = await scope.ServiceProvider.GetRequiredService<IBackupService>()
                .VerifyAsync(archive, "synthetic-recovery-passphrase-12345".AsMemory(), CancellationToken.None);

            Assert.True(verified.IsValid, string.Join(", ", verified.Issues.Select(item => item.Code)));

            LongRunningOperation? operation = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                .GetAsync(created.OperationId, CancellationToken.None);

            Assert.NotNull(operation);

            Assert.Equal(LongRunningOperationState.Completed, operation.State);

            string extracted = Path.Combine(_home.Root, "extracted");

            string scratch = Path.Combine(_home.Root, "scratch");

            Directory.CreateDirectory(extracted);

            Directory.CreateDirectory(scratch);

            BackupArchiveExtraction unpacked = await provider.GetRequiredService<BackupArchiveCodec>().ExtractAsync(
                archive, "synthetic-recovery-passphrase-12345".AsMemory(), extracted, scratch, CancellationToken.None);

            Assert.Empty(unpacked.Issues);

            Assert.NotNull(unpacked.Manifest);

            BackupManifestEntry database = Assert.Single(unpacked.Manifest.Entries,
                entry => entry.Component == BackupComponent.GrimoireDatabase);

            await using SqliteConnection copy = await OpenAsync(Path.Combine(extracted, database.Path), passphrase);

            await using SqliteCommand normalized = copy.CreateCommand();

            normalized.CommandText = "SELECT count(*) FROM LongRunningOperations WHERE Id = $id;";

            normalized.Parameters.AddWithValue("$id", created.OperationId.ToString("N"));

            Assert.Equal(0L, await normalized.ExecuteScalarAsync());

            normalized.CommandText = "SELECT count(*) FROM session_summary_artifacts;";

            Assert.Equal(1L, await normalized.ExecuteScalarAsync());
        }

        await using SqliteConnection original = await OpenAsync(ArcanumPaths.GrimoireDatabaseFile, passphrase);

        await using SqliteCommand count = original.CreateCommand();

        count.CommandText = "SELECT count(*) FROM session_summary_artifacts;";

        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    private static ServiceProvider CreateServices(SyntheticSecrets secrets)
    {
        ServiceCollection services = new();

        services.AddSingleton<ISecretStore>(secrets);

        services.AddSingleton<IBackupSecretSnapshotReader>(secrets);

        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        services.AddSingleton(new BackupArchiveCodec(new BackupArchiveCodecOptions { KdfIterations = 10_000, ChunkSize = 64 * 1024 }));

        services.AddLogging();

        services.AddArcanumGrimoireForCli();

        services.AddArcanumBackup();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<SqliteConnection> OpenAsync(string path, string passphrase)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed class ClearClientMutationEvidenceProbe : IClientMutationEvidenceProbe
    {
        public Task<ClientMutationEvidenceResult> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ClientMutationEvidenceResult.Clear());
    }

    private sealed class ClearStartupProbe : IInstallationStartupProbe
    {
        public Result<bool> IsFreshInstallation() => Result<bool>.Success(false);

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ActiveInstallationReset?>.Success(null));
    }

    private sealed class SyntheticSecrets : ISecretStore, IBackupSecretSnapshotReader
    {
        private string? _secret;

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("synthetic-master-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok("synthetic-master-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(_secret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {
            _secret = encryptionSecret;

            return Task.CompletedTask;
        }

        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() => Task.FromResult(SecretStoreReadResult.Ok(_secret!));

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Convert.ToBase64String(new byte[32])));

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() => GetApiKeyReadResultAsync();
    }
}
