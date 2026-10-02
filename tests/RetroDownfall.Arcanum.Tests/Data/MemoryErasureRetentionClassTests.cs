using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Tests.Data.Schema;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Erasure evidence is its own retention class: inventoried by status, never given a rule, and never
/// aged out.
/// </summary>
/// <remarks>
/// Fingerprints, receipts and receipt subjects are what make an erasure checkable and stop an erased
/// item coming back. A rule that could age them out would quietly lift every erasure on a schedule, so
/// the class resolves to no rule whatever the settings hold, and a catalog that cannot hold the evidence
/// yet reports it as empty rather than failing the whole status.
/// </remarks>
public sealed class MemoryErasureRetentionClassTests
{
    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void The_erasure_evidence_class_has_no_rule_whatever_the_settings_say()
    {
        RetentionSettings everyRuleConfigured = new()
        {
            ActiveSessions = Rule(),

            ArchivedSessions = Rule(),

            Entries = Rule(),

            Attachments = Rule(),

            UploadedFiles = Rule(),

            CompletedBatches = Rule(),

            SagaMemories = Rule(),

            LexiconEntries = Rule(),

            WorkspaceIndexes = Rule(),

            SessionEntryEmbeddings = Rule(),

            AuditLogs = Rule(),

            GuardrailLogs = Rule(),

            IdempotencyClaims = Rule(),

            Accounting = Rule(),

            LongRunningOperations = Rule(),

            SanctumBreaches = Rule(),

            DaemonHistory = Rule(),
        };

        Assert.Null(
            DataRetentionSettingsCatalog.ResolveRule(
                everyRuleConfigured,
                RetentionDataClass.MemoryErasureEvidence));
    }

    [Fact]
    public void Erasure_evidence_parses_from_its_name_and_never_from_a_numeric_code()
    {
        Assert.True(DataRetentionDataClassParser.TryParse("memory-erasure-evidence", out RetentionDataClass parsed));

        Assert.Equal(RetentionDataClass.MemoryErasureEvidence, parsed);

        Assert.True(DataRetentionDataClassParser.TryParse("MemoryErasureEvidence", out parsed));

        Assert.Equal(RetentionDataClass.MemoryErasureEvidence, parsed);

        Assert.False(DataRetentionDataClassParser.TryParse("30", out _));
    }

    /// <summary>
    /// A catalog below Core 13 has no evidence tables, and status reports the class with zero rows, no
    /// policy and an empty inventory rather than failing.
    /// </summary>
    [SkippableFact]
    public async Task Status_below_core_v13_reports_empty_erasure_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(Token))
        {
            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionTwelveFixture.ChainSet(),
                1536,
                Token);

            Assert.Equal(12, installed.Core.SchemaVersion);
        }

        string root = Path.Combine(Path.GetTempPath(), "arcanum-erasure-retention-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

            DataRetentionService service = new(
                db,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db)),
                TimeProvider.System,
                NullLogger<DataRetentionService>.Instance,
                FixtureLabeledArtifactGuard.For(db),
                Directory.CreateDirectory(Path.Combine(root, "attachments")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "files")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "logs")).FullName);

            DataRetentionStatus status = await service.GetStatusAsync(Token);

            DataRetentionStatusItem evidence = Assert.Single(
                status.Items,
                static item => item.DataClass == RetentionDataClass.MemoryErasureEvidence);

            Assert.Equal(0, evidence.Rows);

            Assert.False(evidence.PolicyEnabled);

            Assert.Null(evidence.RetentionDays);

            Assert.Equal(new DataRetentionMemoryErasureInventory(0, 0, 0), status.MemoryErasure);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RetentionRuleSettings Rule() =>
        new()
        {
            Enabled = true,

            Days = 1,
        };
}
