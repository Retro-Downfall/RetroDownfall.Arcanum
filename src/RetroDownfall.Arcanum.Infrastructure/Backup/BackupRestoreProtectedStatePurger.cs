using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// What one staged protected-state purge removed, in counts and nothing else.
/// </summary>
internal sealed record BackupRestoreProtectedStatePurgeReceipt(
    ulong CanonicalRows,
    ulong AcceleratorRows,
    ulong RemovedLabels,
    ulong RemovedArtifacts,
    ulong RepairedSessionProjections);

/// <summary>
/// Removes the whole Covenant family and every protected artifact from a staged generation, before
/// that generation is ever published as live.
/// </summary>
/// <remarks>
/// This is the <c>PurgeProtectedState</c> arm of §10.19.10, and it is the only supported continuation
/// for an archive whose source machine could not prove a clean authority state. It runs inside the
/// caller's staged core transaction, <em>after</em> the destination-monotonic authority and disclosure
/// joins, so what survives is exactly the content-free evidence this machine is entitled to keep: its
/// own host-tools taint, its own joined disclosure counts, and the receipts behind them.
///
/// <para>Two authorizations are open together, and both are needed. The canonical family and the label
/// ledger are reachable under <c>CovenantFamilyMaintenance</c>; the per-artifact content tables the
/// labels name are guarded by the ordinary purge scopes instead, so <c>SensitivityRetentionPurge</c> is
/// what lets a labelled summary or title be removed beside its label. Neither authorization outlives
/// the statements it was opened for, and both begin denied on every connection.</para>
///
/// <para>It makes <b>no filesystem call at all</b>, for the same reason §10.19.4's sanitizer makes none:
/// a <c>ManagedWorkspaceFile</c> label in a staged archive names a file on the machine the backup came
/// from, and unlinking it would be acting on authority this installation has just proven it does not
/// have. Removing the label is the whole effect, and the label is the only durable record that made the
/// artifact protected here.</para>
/// </remarks>
internal static class BackupRestoreProtectedStatePurger
{
    /// <summary>The persisted artifact-kind code to policy map, keyed the way the column stores it.</summary>
    /// <remarks>
    /// Keyed on <see cref="CovenantSensitiveArtifactPurgeRule.Code"/> rather than on the enum value, so
    /// a renumbered enum whose persisted codes did not move is caught here instead of quietly purging
    /// one kind's rows under another kind's policy.
    /// </remarks>
    private static readonly Dictionary<long, CovenantSensitiveArtifactPurgeRule> RulesByCode =
        CovenantSensitiveArtifactPurgePolicy.All.ToDictionary(static rule => (long)rule.Code);

    /// <summary>
    /// Empties the Covenant family, removes every labelled artifact, and folds each Session's
    /// projection back to zero.
    /// </summary>
    internal static async Task<Result<BackupRestoreProtectedStatePurgeReceipt>> PurgeStagedAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CovenantSqliteConnectionInitializer initializer,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (staged is null || transaction is null || initializer is null || timeProvider is null)
        {
            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A staged protected-state purge requires its connection, transaction, and clock.");
        }

