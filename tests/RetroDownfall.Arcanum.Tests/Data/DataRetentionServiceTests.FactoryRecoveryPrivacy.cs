using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Factory_postcommit_directory_failure_preserves_closed_diagnostics_and_recovery(
        bool continuation,
        bool denyDirectory)
    {
        RequireSqlCipher();

        Skip.If(OperatingSystem.IsWindows(), "The native permission interleaving requires Unix directory permissions.");

        _ = await SeedSessionAsync(pinned: false);

        const string canary = "PRIVATE-NATIVE-DIRECTORY-CANARY";

        string privateDirectory = Directory.CreateDirectory(Path.Combine(_filesRoot, canary)).FullName;

        await File.WriteAllTextAsync(Path.Combine(_filesRoot, "factory-probe.bin"), "fake bytes only");

        MutationRecoveryLogger log = new();

        DataRetentionService service = CreateService(logger: log);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool permissionChanged = false;

        int committedObservations = 0;

        LongRunningOperation operation;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (path.Contains(".arcanum-cleanup-", StringComparison.Ordinal)
                    && File.Exists(path)
                    && SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) != 0)
                {
                    committedObservations++;

                    if (denyDirectory && !permissionChanged && !OperatingSystem.IsWindows())
                    {
                        permissionChanged = true;

                        File.SetUnixFileMode(privateDirectory, UnixFileMode.None);
                    }
                }

                return ReadActualNoFollowMetadata(path);
            };

            if (continuation)
            {
                LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

                DateTimeOffset now = DateTimeOffset.UtcNow;

                LongRunningOperation created = await store.CreateAsync(new(
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Factory continuation privacy control.", now));

                LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
                    created.Id, "factory-privacy-owner", now, now.AddMinutes(5));

                Assert.True(lease.Acquired);

                MethodInfo method = typeof(DataRetentionService).GetMethod("ContinueFactoryResetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

                Result<DataRetentionApplyResult> result = await Assert.IsAssignableFrom<Task<Result<DataRetentionApplyResult>>>(
                    method.Invoke(service, [created.Id, lease.Operation.LeaseOwner!, CancellationToken.None]));

                Assert.Equal(!denyDirectory, result.IsSuccess);

                if (denyDirectory) Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

                operation = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id));

                Assert.Equal(lease.Operation.LeaseOwner, operation.LeaseOwner);

                Assert.Equal(lease.Operation.Revision, operation.Revision);

                Assert.Equal(LongRunningOperationState.Running, operation.State);
            }
            else
            {
                var recovered = await ReconcileFactoryResetV0Async(service, "factory-private-directory-recovery");

                operation = recovered.Operation;

                Assert.Equal(denyDirectory ? 0 : 1, recovered.Summary.Completed);

                Assert.Equal(denyDirectory ? 1 : 0, recovered.Summary.RequiresAttention);

                Assert.Equal(0, recovered.Summary.Failed);

                Assert.Equal(denyDirectory ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Completed, operation.State);

                if (denyDirectory) Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, operation.TerminalErrorCode);

                Assert.Null(operation.LeaseOwner);
            }
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;

            if (Directory.Exists(privateDirectory) && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.True(committedObservations > 0);

        Assert.Equal(denyDirectory, permissionChanged);

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.False(File.Exists(Path.Combine(_filesRoot, "factory-probe.bin")));

        Assert.Equal(0, operation.CheckpointVersion);

        Assert.Null(operation.CheckpointPayload);

        if (!denyDirectory)
        {
            Assert.Empty(log.Entries);

            return;
        }

        MutationRecoveryLog warning = Assert.Single(log.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Null(warning.Exception);

        Assert.Equal(operation.Id, warning.Values["OperationId"]);

        Assert.Equal(continuation ? "FactoryContinuation" : "FactoryRecovery", warning.Values["Stage"]);

        Assert.Equal("UnauthorizedAccess", warning.Values["Failure"]?.ToString());

        Assert.Null(warning.Values["SqliteCode"]);

        Assert.Null(warning.Values["SqliteExtendedCode"]);

        Assert.Equal(new[] { "Failure", "OperationId", "SqliteCode", "SqliteExtendedCode", "Stage", "{OriginalFormat}" },
            warning.Values.Keys.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Values.Values, value => Assert.DoesNotContain(canary, value?.ToString() ?? "", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Factory_postcommit_diagnostics_keep_native_codes_without_native_error_text()
    {
        RequireSqlCipher();

        const string canary = "PRIVATE_FACTORY_SQL_CANARY";

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT " + canary;

        SqliteException native = await Assert.ThrowsAsync<SqliteException>(async () => await command.ExecuteScalarAsync());

        Assert.Contains(canary, native.Message, StringComparison.Ordinal);

        MutationRecoveryLogger log = new();

        DataRetentionService service = CreateService(logger: log);

        Type wrapper = typeof(DataRetentionService).GetNestedType("RetentionQuarantineRecoveryRequiredException", BindingFlags.NonPublic)!;

        object failure = Activator.CreateInstance(wrapper, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            binder: null, args: ["Fixed factory cleanup wrapper.", native], culture: null)!;

        Guid operationId = Guid.NewGuid();

        typeof(DataRetentionService).GetMethod("LogFactoryPostCommitRecoveryFailure", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [operationId, failure, "FactoryRecovery"]);

        MutationRecoveryLog warning = Assert.Single(log.Entries);

        Assert.Null(warning.Exception);

        Assert.Equal(operationId, warning.Values["OperationId"]);

        Assert.Equal("Sqlite", warning.Values["Failure"]?.ToString());

        Assert.Equal(native.SqliteErrorCode, warning.Values["SqliteCode"]);

        Assert.Equal(native.SqliteExtendedErrorCode, warning.Values["SqliteExtendedCode"]);

        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Values.Values, value => Assert.DoesNotContain(canary, value?.ToString() ?? "", StringComparison.Ordinal));
    }
}
