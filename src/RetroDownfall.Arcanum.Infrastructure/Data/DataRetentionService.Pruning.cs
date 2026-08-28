using System.Data.Common;

using System.Globalization;

using System.Runtime.CompilerServices;

using System.Text;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Daemons;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{

    private readonly ConditionalWeakTable<DataRetentionPlan, FrozenPruneAuthority>
        _frozenPruneAuthorities = new();

    private readonly AsyncLocal<DateTimeOffset?> _prunePlanningTimestamp = new();

    private DateTimeOffset PrunePlanningTimestamp =>
        _prunePlanningTimestamp.Value
        ?? timeProvider.GetUtcNow();

    private const string BatchCandidatePrefix = "batch:";

    private const string FileCandidatePrefix = "file:";

    private const string EntryCandidatePrefix = "entry:";

    private const string AttachmentCandidatePrefix = "attachment:";

    private const string EntryEmbeddingCandidatePrefix = "entry-embedding:";

    private const string WorkspaceCandidatePrefix = "workspace:";

    private const string SagaCandidatePrefix = "saga:";

    private const string LexiconCandidatePrefix = "lexicon:";

    private const string AuditLogCandidatePrefix = "audit-log:";

    private const string GuardrailLogCandidatePrefix = "guardrail-log:";

    private const string IdempotencyClaimCandidatePrefix = "idempotency-claim:";

    private const string IdempotencyKeyCandidatePrefix = "idempotency-key:";

    private const string AccountingCandidatePrefix = "accounting:";

    private const string AccountingAdjustmentCandidatePrefix = "accounting-adjustment:";

    private const string BudgetAlertCandidatePrefix = "budget-alert:";

    private const string OperationCandidatePrefix = "operation:";

    private const string SanctumCandidatePrefix = "sanctum:";

    private const string DaemonCandidatePrefix = "daemon:";

    private const int PruneCheckpointInterval = 50;

    private async Task AddBatchFileStatusAsync(
        List<DataRetentionStatusItem> items,
        RetentionDataClass dataClass,
        string column,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync("Batches", cancellationToken).ConfigureAwait(false)
            || !await TableExistsAsync("UploadedFiles", cancellationToken).ConfigureAwait(false))
        {

            AddStatus(
                items,
                dataClass,
                0,
                0,
                0,
                "Batches + UploadedFiles",
                "Batch file role references; bytes are owned by UploadedFiles.",
                retention);

            return;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT COUNT(*), COALESCE(SUM(file.Bytes), 0)
            FROM Batches batch
            JOIN UploadedFiles file
              ON lower(replace(file.Id, '-', '')) = lower(replace(batch.{column}, '-', ''))
            WHERE batch.{column} IS NOT NULL
            """;

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        long references = reader.GetInt64(0);

        long bytes = reader.GetInt64(1);

        AddStatus(
            items,
            dataClass,
            references,
            references,
            bytes,
            $"Batches.{column} + UploadedFiles",
            "Batch file role references; one uploaded file can appear in more than one role.",
            retention);

    }

    private async Task<DataRetentionConflict[]> ReadActiveInferenceConflictsAsync(
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync("InferenceRuns", cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        return await ReadConflictsAsync(
            "SELECT Id FROM InferenceRuns WHERE Status = @status",
            "Data.InferenceRunActive",
            "An active inference run protects its inputs and accounting chain.",
            cancellationToken,
            ("@status", (int)InferenceRunStatus.Running)).ConfigureAwait(false);

    }

    private async Task<DataRetentionConflict[]> ReadActiveIdempotencyConflictsAsync(
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync("IdempotencyClaims", cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        return await ReadConflictsAsync(
            """
            SELECT Id
            FROM IdempotencyClaims
            WHERE State IN (@claimed, @running)
              AND LeaseExpiresAt > @now
            """,
            "Data.IdempotencyLeaseActive",
            "An active idempotency lease protects its request and response state.",
            cancellationToken,
            ("@claimed", (int)IdempotencyClaimState.Claimed),
            ("@running", (int)IdempotencyClaimState.Running),
            ("@now", FormatTimestamp(PrunePlanningTimestamp))).ConfigureAwait(false);

    }

    private async Task<DataRetentionConflict[]> ReadInProgressBatchConflictsAsync(
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync("Batches", cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        return await ReadConflictsAsync(
            "SELECT Id FROM Batches WHERE Status NOT IN (@completed, @failed, @cancelled, @expired)",
            "Data.BatchInProgress",
            "An in-progress batch protects its input, output, and error files.",
            cancellationToken,
            ("@completed", BatchStatuses.Completed),
            ("@failed", BatchStatuses.Failed),
            ("@cancelled", BatchStatuses.Cancelled),
            ("@expired", BatchStatuses.Expired)).ConfigureAwait(false);

    }

    private async Task<DataRetentionConflict[]> ReadMemoryResetConflictsAsync(
        MemoryResetScope scope,
        CancellationToken cancellationToken)
    {

        List<DataRetentionConflict> conflicts =
            [.. await ReadActiveInferenceConflictsAsync(cancellationToken).ConfigureAwait(false)];

        string? operationKind = scope switch
        {

            MemoryResetScope.Attachments => LongRunningOperationKinds.AttachmentPromotion,

            MemoryResetScope.Workspace => LongRunningOperationKinds.WorkspaceIndex,

            _ => null,

        };

        if (operationKind is not null
            && await TableExistsAsync("LongRunningOperations", cancellationToken).ConfigureAwait(false))
        {

            conflicts.AddRange(
                await ReadConflictsAsync(
                    $"""
                    SELECT Id
                    FROM LongRunningOperations
                    WHERE Kind = @kind
                      AND State IN ({string.Join(",", ActiveOperationStates)})
                    """,
                    "Data.ActiveOperation",
                    "An active derived-data operation protects this memory scope.",
                    cancellationToken,
                    ("@kind", operationKind)).ConfigureAwait(false));

        }

        return [.. conflicts
            .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))];

    }

    private async Task<DataRetentionConflict[]> ReadConflictsAsync(
        string sql,
        string code,
        string message,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        List<DataRetentionConflict> conflicts = [];

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = reader.GetString(0);

            conflicts.Add(
                new DataRetentionConflict(
                    code,
                    Guid.TryParse(id, out Guid guid) ? guid.ToString("D") : id,
                    message));

        }

        return [.. conflicts];

    }

    private async Task<DataRetentionPlan> BuildUnifiedPrunePlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken)
    {

        DateTimeOffset? previous = _prunePlanningTimestamp.Value;

        DateTimeOffset generatedAt = timeProvider.GetUtcNow();

        _prunePlanningTimestamp.Value = generatedAt;

        try
        {

            return await BuildUnifiedPrunePlanCoreAsync(
                request,
                generatedAt,
                cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _prunePlanningTimestamp.Value = previous;

        }

    }

    private async Task<DataRetentionPlan> BuildUnifiedPrunePlanCoreAsync(
        DataRetentionRequest request,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {

        RetentionSettings retention = SnapshotRetention(CurrentRetention);

        const int limit = int.MaxValue;

        List<DataRetentionPlanItem> items = [];

        List<DataRetentionBlocker> blockers = [];

        List<DataRetentionConflict> conflicts = [];

        List<string> candidates = [];

        HashSet<Guid> selectedBatches = [];

        await AddCompletedBatchCandidatesAsync(
            retention,
            limit,
            items,
            blockers,
            conflicts,
            candidates,
            selectedBatches,
            cancellationToken).ConfigureAwait(false);

        await AddUploadedFileCandidatesAsync(
            retention,
            limit,
            selectedBatches,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddSessionPruneCandidatesAsync(
            request,
            retention,
            limit - candidates.Count,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        HashSet<Guid> selectedSessions = candidates
            .Where(static candidate => candidate.StartsWith("session:", StringComparison.Ordinal))
            .Select(static candidate => Guid.Parse(candidate[8..]))
            .ToHashSet();

        await AddEntryCandidatesAsync(
            retention,
            limit,
            selectedSessions,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddAttachmentCandidatesAsync(
            retention,
            limit,
            selectedSessions,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddEntryEmbeddingCandidatesAsync(
            retention,
            limit,
            selectedSessions,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddWorkspaceCandidatesAsync(
            retention,
            limit,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        DataRetentionSagaCurationInventory sagaCuration = await AddSagaCandidatesAsync(
            retention,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddLexiconCandidatesAsync(
            retention,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

        AddLogCandidates(
            retention,
            limit,
            items,
            candidates);

        await AddIdempotencyCandidatesAsync(
            retention,
            limit,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddAccountingCandidatesAsync(
            retention,
            limit,
            items,
            blockers,
            conflicts,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddOperationHistoryCandidatesAsync(
            retention,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddSanctumCandidatesAsync(
            retention,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

        await AddDaemonHistoryCandidatesAsync(
            retention,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

        DataRetentionPlan finalized = FinalizePlan(
            request,
            items,
            blockers,
            conflicts,
            candidates,
            requiresConfirmation: true,
            planAuthority: BuildPrunePolicyAuthority(retention),
            generatedAt: generatedAt);

        // Attached before the frozen-authority registration below, which is keyed by reference: a
        // `with` after it would register the authority against a plan instance nothing else holds, and
        // apply would fall back to recomputing cutoffs from whatever the policy says by then.
        DataRetentionPlan plan = finalized with
        {

            SagaCuration = sagaCuration,

        };

        IReadOnlyDictionary<string, DateTimeOffset> cutoffs =
            await BuildCandidateCutoffsAsync(
                plan.CandidateIds,
                plan.GeneratedAt,
                retention,
                cancellationToken).ConfigureAwait(false);

        _frozenPruneAuthorities.Add(
            plan,
            new FrozenPruneAuthority(cutoffs));

        return plan;

    }

    private static RetentionSettings SnapshotRetention(RetentionSettings retention) =>
        new()
        {

            AutomaticSweepsEnabled = retention.AutomaticSweepsEnabled,

            SweepIntervalHours = retention.SweepIntervalHours,

            AccountingMinimumDays = retention.AccountingMinimumDays,

            ActiveSessions = retention.ActiveSessions with { },

            ArchivedSessions = retention.ArchivedSessions with { },

            Entries = retention.Entries with { },

            Attachments = retention.Attachments with { },

            UploadedFiles = retention.UploadedFiles with { },

            CompletedBatches = retention.CompletedBatches with { },

            SagaMemories = retention.SagaMemories with { },

            LexiconEntries = retention.LexiconEntries with { },

            WorkspaceIndexes = retention.WorkspaceIndexes with { },

            SessionEntryEmbeddings = retention.SessionEntryEmbeddings with { },

            AuditLogs = retention.AuditLogs with { },

            GuardrailLogs = retention.GuardrailLogs with { },

            IdempotencyClaims = retention.IdempotencyClaims with { },

            Accounting = retention.Accounting with { },

            LongRunningOperations = retention.LongRunningOperations with { },

            SanctumBreaches = retention.SanctumBreaches with { },

            DaemonHistory = retention.DaemonHistory with { },

            ProtectedSessionIds = [.. retention.ProtectedSessionIds ?? []],

        };

    private static string BuildPrunePolicyAuthority(RetentionSettings retention)
    {

        StringBuilder authority = new();

        authority.Append("floor:")
            .Append(ArcanumSettingClamps.RetentionAccountingMinimumDays(
                retention.AccountingMinimumDays));

        AppendRuleAuthority(authority, "active", retention.ActiveSessions);

        AppendRuleAuthority(authority, "archived", retention.ArchivedSessions);

        AppendRuleAuthority(authority, "entries", retention.Entries);

        AppendRuleAuthority(authority, "attachments", retention.Attachments);

        AppendRuleAuthority(authority, "uploads", retention.UploadedFiles);

        AppendRuleAuthority(authority, "batches", retention.CompletedBatches);

        AppendRuleAuthority(authority, "saga", retention.SagaMemories);

        AppendRuleAuthority(authority, "lexicon", retention.LexiconEntries);

        AppendRuleAuthority(authority, "workspace", retention.WorkspaceIndexes);

        AppendRuleAuthority(authority, "entry-embeddings", retention.SessionEntryEmbeddings);

        AppendRuleAuthority(authority, "audit", retention.AuditLogs);

        AppendRuleAuthority(authority, "guardrail", retention.GuardrailLogs);

        AppendRuleAuthority(authority, "idempotency", retention.IdempotencyClaims);

        AppendRuleAuthority(authority, "accounting", retention.Accounting);

        AppendRuleAuthority(authority, "operations", retention.LongRunningOperations);

        AppendRuleAuthority(authority, "sanctum", retention.SanctumBreaches);

        AppendRuleAuthority(authority, "daemons", retention.DaemonHistory);

        foreach (Guid sessionId in (retention.ProtectedSessionIds ?? [])
                     .Distinct()
                     .Order())
        {

            authority.Append("|protected:").Append(sessionId.ToString("N"));

        }

        return authority.ToString();

    }

    private static void AppendRuleAuthority(
        StringBuilder authority,
        string name,
        RetentionRuleSettings rule)
    {

        authority.Append('|')
            .Append(name)
            .Append(':')
            .Append(rule.Enabled ? '1' : '0')
            .Append(':')
            .Append(ArcanumSettingClamps.RetentionRuleDays(rule.Days));

    }

    private async Task AddCompletedBatchCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        HashSet<Guid> selectedBatches,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.CompletedBatches;

        if (!rule.Enabled
            || candidates.Count >= limit
            || !await TableExistsAsync("Batches", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using (DbCommand blockedCommand = connection.CreateCommand())
        {

            blockedCommand.CommandText =
                """
                SELECT Id
                FROM Batches
                WHERE julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
                  AND Status NOT IN (@completed, @failed, @cancelled, @expired)
                ORDER BY COALESCE(CompletedAt, CreatedAt), Id
                LIMIT @limit
                """;

            Add(blockedCommand, "@cutoff", FormatTimestamp(cutoff));

            Add(blockedCommand, "@completed", BatchStatuses.Completed);

            Add(blockedCommand, "@failed", BatchStatuses.Failed);

            Add(blockedCommand, "@cancelled", BatchStatuses.Cancelled);

            Add(blockedCommand, "@expired", BatchStatuses.Expired);

            Add(blockedCommand, "@limit", limit - candidates.Count);

            await using DbDataReader blockedReader = await blockedCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await blockedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                Guid id = Guid.Parse(blockedReader.GetString(0));

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.CompletedBatches,
                        id.ToString("D"),
                        "Data.BatchInProgress",
                        "An in-progress batch cannot be pruned."));

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.BatchInProgress",
                        id.ToString("D"),
                        "An in-progress batch protects its input, output, and error files."));

            }

        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT Id, Status
            FROM Batches
            WHERE julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
              AND Status IN (@completed, @failed, @cancelled, @expired)
            ORDER BY COALESCE(CompletedAt, CreatedAt), Id
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@completed", BatchStatuses.Completed);

        Add(command, "@failed", BatchStatuses.Failed);

        Add(command, "@cancelled", BatchStatuses.Cancelled);

        Add(command, "@expired", BatchStatuses.Expired);

        Add(command, "@limit", limit - candidates.Count);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Guid id = Guid.Parse(reader.GetString(0));

            string status = reader.GetString(1);

            if (!BatchStatuses.IsTerminal(status))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.CompletedBatches,
                        id.ToString("D"),
                        "Data.BatchInProgress",
                        "An in-progress batch cannot be pruned."));

                conflicts.Add(
                    new DataRetentionConflict(
                        "Data.BatchInProgress",
                        id.ToString("D"),
                        "An in-progress batch protects its input, output, and error files."));

                continue;

            }

            selectedBatches.Add(id);

            candidates.Add(BatchCandidatePrefix + id.ToString("D"));

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.CompletedBatches,
                    1,
                    0,
                    0,
                    0));

        }

    }

    private async Task AddUploadedFileCandidatesAsync(
        RetentionSettings retention,
        int limit,
        HashSet<Guid> selectedBatches,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.UploadedFiles;

        if (!rule.Enabled)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        UploadedFileSnapshot[] blockedFiles = await ReadBlockedUploadedFilesBeforeAsync(
            cutoff,
            limit,
            selectedBatches,
            cancellationToken).ConfigureAwait(false);

        int remainingReferenceConflicts = limit;

        foreach (UploadedFileSnapshot file in blockedFiles)
        {

            BatchReferenceSnapshot[] references = await ReadBlockingBatchReferencesAsync(
                file.Id,
                selectedBatches,
                Math.Max(1, remainingReferenceConflicts),
                cancellationToken).ConfigureAwait(false);

            BatchReferenceSnapshot[] active =
                [.. references.Where(static reference =>
                    !BatchStatuses.IsTerminal(reference.Status))];

            if (active.Length > 0)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.UploadedFiles,
                        file.Id.ToString("D"),
                        "Data.BatchReference",
                        "An uploaded file referenced by an in-progress batch cannot be pruned."));

                foreach (BatchReferenceSnapshot reference in active.Take(remainingReferenceConflicts))
                {

                    conflicts.Add(
                        new DataRetentionConflict(
                            "Data.BatchInProgress",
                            reference.BatchId.ToString("D"),
                            "An in-progress batch protects every referenced file."));

                    remainingReferenceConflicts--;

                }

                continue;

            }

            if (references.Length > 0)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.UploadedFiles,
                        file.Id.ToString("D"),
                        "Data.BatchReference",
                        "A retained terminal batch still references this uploaded file."));

                continue;

            }

        }

        if (candidates.Count >= limit)
        {

            return;

        }

        UploadedFileSnapshot[] eligibleFiles = await ReadEligibleUploadedFilesBeforeAsync(
            cutoff,
            limit - candidates.Count,
            selectedBatches,
            cancellationToken).ConfigureAwait(false);

        foreach (UploadedFileSnapshot file in eligibleFiles)
        {

            candidates.Add(FileCandidatePrefix + file.Id.ToString("D"));

            (long physicalFiles, long physicalBytes) = MeasureOwnedFile(
                _filesRoot,
                file.Id.ToString("N"));

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.UploadedFiles,
                    1,
                    physicalFiles,
                    physicalBytes,
                    0));

        }

    }

    private async Task AddEntryProtectionDiagnosticsAsync(
        RetentionSettings retention,
        DateTimeOffset cutoff,
        int diagnosticLimit,
        HashSet<Guid> selectedSessions,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        Guid[] protectedSessionIds = [.. retention.ProtectedSessionIds.Distinct().Order()];

        Guid[] selectedSessionIds = [.. selectedSessions.Order()];

        string heldPredicate = protectedSessionIds.Length == 0
            ? "0"
            : $"lower(replace(entry.SessionId, '-', '')) IN ({string.Join(", ", protectedSessionIds.Select((_, index) => "@protectedDiagnostic" + index.ToString(CultureInfo.InvariantCulture)))})";

        string selectedClause = selectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(entry.SessionId, '-', '')) NOT IN ({string.Join(", ", selectedSessionIds.Select((_, index) => "@selectedDiagnostic" + index.ToString(CultureInfo.InvariantCulture)))})";

        const string contextPinPredicate =
            """
            EXISTS (
                SELECT 1
                FROM SessionContextPins pin
                WHERE pin.Kind = @entryDiagnosticKind
                  AND lower(replace(pin.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(entry.Id, '-', '')))
            """;

        string sessionConflictPredicate =
            $"""
            EXISTS (
                SELECT 1
                FROM LongRunningOperations operation
                WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
            OR EXISTS (
                SELECT 1
                FROM InferenceRuns run
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND run.Status = @entryDiagnosticRunning)
            OR EXISTS (
                SELECT 1
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND reservation.Status = @entryDiagnosticReserved)
            """;

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT entry.Id,
                   entry.SessionId,
                   entry.IsPinned,
                   CASE WHEN {contextPinPredicate} THEN 1 ELSE 0 END
            FROM Entries entry
            WHERE julianday(entry.CreatedAt) <= julianday(@entryDiagnosticCutoff)
              {selectedClause}
              AND (
                  entry.IsPinned <> 0
                  OR {heldPredicate}
                  OR {contextPinPredicate}
                  OR {sessionConflictPredicate})
            ORDER BY entry.CreatedAt, entry.Id
            LIMIT @entryDiagnosticLimit
            """;

        Add(command, "@entryDiagnosticCutoff", FormatTimestamp(cutoff));

        Add(command, "@entryDiagnosticKind", (int)SessionContextPinKind.SessionEntry);

        Add(command, "@entryDiagnosticRunning", (int)InferenceRunStatus.Running);

        Add(command, "@entryDiagnosticReserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@entryDiagnosticLimit", diagnosticLimit);

        AddGuidParameters(
            command,
            "@protectedDiagnostic",
            protectedSessionIds);

        AddGuidParameters(
            command,
            "@selectedDiagnostic",
            selectedSessionIds);

        List<(Guid Id, Guid SessionId, bool Pinned, bool ContextPinned)> protectedEntries = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                protectedEntries.Add(
                    (
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt64(2) != 0,
                        reader.GetInt64(3) != 0
                    ));

            }

        }

        int remainingConflictDiagnostics = diagnosticLimit;

        foreach ((Guid id, Guid sessionId, bool pinned, bool contextPinned) in protectedEntries)
        {

            if (pinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.PinnedEntry",
                        "A pinned entry cannot be pruned."));

            }
            else if (protectedSessionIds.Contains(sessionId))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.SessionHold",
                        "An operator-retained session protects its entries."));

            }
            else if (contextPinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.ContextPin",
                        "A pinned session-entry context protects this entry."));

            }

            remainingConflictDiagnostics = await AddBoundedSessionConflictDiagnosticsAsync(
                sessionId,
                remainingConflictDiagnostics,
                conflicts,
                cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task AddAttachmentProtectionDiagnosticsAsync(
        RetentionSettings retention,
        DateTimeOffset cutoff,
        int diagnosticLimit,
        HashSet<Guid> selectedSessions,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        Guid[] protectedSessionIds = [.. retention.ProtectedSessionIds.Distinct().Order()];

        Guid[] selectedSessionIds = [.. selectedSessions.Order()];

        string heldPredicate = protectedSessionIds.Length == 0
            ? "0"
            : $"lower(replace(attachment.SessionId, '-', '')) IN ({string.Join(", ", protectedSessionIds.Select((_, index) => "@protectedAttachmentDiagnostic" + index.ToString(CultureInfo.InvariantCulture)))})";

        string selectedClause = selectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(attachment.SessionId, '-', '')) NOT IN ({string.Join(", ", selectedSessionIds.Select((_, index) => "@selectedAttachmentDiagnostic" + index.ToString(CultureInfo.InvariantCulture)))})";

        const string contextPinPredicate =
            """
            EXISTS (
                SELECT 1
                FROM SessionContextPins pin
                WHERE pin.Kind = @attachmentDiagnosticKind
                  AND lower(replace(pin.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                  AND (
                      lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(attachment.Id, '-', ''))
                      OR pin.TargetIdentifier = attachment.LogicalKey))
            """;

        string sessionConflictPredicate =
            $"""
            EXISTS (
                SELECT 1
                FROM LongRunningOperations operation
                WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                  AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
            OR EXISTS (
                SELECT 1
                FROM InferenceRuns run
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                  AND run.Status = @attachmentDiagnosticRunning)
            OR EXISTS (
                SELECT 1
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                  AND reservation.Status = @attachmentDiagnosticReserved)
            """;

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT attachment.Id,
                   attachment.SessionId,
                   CASE WHEN {contextPinPredicate} THEN 1 ELSE 0 END
            FROM SessionAttachments attachment
            WHERE attachment.State = 'Bound'
              AND julianday(attachment.CreatedAt) <= julianday(@attachmentDiagnosticCutoff)
              {selectedClause}
              AND (
                  {heldPredicate}
                  OR {contextPinPredicate}
                  OR {sessionConflictPredicate})
            ORDER BY attachment.CreatedAt, attachment.Id
            LIMIT @attachmentDiagnosticLimit
            """;

        Add(command, "@attachmentDiagnosticCutoff", FormatTimestamp(cutoff));

        Add(command, "@attachmentDiagnosticKind", (int)SessionContextPinKind.Attachment);

        Add(command, "@attachmentDiagnosticRunning", (int)InferenceRunStatus.Running);

        Add(command, "@attachmentDiagnosticReserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@attachmentDiagnosticLimit", diagnosticLimit);

        AddGuidParameters(
            command,
            "@protectedAttachmentDiagnostic",
            protectedSessionIds);

        AddGuidParameters(
            command,
            "@selectedAttachmentDiagnostic",
            selectedSessionIds);

        List<(Guid Id, Guid SessionId, bool ContextPinned)> protectedAttachments = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                protectedAttachments.Add(
                    (
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt64(2) != 0
                    ));

            }

        }

        int remainingConflictDiagnostics = diagnosticLimit;

        foreach ((Guid id, Guid sessionId, bool contextPinned) in protectedAttachments)
        {

            if (protectedSessionIds.Contains(sessionId))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.AttachmentVersions,
                        id.ToString("D"),
                        "Data.SessionHold",
                        "An operator-retained session protects its attachments."));

            }
            else if (contextPinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.AttachmentVersions,
                        id.ToString("D"),
                        "Data.PinnedAttachment",
                        "A pinned attachment/context protects this attachment from deletion."));

            }

            remainingConflictDiagnostics = await AddBoundedSessionConflictDiagnosticsAsync(
                sessionId,
                remainingConflictDiagnostics,
                conflicts,
                cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task AddEntryEmbeddingProtectionDiagnosticsAsync(
        RetentionSettings retention,
        DateTimeOffset cutoff,
        int diagnosticLimit,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        Guid[] protectedSessionIds = [.. retention.ProtectedSessionIds.Distinct().Order()];

        string heldPredicate = protectedSessionIds.Length == 0
            ? "0"
            : $"lower(replace(entry.SessionId, '-', '')) IN ({string.Join(", ", protectedSessionIds.Select((_, index) => "@protectedEmbeddingDiagnostic" + index.ToString(CultureInfo.InvariantCulture)))})";

        const string contextPinPredicate =
            """
            EXISTS (
                SELECT 1
                FROM SessionContextPins pin
                WHERE pin.Kind = @embeddingDiagnosticKind
                  AND lower(replace(pin.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(entry.Id, '-', '')))
            """;

        string sessionConflictPredicate =
            $"""
            EXISTS (
                SELECT 1
                FROM LongRunningOperations operation
                WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
            OR EXISTS (
                SELECT 1
                FROM InferenceRuns run
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND run.Status = @embeddingDiagnosticRunning)
            OR EXISTS (
                SELECT 1
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                  AND reservation.Status = @embeddingDiagnosticReserved)
            """;

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT embedding.EntryId,
                   entry.SessionId,
                   entry.IsPinned,
                   CASE WHEN {contextPinPredicate} THEN 1 ELSE 0 END
            FROM entry_embeddings embedding
            JOIN Entries entry
              ON lower(replace(entry.Id, '-', '')) = lower(replace(embedding.EntryId, '-', ''))
            WHERE julianday(entry.CreatedAt) <= julianday(@embeddingDiagnosticCutoff)
              AND (
                  entry.IsPinned <> 0
                  OR {heldPredicate}
                  OR {contextPinPredicate}
                  OR {sessionConflictPredicate})
            ORDER BY entry.CreatedAt, embedding.EntryId
            LIMIT @embeddingDiagnosticLimit
            """;

        Add(command, "@embeddingDiagnosticCutoff", FormatTimestamp(cutoff));

        Add(command, "@embeddingDiagnosticKind", (int)SessionContextPinKind.SessionEntry);

        Add(command, "@embeddingDiagnosticRunning", (int)InferenceRunStatus.Running);

        Add(command, "@embeddingDiagnosticReserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@embeddingDiagnosticLimit", diagnosticLimit);

        AddGuidParameters(
            command,
            "@protectedEmbeddingDiagnostic",
            protectedSessionIds);

        List<(Guid EntryId, Guid SessionId, bool Pinned, bool ContextPinned)> protectedEmbeddings = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                protectedEmbeddings.Add(
                    (
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt64(2) != 0,
                        reader.GetInt64(3) != 0
                    ));

            }

        }

        int remainingConflictDiagnostics = diagnosticLimit;

        foreach ((Guid entryId, Guid sessionId, bool pinned, bool contextPinned) in protectedEmbeddings)
        {

            if (pinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.SessionEntryEmbeddings,
                        entryId.ToString("D"),
                        "Data.PinnedEntry",
                        "A pinned entry protects its derived embeddings."));

            }
            else if (protectedSessionIds.Contains(sessionId))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.SessionEntryEmbeddings,
                        entryId.ToString("D"),
                        "Data.SessionHold",
                        "An operator-retained session protects its derived entry embeddings."));

            }
            else if (contextPinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.SessionEntryEmbeddings,
                        entryId.ToString("D"),
                        "Data.ContextPin",
                        "A pinned session-entry context protects its derived embeddings."));

            }

            remainingConflictDiagnostics = await AddBoundedSessionConflictDiagnosticsAsync(
                sessionId,
                remainingConflictDiagnostics,
                conflicts,
                cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task<int> AddBoundedSessionConflictDiagnosticsAsync(
        Guid sessionId,
        int remaining,
        List<DataRetentionConflict> conflicts,
        CancellationToken cancellationToken)
    {

        if (remaining <= 0)
        {

            return 0;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT Code, ResourceId
            FROM (
                SELECT 0 AS ConflictOrder,
                       'Data.ActiveOperation' AS Code,
                       Id AS ResourceId
                FROM LongRunningOperations
                WHERE lower(replace(SessionId, '-', '')) = @diagnosticSessionId
                  AND State IN ({string.Join(",", ActiveOperationStates)})
                UNION ALL
                SELECT 1 AS ConflictOrder,
                       'Data.InferenceRunActive' AS Code,
                       Id AS ResourceId
                FROM InferenceRuns
                WHERE lower(replace(SessionId, '-', '')) = @diagnosticSessionId
                  AND Status = @diagnosticRunning
                UNION ALL
                SELECT 2 AS ConflictOrder,
                       'Data.BudgetReservationOutstanding' AS Code,
                       reservation.Id AS ResourceId
                FROM BudgetReservations reservation
                JOIN InferenceRuns run
                  ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                WHERE lower(replace(run.SessionId, '-', '')) = @diagnosticSessionId
                  AND reservation.Status = @diagnosticReserved)
            ORDER BY ConflictOrder, ResourceId
            LIMIT @diagnosticConflictLimit
            """;

        Add(command, "@diagnosticSessionId", sessionId.ToString("N"));

        Add(command, "@diagnosticRunning", (int)InferenceRunStatus.Running);

        Add(command, "@diagnosticReserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@diagnosticConflictLimit", remaining);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string code = reader.GetString(0);

            string message = code switch
            {

                "Data.ActiveOperation" =>
                    "An active durable operation protects this session.",

                "Data.InferenceRunActive" =>
                    "An active inference run protects this session.",

                _ =>
                    "An outstanding budget reservation protects the accounting chain.",

            };

            conflicts.Add(
                new DataRetentionConflict(
                    code,
                    Guid.Parse(reader.GetString(1)).ToString("D"),
                    message));

            remaining--;

        }

        return remaining;

    }

    private static void AddGuidParameters(
        DbCommand command,
        string prefix,
        Guid[] values)
    {

        for (int index = 0; index < values.Length; index++)
        {

            Add(
                command,
                prefix + index.ToString(CultureInfo.InvariantCulture),
                values[index].ToString("N"));

        }

    }

    private async Task AddEntryCandidatesAsync(
        RetentionSettings retention,
        int limit,
        HashSet<Guid> selectedSessions,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.Entries;

        if (!rule.Enabled)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        await AddEntryProtectionDiagnosticsAsync(
            retention,
            cutoff,
            limit,
            selectedSessions,
            blockers,
            conflicts,
            cancellationToken).ConfigureAwait(false);

        if (candidates.Count >= limit)
        {

            return;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        string protectedClause = retention.ProtectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(entry.SessionId, '-', '')) NOT IN ({string.Join(", ", retention.ProtectedSessionIds.Select((_, index) => "@protected" + index.ToString(CultureInfo.InvariantCulture)))})";

        Guid[] selectedSessionIds = [.. selectedSessions];

        string selectedClause = selectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(entry.SessionId, '-', '')) NOT IN ({string.Join(", ", selectedSessionIds.Select((_, index) => "@selected" + index.ToString(CultureInfo.InvariantCulture)))})";

        command.CommandText =
            $"""
            SELECT entry.Id, entry.SessionId, entry.IsPinned
            FROM Entries entry
            WHERE julianday(entry.CreatedAt) <= julianday(@cutoff)
              AND entry.IsPinned = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM SessionContextPins pin
                  WHERE pin.Kind = @kind
                    AND lower(replace(pin.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(entry.Id, '-', '')))
              AND NOT EXISTS (
                  SELECT 1
                  FROM LongRunningOperations operation
                  WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
              AND NOT EXISTS (
                  SELECT 1
                  FROM InferenceRuns run
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND run.Status = @running)
              AND NOT EXISTS (
                  SELECT 1
                  FROM BudgetReservations reservation
                  JOIN InferenceRuns run
                    ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND reservation.Status = @reserved)
              {protectedClause}
              {selectedClause}
            ORDER BY entry.CreatedAt, entry.Id
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@kind", (int)SessionContextPinKind.SessionEntry);

        Add(command, "@running", (int)InferenceRunStatus.Running);

        Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@limit", limit - candidates.Count);

        for (int index = 0; index < retention.ProtectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@protected" + index.ToString(CultureInfo.InvariantCulture),
                retention.ProtectedSessionIds[index].ToString("N"));

        }

        for (int index = 0; index < selectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@selected" + index.ToString(CultureInfo.InvariantCulture),
                selectedSessionIds[index].ToString("N"));

        }

        List<(Guid Id, Guid SessionId, bool Pinned)> rows = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add(
                    (
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0
                    ));

            }

        }

        foreach ((Guid id, Guid sessionId, bool pinned) in rows)
        {

            if (candidates.Count >= limit || selectedSessions.Contains(sessionId))
            {

                continue;

            }

            if (pinned)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.PinnedEntry",
                        "A pinned entry cannot be pruned."));

                continue;

            }

            if (retention.ProtectedSessionIds.Contains(sessionId))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.SessionHold",
                        "An operator-retained session protects its entries."));

                continue;

            }

            long contextPins = await CountTableAsync(
                "SessionContextPins",
                """
                Kind = @kind
                AND lower(replace(SessionId, '-', '')) = @sessionId
                AND lower(replace(TargetIdentifier, '-', '')) = @entryId
                """,
                cancellationToken,
                ("@kind", (int)SessionContextPinKind.SessionEntry),
                ("@sessionId", sessionId.ToString("N")),
                ("@entryId", id.ToString("N"))).ConfigureAwait(false);

            if (contextPins > 0)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.Entries,
                        id.ToString("D"),
                        "Data.ContextPin",
                        "A pinned session-entry context protects this entry."));

                continue;

            }

            DataRetentionConflict[] entryConflicts = await ReadSessionConflictsAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false);

            if (entryConflicts.Length > 0)
            {

                conflicts.AddRange(entryConflicts);

                continue;

            }

            long embeddings = await CountTableAsync(
                "entry_embeddings",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", id.ToString("N"))).ConfigureAwait(false);

            embeddings += await CountTableAsync(
                "entry_embeddings_vec",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", id.ToString("N"))).ConfigureAwait(false);

            long entryDerived = await CountTableAsync(
                "Entries_fts",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", id.ToString("N"))).ConfigureAwait(false);

            entryDerived += await CountTableAsync(
                "attachment_memory_consultations",
                "lower(replace(SourceEntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", id.ToString("N"))).ConfigureAwait(false);

            candidates.Add(EntryCandidatePrefix + id.ToString("D"));

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.Entries,
                    1,
                    0,
                    0,
                    entryDerived));

            if (embeddings > 0)
            {

                items.Add(
                    new DataRetentionPlanItem(
                        RetentionDataClass.SessionEntryEmbeddings,
                        0,
                        0,
                        0,
                        embeddings));

            }

        }

    }

    private async Task AddAttachmentCandidatesAsync(
        RetentionSettings retention,
        int limit,
        HashSet<Guid> selectedSessions,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.Attachments;

        if (!rule.Enabled
            || !await TableExistsAsync("SessionAttachments", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        await AddAttachmentProtectionDiagnosticsAsync(
            retention,
            cutoff,
            limit,
            selectedSessions,
            blockers,
            conflicts,
            cancellationToken).ConfigureAwait(false);

        if (candidates.Count >= limit)
        {

            return;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        string protectedClause = retention.ProtectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(attachment.SessionId, '-', '')) NOT IN ({string.Join(", ", retention.ProtectedSessionIds.Select((_, index) => "@protected" + index.ToString(CultureInfo.InvariantCulture)))})";

        string selectedClause = selectedSessions.Count == 0
            ? string.Empty
            : $"AND lower(replace(attachment.SessionId, '-', '')) NOT IN ({string.Join(", ", selectedSessions.Select((_, index) => "@selected" + index.ToString(CultureInfo.InvariantCulture)))})";

        command.CommandText =
            $"""
            SELECT attachment.Id, attachment.SessionId
            FROM SessionAttachments attachment
            WHERE attachment.State = 'Bound'
              AND julianday(attachment.CreatedAt) <= julianday(@cutoff)
              {protectedClause}
              {selectedClause}
              AND NOT EXISTS (
                  SELECT 1
                  FROM SessionContextPins pin
                  WHERE pin.Kind = @attachmentKind
                    AND lower(replace(pin.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                    AND (
                        lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(attachment.Id, '-', ''))
                        OR pin.TargetIdentifier = attachment.LogicalKey))
              AND NOT EXISTS (
                  SELECT 1
                  FROM LongRunningOperations operation
                  WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                    AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
              AND NOT EXISTS (
                  SELECT 1
                  FROM InferenceRuns run
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                    AND run.Status = @running)
              AND NOT EXISTS (
                  SELECT 1
                  FROM BudgetReservations reservation
                  JOIN InferenceRuns run
                    ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(attachment.SessionId, '-', ''))
                    AND reservation.Status = @reserved)
            ORDER BY attachment.CreatedAt, attachment.Id
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@limit", limit);

        Add(command, "@attachmentKind", (int)SessionContextPinKind.Attachment);

        Add(command, "@running", (int)InferenceRunStatus.Running);

        Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        for (int index = 0; index < retention.ProtectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@protected" + index.ToString(CultureInfo.InvariantCulture),
                retention.ProtectedSessionIds[index].ToString("N"));

        }

        int selectedIndex = 0;

        foreach (Guid selectedSession in selectedSessions)
        {

            Add(
                command,
                "@selected" + selectedIndex.ToString(CultureInfo.InvariantCulture),
                selectedSession.ToString("N"));

            selectedIndex++;

        }

        List<(Guid Id, Guid? SessionId)> rows = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add(
                    (
                        Guid.Parse(reader.GetString(0)),
                        reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))
                    ));

            }

        }

        foreach ((Guid id, Guid? sessionId) in rows)
        {

            if (candidates.Count >= limit
                || sessionId is Guid selectedSession && selectedSessions.Contains(selectedSession))
            {

                continue;

            }

            if (sessionId is Guid heldSession
                && retention.ProtectedSessionIds.Contains(heldSession))
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.AttachmentVersions,
                        id.ToString("D"),
                        "Data.SessionHold",
                        "An operator-retained session protects its attachments."));

                continue;

            }

            DataRetentionPlan plan = await BuildDeleteAttachmentPlanAsync(
                new DataRetentionRequest(DataRetentionOperation.DeleteAttachment, id),
                id,
                cancellationToken).ConfigureAwait(false);

            if (plan.Blockers.Length > 0 || plan.Conflicts.Length > 0)
            {

                blockers.AddRange(plan.Blockers);

                conflicts.AddRange(plan.Conflicts);

                continue;

            }

            candidates.Add(AttachmentCandidatePrefix + id.ToString("D"));

            items.AddRange(plan.Items);

        }

    }

    private async Task AddEntryEmbeddingCandidatesAsync(
        RetentionSettings retention,
        int limit,
        HashSet<Guid> selectedSessions,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.SessionEntryEmbeddings;

        if (!rule.Enabled
            || !await TableExistsAsync("entry_embeddings", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        await AddEntryEmbeddingProtectionDiagnosticsAsync(
            retention,
            cutoff,
            limit,
            blockers,
            conflicts,
            cancellationToken).ConfigureAwait(false);

        if (candidates.Count >= limit)
        {

            return;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        string protectedClause = retention.ProtectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(entry.SessionId, '-', '')) NOT IN ({string.Join(", ", retention.ProtectedSessionIds.Select((_, index) => "@protected" + index.ToString(CultureInfo.InvariantCulture)))})";

        Guid[] selectedSessionIds = [.. selectedSessions];

        string selectedClause = selectedSessionIds.Length == 0
            ? string.Empty
            : $"AND lower(replace(entry.SessionId, '-', '')) NOT IN ({string.Join(", ", selectedSessionIds.Select((_, index) => "@selected" + index.ToString(CultureInfo.InvariantCulture)))})";

        command.CommandText =
            $"""
            SELECT embedding.EntryId, entry.SessionId, entry.IsPinned
            FROM entry_embeddings embedding
            JOIN Entries entry
              ON lower(replace(entry.Id, '-', '')) = lower(replace(embedding.EntryId, '-', ''))
            WHERE julianday(entry.CreatedAt) <= julianday(@cutoff)
              AND entry.IsPinned = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM SessionContextPins pin
                  WHERE pin.Kind = @kind
                    AND lower(replace(pin.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(entry.Id, '-', '')))
              AND NOT EXISTS (
                  SELECT 1
                  FROM LongRunningOperations operation
                  WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
              AND NOT EXISTS (
                  SELECT 1
                  FROM InferenceRuns run
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND run.Status = @running)
              AND NOT EXISTS (
                  SELECT 1
                  FROM BudgetReservations reservation
                  JOIN InferenceRuns run
                    ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                  WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                    AND reservation.Status = @reserved)
              {protectedClause}
              {selectedClause}
            ORDER BY entry.CreatedAt, embedding.EntryId
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@kind", (int)SessionContextPinKind.SessionEntry);

        Add(command, "@running", (int)InferenceRunStatus.Running);

        Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@limit", limit - candidates.Count);

        for (int index = 0; index < retention.ProtectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@protected" + index.ToString(CultureInfo.InvariantCulture),
                retention.ProtectedSessionIds[index].ToString("N"));

        }

        for (int index = 0; index < selectedSessionIds.Length; index++)
        {

            Add(
                command,
                "@selected" + index.ToString(CultureInfo.InvariantCulture),
                selectedSessionIds[index].ToString("N"));

        }

        List<(string EntryId, Guid SessionId, bool Pinned)> rows = [];

        await using (DbDataReader reader = await command
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add(
                    (
                        reader.GetString(0),
                        Guid.Parse(reader.GetString(1)),
                        Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0
                    ));

            }

        }

        foreach ((string entryId, Guid sessionId, bool pinned) in rows)
        {

            if (selectedSessions.Contains(sessionId)
                || candidates.Any(candidate =>
                    candidate == EntryCandidatePrefix + NormalizeGuid(entryId)))
            {

                continue;

            }

            if (pinned || retention.ProtectedSessionIds.Contains(sessionId))
            {

                continue;

            }

            long contextPins = await CountTableAsync(
                "SessionContextPins",
                """
                Kind = @kind
                AND lower(replace(SessionId, '-', '')) = @sessionId
                AND lower(replace(TargetIdentifier, '-', '')) = @entryId
                """,
                cancellationToken,
                ("@kind", (int)SessionContextPinKind.SessionEntry),
                ("@sessionId", sessionId.ToString("N")),
                ("@entryId", entryId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())).ConfigureAwait(false);

            if (contextPins > 0)
            {

                continue;

            }

            DataRetentionConflict[] entryConflicts = await ReadSessionConflictsAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false);

            if (entryConflicts.Length > 0)
            {

                conflicts.AddRange(entryConflicts);

                continue;

            }

            candidates.Add(EntryEmbeddingCandidatePrefix + entryId);

            long derived = await CountTableAsync(
                "entry_embeddings",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", entryId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())).ConfigureAwait(false);

            derived += await CountTableAsync(
                "entry_embeddings_vec",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", entryId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())).ConfigureAwait(false);

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.SessionEntryEmbeddings,
                    0,
                    0,
                    0,
                    derived));

        }

    }

    private async Task AddWorkspaceCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.WorkspaceIndexes;

        if (!rule.Enabled
            || candidates.Count >= limit
            || !await TableExistsAsync("workspace_file_chunks", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        DataRetentionConflict[] activeWorkspaceOperations = await ReadConflictsAsync(
            $"""
            SELECT Id
            FROM LongRunningOperations
            WHERE Kind = @kind
              AND State IN ({string.Join(",", ActiveOperationStates)})
            """,
            "Data.WorkspaceIndexActive",
            "An active workspace-index operation protects derived workspace indexes.",
            cancellationToken,
            ("@kind", LongRunningOperationKinds.WorkspaceIndex)).ConfigureAwait(false);

        if (activeWorkspaceOperations.Length > 0)
        {

            conflicts.AddRange(activeWorkspaceOperations);

            blockers.Add(
                new DataRetentionBlocker(
                    RetentionDataClass.WorkspaceChunks,
                    "workspace-index",
                    "Data.WorkspaceIndexActive",
                    "Workspace indexes cannot be pruned while an index operation is active."));

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        bool vectorTableExists = await TableExistsAsync(
            "workspace_file_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        string vectorCount = vectorTableExists
            ? """
              + (SELECT COUNT(*)
                 FROM workspace_file_embeddings_vec vector
                 WHERE vector.ChunkId = chunk.ChunkId)
              """
            : string.Empty;

        command.CommandText =
            $"""
            SELECT chunk.ChunkId,
                   (SELECT COUNT(*)
                    FROM workspace_file_embeddings embedding
                    WHERE embedding.ChunkId = chunk.ChunkId)
                   {vectorCount}
            FROM workspace_file_chunks chunk
            WHERE julianday(chunk.IndexedAt) <= julianday(@cutoff)
            ORDER BY chunk.IndexedAt, chunk.ChunkId
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@limit", limit - candidates.Count);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string chunkId = reader.GetString(0);

            long embeddings = reader.GetInt64(1);

            candidates.Add(WorkspaceCandidatePrefix + chunkId);

            items.Add(new DataRetentionPlanItem(RetentionDataClass.WorkspaceChunks, 1, 0, 0, 0));

            if (embeddings > 0)
            {

                items.Add(
                    new DataRetentionPlanItem(
                        RetentionDataClass.WorkspaceEmbeddings,
                        0,
                        0,
                        0,
                        embeddings));

            }

        }

    }

    /// <summary>
    /// Selects the aged, unpinned Saga memories, and reports what the pin kept out of that selection.
    /// </summary>
    /// <remarks>
    /// A pinned memory is not a candidate: the pin binds the automatic path, which is the one that acts
    /// without being asked. The returned inventory is what keeps the preview honest about it — an
    /// operator reading a dry-run that silently omitted the exempted rows would believe their rule
    /// reaches further than it does.
    /// </remarks>
    private async Task<DataRetentionSagaCurationInventory> AddSagaCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.SagaMemories;

        long pinnedRows = await CountTableAsync(
            "saga_memories",
            "PinnedAtUtc IS NOT NULL",
            cancellationToken).ConfigureAwait(false);

        if (!rule.Enabled || candidates.Count >= limit)
        {

            // This plan selects no Saga memory at all, so none of the pinned ones was exempted from
            // anything.
            return new DataRetentionSagaCurationInventory(pinnedRows, 0);

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        long pinnedRowsExemptFromPlan = await CountTableAsync(
            "saga_memories",
            "PinnedAtUtc IS NOT NULL AND julianday(CreatedAt) <= julianday(@cutoff)",
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        string[] ids = await ReadStringIdsAsync(
            "saga_memories",
            "Id",
            "julianday(CreatedAt) <= julianday(@cutoff) AND PinnedAtUtc IS NULL",
            "CreatedAt, Id",
            limit - candidates.Count,
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        foreach (string id in ids)
        {

            long derived = await CountTableAsync(
                "saga_memory_embeddings",
                "MemoryId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            derived += await CountTableAsync(
                "saga_memory_embeddings_vec",
                "MemoryId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            derived += await CountTableAsync(
                "saga_memory_attachment_provenance",
                "MemoryId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            derived += await CountTableAsync(
                "annal_claims",
                "SubjectStoreCode = 1 AND SubjectId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            candidates.Add(SagaCandidatePrefix + id);

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.SagaMemories,
                    1,
                    0,
                    0,
                    derived));

        }

        return new DataRetentionSagaCurationInventory(
            pinnedRows,
            pinnedRowsExemptFromPlan);

    }

    private async Task AddLexiconCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.LexiconEntries;

        if (!rule.Enabled || candidates.Count >= limit)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        string[] ids = await ReadStringIdsAsync(
            "lexicon_entries",
            "Id",
            "julianday(UpdatedAt) <= julianday(@cutoff)",
            "UpdatedAt, Id",
            limit - candidates.Count,
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        foreach (string id in ids)
        {

            long derived = await CountTableAsync(
                "lexicon_fact_attachment_provenance",
                "EntryId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            derived += await CountTableAsync(
                "lexicon_fts",
                "rowid IN (SELECT rowid FROM lexicon_entries WHERE Id = @id)",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            derived += await CountTableAsync(
                "annal_claims",
                "SubjectStoreCode = 2 AND SubjectId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            candidates.Add(LexiconCandidatePrefix + id);

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.LexiconEntries,
                    1,
                    0,
                    0,
                    derived));

        }

    }

    private void AddLogCandidates(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates)
    {

        AddLogCandidates(
            retention.AuditLogs,
            RetentionDataClass.AuditLogs,
            "audit-????????.jsonl",
            AuditLogCandidatePrefix,
            limit,
            items,
            candidates);

        AddLogCandidates(
            retention.GuardrailLogs,
            RetentionDataClass.GuardrailLogs,
            "guardrails-????????.jsonl",
            GuardrailLogCandidatePrefix,
            limit,
            items,
            candidates);

    }

    private async Task AddIdempotencyCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.IdempotencyClaims;

        if (!rule.Enabled || candidates.Count >= limit)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        if (await TableExistsAsync("IdempotencyClaims", cancellationToken).ConfigureAwait(false))
        {

            DbConnection connection = await OpenConnectionAsync(
                cancellationToken).ConfigureAwait(false);

            await using (DbCommand blockedCommand = connection.CreateCommand())
            {

                blockedCommand.CommandText =
                    """
                    SELECT Id, LeaseExpiresAt
                    FROM IdempotencyClaims
                    WHERE julianday(UpdatedAt) <= julianday(@cutoff)
                      AND State IN (@claimed, @running)
                    ORDER BY UpdatedAt, Id
                    LIMIT @limit
                    """;

                Add(blockedCommand, "@cutoff", FormatTimestamp(cutoff));

                Add(blockedCommand, "@claimed", (int)IdempotencyClaimState.Claimed);

                Add(blockedCommand, "@running", (int)IdempotencyClaimState.Running);

                Add(blockedCommand, "@limit", limit - candidates.Count);

                await using DbDataReader blockedReader = await blockedCommand
                    .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await blockedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    Guid id = Guid.Parse(blockedReader.GetString(0));

                    DateTimeOffset leaseExpiresAt = DateTimeOffset.Parse(
                        blockedReader.GetString(1),
                        CultureInfo.InvariantCulture);

                    blockers.Add(
                        new DataRetentionBlocker(
                            RetentionDataClass.IdempotencyClaims,
                            id.ToString("D"),
                            "Data.IdempotencyLeaseActive",
                            leaseExpiresAt > PrunePlanningTimestamp
                                ? "An active idempotency lease cannot be pruned."
                                : "An unfinished idempotency claim requires reconciliation before pruning."));

                    conflicts.Add(
                        new DataRetentionConflict(
                            "Data.IdempotencyLeaseActive",
                            id.ToString("D"),
                            "An unfinished idempotency claim protects its request state."));

                }

            }

            await using DbCommand command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT Id, State, LeaseExpiresAt
                FROM IdempotencyClaims
                WHERE julianday(UpdatedAt) <= julianday(@cutoff)
                  AND State IN (@completed, @failed, @abandoned)
                ORDER BY UpdatedAt, Id
                LIMIT @limit
                """;

            Add(command, "@cutoff", FormatTimestamp(cutoff));

            Add(command, "@completed", (int)IdempotencyClaimState.Completed);

            Add(command, "@failed", (int)IdempotencyClaimState.Failed);

            Add(command, "@abandoned", (int)IdempotencyClaimState.Abandoned);

            Add(command, "@limit", limit - candidates.Count);

            List<(Guid Id, IdempotencyClaimState State, DateTimeOffset LeaseExpiresAt)> rows = [];

            await using (DbDataReader reader = await command
                             .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    rows.Add(
                        (
                            Guid.Parse(reader.GetString(0)),
                            (IdempotencyClaimState)reader.GetInt32(1),
                            DateTimeOffset.Parse(
                                reader.GetString(2),
                                CultureInfo.InvariantCulture)
                        ));

                }

            }

            foreach ((Guid id, IdempotencyClaimState state, DateTimeOffset leaseExpiresAt) in rows)
            {

                if (state is IdempotencyClaimState.Claimed or IdempotencyClaimState.Running)
                {

                    blockers.Add(
                        new DataRetentionBlocker(
                            RetentionDataClass.IdempotencyClaims,
                            id.ToString("D"),
                            "Data.IdempotencyLeaseActive",
                            leaseExpiresAt > PrunePlanningTimestamp
                                ? "An active idempotency lease cannot be pruned."
                                : "An unfinished idempotency claim requires reconciliation before pruning."));

                    conflicts.Add(
                        new DataRetentionConflict(
                            "Data.IdempotencyLeaseActive",
                            id.ToString("D"),
                            "An unfinished idempotency claim protects its request state."));

                    continue;

                }

                candidates.Add(IdempotencyClaimCandidatePrefix + id.ToString("D"));

                items.Add(
                    new DataRetentionPlanItem(
                        RetentionDataClass.IdempotencyClaims,
                        1,
                        0,
                        0,
                        0));

            }

        }

        if (candidates.Count >= limit)
        {

            return;

        }

        string[] legacyKeys = await ReadStringIdsAsync(
            "IdempotencyKeys",
            "KeyHash",
            "julianday(CreatedAt) <= julianday(@cutoff)",
            "CreatedAt, KeyHash",
            limit - candidates.Count,
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        foreach (string key in legacyKeys)
        {

            candidates.Add(IdempotencyKeyCandidatePrefix + key);

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.IdempotencyClaims,
                    1,
                    0,
                    0,
                    0));

        }

    }

    private async Task AddAccountingCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<DataRetentionBlocker> blockers,
        List<DataRetentionConflict> conflicts,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.Accounting;

        if (!rule.Enabled
            || candidates.Count >= limit
            || !await TableExistsAsync("InferenceRuns", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        int days = Math.Max(
            ArcanumSettingClamps.RetentionRuleDays(rule.Days),
            ArcanumSettingClamps.RetentionAccountingMinimumDays(
                retention.AccountingMinimumDays));

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(-days);

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using (DbCommand blockedCommand = connection.CreateCommand())
        {

            blockedCommand.CommandText =
                """
                SELECT run.Id,
                       run.Status,
                       EXISTS(
                           SELECT 1
                           FROM Sessions session
                           WHERE lower(replace(session.Id, '-', '')) = lower(replace(run.SessionId, '-', ''))),
                       EXISTS(
                           SELECT 1
                           FROM BudgetReservations reservation
                           WHERE lower(replace(reservation.RunId, '-', '')) = lower(replace(run.Id, '-', ''))
                             AND reservation.Status = @reserved)
                FROM InferenceRuns run
                WHERE julianday(COALESCE(run.CompletedAt, run.StartedAt)) <= julianday(@cutoff)
                  AND (
                      run.Status = @running
                      OR EXISTS(
                          SELECT 1
                          FROM Sessions session
                          WHERE lower(replace(session.Id, '-', '')) = lower(replace(run.SessionId, '-', '')))
                      OR EXISTS(
                          SELECT 1
                          FROM BudgetReservations reservation
                          WHERE lower(replace(reservation.RunId, '-', '')) = lower(replace(run.Id, '-', ''))
                            AND reservation.Status = @reserved))
                ORDER BY COALESCE(run.CompletedAt, run.StartedAt), run.Id
                LIMIT @limit
                """;

            Add(blockedCommand, "@running", (int)InferenceRunStatus.Running);

            Add(blockedCommand, "@reserved", (int)BudgetReservationStatus.Reserved);

            Add(blockedCommand, "@cutoff", FormatTimestamp(cutoff));

            Add(blockedCommand, "@limit", limit - candidates.Count);

            await using DbDataReader blockedReader = await blockedCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await blockedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                Guid runId = Guid.Parse(blockedReader.GetString(0));

                InferenceRunStatus status = (InferenceRunStatus)blockedReader.GetInt32(1);

                bool retainedSession = blockedReader.GetInt64(2) != 0;

                bool reserved = blockedReader.GetInt64(3) != 0;

                bool active = status == InferenceRunStatus.Running || reserved;

                string code = status == InferenceRunStatus.Running
                    ? "Data.InferenceRunActive"
                    : reserved
                        ? "Data.BudgetReservationOutstanding"
                        : "Data.SessionAccountingAuthority";

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.InferenceRuns,
                        runId.ToString("D"),
                        code,
                        active
                            ? "Active accounting state cannot be pruned."
                            : "Accounting for a retained session remains available to explain its cost."));

                if (active)
                {

                    conflicts.Add(
                        new DataRetentionConflict(
                            code,
                            runId.ToString("D"),
                            "Active accounting state protects the entire run chain."));

                }

            }

        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT run.Id, run.Status,
                   EXISTS(
                       SELECT 1
                       FROM Sessions session
                       WHERE lower(replace(session.Id, '-', '')) = lower(replace(run.SessionId, '-', ''))),
                   (SELECT COUNT(*) FROM BillableOperations billable
                    WHERE lower(replace(billable.RunId, '-', '')) = lower(replace(run.Id, '-', ''))),
                   (SELECT COUNT(*) FROM BudgetReservations reservation
                    WHERE lower(replace(reservation.RunId, '-', '')) = lower(replace(run.Id, '-', ''))),
                   (SELECT COUNT(*) FROM CostAdjustments adjustment
                    WHERE lower(replace(adjustment.RunId, '-', '')) = lower(replace(run.Id, '-', ''))
                       OR adjustment.BillableOperationId IN (
                           SELECT Id FROM BillableOperations billable
                           WHERE lower(replace(billable.RunId, '-', '')) = lower(replace(run.Id, '-', '')))),
                   EXISTS(
                       SELECT 1 FROM BudgetReservations reservation
                       WHERE lower(replace(reservation.RunId, '-', '')) = lower(replace(run.Id, '-', ''))
                         AND reservation.Status = @reserved)
            FROM InferenceRuns run
            WHERE julianday(COALESCE(run.CompletedAt, run.StartedAt)) <= julianday(@cutoff)
              AND run.Status <> @running
              AND NOT EXISTS (
                  SELECT 1
                  FROM Sessions session
                  WHERE lower(replace(session.Id, '-', '')) = lower(replace(run.SessionId, '-', '')))
              AND NOT EXISTS (
                  SELECT 1
                  FROM BudgetReservations reservation
                  WHERE lower(replace(reservation.RunId, '-', '')) = lower(replace(run.Id, '-', ''))
                    AND reservation.Status = @reserved)
            ORDER BY COALESCE(run.CompletedAt, run.StartedAt), run.Id
            LIMIT @limit
            """;

        Add(command, "@running", (int)InferenceRunStatus.Running);

        Add(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@limit", limit - candidates.Count);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Guid runId = Guid.Parse(reader.GetString(0));

            InferenceRunStatus status = (InferenceRunStatus)reader.GetInt32(1);

            bool retainedSession = reader.GetInt64(2) != 0;

            long billable = reader.GetInt64(3);

            long reservations = reader.GetInt64(4);

            long adjustments = reader.GetInt64(5);

            bool reserved = reader.GetInt64(6) != 0;

            if (status == InferenceRunStatus.Running || reserved)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.InferenceRuns,
                        runId.ToString("D"),
                        status == InferenceRunStatus.Running
                            ? "Data.InferenceRunActive"
                            : "Data.BudgetReservationOutstanding",
                        "Active accounting state cannot be pruned."));

                conflicts.Add(
                    new DataRetentionConflict(
                        status == InferenceRunStatus.Running
                            ? "Data.InferenceRunActive"
                            : "Data.BudgetReservationOutstanding",
                        runId.ToString("D"),
                        "Active accounting state protects the entire run chain."));

                continue;

            }

            if (retainedSession)
            {

                blockers.Add(
                    new DataRetentionBlocker(
                        RetentionDataClass.InferenceRuns,
                        runId.ToString("D"),
                        "Data.SessionAccountingAuthority",
                        "Accounting for a retained session remains available to explain its cost."));

                continue;

            }

            candidates.Add(AccountingCandidatePrefix + runId.ToString("D"));

            items.Add(new DataRetentionPlanItem(RetentionDataClass.InferenceRuns, 1, 0, 0, 0));

            if (billable > 0)
            {

                items.Add(new DataRetentionPlanItem(RetentionDataClass.BillableOperations, billable, 0, 0, 0));

            }

            if (reservations > 0)
            {

                items.Add(new DataRetentionPlanItem(RetentionDataClass.BudgetReservations, reservations, 0, 0, 0));

            }

            if (adjustments > 0)
            {

                items.Add(new DataRetentionPlanItem(RetentionDataClass.CostAdjustments, adjustments, 0, 0, 0));

            }

        }

        await reader.DisposeAsync().ConfigureAwait(false);

        if (candidates.Count >= limit)
        {

            return;

        }

        await AddStandaloneAccountingCandidatesAsync(
            connection,
            cutoff,
            limit,
            items,
            candidates,
            cancellationToken).ConfigureAwait(false);

    }

    private static async Task AddStandaloneAccountingCandidatesAsync(
        DbConnection connection,
        DateTimeOffset cutoff,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT CandidateKind, Id
            FROM (
                SELECT 0 AS CandidateKind, Id, CreatedAt AS CandidateAt
                FROM CostAdjustments
                WHERE BillableOperationId IS NULL
                  AND RunId IS NULL
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                UNION ALL
                SELECT 1 AS CandidateKind, Id, AlertedAt AS CandidateAt
                FROM BudgetAlerts
                WHERE julianday(AlertedAt) <= julianday(@cutoff))
            ORDER BY julianday(CandidateAt), CandidateKind, Id
            LIMIT @limit
            """;

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        Add(command, "@limit", limit - candidates.Count);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            int candidateKind = reader.GetInt32(0);

            Guid id = Guid.Parse(reader.GetString(1));

            candidates.Add(
                (candidateKind == 0
                    ? AccountingAdjustmentCandidatePrefix
                    : BudgetAlertCandidatePrefix)
                + id.ToString("D"));

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.CostAdjustments,
                    1,
                    0,
                    0,
                    0));

        }

    }

    private async Task AddOperationHistoryCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.LongRunningOperations;

        if (!rule.Enabled || candidates.Count >= limit)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        string[] ids = await ReadStringIdsAsync(
            "LongRunningOperations",
            "Id",
            $"""
            State IN ({(int)LongRunningOperationState.Completed},
                      {(int)LongRunningOperationState.Failed},
                      {(int)LongRunningOperationState.Abandoned})
              AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
              AND NOT EXISTS (
                  SELECT 1 FROM LongRunningOperations child
                  WHERE child.ParentOperationId = LongRunningOperations.Id
                     OR child.RootOperationId = LongRunningOperations.Id)
            """,
            "COALESCE(CompletedAt, CreatedAt), Id",
            limit - candidates.Count,
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        foreach (string id in ids)
        {

            candidates.Add(OperationCandidatePrefix + NormalizeGuid(id));

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.LongRunningOperations,
                    1,
                    0,
                    0,
                    0));

        }

    }

    private async Task AddSanctumCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.SanctumBreaches;

        if (!rule.Enabled || candidates.Count >= limit)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        string[] ids = await ReadStringIdsAsync(
            "SanctumBreaches",
            "Id",
            "julianday(OccurredAt) <= julianday(@cutoff)",
            "OccurredAt, Id",
            limit - candidates.Count,
            cancellationToken,
            ("@cutoff", FormatTimestamp(cutoff))).ConfigureAwait(false);

        foreach (string id in ids)
        {

            candidates.Add(SanctumCandidatePrefix + NormalizeGuid(id));

            items.Add(new DataRetentionPlanItem(RetentionDataClass.SanctumBreaches, 1, 0, 0, 0));

        }

    }

    private async Task<string[]> ReadStringIdsAsync(
        string table,
        string idColumn,
        string predicate,
        string orderBy,
        int limit,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        if (limit <= 0
            || !await TableExistsAsync(table, cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"SELECT \"{idColumn}\" FROM \"{table}\" WHERE {predicate} ORDER BY {orderBy} LIMIT @limit";

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        Add(command, "@limit", limit);

        List<string> ids = [];

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(reader.GetString(0));

        }

        return [.. ids];

    }

    private async Task AddDaemonHistoryCandidatesAsync(
        RetentionSettings retention,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {

        RetentionRuleSettings rule = retention.DaemonHistory;

        if (daemonExecutions is null
            || !rule.Enabled
            || candidates.Count >= limit)
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        DaemonExecutionSummary[] history = await daemonExecutions.GetHistoryAsync(
            null,
            cancellationToken).ConfigureAwait(false);

        DaemonExecutionSummary[] selected =
        [
            .. history
                .Where(execution => execution.Status is
                    DaemonJobStatus.Completed
                    or DaemonJobStatus.Failed
                    or DaemonJobStatus.Cancelled)
                .Where(execution => execution.CompletedAt is not null
                    && execution.CompletedAt.Value <= cutoff)
                .OrderBy(static execution => execution.CompletedAt)
                .ThenBy(static execution => execution.Id, StringComparer.Ordinal)
                .Take(limit - candidates.Count),
        ];

        foreach (DaemonExecutionSummary execution in selected)
        {

            string candidate = DaemonCandidatePrefix + execution.Id;

            if (!IsValidCheckpointCandidate(candidate))
            {

                continue;

            }

            candidates.Add(candidate);

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.DaemonExecutions,
                    1,
                    0,
                    0,
                    0));

        }

    }

    private static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out Guid guid) ? guid.ToString("D") : value;

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> BuildCandidateCutoffsAsync(
        IEnumerable<string> candidates,
        DateTimeOffset generatedAt,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {

        Dictionary<string, DateTimeOffset> cutoffs =
            new(StringComparer.Ordinal);

        foreach (string candidate in candidates)
        {

            CandidateAgeBoundary? boundary = await ReadCandidateAgeBoundaryAsync(
                candidate,
                generatedAt,
                retention,
                cancellationToken).ConfigureAwait(false);

            if (boundary is null)
            {

                throw new InvalidOperationException(
                    $"The retention candidate '{candidate}' has no enabled age boundary.");

            }

            cutoffs.Add(candidate, boundary.Cutoff);

        }

        return cutoffs;

    }

    private async Task<RetentionMutationJournal?> BuildPruneCandidateJournalAsync(
        string candidate,
        CancellationToken cancellationToken)
    {

        List<RetentionMutationJournalEntry> entries = [];

        if (TryReadGuidCandidate(candidate, FileCandidatePrefix, out Guid fileId))
        {

            AddPruneJournalFile(
                entries,
                "files",
                _filesRoot,
                fileId.ToString("N"));

        }
        else if (TryReadGuidCandidate(candidate, "session:", out Guid sessionId))
        {

            SessionPlanSnapshot? snapshot = await ReadSessionSnapshotAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false);

            IEnumerable<AttachmentPlanSnapshot> attachments = snapshot?.Attachments
                .Where(static item => item.FileExists)
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                ?? Enumerable.Empty<AttachmentPlanSnapshot>();

            foreach (AttachmentPlanSnapshot attachment in attachments)
            {

                AddPruneJournalFile(
                    entries,
                    "attachments",
                    _attachmentsRoot,
                    attachment.RelativePath);

            }

        }
        else if (TryReadGuidCandidate(
                     candidate,
                     AttachmentCandidatePrefix,
                     out Guid attachmentId))
        {

            AttachmentPlanSnapshot? snapshot = await ReadAttachmentSnapshotAsync(
                attachmentId,
                cancellationToken).ConfigureAwait(false);

            if (snapshot?.FileExists == true)
            {

                AddPruneJournalFile(
                    entries,
                    "attachments",
                    _attachmentsRoot,
                    snapshot.RelativePath);

            }

        }
        else if (candidate.StartsWith(AuditLogCandidatePrefix, StringComparison.Ordinal))
        {

            AddPruneJournalFile(
                entries,
                "logs",
                _logsRoot,
                candidate[AuditLogCandidatePrefix.Length..]);

        }
        else if (candidate.StartsWith(GuardrailLogCandidatePrefix, StringComparison.Ordinal))
        {

            AddPruneJournalFile(
                entries,
                "logs",
                _logsRoot,
                candidate[GuardrailLogCandidatePrefix.Length..]);

        }
        else
        {

            return null;

        }

        return new RetentionMutationJournal(
            "prune-candidate",
            candidate,
            [.. entries]);

    }

    private static void AddPruneJournalFile(
        List<RetentionMutationJournalEntry> entries,
        string rootRole,
        string root,
        string relativePath)
    {

        string path = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                path,
                out _))
        {

            throw new InvalidDataException(
                "A prune candidate file escaped its managed root.");

        }

        if (!File.Exists(path))
        {

            return;

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            throw new IOException(
                "A prune candidate file changed before its durable journal was written.");

        }

        entries.Add(
            new RetentionMutationJournalEntry(
                rootRole,
                relativePath,
                artifact.Metadata));

    }

    private async Task<CandidateAgeBoundary?> ReadCandidateAgeBoundaryAsync(
        string candidate,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken) =>
        await ReadCandidateAgeBoundaryAsync(
            candidate,
            generatedAt,
            CurrentRetention,
            cancellationToken).ConfigureAwait(false);

    private async Task<CandidateAgeBoundary?> ReadCandidateAgeBoundaryAsync(
        string candidate,
        DateTimeOffset generatedAt,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {

        if (TryReadGuidCandidate(candidate, "session:", out Guid sessionId))
        {

            DbConnection connection = await OpenConnectionAsync(
                cancellationToken).ConfigureAwait(false);

            string? status = await ScalarStringAsync(
                connection,
                "SELECT lower(Status) FROM Sessions WHERE lower(replace(Id, '-', '')) = @id LIMIT 1",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            return status switch
            {

                "active" => Boundary(retention.ActiveSessions, generatedAt),

                "archived" => Boundary(retention.ArchivedSessions, generatedAt),

                _ => null,

            };

        }

        if (candidate.StartsWith(BatchCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.CompletedBatches, generatedAt);

        }

        if (candidate.StartsWith(FileCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.UploadedFiles, generatedAt);

        }

        if (candidate.StartsWith(EntryCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.Entries, generatedAt);

        }

        if (candidate.StartsWith(AttachmentCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.Attachments, generatedAt);

        }

        if (candidate.StartsWith(EntryEmbeddingCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.SessionEntryEmbeddings, generatedAt);

        }

        if (candidate.StartsWith(WorkspaceCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.WorkspaceIndexes, generatedAt);

        }

        if (candidate.StartsWith(SagaCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.SagaMemories, generatedAt);

        }

        if (candidate.StartsWith(LexiconCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.LexiconEntries, generatedAt);

        }

        if (candidate.StartsWith(AuditLogCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.AuditLogs, generatedAt);

        }

        if (candidate.StartsWith(GuardrailLogCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.GuardrailLogs, generatedAt);

        }

        if (candidate.StartsWith(IdempotencyClaimCandidatePrefix, StringComparison.Ordinal)
            || candidate.StartsWith(IdempotencyKeyCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.IdempotencyClaims, generatedAt);

        }

        if (candidate.StartsWith(AccountingCandidatePrefix, StringComparison.Ordinal)
            || candidate.StartsWith(AccountingAdjustmentCandidatePrefix, StringComparison.Ordinal)
            || candidate.StartsWith(BudgetAlertCandidatePrefix, StringComparison.Ordinal))
        {

            if (!retention.Accounting.Enabled)
            {

                return null;

            }

            int days = Math.Max(
                ArcanumSettingClamps.RetentionRuleDays(retention.Accounting.Days),
                ArcanumSettingClamps.RetentionAccountingMinimumDays(
                    retention.AccountingMinimumDays));

            return new CandidateAgeBoundary(generatedAt.AddDays(-days), days);

        }

        if (candidate.StartsWith(OperationCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.LongRunningOperations, generatedAt);

        }

        if (candidate.StartsWith(SanctumCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.SanctumBreaches, generatedAt);

        }

        if (candidate.StartsWith(DaemonCandidatePrefix, StringComparison.Ordinal))
        {

            return Boundary(retention.DaemonHistory, generatedAt);

        }

        return null;

    }

    private static CandidateAgeBoundary? Boundary(
        RetentionRuleSettings rule,
        DateTimeOffset generatedAt)
    {

        if (!rule.Enabled)
        {

            return null;

        }

        int days = ArcanumSettingClamps.RetentionRuleDays(rule.Days);

        return new CandidateAgeBoundary(generatedAt.AddDays(-days), days);

    }

    private async Task<DataRetentionApplyResult> ApplyUnifiedPruneAsync(
        Guid operationId,
        string ownerId,
        DataRetentionPlan plan,
        int startIndex,
        int checkpointVersion,
        bool saveCheckpoints,
        IReadOnlyDictionary<string, DateTimeOffset>? frozenCutoffs,
        CancellationToken cancellationToken)
    {

        const int checkpointInterval = PruneCheckpointInterval;

        int currentCheckpointVersion = checkpointVersion;

        IReadOnlyDictionary<string, DateTimeOffset> originalCutoffs =
            frozenCutoffs is not null && frozenCutoffs.Count > 0
                ? frozenCutoffs
                : _frozenPruneAuthorities.TryGetValue(
                    plan,
                    out FrozenPruneAuthority? authority)
                    ? authority.Cutoffs
                    : await BuildCandidateCutoffsAsync(
                        plan.CandidateIds,
                        plan.GeneratedAt,
                        SnapshotRetention(CurrentRetention),
                        cancellationToken).ConfigureAwait(false);

        if (saveCheckpoints && currentCheckpointVersion == 0)
        {

            currentCheckpointVersion = await SavePruneCheckpointAsync(
                operationId,
                ownerId,
                plan,
                originalCutoffs,
                nextCandidateIndex: startIndex,
                expectedCheckpointVersion: 0,
                pendingJournal: null,
                cancellationToken).ConfigureAwait(false);

        }

        long rowsDeleted = 0;

        long filesDeleted = 0;

        long bytesDeleted = 0;

        long derivedDeleted = 0;

        List<DataRetentionConflict> appliedConflicts = [.. plan.Conflicts];

        int? earliestSkippedIndex = null;

        DateTimeOffset lastLeaseRenewalAt = timeProvider.GetUtcNow();

        for (int index = startIndex; index < plan.CandidateIds.Length; index++)
        {

            cancellationToken.ThrowIfCancellationRequested();

            // Renewal is wall-clock driven, not candidate-count driven. A sweep of candidates that are
            // each faster than the heartbeat interval never wakes the lease maintainer, and the
            // checkpoint-boundary renewal below is up to PruneCheckpointInterval candidates away, so
            // without this the lease can lapse mid-sweep — which lets the background reconciler adopt
            // this operation and run a second concurrent prune under the same plan. Time spent outside
            // the lease maintainer (age boundaries, journal builds, checkpoints, existence probes) is
            // counted here too, because none of it renews anything.
            if (saveCheckpoints)
            {

                DateTimeOffset iterationStartedAt = timeProvider.GetUtcNow();

                if (iterationStartedAt - lastLeaseRenewalAt
                    >= DataRetentionLeaseMaintainer.DefaultHeartbeatInterval)
                {

                    bool renewedLease = await operations.HeartbeatAsync(
                        operationId,
                        ownerId,
                        iterationStartedAt,
                        iterationStartedAt.Add(DataRetentionLeaseMaintainer.DefaultLeaseDuration),
                        cancellationToken).ConfigureAwait(false);

                    if (!renewedLease)
                    {

                        throw new InvalidOperationException(
                            "The retention operation lost its durable lease while applying candidates.");

                    }

                    lastLeaseRenewalAt = iterationStartedAt;

                }

            }

            string candidate = plan.CandidateIds[index];

            CandidateAgeBoundary? currentBoundary =
                await ReadCandidateAgeBoundaryAsync(
                    candidate,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

            CandidateDeleteResult deleted;

            RetentionMutationJournal? pendingJournal = null;

            if (currentBoundary is null
                || !originalCutoffs.TryGetValue(
                    candidate,
                    out DateTimeOffset originalCutoff))
            {

                deleted = CandidateDeleteResult.Empty;

            }
            else
            {

                DateTimeOffset effectiveCutoff = originalCutoff <= currentBoundary.Cutoff
                    ? originalCutoff
                    : currentBoundary.Cutoff;

                pendingJournal = await BuildPruneCandidateJournalAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);

                if (saveCheckpoints && pendingJournal is not null)
                {

                    currentCheckpointVersion = await SavePruneCheckpointAsync(
                        operationId,
                        ownerId,
                        plan,
                        originalCutoffs,
                        nextCandidateIndex: earliestSkippedIndex ?? index,
                        expectedCheckpointVersion: currentCheckpointVersion,
                        pendingJournal,
                        cancellationToken).ConfigureAwait(false);

                }

                deleted = await _leaseMaintainer.RunAsync(
                    operationId,
                    ownerId,
                    candidateCancellationToken => ApplyCandidateAsync(
                        operationId,
                        plan,
                        candidate,
                        effectiveCutoff,
                        pendingJournal,
                        candidateCancellationToken),
                    cancellationToken).ConfigureAwait(false);

            }

            if (!deleted.Reconciled)
            {

                throw new IOException(
                    "Post-delete reconciliation found retained owned data for the current candidate.");

            }

            bool preserved = deleted.Rows == 0
                && deleted.Files == 0
                && deleted.Derived == 0
                && await CandidateStillExistsAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);

            if (saveCheckpoints && pendingJournal is not null)
            {

                currentCheckpointVersion = await SavePruneCheckpointAsync(
                    operationId,
                    ownerId,
                    plan,
                    originalCutoffs,
                    nextCandidateIndex: earliestSkippedIndex ?? (preserved ? index : index + 1),
                    expectedCheckpointVersion: currentCheckpointVersion,
                    pendingJournal: null,
                    cancellationToken).ConfigureAwait(false);

            }

            if (preserved)
            {

                appliedConflicts.Add(
                    new DataRetentionConflict(
                        ErrorCodes.Data.PlanChanged,
                        candidate,
                        "The retention candidate changed or became protected after planning; it was preserved."));

                earliestSkippedIndex ??= index;

            }
            else
            {

                rowsDeleted += deleted.Rows;

                filesDeleted += deleted.Files;

                bytesDeleted += deleted.Bytes;

                derivedDeleted += deleted.Derived;

            }

            int nextIndex = index + 1;

            if (!saveCheckpoints
                || (nextIndex % checkpointInterval != 0
                    && nextIndex != plan.CandidateIds.Length))
            {

                continue;

            }

            // The lease is renewed on every checkpoint boundary, including boundaries the cursor
            // cannot advance past. A preserved candidate or a journal-bearing one must not silence
            // renewal for the rest of the sweep: an expired lease lets the background reconciler
            // adopt this operation and run a second concurrent prune under the same plan.
            DateTimeOffset now = timeProvider.GetUtcNow();

            bool heartbeat = await operations.HeartbeatAsync(
                operationId,
                ownerId,
                now,
                now.Add(DataRetentionLeaseMaintainer.DefaultLeaseDuration),
                cancellationToken).ConfigureAwait(false);

            if (!heartbeat)
            {

                throw new InvalidOperationException(
                    "The retention operation lost its durable lease while checkpointing.");

            }

            lastLeaseRenewalAt = now;

            // A journal-bearing candidate already wrote its own cursor above, and an earlier
            // preserved candidate pins the resume point, so neither may advance it here.
            if (pendingJournal is null && earliestSkippedIndex is null)
            {

                currentCheckpointVersion = await SavePruneCheckpointAsync(
                    operationId,
                    ownerId,
                    plan,
                    originalCutoffs,
                    nextIndex,
                    currentCheckpointVersion,
                    pendingJournal: null,
                    cancellationToken).ConfigureAwait(false);

            }

        }

        return new DataRetentionApplyResult(
            operationId,
            plan.PlanId,
            rowsDeleted,
            filesDeleted,
            bytesDeleted,
            derivedDeleted,
            Reconciled: true,
            plan.Blockers,
            [.. appliedConflicts
                .DistinctBy(static conflict => (conflict.Code, conflict.ResourceId))
                .OrderBy(static conflict => conflict.Code, StringComparer.Ordinal)
                .ThenBy(static conflict => conflict.ResourceId, StringComparer.Ordinal)]);

    }

    private async Task<int> SavePruneCheckpointAsync(
        Guid operationId,
        string ownerId,
        DataRetentionPlan plan,
        IReadOnlyDictionary<string, DateTimeOffset> frozenCutoffs,
        int nextCandidateIndex,
        int expectedCheckpointVersion,
        RetentionMutationJournal? pendingJournal,
        CancellationToken cancellationToken)
    {

        const int checkpointFormatVersion = 2;

        byte[] payload = SerializePruneCheckpoint(
            plan.PlanId,
            nextCandidateIndex,
            plan.CandidateIds,
            plan.GeneratedAt,
            frozenCutoffs,
            pendingJournal);

        if (payload.Length > 5 * 1024 * 1024)
        {

            throw new InvalidOperationException(
                "The retention checkpoint exceeds its bounded payload size.");

        }

        bool saved = await operations.SaveCheckpointAsync(
            operationId,
            ownerId,
            expectedCheckpointVersion,
            checkpointFormatVersion,
            payload,
            checkpointReference: "retention-prune:" + operationId.ToString("N"),
            $"Retention prune checkpoint {nextCandidateIndex}/{plan.CandidateIds.Length}.",
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (!saved)
        {

            throw new InvalidOperationException(
                "The retention operation checkpoint could not be saved atomically.");

        }

        return checkpointFormatVersion;

    }

    private static byte[] SerializePruneCheckpoint(
        string planId,
        int nextCandidateIndex,
        string[] candidates,
        DateTimeOffset generatedAt,
        IReadOnlyDictionary<string, DateTimeOffset> frozenCutoffs,
        RetentionMutationJournal? pendingJournal)
    {

        StringBuilder builder = new();

        builder.Append("ARCADATA2\n");

        builder.Append(planId).Append('\n');

        builder.Append(nextCandidateIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');

        builder.Append("G:")
            .Append(Convert.ToBase64String(
                Encoding.UTF8.GetBytes(FormatTimestamp(generatedAt))))
            .Append('\n');

        if (pendingJournal is not null)
        {

            builder.Append("P:")
                .Append(Convert.ToBase64String(
                    SerializeMutationJournal(pendingJournal)))
                .Append('\n');

        }

        foreach (string candidate in candidates)
        {

            if (!frozenCutoffs.TryGetValue(candidate, out DateTimeOffset cutoff))
            {

                throw new InvalidOperationException(
                    "The retention checkpoint is missing a frozen candidate cutoff.");

            }

            builder.Append("C:")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate)))
                .Append(':')
                .Append(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(FormatTimestamp(cutoff))))
                .Append('\n');

        }

        return Encoding.UTF8.GetBytes(builder.ToString());

    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverPruneAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        if (!string.Equals(
                operation.Kind,
                LongRunningOperationKinds.DataRetentionPrune,
                StringComparison.Ordinal))
        {

            throw new InvalidDataException("The operation is not a retention prune.");

        }

        if (string.IsNullOrWhiteSpace(operation.LeaseOwner))
        {

            throw new InvalidDataException("The recovered retention operation has no lease owner.");

        }

        DataRetentionPlan plan;

        int nextIndex;

        IReadOnlyDictionary<string, DateTimeOffset>? frozenCutoffs;

        RetentionMutationJournal? pendingJournal = null;

        if (operation.CheckpointPayload is null)
        {

            plan = await BuildUnifiedPrunePlanAsync(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                cancellationToken).ConfigureAwait(false);

            nextIndex = 0;

            frozenCutoffs = null;

        }
        else
        {

            (string planId,
                int checkpointIndex,
                string[] checkpointCandidates,
                DateTimeOffset? checkpointGeneratedAt,
                IReadOnlyDictionary<string, DateTimeOffset> checkpointCutoffs,
                RetentionMutationJournal? checkpointPendingJournal) =
                ParsePruneCheckpoint(operation.CheckpointPayload);

            pendingJournal = checkpointPendingJournal;

            if (pendingJournal is not null)
            {

                // The journal target is the candidate that was mid-flight, which is at or after the
                // cursor rather than exactly on it: the cursor is held back at the earliest
                // preserved candidate so recovery re-evaluates that protection, while apply keeps
                // working through the candidates that follow it.
                if (!string.Equals(
                        pendingJournal.Subtype,
                        "prune-candidate",
                        StringComparison.Ordinal)
                    || checkpointIndex >= checkpointCandidates.Length
                    || !checkpointCandidates
                        .Skip(checkpointIndex)
                        .Contains(pendingJournal.Target, StringComparer.Ordinal))
                {

                    return LongRunningOperationRecoveryResult.RequiresAttention(
                        ErrorCodes.Data.ReconciliationFailed);

                }

                bool targetExists = await CandidateStillExistsAsync(
                    pendingJournal.Target,
                    cancellationToken).ConfigureAwait(false);

                LongRunningOperationRecoveryResult journalRecovery =
                    await RecoverPrunePendingJournalAsync(
                        operation.Id,
                        pendingJournal,
                        targetExists,
                        cancellationToken).ConfigureAwait(false);

                if (journalRecovery.State
                    == LongRunningOperationState.ReconciliationRequired)
                {

                    return journalRecovery;

                }

            }

            DataRetentionPlan current = await BuildUnifiedPrunePlanAsync(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                cancellationToken).ConfigureAwait(false);

            HashSet<string> remaining = checkpointCandidates
                .Skip(checkpointIndex)
                .ToHashSet(StringComparer.Ordinal);

            string[] candidates =
                [.. current.CandidateIds.Where(remaining.Contains)];

            plan = new DataRetentionPlan(
                planId,
                new DataRetentionRequest(DataRetentionOperation.Prune),
                checkpointGeneratedAt ?? operation.CreatedAt,
                current.Items,
                current.Blockers,
                current.Conflicts,
                current.Rows,
                current.Files,
                current.EstimatedBytes,
                current.DerivedRecords,
                candidates,
                RequiresConfirmation: true);

            nextIndex = 0;

            frozenCutoffs = checkpointCutoffs.Count == 0
                ? null
                : checkpointCutoffs
                    .Where(pair => candidates.Contains(pair.Key, StringComparer.Ordinal))
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal);

        }

        _ = await ApplyUnifiedPruneAsync(
            operation.Id,
            operation.LeaseOwner,
            plan,
            nextIndex,
            operation.CheckpointVersion,
            saveCheckpoints: true,
            frozenCutoffs,
            cancellationToken).ConfigureAwait(false);

        return LongRunningOperationRecoveryResult.Completed();

    }

    private Task<LongRunningOperationRecoveryResult> RecoverPrunePendingJournalAsync(
        Guid operationId,
        RetentionMutationJournal journal,
        bool targetExists,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<RetentionMutationJournalEntry, IdentityOwnedFileSystemQuarantine>
            quarantines;

        try
        {

            quarantines = DiscoverMutationQuarantines(
                operationId,
                journal,
                targetExists);

        }
        catch (InvalidDataException)
        {

            return Task.FromResult(
                LongRunningOperationRecoveryResult.RequiresAttention(
                    ErrorCodes.Data.ReconciliationFailed));

        }

        foreach (RetentionMutationJournalEntry entry in journal.Entries)
        {

            string root = RootForRole(entry.RootRole);

            string originalPath = Path.GetFullPath(
                Path.Combine(root, entry.RelativePath));

            if (quarantines.TryGetValue(entry, out IdentityOwnedFileSystemQuarantine quarantine))
            {

                bool recovered = targetExists
                    ? IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine)
                    : IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine);

                if (!recovered)
                {

                    return Task.FromResult(
                        LongRunningOperationRecoveryResult.RequiresAttention(
                            ErrorCodes.Data.ReconciliationFailed));

                }

            }

            bool originalExists = IdentityOwnedFileSystemCleanup.TryCapturePath(
                originalPath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact original);

            if (targetExists && (!originalExists || original.Metadata != entry.Metadata)
                || !targetExists && originalExists && original.Metadata == entry.Metadata)
            {

                return Task.FromResult(
                    LongRunningOperationRecoveryResult.RequiresAttention(
                        ErrorCodes.Data.ReconciliationFailed));

            }

        }

        return Task.FromResult(
            targetExists
                ? LongRunningOperationRecoveryResult.Failed(
                    ErrorCodes.Data.ReconciliationFailed)
                : LongRunningOperationRecoveryResult.Completed());

    }

    private static (
        string PlanId,
        int NextIndex,
        string[] Candidates,
        DateTimeOffset? GeneratedAt,
        IReadOnlyDictionary<string, DateTimeOffset> FrozenCutoffs,
        RetentionMutationJournal? PendingJournal) ParsePruneCheckpoint(
        byte[] payload)
    {

        if (payload.Length > 5 * 1024 * 1024)
        {

            throw new InvalidDataException("The retention checkpoint exceeds its bounded payload size.");

        }

        string[] lines = Encoding.UTF8
            .GetString(payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        bool legacy = lines.Length >= 3
            && string.Equals(lines[0], "ARCADATA1", StringComparison.Ordinal);

        if (legacy)
        {

            throw new InvalidDataException(
                "Legacy retention checkpoints cannot prove their original age boundary.");

        }

        bool current = lines.Length >= 4
            && string.Equals(lines[0], "ARCADATA2", StringComparison.Ordinal);

        if (!current
            || lines[1].Length != 64
            || !lines[1].All(Uri.IsHexDigit)
            || !int.TryParse(
                lines[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int nextIndex))
        {

            throw new InvalidDataException("The retention checkpoint header is invalid.");

        }

        DateTimeOffset? generatedAt = null;

        int candidateStart = 3;

        RetentionMutationJournal? pendingJournal = null;

        if (current)
        {

            if (!lines[3].StartsWith("G:", StringComparison.Ordinal))
            {

                throw new InvalidDataException(
                    "The retention checkpoint is missing its frozen generation time.");

            }

            try
            {

                generatedAt = DateTimeOffset.ParseExact(
                    Encoding.UTF8.GetString(
                        Convert.FromBase64String(lines[3][2..])),
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);

            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {

                throw new InvalidDataException(
                    "The retention checkpoint generation time is invalid.",
                    ex);

            }

            candidateStart = 4;

            if (lines.Length > candidateStart
                && lines[candidateStart].StartsWith("P:", StringComparison.Ordinal))
            {

                try
                {

                    pendingJournal = ParseMutationJournal(
                        Convert.FromBase64String(lines[candidateStart][2..]));

                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {

                    throw new InvalidDataException(
                        "The retention checkpoint pending mutation journal is invalid.",
                        ex);

                }

                candidateStart++;

            }

        }

        if (lines.Length - candidateStart > 10_000)
        {

            throw new InvalidDataException("The retention checkpoint exceeds the maximum sweep size.");

        }

        List<string> candidates = [];

        Dictionary<string, DateTimeOffset> frozenCutoffs =
            new(StringComparer.Ordinal);

        try
        {

            for (int index = candidateStart; index < lines.Length; index++)
            {

                string candidate;

                if (current)
                {

                    string[] parts = lines[index].Split(':');

                    if (parts.Length != 3
                        || !string.Equals(parts[0], "C", StringComparison.Ordinal))
                    {

                        throw new InvalidDataException(
                            "The retention checkpoint candidate boundary is invalid.");

                    }

                    candidate = Encoding.UTF8.GetString(
                        Convert.FromBase64String(parts[1]));

                    frozenCutoffs.Add(
                        candidate,
                        DateTimeOffset.ParseExact(
                            Encoding.UTF8.GetString(
                                Convert.FromBase64String(parts[2])),
                            "o",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind));

                }
                else
                {

                    candidate = Encoding.UTF8.GetString(
                        Convert.FromBase64String(lines[index]));

                }

                if (!IsValidCheckpointCandidate(candidate))
                {

                    throw new InvalidDataException(
                        "The retention checkpoint contains an unknown or malformed candidate.");

                }

                candidates.Add(candidate);

            }

        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {

            throw new InvalidDataException("The retention checkpoint candidate list is invalid.", ex);

        }

        if (nextIndex < 0 || nextIndex > candidates.Count)
        {

            throw new InvalidDataException("The retention checkpoint cursor is outside its candidate list.");

        }

        if (candidates.Count != candidates.Distinct(StringComparer.Ordinal).Count())
        {

            throw new InvalidDataException("The retention checkpoint contains duplicate candidates.");

        }

        if (current && frozenCutoffs.Count != candidates.Count)
        {

            throw new InvalidDataException(
                "The retention checkpoint does not define every frozen candidate cutoff.");

        }

        return (
            lines[1],
            nextIndex,
            [.. candidates],
            generatedAt,
            frozenCutoffs,
            pendingJournal);

    }

    private static bool IsValidCheckpointCandidate(string candidate)
    {

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 2_048)
        {

            return false;

        }

        string[] guidPrefixes =
        [
            BatchCandidatePrefix,
            FileCandidatePrefix,
            "session:",
            EntryCandidatePrefix,
            AttachmentCandidatePrefix,
            EntryEmbeddingCandidatePrefix,
            IdempotencyClaimCandidatePrefix,
            AccountingCandidatePrefix,
            AccountingAdjustmentCandidatePrefix,
            BudgetAlertCandidatePrefix,
            OperationCandidatePrefix,
            SanctumCandidatePrefix,
        ];

        foreach (string prefix in guidPrefixes)
        {

            if (candidate.StartsWith(prefix, StringComparison.Ordinal))
            {

                return Guid.TryParse(candidate[prefix.Length..], out _);

            }

        }

        if (candidate.StartsWith(AuditLogCandidatePrefix, StringComparison.Ordinal))
        {

            return IsDatedLogFile(
                candidate[AuditLogCandidatePrefix.Length..],
                "audit-");

        }

        if (candidate.StartsWith(GuardrailLogCandidatePrefix, StringComparison.Ordinal))
        {

            return IsDatedLogFile(
                candidate[GuardrailLogCandidatePrefix.Length..],
                "guardrails-");

        }

        return HasBoundedValue(candidate, WorkspaceCandidatePrefix)
            || HasBoundedValue(candidate, SagaCandidatePrefix)
            || HasBoundedValue(candidate, LexiconCandidatePrefix)
            || HasBoundedValue(candidate, IdempotencyKeyCandidatePrefix)
            || HasBoundedValue(candidate, DaemonCandidatePrefix);

    }

    private static bool HasBoundedValue(string candidate, string prefix) =>
        candidate.StartsWith(prefix, StringComparison.Ordinal)
        && candidate.Length > prefix.Length;

    private static bool IsDatedLogFile(string fileName, string stem)
    {

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || !fileName.StartsWith(stem, StringComparison.Ordinal)
            || !fileName.EndsWith(".jsonl", StringComparison.Ordinal))
        {

            return false;

        }

        string date = fileName[stem.Length..^6];

        return date.Length == 8 && date.All(char.IsAsciiDigit);

    }

    private async Task<CandidateDeleteResult> ApplyCandidateAsync(
        Guid operationId,
        DataRetentionPlan plan,
        string candidate,
        DateTimeOffset effectiveCutoff,
        RetentionMutationJournal? mutationJournal,
        CancellationToken cancellationToken)
    {

        if (!await CandidateStillMeetsFrozenAgeAsync(
                candidate,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false))
        {

            return CandidateDeleteResult.Empty;

        }

        if (TryReadGuidCandidate(candidate, BatchCandidatePrefix, out Guid batchId))
        {

            return await DeleteBatchCandidateAsync(
                batchId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, FileCandidatePrefix, out Guid fileId))
        {

            return await DeleteUploadedFileCandidateAsync(
                operationId,
                fileId,
                effectiveCutoff,
                mutationJournal,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, "session:", out Guid sessionId))
        {

            DataRetentionPlan current = await BuildDeleteSessionPlanAsync(
                new DataRetentionRequest(DataRetentionOperation.DeleteSession, sessionId),
                sessionId,
                cancellationToken).ConfigureAwait(false);

            if (current.Blockers.Length > 0 || current.Conflicts.Length > 0)
            {

                return CandidateDeleteResult.Empty;

            }

            DataRetentionApplyResult result = await DeleteSessionAsync(
                operationId,
                current,
                sessionId,
                expectedSnapshot: null,
                ageCutoff: effectiveCutoff,
                mutationJournal,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return CandidateDeleteResult.From(result);

        }

        if (TryReadGuidCandidate(candidate, EntryCandidatePrefix, out Guid entryId))
        {

            return await DeleteEntryCandidateAsync(
                entryId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, AttachmentCandidatePrefix, out Guid attachmentId))
        {

            DataRetentionPlan current = await BuildDeleteAttachmentPlanAsync(
                new DataRetentionRequest(DataRetentionOperation.DeleteAttachment, attachmentId),
                attachmentId,
                cancellationToken).ConfigureAwait(false);

            if (current.Blockers.Length > 0 || current.Conflicts.Length > 0)
            {

                return CandidateDeleteResult.Empty;

            }

            DataRetentionApplyResult result = await DeleteAttachmentAsync(
                operationId,
                current,
                attachmentId,
                expectedSnapshot: null,
                ageCutoff: effectiveCutoff,
                mutationJournal,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return CandidateDeleteResult.From(result);

        }

        if (candidate.StartsWith(EntryEmbeddingCandidatePrefix, StringComparison.Ordinal))
        {

            return await DeleteEntryEmbeddingCandidateAsync(
                candidate[EntryEmbeddingCandidatePrefix.Length..],
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (candidate.StartsWith(WorkspaceCandidatePrefix, StringComparison.Ordinal))
        {

            return await DeleteWorkspaceCandidateAsync(
                candidate[WorkspaceCandidatePrefix.Length..],
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (candidate.StartsWith(SagaCandidatePrefix, StringComparison.Ordinal))
        {

            return await DeleteSagaCandidateAsync(
                candidate[SagaCandidatePrefix.Length..],
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (candidate.StartsWith(LexiconCandidatePrefix, StringComparison.Ordinal))
        {

            return await DeleteLexiconCandidateAsync(
                candidate[LexiconCandidatePrefix.Length..],
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (candidate.StartsWith(AuditLogCandidatePrefix, StringComparison.Ordinal))
        {

            return DeleteLogCandidate(
                candidate[AuditLogCandidatePrefix.Length..],
                CurrentRetention.AuditLogs,
                effectiveCutoff,
                operationId,
                mutationJournal);

        }

        if (candidate.StartsWith(GuardrailLogCandidatePrefix, StringComparison.Ordinal))
        {

            return DeleteLogCandidate(
                candidate[GuardrailLogCandidatePrefix.Length..],
                CurrentRetention.GuardrailLogs,
                effectiveCutoff,
                operationId,
                mutationJournal);

        }

        if (TryReadGuidCandidate(candidate, IdempotencyClaimCandidatePrefix, out Guid claimId))
        {

            return await DeleteIdempotencyClaimCandidateAsync(
                claimId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (candidate.StartsWith(IdempotencyKeyCandidatePrefix, StringComparison.Ordinal))
        {

            string key = candidate[IdempotencyKeyCandidatePrefix.Length..];

            int rows = await ExecuteStandaloneAsync(
                """
                DELETE FROM IdempotencyKeys
                WHERE KeyHash = @id
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", key),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            bool reconciled = await CountTableAsync(
                "IdempotencyKeys",
                "KeyHash = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", key),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

            return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

        }

        if (TryReadGuidCandidate(candidate, AccountingCandidatePrefix, out Guid runId))
        {

            return await DeleteAccountingCandidateAsync(
                runId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(
                candidate,
                AccountingAdjustmentCandidatePrefix,
                out Guid adjustmentId))
        {

            return await DeleteStandaloneCostAdjustmentCandidateAsync(
                adjustmentId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, BudgetAlertCandidatePrefix, out Guid alertId))
        {

            return await DeleteBudgetAlertCandidateAsync(
                alertId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, OperationCandidatePrefix, out Guid historyId))
        {

            int rows = await ExecuteStandaloneAsync(
                $"""
                DELETE FROM LongRunningOperations
                WHERE lower(replace(Id, '-', '')) = @id
                  AND State IN ({(int)LongRunningOperationState.Completed},
                                {(int)LongRunningOperationState.Failed},
                                {(int)LongRunningOperationState.Abandoned})
                  AND NOT EXISTS (
                      SELECT 1 FROM LongRunningOperations child
                      WHERE child.ParentOperationId = LongRunningOperations.Id
                         OR child.RootOperationId = LongRunningOperations.Id)
                  AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", historyId.ToString("N")),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            bool reconciled = await CountTableAsync(
                "LongRunningOperations",
                $"""
                lower(replace(Id, '-', '')) = @id
                AND State IN ({(int)LongRunningOperationState.Completed},
                              {(int)LongRunningOperationState.Failed},
                              {(int)LongRunningOperationState.Abandoned})
                AND NOT EXISTS (
                    SELECT 1 FROM LongRunningOperations child
                    WHERE child.ParentOperationId = LongRunningOperations.Id
                       OR child.RootOperationId = LongRunningOperations.Id)
                AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", historyId.ToString("N")),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

            return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

        }

        if (TryReadGuidCandidate(candidate, SanctumCandidatePrefix, out Guid breachId))
        {

            int rows = await ExecuteStandaloneAsync(
                """
                DELETE FROM SanctumBreaches
                WHERE lower(replace(Id, '-', '')) = @id
                  AND julianday(OccurredAt) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", breachId.ToString("N")),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            bool reconciled = await CountTableAsync(
                "SanctumBreaches",
                "lower(replace(Id, '-', '')) = @id AND julianday(OccurredAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", breachId.ToString("N")),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

            return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

        }

        if (candidate.StartsWith(DaemonCandidatePrefix, StringComparison.Ordinal)
            && daemonExecutions is not null)
        {

            string executionId = candidate[DaemonCandidatePrefix.Length..];

            bool deleted = await daemonExecutions.TryDeleteTerminalBeforeAsync(
                executionId,
                effectiveCutoff,
                cancellationToken).ConfigureAwait(false);

            DaemonExecutionDetail? remaining = await daemonExecutions.GetAsync(
                executionId,
                cancellationToken).ConfigureAwait(false);

            bool reconciled = remaining is null
                || remaining.Status is not (
                    DaemonJobStatus.Completed
                    or DaemonJobStatus.Failed
                    or DaemonJobStatus.Cancelled)
                || remaining.CompletedAt is DateTimeOffset completedAt
                    && completedAt > effectiveCutoff;

            return new CandidateDeleteResult(
                deleted ? 1 : 0,
                0,
                0,
                0,
                reconciled);

        }

        logger.LogWarning(
            "Ignored unknown retention candidate {Candidate} from plan {PlanId}.",
            candidate,
            plan.PlanId);

        return CandidateDeleteResult.Empty;

    }

    private async Task<bool> CandidateStillMeetsFrozenAgeAsync(
        string candidate,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        RetentionSettings retention = CurrentRetention;

        if (TryReadGuidCandidate(candidate, BatchCandidatePrefix, out Guid batchId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.CompletedBatches,
                effectiveCutoff,
                "Batches",
                "lower(replace(Id, '-', '')) = @id AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", batchId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, FileCandidatePrefix, out Guid fileId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.UploadedFiles,
                effectiveCutoff,
                "UploadedFiles",
                "lower(replace(Id, '-', '')) = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", fileId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, "session:", out Guid sessionId))
        {

            DbConnection connection = await OpenConnectionAsync(
                cancellationToken).ConfigureAwait(false);

            string? status = await ScalarStringAsync(
                connection,
                "SELECT lower(Status) FROM Sessions WHERE lower(replace(Id, '-', '')) = @id LIMIT 1",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

            RetentionRuleSettings? rule = status switch
            {

                "active" => retention.ActiveSessions,

                "archived" => retention.ArchivedSessions,

                _ => null,

            };

            return rule is not null
                && await IsDatabaseCandidateOldEnoughAsync(
                    rule,
                    effectiveCutoff,
                    "Sessions",
                    "lower(replace(Id, '-', '')) = @id AND julianday(UpdatedAt) <= julianday(@cutoff)",
                    cancellationToken,
                    ("@id", sessionId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, EntryCandidatePrefix, out Guid entryId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.Entries,
                effectiveCutoff,
                "Entries",
                "lower(replace(Id, '-', '')) = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", entryId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(
                candidate,
                AttachmentCandidatePrefix,
                out Guid attachmentId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.Attachments,
                effectiveCutoff,
                "SessionAttachments",
                "lower(replace(Id, '-', '')) = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", attachmentId.ToString("N"))).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, EntryEmbeddingCandidatePrefix))
        {

            string id = candidate[EntryEmbeddingCandidatePrefix.Length..]
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            return await IsSqlCandidateOldEnoughAsync(
                retention.SessionEntryEmbeddings,
                effectiveCutoff,
                """
                SELECT COUNT(*)
                FROM Entries
                WHERE lower(replace(Id, '-', '')) = @id
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, WorkspaceCandidatePrefix))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.WorkspaceIndexes,
                effectiveCutoff,
                "workspace_file_chunks",
                "ChunkId = @id AND julianday(IndexedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", candidate[WorkspaceCandidatePrefix.Length..])).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, SagaCandidatePrefix))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.SagaMemories,
                effectiveCutoff,
                "saga_memories",
                "Id = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", candidate[SagaCandidatePrefix.Length..])).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, LexiconCandidatePrefix))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.LexiconEntries,
                effectiveCutoff,
                "lexicon_entries",
                "Id = @id AND julianday(UpdatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", candidate[LexiconCandidatePrefix.Length..])).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, AuditLogCandidatePrefix))
        {

            return IsLogCandidateOldEnough(
                retention.AuditLogs,
                effectiveCutoff,
                candidate[AuditLogCandidatePrefix.Length..],
                "audit-");

        }

        if (HasBoundedValue(candidate, GuardrailLogCandidatePrefix))
        {

            return IsLogCandidateOldEnough(
                retention.GuardrailLogs,
                effectiveCutoff,
                candidate[GuardrailLogCandidatePrefix.Length..],
                "guardrails-");

        }

        if (TryReadGuidCandidate(
                candidate,
                IdempotencyClaimCandidatePrefix,
                out Guid claimId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.IdempotencyClaims,
                effectiveCutoff,
                "IdempotencyClaims",
                "lower(replace(Id, '-', '')) = @id AND julianday(UpdatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", claimId.ToString("N"))).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, IdempotencyKeyCandidatePrefix))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.IdempotencyClaims,
                effectiveCutoff,
                "IdempotencyKeys",
                "KeyHash = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", candidate[IdempotencyKeyCandidatePrefix.Length..])).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, AccountingCandidatePrefix, out Guid runId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.Accounting,
                effectiveCutoff,
                "InferenceRuns",
                "lower(replace(Id, '-', '')) = @id AND julianday(COALESCE(CompletedAt, StartedAt)) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", runId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(
                candidate,
                AccountingAdjustmentCandidatePrefix,
                out Guid adjustmentId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.Accounting,
                effectiveCutoff,
                "CostAdjustments",
                "lower(replace(Id, '-', '')) = @id AND julianday(CreatedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", adjustmentId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, BudgetAlertCandidatePrefix, out Guid alertId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.Accounting,
                effectiveCutoff,
                "BudgetAlerts",
                "lower(replace(Id, '-', '')) = @id AND julianday(AlertedAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", alertId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, OperationCandidatePrefix, out Guid historyId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.LongRunningOperations,
                effectiveCutoff,
                "LongRunningOperations",
                "lower(replace(Id, '-', '')) = @id AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", historyId.ToString("N"))).ConfigureAwait(false);

        }

        if (TryReadGuidCandidate(candidate, SanctumCandidatePrefix, out Guid breachId))
        {

            return await IsDatabaseCandidateOldEnoughAsync(
                retention.SanctumBreaches,
                effectiveCutoff,
                "SanctumBreaches",
                "lower(replace(Id, '-', '')) = @id AND julianday(OccurredAt) <= julianday(@cutoff)",
                cancellationToken,
                ("@id", breachId.ToString("N"))).ConfigureAwait(false);

        }

        if (HasBoundedValue(candidate, DaemonCandidatePrefix)
            && daemonExecutions is not null
            && retention.DaemonHistory.Enabled)
        {

            var execution = await daemonExecutions.GetAsync(
                candidate[DaemonCandidatePrefix.Length..],
                cancellationToken).ConfigureAwait(false);

            return execution is not null
                && (execution.Status is
                    DaemonJobStatus.Completed
                    or DaemonJobStatus.Failed
                    or DaemonJobStatus.Cancelled)
                && execution.CompletedAt is DateTimeOffset completedAt
                && completedAt <= effectiveCutoff;

        }

        return false;

    }

    private async Task<bool> IsDatabaseCandidateOldEnoughAsync(
        RetentionRuleSettings rule,
        DateTimeOffset cutoff,
        string table,
        string predicate,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        if (!rule.Enabled)
        {

            return false;

        }

        (string Name, object Value)[] boundedParameters =
        [
            .. parameters,
            ("@cutoff", FormatTimestamp(cutoff)),
        ];

        return await CountTableAsync(
            table,
            predicate,
            cancellationToken,
            boundedParameters).ConfigureAwait(false) > 0;

    }

    private async Task<bool> IsSqlCandidateOldEnoughAsync(
        RetentionRuleSettings rule,
        DateTimeOffset cutoff,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        if (!rule.Enabled)
        {

            return false;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Add(command, name, value);

        }

        Add(
            command,
            "@cutoff",
            FormatTimestamp(cutoff));

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;

    }

    private bool IsLogCandidateOldEnough(
        RetentionRuleSettings rule,
        DateTimeOffset cutoff,
        string fileName,
        string stem)
    {

        if (!rule.Enabled || !IsDatedLogFile(fileName, stem))
        {

            return false;

        }

        string path = Path.GetFullPath(Path.Combine(_logsRoot, fileName));

        return WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _logsRoot,
                path,
                out _)
            && File.Exists(path)
            && File.GetLastWriteTimeUtc(path)
                <= cutoff.UtcDateTime;

    }

    private static DateTimeOffset FrozenCutoff(
        DateTimeOffset generatedAt,
        int days) =>
        generatedAt.AddDays(-ArcanumSettingClamps.RetentionRuleDays(days));

    private static bool TryReadGuidCandidate(
        string candidate,
        string prefix,
        out Guid id)
    {

        id = Guid.Empty;

        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(candidate[prefix.Length..], out id);

    }

    private async Task<bool> CandidateStillExistsAsync(
        string candidate,
        CancellationToken cancellationToken)
    {

        if (TryReadGuidCandidate(candidate, BatchCandidatePrefix, out Guid batchId))
        {

            return await CountTableAsync(
                "Batches",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", batchId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, FileCandidatePrefix, out Guid fileId))
        {

            return await CountTableAsync(
                "UploadedFiles",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", fileId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, "session:", out Guid sessionId))
        {

            return await CountTableAsync(
                "Sessions",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", sessionId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, EntryCandidatePrefix, out Guid entryId))
        {

            return await CountTableAsync(
                "Entries",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", entryId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(
                candidate,
                AttachmentCandidatePrefix,
                out Guid attachmentId))
        {

            return await CountTableAsync(
                "SessionAttachments",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", attachmentId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, EntryEmbeddingCandidatePrefix))
        {

            string id = candidate[EntryEmbeddingCandidatePrefix.Length..]
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            long rows = await CountTableAsync(
                "entry_embeddings",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            rows += await CountTableAsync(
                "entry_embeddings_vec",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);

            return rows > 0;

        }

        if (HasBoundedValue(candidate, WorkspaceCandidatePrefix))
        {

            return await CountTableAsync(
                "workspace_file_chunks",
                "ChunkId = @id",
                cancellationToken,
                ("@id", candidate[WorkspaceCandidatePrefix.Length..])).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, SagaCandidatePrefix))
        {

            return await CountTableAsync(
                "saga_memories",
                "Id = @id",
                cancellationToken,
                ("@id", candidate[SagaCandidatePrefix.Length..])).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, LexiconCandidatePrefix))
        {

            return await CountTableAsync(
                "lexicon_entries",
                "Id = @id",
                cancellationToken,
                ("@id", candidate[LexiconCandidatePrefix.Length..])).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, AuditLogCandidatePrefix))
        {

            return OwnedFileExists(
                _logsRoot,
                candidate[AuditLogCandidatePrefix.Length..]);

        }

        if (HasBoundedValue(candidate, GuardrailLogCandidatePrefix))
        {

            return OwnedFileExists(
                _logsRoot,
                candidate[GuardrailLogCandidatePrefix.Length..]);

        }

        if (TryReadGuidCandidate(
                candidate,
                IdempotencyClaimCandidatePrefix,
                out Guid claimId))
        {

            return await CountTableAsync(
                "IdempotencyClaims",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", claimId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, IdempotencyKeyCandidatePrefix))
        {

            return await CountTableAsync(
                "IdempotencyKeys",
                "KeyHash = @id",
                cancellationToken,
                ("@id", candidate[IdempotencyKeyCandidatePrefix.Length..])).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, AccountingCandidatePrefix, out Guid runId))
        {

            return await CountTableAsync(
                "InferenceRuns",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", runId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(
                candidate,
                AccountingAdjustmentCandidatePrefix,
                out Guid adjustmentId))
        {

            return await CountTableAsync(
                "CostAdjustments",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", adjustmentId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, BudgetAlertCandidatePrefix, out Guid alertId))
        {

            return await CountTableAsync(
                "BudgetAlerts",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", alertId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, OperationCandidatePrefix, out Guid operationId))
        {

            return await CountTableAsync(
                "LongRunningOperations",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", operationId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (TryReadGuidCandidate(candidate, SanctumCandidatePrefix, out Guid breachId))
        {

            return await CountTableAsync(
                "SanctumBreaches",
                "lower(replace(Id, '-', '')) = @id",
                cancellationToken,
                ("@id", breachId.ToString("N"))).ConfigureAwait(false) > 0;

        }

        if (HasBoundedValue(candidate, DaemonCandidatePrefix)
            && daemonExecutions is not null)
        {

            return await daemonExecutions.GetAsync(
                candidate[DaemonCandidatePrefix.Length..],
                cancellationToken).ConfigureAwait(false) is not null;

        }

        return false;

    }

    private async Task<CandidateDeleteResult> DeleteBatchCandidateAsync(
        Guid batchId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        string? status = await ScalarStringAsync(
            connection,
            "SELECT Status FROM Batches WHERE lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", batchId.ToString("N"))).ConfigureAwait(false);

        if (status is null || !BatchStatuses.IsTerminal(status))
        {

            return CandidateDeleteResult.Empty;

        }

        int rows = await ExecuteStandaloneAsync(
            """
            DELETE FROM Batches
            WHERE lower(replace(Id, '-', '')) = @id
              AND Status = @status
              AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", batchId.ToString("N")),
            ("@status", status),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

        bool reconciled = await CountTableAsync(
            "Batches",
            """
            lower(replace(Id, '-', '')) = @id
            AND Status IN (@completed, @failed, @cancelled, @expired)
            AND julianday(COALESCE(CompletedAt, CreatedAt)) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", batchId.ToString("N")),
            ("@completed", BatchStatuses.Completed),
            ("@failed", BatchStatuses.Failed),
            ("@cancelled", BatchStatuses.Cancelled),
            ("@expired", BatchStatuses.Expired),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteUploadedFileCandidateAsync(
        Guid operationId,
        Guid fileId,
        DateTimeOffset effectiveCutoff,
        RetentionMutationJournal? mutationJournal,
        CancellationToken cancellationToken)
    {

        UploadedFileSnapshot? snapshot = await ReadUploadedFileAsync(
            fileId,
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {

            return CandidateDeleteResult.Empty;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        int rows;

        bool fileDeleted;

        long bytes;

        bool committed = false;

        List<IdentityOwnedFileSystemQuarantine> quarantinedFiles = [];

        try
        {

            rows = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM UploadedFiles
                WHERE lower(replace(Id, '-', '')) = @id
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Batches
                      WHERE lower(replace(InputFileId, '-', '')) = @id
                         OR lower(replace(OutputFileId, '-', '')) = @id
                         OR lower(replace(ErrorFileId, '-', '')) = @id)
                """,
                cancellationToken,
                ("@id", fileId.ToString("N")),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (rows == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            string relativePath = fileId.ToString("N");

            fileDeleted = TryQuarantineOwnedFile(
                _filesRoot,
                relativePath,
                operationId,
                mutationJournal,
                out IdentityOwnedFileSystemQuarantine quarantine,
                out bytes);

            if (fileDeleted)
            {

                quarantinedFiles.Add(quarantine);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

        }
        catch
        {

            if (!committed)
            {

                try
                {

                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                }
                finally
                {

                    RestoreQuarantinedFactoryFiles(quarantinedFiles);

                }

            }

            throw;

        }

        FinalizeOperationQuarantines(quarantinedFiles);

        string retainedRelativePath = fileId.ToString("N");

        bool reconciled = !OwnedFileExists(_filesRoot, retainedRelativePath);

        reconciled &= await CountTableAsync(
            "UploadedFiles",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", fileId.ToString("N"))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(
            rows,
            fileDeleted ? 1 : 0,
            bytes,
            0,
            reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteEntryCandidateAsync(
        Guid entryId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbCommand read = connection.CreateCommand();

        read.CommandText =
            "SELECT SessionId, IsPinned FROM Entries WHERE lower(replace(Id, '-', '')) = @id";

        Add(read, "@id", entryId.ToString("N"));

        Guid sessionId;

        await using (DbDataReader reader = await read
                         .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return CandidateDeleteResult.Empty;

            }

            sessionId = Guid.Parse(reader.GetString(0));

            if (Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0)
            {

                return CandidateDeleteResult.Empty;

            }

        }

        RetentionSettings retention = CurrentRetention;

        if (retention.ProtectedSessionIds.Contains(sessionId)
            || (await ReadSessionConflictsAsync(sessionId, cancellationToken).ConfigureAwait(false)).Length > 0)
        {

            return CandidateDeleteResult.Empty;

        }

        long contextPins = await CountTableAsync(
            "SessionContextPins",
            """
            Kind = @kind
            AND lower(replace(SessionId, '-', '')) = @sessionId
            AND lower(replace(TargetIdentifier, '-', '')) = @entryId
            """,
            cancellationToken,
            ("@kind", (int)SessionContextPinKind.SessionEntry),
            ("@sessionId", sessionId.ToString("N")),
            ("@entryId", entryId.ToString("N"))).ConfigureAwait(false);

        if (contextPins > 0)
        {

            return CandidateDeleteResult.Empty;

        }

        bool vectorTableExists = await TableExistsAsync(
            "entry_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        bool embeddingTableExists = await TableExistsAsync(
            "entry_embeddings",
            cancellationToken).ConfigureAwait(false);

        bool consultationTableExists = await TableExistsAsync(
            "attachment_memory_consultations",
            cancellationToken).ConfigureAwait(false);

        bool ftsTableExists = await TableExistsAsync(
            "Entries_fts",
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        int derived = 0;

        int rows;

        try
        {

            string? boundarySessionId = await ScalarStringInTransactionAsync(
                connection,
                transaction,
                """
                SELECT SessionId
                FROM Entries
                WHERE lower(replace(Id, '-', '')) = @id
                  AND IsPinned = 0
                LIMIT 1
                """,
                cancellationToken,
                ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            DataRetentionConflict[] boundaryConflicts =
                await ReadSessionConflictsInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    Guid.Empty,
                    cancellationToken).ConfigureAwait(false);

            string? boundaryPin = await ScalarStringInTransactionAsync(
                connection,
                transaction,
                """
                SELECT Id
                FROM SessionContextPins
                WHERE Kind = @kind
                  AND lower(replace(SessionId, '-', '')) = @sessionId
                  AND lower(replace(TargetIdentifier, '-', '')) = @entryId
                LIMIT 1
                """,
                cancellationToken,
                ("@kind", (int)SessionContextPinKind.SessionEntry),
                ("@sessionId", sessionId.ToString("N")),
                ("@entryId", entryId.ToString("N"))).ConfigureAwait(false);

            if (boundarySessionId is null
                || CurrentRetention.ProtectedSessionIds.Contains(sessionId)
                || boundaryPin is not null
                || boundaryConflicts.Length > 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            if (vectorTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM entry_embeddings_vec WHERE lower(replace(EntryId, '-', '')) = @id",
                    cancellationToken,
                    ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            }

            if (embeddingTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM entry_embeddings WHERE lower(replace(EntryId, '-', '')) = @id",
                    cancellationToken,
                    ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            }

            if (consultationTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM attachment_memory_consultations WHERE lower(replace(SourceEntryId, '-', '')) = @id",
                    cancellationToken,
                    ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            }

            if (ftsTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM Entries_fts WHERE lower(replace(Id, '-', '')) = @id",
                    cancellationToken,
                    ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            }

            _ = await ExecuteAsync(
                connection,
                transaction,
                "UPDATE SessionAttachments SET EntryId = NULL WHERE lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", entryId.ToString("N"))).ConfigureAwait(false);

            rows = await ExecuteAsync(
                connection,
                transaction,
                $"""
                DELETE FROM Entries
                WHERE lower(replace(Id, '-', '')) = @id
                  AND IsPinned = 0
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM SessionContextPins pin
                      WHERE pin.Kind = @kind
                        AND lower(replace(pin.SessionId, '-', '')) = @sessionId
                        AND lower(replace(pin.TargetIdentifier, '-', '')) = @id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM LongRunningOperations operation
                      WHERE lower(replace(operation.SessionId, '-', '')) = @sessionId
                        AND operation.State IN ({string.Join(",", ActiveOperationStates)}))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM InferenceRuns run
                      WHERE lower(replace(run.SessionId, '-', '')) = @sessionId
                        AND run.Status = @running)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM BudgetReservations reservation
                      JOIN InferenceRuns run
                        ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                      WHERE lower(replace(run.SessionId, '-', '')) = @sessionId
                        AND reservation.Status = @reserved)
                """,
                cancellationToken,
                ("@id", entryId.ToString("N")),
                ("@kind", (int)SessionContextPinKind.SessionEntry),
                ("@sessionId", sessionId.ToString("N")),
                ("@running", (int)InferenceRunStatus.Running),
                ("@reserved", (int)BudgetReservationStatus.Reserved),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (rows == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            _ = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE Sessions
                SET Summary = NULL,
                    LastSummarizedMessageAt = NULL,
                    UnsummarizedEntryCount = (
                        SELECT COUNT(*)
                        FROM Entries
                        WHERE lower(replace(SessionId, '-', '')) = @sessionId)
                WHERE lower(replace(Id, '-', '')) = @sessionId
                """,
                cancellationToken,
                ("@sessionId", sessionId.ToString("N"))).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "Entries",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", entryId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "entry_embeddings",
            "lower(replace(EntryId, '-', '')) = @id",
            cancellationToken,
            ("@id", entryId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "entry_embeddings_vec",
            "lower(replace(EntryId, '-', '')) = @id",
            cancellationToken,
            ("@id", entryId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "Entries_fts",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", entryId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "attachment_memory_consultations",
            "lower(replace(SourceEntryId, '-', '')) = @id",
            cancellationToken,
            ("@id", entryId.ToString("N"))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(rows, 0, 0, derived, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteEntryEmbeddingCandidateAsync(
        string entryId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        string normalizedEntryId = Guid.TryParse(entryId, out Guid parsedEntryId)
            ? parsedEntryId.ToString("N")
            : entryId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        bool vectorTableExists = await TableExistsAsync(
            "entry_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        bool embeddingTableExists = await TableExistsAsync(
            "entry_embeddings",
            cancellationToken).ConfigureAwait(false);

        if (!embeddingTableExists)
        {

            return CandidateDeleteResult.Empty;

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        int deleted = 0;

        try
        {

            await using DbCommand protection = connection.CreateCommand();

            protection.Transaction = transaction;

            protection.CommandText =
                $"""
                SELECT entry.SessionId,
                       entry.IsPinned,
                       EXISTS(
                           SELECT 1
                           FROM SessionContextPins pin
                           WHERE pin.Kind = @kind
                             AND lower(replace(pin.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                             AND lower(replace(pin.TargetIdentifier, '-', '')) = lower(replace(entry.Id, '-', ''))),
                       EXISTS(
                           SELECT 1
                           FROM LongRunningOperations operation
                           WHERE lower(replace(operation.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                             AND operation.State IN ({string.Join(",", ActiveOperationStates)})),
                       EXISTS(
                           SELECT 1
                           FROM InferenceRuns run
                           WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                             AND run.Status = @running),
                       EXISTS(
                           SELECT 1
                           FROM BudgetReservations reservation
                           JOIN InferenceRuns run
                             ON lower(replace(run.Id, '-', '')) = lower(replace(reservation.RunId, '-', ''))
                           WHERE lower(replace(run.SessionId, '-', '')) = lower(replace(entry.SessionId, '-', ''))
                             AND reservation.Status = @reserved)
                FROM Entries entry
                WHERE lower(replace(entry.Id, '-', '')) = @id
                  AND julianday(entry.CreatedAt) <= julianday(@cutoff)
                """;

            Add(protection, "@kind", (int)SessionContextPinKind.SessionEntry);

            Add(protection, "@running", (int)InferenceRunStatus.Running);

            Add(protection, "@reserved", (int)BudgetReservationStatus.Reserved);

            Add(protection, "@id", normalizedEntryId);

            Add(
                protection,
                "@cutoff",
                FormatTimestamp(effectiveCutoff));

            Guid sessionId = Guid.Empty;

            bool protectedByDatabase = false;

            bool entryExists;

            await using (DbDataReader reader = await protection
                             .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {

                entryExists = await reader
                    .ReadAsync(cancellationToken).ConfigureAwait(false);

                if (entryExists)
                {

                    sessionId = Guid.Parse(reader.GetString(0));

                    protectedByDatabase = reader.GetInt64(1) != 0
                        || reader.GetInt64(2) != 0
                        || reader.GetInt64(3) != 0
                        || reader.GetInt64(4) != 0
                        || reader.GetInt64(5) != 0;

                }

            }

            if (!entryExists
                || protectedByDatabase
                || CurrentRetention.ProtectedSessionIds.Contains(sessionId))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            if (vectorTableExists)
            {

                deleted += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM entry_embeddings_vec WHERE lower(replace(EntryId, '-', '')) = @id",
                    cancellationToken,
                    ("@id", normalizedEntryId)).ConfigureAwait(false);

            }

            deleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM entry_embeddings WHERE lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", normalizedEntryId)).ConfigureAwait(false);

            string? stillAged = await ScalarStringInTransactionAsync(
                connection,
                transaction,
                """
                SELECT Id
                FROM Entries
                WHERE lower(replace(Id, '-', '')) = @id
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                LIMIT 1
                """,
                cancellationToken,
                ("@id", normalizedEntryId),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (stillAged is null)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "entry_embeddings",
            "lower(replace(EntryId, '-', '')) = @id",
            cancellationToken,
            ("@id", normalizedEntryId)).ConfigureAwait(false) == 0;

        if (vectorTableExists)
        {

            reconciled &= await CountTableAsync(
                "entry_embeddings_vec",
                "lower(replace(EntryId, '-', '')) = @id",
                cancellationToken,
                ("@id", normalizedEntryId)).ConfigureAwait(false) == 0;

        }

        return new CandidateDeleteResult(0, 0, 0, deleted, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteWorkspaceCandidateAsync(
        string chunkId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        bool vectorTableExists = await TableExistsAsync(
            "workspace_file_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        bool embeddingTableExists = await TableExistsAsync(
            "workspace_file_embeddings",
            cancellationToken).ConfigureAwait(false);

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        int derived = 0;

        int rows;

        try
        {

            await using DbCommand protection = connection.CreateCommand();

            protection.Transaction = transaction;

            protection.CommandText =
                $"""
                SELECT EXISTS(
                    SELECT 1
                    FROM LongRunningOperations
                    WHERE Kind = @kind
                      AND State IN ({string.Join(",", ActiveOperationStates)}))
                """;

            Add(protection, "@kind", LongRunningOperationKinds.WorkspaceIndex);

            bool active = Convert.ToInt64(
                await protection.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0;

            if (active)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            string? agedChunk = await ScalarStringInTransactionAsync(
                connection,
                transaction,
                """
                SELECT ChunkId
                FROM workspace_file_chunks
                WHERE ChunkId = @id
                  AND julianday(IndexedAt) <= julianday(@cutoff)
                LIMIT 1
                """,
                cancellationToken,
                ("@id", chunkId),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (agedChunk is null)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            if (vectorTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM workspace_file_embeddings_vec WHERE ChunkId = @id",
                    cancellationToken,
                    ("@id", chunkId)).ConfigureAwait(false);

            }

            if (embeddingTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM workspace_file_embeddings WHERE ChunkId = @id",
                    cancellationToken,
                    ("@id", chunkId)).ConfigureAwait(false);

            }

            rows = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM workspace_file_chunks
                WHERE ChunkId = @id
                  AND julianday(IndexedAt) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", chunkId),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (rows == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "workspace_file_chunks",
            "ChunkId = @id",
            cancellationToken,
            ("@id", chunkId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "workspace_file_embeddings",
            "ChunkId = @id",
            cancellationToken,
            ("@id", chunkId)).ConfigureAwait(false) == 0;

        if (vectorTableExists)
        {

            reconciled &= await CountTableAsync(
                "workspace_file_embeddings_vec",
                "ChunkId = @id",
                cancellationToken,
                ("@id", chunkId)).ConfigureAwait(false) == 0;

        }

        return new CandidateDeleteResult(rows, 0, 0, derived, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteSagaCandidateAsync(
        string memoryId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        int derived = 0;

        int provenance;

        int rows;

        bool vectorTableExists = await TableExistsAsync(
            "saga_memory_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        try
        {

            if (vectorTableExists)
            {

                derived += await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM saga_memory_embeddings_vec WHERE MemoryId = @id",
                    cancellationToken,
                    ("@id", memoryId)).ConfigureAwait(false);

            }

            derived += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM saga_memory_embeddings WHERE MemoryId = @id",
                cancellationToken,
                ("@id", memoryId)).ConfigureAwait(false);

            provenance = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM saga_memory_attachment_provenance WHERE MemoryId = @id",
                cancellationToken,
                ("@id", memoryId)).ConfigureAwait(false);

            // Before the memory row, because the claim is found through the row that names it. The
            // rows-zero arm below rolls the whole transaction back, so a candidate the delete's own
            // WHERE refuses keeps its claim along with its memory.
            await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(
                connection,
                transaction,
                AnnalSubjectStore.Saga,
                memoryId,
                cancellationToken).ConfigureAwait(false);

            // The pin is re-proved here rather than trusted from the plan. A plan is a measurement, and
            // a measurement that authorizes a later delete has to hold at the moment it is used --
            // otherwise a pin taken while apply is running loses exactly the race it exists to win.
            rows = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM saga_memories
                WHERE Id = @id
                  AND julianday(CreatedAt) <= julianday(@cutoff)
                  AND PinnedAtUtc IS NULL
                """,
                cancellationToken,
                ("@id", memoryId),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (rows == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "saga_memories",
            "Id = @id",
            cancellationToken,
            ("@id", memoryId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "saga_memory_embeddings",
            "MemoryId = @id",
            cancellationToken,
            ("@id", memoryId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "saga_memory_attachment_provenance",
            "MemoryId = @id",
            cancellationToken,
            ("@id", memoryId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "annal_claims",
            "SubjectStoreCode = 1 AND SubjectId = @id",
            cancellationToken,
            ("@id", memoryId)).ConfigureAwait(false) == 0;

        if (vectorTableExists)
        {

            reconciled &= await CountTableAsync(
                "saga_memory_embeddings_vec",
                "MemoryId = @id",
                cancellationToken,
                ("@id", memoryId)).ConfigureAwait(false) == 0;

        }

        return new CandidateDeleteResult(
            rows,
            0,
            0,
            derived + provenance,
            reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteLexiconCandidateAsync(
        string entryId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        string? rowId;

        long fts;

        int provenance;

        int rows;

        try
        {

            rowId = await ScalarStringInTransactionAsync(
            connection,
            transaction,
            "SELECT CAST(rowid AS TEXT) FROM lexicon_entries WHERE Id = @id",
            cancellationToken,
            ("@id", entryId)).ConfigureAwait(false);

            fts = rowId is null
                ? 0
                : await CountInTransactionAsync(
                    connection,
                    transaction,
                    "lexicon_fts",
                    "rowid = @rowId",
                    cancellationToken,
                    ("@rowId", rowId)).ConfigureAwait(false);

            provenance = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM lexicon_fact_attachment_provenance WHERE EntryId = @id",
                cancellationToken,
                ("@id", entryId)).ConfigureAwait(false);

            await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(
                connection,
                transaction,
                AnnalSubjectStore.Lexicon,
                entryId,
                cancellationToken).ConfigureAwait(false);

            rows = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM lexicon_entries
                WHERE Id = @id
                  AND julianday(UpdatedAt) <= julianday(@cutoff)
                """,
                cancellationToken,
                ("@id", entryId),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (rows == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "lexicon_entries",
            "Id = @id",
            cancellationToken,
            ("@id", entryId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "lexicon_fact_attachment_provenance",
            "EntryId = @id",
            cancellationToken,
            ("@id", entryId)).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "annal_claims",
            "SubjectStoreCode = 2 AND SubjectId = @id",
            cancellationToken,
            ("@id", entryId)).ConfigureAwait(false) == 0;

        if (rowId is not null)
        {

            reconciled &= await CountTableAsync(
                "lexicon_fts",
                "rowid = @rowId",
                cancellationToken,
                ("@rowId", rowId)).ConfigureAwait(false) == 0;

        }

        return new CandidateDeleteResult(
            rows,
            0,
            0,
            provenance + fts,
            reconciled);

    }

    private CandidateDeleteResult DeleteLogCandidate(
        string fileName,
        RetentionRuleSettings rule,
        DateTimeOffset effectiveCutoff,
        Guid operationId,
        RetentionMutationJournal? mutationJournal)
    {

        if (!rule.Enabled
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {

            return CandidateDeleteResult.Empty;

        }

        string path = Path.GetFullPath(Path.Combine(_logsRoot, fileName));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _logsRoot,
                path,
                out _)
            || !File.Exists(path))
        {

            return CandidateDeleteResult.Empty;

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            throw new IOException(
                "Retention refused a log file whose identity changed before quarantine.");

        }

        RetentionMutationJournalEntry? expected = mutationJournal?.Entries
            .SingleOrDefault(entry =>
                string.Equals(entry.RootRole, "logs", StringComparison.Ordinal)
                && string.Equals(entry.RelativePath, fileName, StringComparison.Ordinal));

        if (mutationJournal is not null
            && (expected is null || expected.Metadata != artifact.Metadata))
        {

            throw new IOException(
                "Retention refused a log file that did not match its durable journal.");

        }

        if (!IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                OperationQuarantineDirectoryPrefix(operationId),
                out IdentityOwnedFileSystemQuarantine quarantine)
            || quarantine == default)
        {

            throw new IOException(
                "Retention refused a log file whose identity changed before quarantine.");

        }

        FileInfo quarantined = new(quarantine.Quarantined.Path);

        if (quarantined.LastWriteTimeUtc
            > effectiveCutoff.UtcDateTime)
        {

            if (!IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine))
            {

                throw new IOException(
                    "Retention could not restore a log file that became fresh before deletion.");

            }

            return CandidateDeleteResult.Empty;

        }

        long bytes = quarantined.Length;

        if (!IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine))
        {

            throw new RetentionQuarantineRecoveryRequiredException(
                "Retention could not finalize a quarantined log file.");

        }

        return new CandidateDeleteResult(
            0,
            1,
            bytes,
            0,
            !OwnedFileExists(_logsRoot, fileName));

    }

    private void AddLogCandidates(
        RetentionRuleSettings rule,
        RetentionDataClass dataClass,
        string pattern,
        string prefix,
        int limit,
        List<DataRetentionPlanItem> items,
        List<string> candidates)
    {

        if (!rule.Enabled || candidates.Count >= limit || !Directory.Exists(_logsRoot))
        {

            return;

        }

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(
            -ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        foreach (string path in Directory
                     .EnumerateFiles(_logsRoot, pattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {

            if (candidates.Count >= limit)
            {

                break;

            }

            FileInfo info = new(path);

            if (info.LastWriteTimeUtc > cutoff.UtcDateTime)
            {

                continue;

            }

            candidates.Add(prefix + info.Name);

            items.Add(new DataRetentionPlanItem(dataClass, 0, 1, info.Length, 0));

        }

    }

    private async Task<CandidateDeleteResult> DeleteIdempotencyClaimCandidateAsync(
        Guid claimId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        int rows = await ExecuteStandaloneAsync(
            """
            DELETE FROM IdempotencyClaims
            WHERE lower(replace(Id, '-', '')) = @id
              AND State IN (@completed, @failed, @abandoned)
              AND julianday(UpdatedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", claimId.ToString("N")),
            ("@completed", (int)IdempotencyClaimState.Completed),
            ("@failed", (int)IdempotencyClaimState.Failed),
            ("@abandoned", (int)IdempotencyClaimState.Abandoned),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

        bool reconciled = await CountTableAsync(
            "IdempotencyClaims",
            """
            lower(replace(Id, '-', '')) = @id
            AND State IN (@completed, @failed, @abandoned)
            AND julianday(UpdatedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", claimId.ToString("N")),
            ("@completed", (int)IdempotencyClaimState.Completed),
            ("@failed", (int)IdempotencyClaimState.Failed),
            ("@abandoned", (int)IdempotencyClaimState.Abandoned),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteStandaloneCostAdjustmentCandidateAsync(
        Guid adjustmentId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        int rows = await ExecuteStandaloneAsync(
            """
            DELETE FROM CostAdjustments
            WHERE lower(replace(Id, '-', '')) = @id
              AND BillableOperationId IS NULL
              AND RunId IS NULL
              AND julianday(CreatedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", adjustmentId.ToString("N")),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

        bool reconciled = await CountTableAsync(
            "CostAdjustments",
            """
            lower(replace(Id, '-', '')) = @id
              AND BillableOperationId IS NULL
              AND RunId IS NULL
              AND julianday(CreatedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", adjustmentId.ToString("N")),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteBudgetAlertCandidateAsync(
        Guid alertId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        int rows = await ExecuteStandaloneAsync(
            """
            DELETE FROM BudgetAlerts
            WHERE lower(replace(Id, '-', '')) = @id
              AND julianday(AlertedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", alertId.ToString("N")),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

        bool reconciled = await CountTableAsync(
            "BudgetAlerts",
            """
            lower(replace(Id, '-', '')) = @id
              AND julianday(AlertedAt) <= julianday(@cutoff)
            """,
            cancellationToken,
            ("@id", alertId.ToString("N")),
            ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(rows, 0, 0, 0, reconciled);

    }

    private async Task<CandidateDeleteResult> DeleteAccountingCandidateAsync(
        Guid runId,
        DateTimeOffset effectiveCutoff,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        long blockers = await CountTableAsync(
            "BudgetReservations",
            "lower(replace(RunId, '-', '')) = @id AND Status = @reserved",
            cancellationToken,
            ("@id", runId.ToString("N")),
            ("@reserved", (int)BudgetReservationStatus.Reserved)).ConfigureAwait(false);

        await using DbCommand retainedSession = connection.CreateCommand();

        retainedSession.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM InferenceRuns run
                JOIN Sessions session
                  ON lower(replace(session.Id, '-', '')) = lower(replace(run.SessionId, '-', ''))
                WHERE lower(replace(run.Id, '-', '')) = @id)
            """;

        Add(retainedSession, "@id", runId.ToString("N"));

        bool sessionExists = Convert.ToInt32(
            await retainedSession.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;

        if (blockers > 0 || sessionExists)
        {

            return CandidateDeleteResult.Empty;

        }

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        int adjustments;

        int reservations;

        int billable;

        int runs;

        try
        {

            adjustments = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM CostAdjustments
                WHERE lower(replace(RunId, '-', '')) = @id
                   OR BillableOperationId IN (
                       SELECT Id FROM BillableOperations
                       WHERE lower(replace(RunId, '-', '')) = @id)
                """,
                cancellationToken,
                ("@id", runId.ToString("N"))).ConfigureAwait(false);

            reservations = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM BudgetReservations WHERE lower(replace(RunId, '-', '')) = @id AND Status <> @reserved",
                cancellationToken,
                ("@id", runId.ToString("N")),
                ("@reserved", (int)BudgetReservationStatus.Reserved)).ConfigureAwait(false);

            billable = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM BillableOperations WHERE lower(replace(RunId, '-', '')) = @id",
                cancellationToken,
                ("@id", runId.ToString("N"))).ConfigureAwait(false);

            runs = await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM InferenceRuns
                WHERE lower(replace(Id, '-', '')) = @id
                  AND Status <> @running
                  AND julianday(COALESCE(CompletedAt, StartedAt)) <= julianday(@cutoff)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Sessions session
                      WHERE lower(replace(session.Id, '-', '')) = lower(replace(InferenceRuns.SessionId, '-', '')))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM BudgetReservations reservation
                      WHERE lower(replace(reservation.RunId, '-', '')) = @id
                        AND reservation.Status = @reserved)
                """,
                cancellationToken,
                ("@id", runId.ToString("N")),
                ("@running", (int)InferenceRunStatus.Running),
                ("@reserved", (int)BudgetReservationStatus.Reserved),
                ("@cutoff", FormatTimestamp(effectiveCutoff))).ConfigureAwait(false);

            if (runs == 0)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return CandidateDeleteResult.Empty;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        bool reconciled = await CountTableAsync(
            "InferenceRuns",
            "lower(replace(Id, '-', '')) = @id",
            cancellationToken,
            ("@id", runId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "BillableOperations",
            "lower(replace(RunId, '-', '')) = @id",
            cancellationToken,
            ("@id", runId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "BudgetReservations",
            "lower(replace(RunId, '-', '')) = @id",
            cancellationToken,
            ("@id", runId.ToString("N"))).ConfigureAwait(false) == 0;

        reconciled &= await CountTableAsync(
            "CostAdjustments",
            "lower(replace(RunId, '-', '')) = @id",
            cancellationToken,
            ("@id", runId.ToString("N"))).ConfigureAwait(false) == 0;

        return new CandidateDeleteResult(
            runs + billable + reservations + adjustments,
            0,
            0,
            0,
            reconciled);

    }

    private bool OwnedFileExists(string root, string relativePath)
    {

        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        return WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                candidate,
                out _)
            && File.Exists(candidate);

    }

    private static (long Files, long Bytes) MeasureOwnedFile(
        string root,
        string relativePath)
    {

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                fullRoot,
                candidate,
                out _)
            || !File.Exists(candidate))
        {

            return (0, 0);

        }

        FileInfo file = new(candidate);

        return (1, file.Length);

    }

    private sealed record CandidateDeleteResult(
        long Rows,
        long Files,
        long Bytes,
        long Derived,
        bool Reconciled)
    {

        public static CandidateDeleteResult Empty { get; } =
            new(0, 0, 0, 0, true);

        public static CandidateDeleteResult From(DataRetentionApplyResult result) =>
            new(
                result.RowsDeleted,
                result.FilesDeleted,
                result.EstimatedBytesDeleted,
                result.DerivedRecordsDeleted,
                result.Reconciled);

    }

    private sealed record CandidateAgeBoundary(
        DateTimeOffset Cutoff,
        int Days);

    private sealed record FrozenPruneAuthority(
        IReadOnlyDictionary<string, DateTimeOffset> Cutoffs);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("o", CultureInfo.InvariantCulture);

}
