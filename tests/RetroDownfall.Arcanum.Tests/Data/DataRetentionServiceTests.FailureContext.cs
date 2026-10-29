using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("kind", "InvalidRecoveryRequest")]
    [InlineData("policy", "InvalidRecoveryRequest")]
    [InlineData("version", "CheckpointVersionUnsupported")]
    public async Task Factory_recovery_refusal_identifies_fixed_guard_without_mutating_operation(string defect, string stage)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "factory guard diagnostic", DateTimeOffset.UtcNow));

        const string canary = "PRIVATE-INVALID-RECOVERY-KIND";

        LongRunningOperation input = defect switch
        {
            "kind" => operation with { Kind = canary },
            "policy" => operation with { RecoveryPolicy = LongRunningOperationRecoveryPolicy.AbandonSafely },
            _ => operation with { CheckpointVersion = 1 },
        };

        TestCapturingLogger<DataRetentionService> logger = new();

        LongRunningOperationRecoveryResult result = await CreateService(logger: logger).RecoverFactoryResetAsync(input, CancellationToken.None);

        Assert.Equal(defect == "version" ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Failed, result.State);

        Assert.Equal(defect == "version" ? ErrorCodes.Covenant.ManualRecoveryRequired : LongRunningOperationErrorCodes.InvalidRecoveryResult, result.ErrorCode);

        Assert.Equal(operation, await store.GetAsync(operation.Id));

        TestLogEntry warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Null(warning.Exception);

        Assert.Contains(operation.Id.ToString("D"), warning.Message, StringComparison.Ordinal);

        Assert.Contains(stage, warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("target", "JournalTargetInvalid")]
    [InlineData("missing", "OriginalFileMissingOrChanged")]
    [InlineData("committed", "CommittedOriginalStillPresent")]
    [InlineData("restore", "QuarantineRestoreRefused")]
    [InlineData("inventory", "QuarantineNameInvalid")]
    public async Task Prune_pending_journal_failure_names_exact_closed_cause_without_private_file_data(string defect, string reason)
    {
        RequireSqlCipher();

        QuarantineRecoveryCandidate seeded = await SeedQuarantineRecoveryCandidateAsync("uploaded-file");

        TestCapturingLogger<DataRetentionService> logger = new();

        DataRetentionService service = CreateService(seeded.Settings, logger: logger);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal(seeded.CandidateId, Assert.Single(plan.CandidateIds));

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation operation = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Interrupted diagnostic prune", now));

        Assert.True((await store.TryAcquireLeaseAsync(operation.Id, "diagnostic-prune-owner", now, now.AddMinutes(5))).Acquired);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(seeded.OriginalPath,
            FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        byte[] journal = SerializeMutationCheckpoint("prune-candidate",
            defect == "target" ? "file:" + Guid.NewGuid().ToString("D") : seeded.CandidateId,
            Path.GetRelativePath(_filesRoot, seeded.OriginalPath), artifact.Metadata, "files");

        byte[] checkpoint = Encoding.UTF8.GetBytes("ARCADATA2\n" + plan.PlanId + "\n0\nG:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.GeneratedAt.ToString("o")))
            + "\nP:" + Convert.ToBase64String(journal)
            + "\nC:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(seeded.CandidateId))
            + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.GeneratedAt.AddDays(-30).ToString("o"))) + "\n");

        Assert.True(await store.SaveCheckpointAsync(operation.Id, "diagnostic-prune-owner", 0, 2, checkpoint,
            "retention-prune:" + operation.Id.ToString("N"), "Interrupted diagnostic prune", now));

        const string canary = "PRIVATE-PRUNE-DIAGNOSTIC-FILE";

        string? quarantineDirectory = null;

        if (defect == "missing") File.Delete(seeded.OriginalPath);

        if (defect == "committed") await ExecuteAsync("DELETE FROM UploadedFiles");

        if (defect == "restore")
        {
            Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact,
                $".arcanum-retention-{operation.Id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

            quarantineDirectory = quarantine.Directory.Path;

            await File.WriteAllTextAsync(seeded.OriginalPath, canary);
        }

        if (defect == "inventory")
        {
            quarantineDirectory = Path.Combine(_filesRoot, $".arcanum-retention-{operation.Id:N}-" + canary);

            SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(quarantineDirectory);
        }

        LongRunningOperation interrupted = (await store.GetAsync(operation.Id))!;

        LongRunningOperationRecoveryResult result = await service.RecoverPruneAsync(interrupted, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.ErrorCode);

        Assert.Equal(checkpoint, (await store.GetAsync(operation.Id))!.CheckpointPayload);

        Assert.Equal(defect != "committed", await seeded.Exists());

        if (quarantineDirectory is not null) Assert.True(Directory.Exists(quarantineDirectory));

        TestLogEntry warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Null(warning.Exception);

        Assert.Contains(operation.Id.ToString("D"), warning.Message, StringComparison.Ordinal);

        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(_filesRoot, warning.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Legacy_factory_active_conflict_reports_exact_operation_without_claiming_recovery()
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation active = await store.CreateAsync(new(LongRunningOperationKinds.MemoryEvaluation,
            LongRunningOperationRecoveryPolicy.AbandonSafely, "active independent work", now));

        Assert.True((await store.TryAcquireLeaseAsync(active.Id, "independent-owner", now, now.AddMinutes(5))).Acquired);

        TestCapturingLogger<DataRetentionService> logger = new();

        var recovered = await ReconcileFactoryResetV0Async(CreateService(logger: logger), "factory-conflict-diagnostic");

        Assert.Equal(1, recovered.Summary.Failed);

        Assert.Equal(ErrorCodes.Data.Conflict, recovered.Operation.TerminalErrorCode);

        Assert.Equal(LongRunningOperationState.Running, (await store.GetAsync(active.Id))!.State);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("LegacyPlanConflicts", StringComparison.Ordinal)
            && entry.Message.Contains(recovered.Operation.Id.ToString("D"), StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Automatic_retention_without_process_ownership_reports_an_unstarted_conflict()
    {
        RequireSqlCipher();

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.DataRetentionSweep, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        DataRetentionHostedSweepOutcome outcome = await ((IDataRetentionHostedSweep)CreateService()).ApplyOrResumeHostedPruneAsync(null, work, CancellationToken.None);

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

        Assert.True(outcome.Result!.IsFailure);

        Assert.Null(outcome.Result.Error.RetentionOperationId);

        Assert.Equal(0, await CountAllAsync("LongRunningOperations"));

        Assert.Equal(ErrorCodes.Data.Conflict, outcome.Result.Error.Code);
    }

    [SkippableFact]
    public async Task Automatic_retention_missing_durable_continuation_names_its_known_operation()
    {
        RequireSqlCipher();

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.DataRetentionSweep, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        LongRunningOperationOwnership ownership = new();

        Guid operation = Guid.NewGuid();

        Assert.True(ownership.TryClaim(operation, out Guid token));

        DataRetentionHostedSweepContinuation continuation = new(operation, "known-diagnostic-owner", token);

        DataRetentionHostedSweepOutcome outcome = await ((IDataRetentionHostedSweep)CreateService(operationOwnership: ownership))
            .ApplyOrResumeHostedPruneAsync(continuation, work, CancellationToken.None);

        Assert.True(outcome.Result!.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, outcome.Result.Error.Code);

        Assert.False(ownership.IsClaimedBy(operation, token));

        Assert.Contains(operation.ToString("D"), outcome.Result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(operation, outcome.Result.Error.RetentionOperationId);
    }

    [Fact]
    public void Retention_operation_correlation_is_not_wire_shape_or_empty_error()
    {
        Error metadataOnly = Error.None with { RetentionOperationId = Guid.NewGuid() };

        Assert.False(metadataOnly.IsNone);

        Assert.Equal(metadataOnly == Error.None, metadataOnly.IsNone);

        Assert.Equal(JsonSerializer.Serialize(Error.None, ArcanumJsonContext.Default.Error),
            JsonSerializer.Serialize(metadataOnly, ArcanumJsonContext.Default.Error));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retained_prune_candidate_names_its_operation_and_closed_candidate(bool operationMessage)
    {
        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        await SeedUploadedFileAsync(fileId, 0);

        await ExecuteAsync("""
            CREATE TRIGGER retain_file_for_diagnostic AFTER DELETE ON UploadedFiles
            BEGIN
                INSERT INTO UploadedFiles (Id, Filename, Bytes, Purpose, MimeType, CreatedAt)
                VALUES (OLD.Id, OLD.Filename, OLD.Bytes, OLD.Purpose, OLD.MimeType, OLD.CreatedAt);
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        TestCapturingLogger<DataRetentionService> logger = new();

        DataRetentionService service = CreateService(settings, logger: logger);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Single(plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(await store.ListAsync(new(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));

        if (operationMessage)
        {
            Assert.Contains(operation.Id.ToString("D"), result.Error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(fileId.ToString("D"), StringComparison.Ordinal)
                && entry.Message.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
                && entry.Message.Contains("CandidateRetained", StringComparison.Ordinal));
        }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_factory_without_owner_reports_Data_refusal_and_exact_durable_identity(bool code)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "legacy diagnostic fixture", DateTimeOffset.UtcNow));

        TestCapturingLogger<DataRetentionService> logger = new();

        LongRunningOperationRecoveryResult result = await CreateService(logger: logger).RecoverFactoryResetAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        if (code)
        {
            Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.ErrorCode);
        }
        else
        {
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(operation.Id.ToString("D"), StringComparison.Ordinal)
                && entry.Message.Contains("LegacyOwnerMissing", StringComparison.Ordinal));
        }

        Assert.Equal(operation, await store.GetAsync(operation.Id));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Factory_reconciliation_names_the_first_retained_table_and_preserves_recovery(bool retained)
    {
        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        if (retained)
        {
            await ExecuteAsync("""
                CREATE TRIGGER retain_session_for_diagnostic AFTER DELETE ON Sessions
                BEGIN
                    INSERT INTO Sessions (Id, Status, CreatedAt, UpdatedAt)
                    VALUES (OLD.Id, OLD.Status, OLD.CreatedAt, OLD.UpdatedAt);
                END;
                """);
        }

        TestCapturingLogger<DataRetentionService> logger = new();

        var recovered = await ReconcileFactoryResetV0Async(CreateService(logger: logger), "factory-diagnostic-recovery");

        Assert.Equal(retained ? 1 : 0, recovered.Summary.RequiresAttention);

        Assert.Equal(retained ? 0 : 1, recovered.Summary.Completed);

        Assert.Equal(retained ? 1 : 0, await CountAllAsync("Sessions"));

        if (retained)
        {
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(recovered.Operation.Id.ToString("D"), StringComparison.Ordinal)
                && entry.Properties!.Any(property => property.Key == "Target" && Equals(property.Value, "Sessions")));
        }
        else
        {
            Assert.Empty(logger.Entries);
        }
    }
}
