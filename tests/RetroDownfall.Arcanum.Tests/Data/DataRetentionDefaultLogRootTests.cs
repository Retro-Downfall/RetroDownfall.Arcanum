using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]

[Trait("Category", "Integration")]

public sealed class DataRetentionDefaultLogRootTests(GrimoireFixture fixture)
{

    [SkippableFact]

    public async Task Default_code_owned_audit_roots_are_inventoried_and_pruned()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string? originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        string? originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-retention-default-log-root-" + Guid.NewGuid().ToString("N"));

        string databasePath = string.Empty;

        ArcanumDbContext? db = null;

        try
        {

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                testHome);

            databasePath = fixture.CopyDatabase();

            db = fixture.CreateContext(databasePath);

            HostAuditLogSettings auditSettings = new();

            GuardrailsAuditLogSettings guardrailSettings = new();

            string auditRoot = Path.GetFullPath(
                Path.GetDirectoryName(auditSettings.FilePath)!);

            string guardrailRoot = Path.GetFullPath(
                Path.GetDirectoryName(guardrailSettings.FilePath)!);

            Assert.Equal(
                Path.GetFullPath(ArcanumPaths.GrimoireDirectory),
                auditRoot);

            Assert.Equal(auditRoot, guardrailRoot);

            Directory.CreateDirectory(auditRoot);

            string auditPath = ResolveDatedLogPath(
                auditSettings.FilePath,
                "20000101");

            string guardrailPath = ResolveDatedLogPath(
                guardrailSettings.FilePath,
                "20000101");

            await File.WriteAllTextAsync(auditPath, "{}\n");

            await File.WriteAllTextAsync(guardrailPath, "{}\n");

            File.SetLastWriteTimeUtc(auditPath, DateTime.UnixEpoch);

            File.SetLastWriteTimeUtc(guardrailPath, DateTime.UnixEpoch);

            string attachmentsRoot = Path.Combine(testHome, "owned", "attachments");

            string filesRoot = Path.Combine(testHome, "owned", "files");

            Directory.CreateDirectory(attachmentsRoot);

            Directory.CreateDirectory(filesRoot);

            FakeTimeProvider timeProvider = new();

            timeProvider.SetUtcNow(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            DataRetentionService service = new(
                db,
                new TestOptionsMonitor<ArcanumSettings>(CreateSettings()),
                new LongRunningOperationStore(
                    db,
                    TestOrdinaryConnectionFactory.For(db)),
                timeProvider,
                NullLogger<DataRetentionService>.Instance,
                FixtureLabeledArtifactGuard.For(db),
                attachmentsRoot,
                filesRoot,
                logsRootOverride: null);

            DataRetentionStatus status = await service.GetStatusAsync(
                CancellationToken.None);

            Assert.Equal(
                1,
                Assert.Single(
                    status.Items,
                    static item => item.DataClass == RetentionDataClass.AuditLogs).Files);

            Assert.Equal(
                1,
                Assert.Single(
                    status.Items,
                    static item => item.DataClass == RetentionDataClass.GuardrailLogs).Files);

            DataRetentionRequest request = new(DataRetentionOperation.Prune);

            DataRetentionPlan plan = await service.PlanAsync(
                request,
                CancellationToken.None);

            Assert.Equal(
                1,
                Assert.Single(
                    plan.Items,
                    static item => item.DataClass == RetentionDataClass.AuditLogs).Files);

            Assert.Equal(
                1,
                Assert.Single(
                    plan.Items,
                    static item => item.DataClass == RetentionDataClass.GuardrailLogs).Files);

            Result<DataRetentionApplyResult> result = await service.ApplyAsync(
                new DataRetentionApplyRequest(request, plan.PlanId),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.False(File.Exists(auditPath));

            Assert.False(File.Exists(guardrailPath));

        }
        finally
        {

            if (db is not null)
            {

                SqliteConnection connection =
                    (SqliteConnection)db.Database.GetDbConnection();

                await db.DisposeAsync();

                SqliteConnection.ClearPool(connection);

            }

            if (File.Exists(databasePath))
            {

                File.Delete(databasePath);

            }

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                originalDotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                originalAspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                originalTestHome);

        }

    }

    private static string ResolveDatedLogPath(
        string configuredPath,
        string dateStamp) =>
        Path.Combine(
            Path.GetDirectoryName(configuredPath)!,
            Path.GetFileNameWithoutExtension(configuredPath)
            + "-"
            + dateStamp
            + ".jsonl");

    private static ArcanumSettings CreateSettings() =>
        new()
        {

            Retention = new RetentionSettings
            {

                UploadedFiles = DisabledRule(),

                CompletedBatches = DisabledRule(),

                WorkspaceIndexes = DisabledRule(),

                SessionEntryEmbeddings = DisabledRule(),

                AuditLogs = EnabledRule(),

                GuardrailLogs = EnabledRule(),

                IdempotencyClaims = DisabledRule(),

                Accounting = DisabledRule(),

                LongRunningOperations = DisabledRule(),

                SanctumBreaches = DisabledRule(),

                DaemonHistory = DisabledRule(),

            },

        };

    private static RetentionRuleSettings EnabledRule() =>
        new()
        {

            Enabled = true,

            Days = 1,

        };

    private static RetentionRuleSettings DisabledRule() =>
        new()
        {

            Enabled = false,

            Days = 30,

        };

}