        using CovenantSqliteAuthorizationScope family = initializer.Authorize(
            staged,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        using CovenantSqliteAuthorizationScope artifacts = initializer.Authorize(
            staged,
            CovenantSqliteAuthorizationKind.SensitivityRetentionPurge);

        Result<LabelPurge> labels = await PurgeLabelledArtifactsAsync(
            staged,
            transaction,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (labels.IsFailure)
        {
            return labels.Error;
        }

        // The accelerator first: its documents are a projection of the canonical rows below, and its
        // own delete triggers keep the FTS index in step with them.
        ulong accelerator = await DeleteAllAsync(
            staged,
            transaction,
            BackupRestoreProtectedStateInspector.AcceleratorContentTables,
            cancellationToken).ConfigureAwait(false);

        ulong canonical = await DeleteAllAsync(
            staged,
            transaction,
            BackupRestoreProtectedStateInspector.CanonicalContentTables,
            cancellationToken).ConfigureAwait(false);

        return new BackupRestoreProtectedStatePurgeReceipt(
            canonical,
            accelerator,
            labels.Value.RemovedLabels,
            labels.Value.RemovedArtifacts,
            labels.Value.RepairedProjections);
    }

    /// <summary>
    /// Removes each labelled artifact through the shared purge policy, then its label.
    /// </summary>
    /// <remarks>
    /// The label is the authority on what the artifact is, because in staging there is no live owner to
    /// reread it from — the whole generation belongs to a machine that is not this one. That is the one
    /// difference from <see cref="CovenantProtectedArtifactErasureKernel"/>, which rereads the live row
    /// inside its own transaction precisely because a live artifact can have changed owner since the
    /// caller listed it. Both resolve <em>where</em> the rows live through the same
    /// <see cref="CovenantArtifactPurgePlans"/> table and compare identity through the same
    /// <see cref="CovenantIdentitySql"/> shape, so a kind whose storage moves cannot be purged
    /// two different ways and neither path can be left matching a spelling the other has outgrown.
    /// </remarks>
    private static async Task<Result<LabelPurge>> PurgeLabelledArtifactsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "artifact_sensitivity", cancellationToken, transaction)
                .ConfigureAwait(false))
        {
            return new LabelPurge(0, 0, 0);
        }

        List<StagedLabel> labels = await ReadLabelsAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false);

        // Every label is judged before the first one is purged, so a refusal never depends on the caller
        // rolling back what earlier labels in the walk had already removed.
        foreach (StagedLabel label in labels)
        {
            if (!RulesByCode.ContainsKey(label.KindCode))
            {
                // Fail closed. A label this build has no policy for describes Covenant-derived content
                // whose storage it cannot enumerate, and removing the label alone would leave that
                // content with nothing admitting it is protected.
                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The staged archive carries a sensitivity label of a kind this build has no "
                    + "protected-artifact purge policy for, so its protected state cannot be removed.");
            }

            if (RulesByCode[label.KindCode].Kind is SensitiveArtifactKind.CampaignRollup or SensitiveArtifactKind.CampaignContribution
                && !Guid.TryParse(label.ArtifactId, out _))
            {
                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The staged archive carries a Campaign summary label without a valid artifact identity.");
            }

            if (CovenantIdentitySql.Key(label.ArtifactId).Length == 0)
            {
                // Fail closed. Every content, pointer, and projection delete compares a normalised column
                // against this key, and an empty key would match any blank-keyed row in those tables rather
                // than the one artifact the label names.
                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The staged archive carries a sensitivity label whose artifact identity is empty, so "
                    + "the content it protects cannot be identified and its protected state cannot be removed.");
            }
        }

        // Measure every labelled primary before any dependency closure is removed. A contribution
        // purge may consume a rollup and its label before the walk reaches that label, so counting
        // after each delete would under-report both protected primaries and their evidence.
        ulong removedArtifacts = 0;

        foreach (StagedLabel label in labels)
        {
            CovenantArtifactPlanTally measured = await CovenantArtifactPlanRunner.RunAsync(
                staged, transaction, RulesByCode[label.KindCode].Kind,
                CovenantIdentitySql.Key(label.ArtifactId), CovenantArtifactPlanMode.Count, cancellationToken)
                .ConfigureAwait(false);

            removedArtifacts = checked(removedArtifacts + (ulong)measured.ArtifactRows);
        }

        HashSet<string> sessions = new(StringComparer.Ordinal);

        foreach (StagedLabel label in labels)
        {
            _ = await ApplyPlanAsync(staged, transaction, label, RulesByCode[label.KindCode], cancellationToken)
                .ConfigureAwait(false);

            if (label.SessionId is { } sessionId)
            {
                _ = sessions.Add(sessionId);
            }
        }

        _ = await DeleteAllAsync(
            staged,
            transaction,
            ["artifact_sensitivity"],
            cancellationToken).ConfigureAwait(false);

        return new LabelPurge(
            checked((ulong)labels.Count),
            removedArtifacts,
            await FoldSessionProjectionsAsync(
                staged,
                transaction,
                sessions,
                timeProvider,
                cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes one artifact's current pointer, redacts the mutable column it shadowed, then deletes its
    /// projections and its content.
    /// </summary>
    /// <remarks>
    /// Returns whether a content row was actually removed. Five of the thirteen kinds have no storage in
    /// this build at all and four more are label-only by policy, so "the label went" and "content went"
    /// are genuinely different counts and folding them would overstate what the purge deleted.
    ///
    /// <para>The projections and the content run as one plan through
    /// <see cref="CovenantArtifactPlanRunner"/>, the same runner the live kernel deletes through, so a
    /// staged archive's mirrors are classified exactly as a live installation's are. The pointer and
    /// redaction go first, which reorders nothing real: no plan has both projections and either of
    /// them.</para>
    ///
    /// <para>An assistant Entry has one derivative the plan cannot name: the Session's hierarchical
    /// summary of its entries (the Tapestry), which is keyed by the Session and not by the artifact. The
    /// live kernel drops it in the entry's own transaction, and this does the same, because a restored
    /// archive that kept it would hold a model-written summary of Covenant words the purge just removed,
    /// retrievable behind a purge that reported success. The Session is read off the Entry row before the
    /// plan deletes it as well as off the label, since a label's Session column is nullable and the row is
    /// what the summary was actually written from. A staged archive from before the Tapestry existed has no
    /// table to drop from and is left alone.</para>
    /// </remarks>
    private static async Task<bool> ApplyPlanAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        StagedLabel label,
        CovenantSensitiveArtifactPurgeRule rule,
        CancellationToken cancellationToken)
    {
        if (rule.Kind is SensitiveArtifactKind.CampaignRollup or SensitiveArtifactKind.CampaignContribution)
        {
            _ = await CampaignSummaryLifecycle.PurgeArtifactAsync(
                staged, transaction, rule.Kind, Guid.Parse(label.ArtifactId), cancellationToken).ConfigureAwait(false);

            return true;
        }

        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(rule.Kind);

        IReadOnlyCollection<string> owningSessions = rule.Kind == SensitiveArtifactKind.AssistantEntry
            ? await ReadOwningSessionKeysAsync(staged, transaction, label, cancellationToken).ConfigureAwait(false)
            : [];

        if (plan.CurrentPointerTable is { } pointer)
        {
            _ = await ExecuteAsync(
                staged,
                transaction,
                $"DELETE FROM {pointer} WHERE {CovenantIdentitySql.Keyed("CurrentArtifactId", "$artifactKey")};",
                label,
                cancellationToken).ConfigureAwait(false);
        }

        if (plan.RedactionSql is { } redaction)
        {
            _ = await ExecuteAsync(staged, transaction, redaction, label, cancellationToken)
                .ConfigureAwait(false);
        }

        CovenantArtifactPlanTally tally = await CovenantArtifactPlanRunner.RunAsync(
            staged,
            transaction,
            rule.Kind,
            CovenantIdentitySql.Key(label.ArtifactId),
            CovenantArtifactPlanMode.Delete,
            cancellationToken).ConfigureAwait(false);

        foreach (string sessionKey in owningSessions)
        {
            Guid sessionId = Guid.ParseExact(sessionKey, "N");

            _ = await SessionSummaryLifecycle.ClearAsync(staged, transaction, sessionId, cancellationToken)
                .ConfigureAwait(false);

            _ = await CampaignSummaryLifecycle.ClearSessionAsync(staged, transaction, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (owningSessions.Count > 0
            && await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "tapestry_generations", cancellationToken, transaction)
                .ConfigureAwait(false))
        {
            foreach (string sessionKey in owningSessions)
            {
                _ = await TapestryStore.DeleteSessionTreesByKeyAsync(
                    staged,
                    transaction,
                    sessionKey,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return tally.ArtifactRows > 0;
    }

    /// <summary>
    /// The normalised identities of the Sessions whose Tapestry summarises one assistant Entry: the Session
    /// the label names and the Session the Entry row belongs to.
    /// </summary>
    /// <remarks>
    /// Read before the plan deletes the row. An identity that normalises to nothing is dropped rather than
    /// passed on, because a predicate keyed by an empty string matches every blank-keyed tree; the other
    /// source still names the Session, or neither does and there is no tree this purge can attribute to the
    /// Entry.
    /// </remarks>
    private static async Task<IReadOnlyCollection<string>> ReadOwningSessionKeysAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        StagedLabel label,
        CancellationToken cancellationToken)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        if (label.SessionId is { } labelled && CovenantIdentitySql.Key(labelled) is { Length: > 0 } labelKey)
        {
            _ = keys.Add(labelKey);
        }

        if (await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "Entries", cancellationToken, transaction)
                .ConfigureAwait(false))
        {
            await using SqliteCommand command = staged.CreateCommand();

            command.Transaction = transaction;

            command.CommandText =
                $"SELECT \"SessionId\" FROM \"Entries\" WHERE {CovenantIdentitySql.Keyed("\"Id\"", "$artifactKey")};";

            _ = command.Parameters.AddWithValue("$artifactKey", CovenantIdentitySql.Key(label.ArtifactId));

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0) && CovenantIdentitySql.Key(reader.GetString(0)) is { Length: > 0 } rowKey)
                {
                    _ = keys.Add(rowKey);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Folds every touched Session's sensitivity projection to zero tainted artifacts without lowering
    /// its maximum.
    /// </summary>
    /// <remarks>
    /// The projection is conservative in exactly one direction, and this is that direction: after the
    /// purge no tainted artifact remains, so the count is zero — but the maximum stays where it was,
    /// because taint that has been purged still bars a cached reply from being replayed into a Session
    /// that once held Covenant content. Reporting <c>None</c> here would hand a restored installation a
    /// cache-replay path the source machine never had.
    /// </remarks>
    private static async Task<ulong> FoldSessionProjectionsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0
            || !await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "session_sensitivity_state", cancellationToken, transaction)
                .ConfigureAwait(false))
        {
            return 0;
        }

        ulong folded = 0;

        foreach (string sessionId in sessions)
        {
            await using SqliteCommand command = staged.CreateCommand();

            command.Transaction = transaction;

            // The identity came out of artifact_sensitivity, which the label ledger spells uppercase,
            // while session_sensitivity_state agrees with "Sessions"."Id" so its foreign key can
            // resolve — lowercase for a Session an import created. The two rows describe the same
            // Session in two spellings, so the fold has to compare them normalised.
            command.CommandText = $"""
                UPDATE session_sensitivity_state
                SET TaintedArtifactCount = 0,
                    Revision = Revision + 1,
                    UpdatedAtUtc = $now
                WHERE {CovenantIdentitySql.Keyed("SessionId", "$sessionKey")};
                """;

            _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

            _ = command.Parameters.AddWithValue("$now", Timestamp(timeProvider));

            folded = checked(
                folded
                + (ulong)await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        return folded;
    }

    private static async Task<List<StagedLabel>> ReadLabelsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        List<StagedLabel> labels = [];

        await using SqliteCommand command = staged.CreateCommand();

        command.Transaction = transaction;

        // Ordered so two runs over the same staged database do the same work in the same order, which
        // is what makes a retry over a partially purged generation converge rather than diverge.
        command.CommandText = """
            SELECT LabelId, ArtifactKindCode, ArtifactId, SessionId
            FROM artifact_sensitivity
            ORDER BY ArtifactKindCode, ArtifactId;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels.Add(
                new StagedLabel(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return labels;
    }

    private static async Task<ulong> DeleteAllAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {
        ulong removed = 0;

        foreach (string table in tables)
        {
            if (!await BackupRestoreDatabaseWorker
                    .TableExistsAsync(staged, table, cancellationToken, transaction)
                    .ConfigureAwait(false))
            {
                continue;
            }

            await using SqliteCommand command = staged.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"DELETE FROM {table};";

            removed = checked(
                removed
                + (ulong)await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        return removed;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        string sql,
        StagedLabel label,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = staged.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        // Every identity is bound for every statement rather than sniffed out of the text. SQLite
        // ignores a bound parameter a statement does not mention, and choosing which to bind by
        // searching the SQL would make the binding a property of how the statement happens to be spelled.
        //
        // The label's own spelling is used verbatim against artifact_sensitivity, which is the table it
        // was read from. The content tables it names are matched through the normalised spelling
        // instead: a staged archive carries whatever forms its source machine's writers produced, and
        // an exact comparison there matches the archives that happen to agree with the label ledger and
        // silently leaves the rest of the protected content in the generation about to be published.
        _ = command.Parameters.AddWithValue("$artifactId", label.ArtifactId);

        _ = command.Parameters.AddWithValue("$artifactKey", CovenantIdentitySql.Key(label.ArtifactId));

        _ = command.Parameters.AddWithValue("$labelId", label.LabelId);

        _ = command.Parameters.AddWithValue(
            "$sessionId",
            label.SessionId ?? (object)DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$sessionKey",
            label.SessionId is { } sessionKey ? CovenantIdentitySql.Key(sessionKey) : (object)DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Timestamp(TimeProvider timeProvider) =>
        UtcInstantText.Format(timeProvider.GetUtcNow());

    /// <summary>One staged label, reduced to the four fields a purge statement binds.</summary>
    private sealed record StagedLabel(
        string LabelId,
        long KindCode,
        string ArtifactId,
        string? SessionId);

    private sealed record LabelPurge(
        ulong RemovedLabels,
        ulong RemovedArtifacts,
        ulong RepairedProjections);
}
