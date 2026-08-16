using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One table the purge deletes from, and the column it is keyed by.
/// </summary>
/// <remarks>
/// Both halves are fixed internal literals, never input. That is what makes it safe to interpolate
/// them into the statement text SQLite gives no parameter form for.
/// </remarks>
internal sealed record CovenantArtifactPurgeTarget(string Table, string KeyColumn);

/// <summary>
/// The statements one artifact kind's rule resolves to in this build's schema.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="CovenantSensitiveArtifactPurgePolicy"/>. The registry states
/// the policy — what must be deleted, repaired, receipted, and preserved — and is the contract every
/// erasure path shares. This table states where those rows currently live, which is a property of the
/// installed schema and changes as storage lands. Keeping them apart is what lets the policy be
/// exhaustive over thirteen kinds while the schema is not yet.
/// </remarks>
internal sealed record CovenantArtifactPurgePlan(
    SensitiveArtifactKind Kind,
    IReadOnlyList<CovenantArtifactPurgeTarget> Projections,
    CovenantArtifactPurgeTarget? Artifact,
    string? CurrentPointerTable,
    string? RedactionSql);

/// <summary>
/// Where each kind's content lives in the schema this build installs.
/// </summary>
internal static class CovenantArtifactPurgePlans
{

    private static readonly Dictionary<SensitiveArtifactKind, CovenantArtifactPurgePlan> Plans = new()
    {
        [SensitiveArtifactKind.AssistantEntry] = new(
            SensitiveArtifactKind.AssistantEntry,
            [new CovenantArtifactPurgeTarget("entry_embeddings", "EntryId")],
            new CovenantArtifactPurgeTarget("\"Entries\"", "\"Id\""),
            CurrentPointerTable: null,
            RedactionSql: null),

        [SensitiveArtifactKind.Summary] = new(
            SensitiveArtifactKind.Summary,
            [],
            new CovenantArtifactPurgeTarget("session_summary_artifacts", "ArtifactId"),
            CurrentPointerTable: "session_summary_state",
            RedactionSql: "UPDATE \"Sessions\" SET \"Summary\" = NULL WHERE \"Id\" = $sessionId;"),

        [SensitiveArtifactKind.SessionTitle] = new(
            SensitiveArtifactKind.SessionTitle,
            [],
            new CovenantArtifactPurgeTarget("session_title_artifacts", "ArtifactId"),
            CurrentPointerTable: "session_title_state",
            RedactionSql: "UPDATE \"Sessions\" SET \"Title\" = NULL WHERE \"Id\" = $sessionId;"),

        [SensitiveArtifactKind.Saga] = new(
            SensitiveArtifactKind.Saga,
            [
                new CovenantArtifactPurgeTarget("saga_memory_embeddings", "MemoryId"),
                new CovenantArtifactPurgeTarget("saga_memory_attachment_provenance", "MemoryId"),
            ],
            new CovenantArtifactPurgeTarget("saga_memories", "Id"),
            CurrentPointerTable: null,
            RedactionSql: null),

        [SensitiveArtifactKind.Lexicon] = new(
            SensitiveArtifactKind.Lexicon,
            [new CovenantArtifactPurgeTarget("lexicon_fact_attachment_provenance", "EntryId")],
            new CovenantArtifactPurgeTarget("lexicon_entries", "Id"),
            CurrentPointerTable: null,
            RedactionSql: null),

        [SensitiveArtifactKind.Embedding] = new(
            SensitiveArtifactKind.Embedding,
            [],
            new CovenantArtifactPurgeTarget("entry_embeddings", "EntryId"),
            CurrentPointerTable: null,
            RedactionSql: null),

        // The claim row itself is preserved on purpose: it is the only thing that can still answer a
        // replayed request with a typed denial. Only the cached protected body is removed.
        [SensitiveArtifactKind.IdempotencyClaim] = new(
            SensitiveArtifactKind.IdempotencyClaim,
            [],
            Artifact: null,
            CurrentPointerTable: null,
            RedactionSql: "UPDATE \"IdempotencyClaims\" SET \"ResponseBody\" = NULL WHERE \"Id\" = $artifactId;"),
    };

    /// <summary>
    /// The plan for a kind, or the label-only plan for a kind whose storage this build does not ship.
    /// </summary>
    /// <remarks>
    /// A label-only plan is not a gap being papered over. Nothing in this build creates a tool
    /// artifact, protected search projection, audit payload, notification, or turn-evidence row, so
    /// there is no content row for the purge to find — and the label, which is the only durable record
    /// that an artifact is Covenant-derived, is still removed. When that storage lands, its owning
    /// slice adds the statements here and the policy above does not move.
    /// </remarks>
    internal static CovenantArtifactPurgePlan Resolve(SensitiveArtifactKind kind) =>
        Plans.TryGetValue(kind, out CovenantArtifactPurgePlan? plan)
            ? plan
            : new CovenantArtifactPurgePlan(kind, [], null, null, null);

    /// <summary>The kinds whose content rows this build can actually delete.</summary>
    internal static IReadOnlyCollection<SensitiveArtifactKind> MaterializedKinds => Plans.Keys;

}

/// <summary>
/// The one kernel that deletes database-owned Covenant-derived artifacts.
/// </summary>
/// <remarks>
/// Every item is rechecked against the live row inside the same transaction that deletes it. The
/// caller's page says which artifacts it believes are there; the database says which ones actually
/// are, who owns them now, and at what revision — and only the second of those may authorize a
/// deletion (§10.17).
///
/// <para>The connection-local SQL authorization is borrowed per transaction and disposed before the
/// commit, so no grant outlives the statements it was opened for.</para>
/// </remarks>
internal sealed class CovenantProtectedArtifactErasureKernel(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer,
    TimeProvider timeProvider) : ICovenantProtectedArtifactErasureKernel
{

    private readonly ICovenantConnectionSource _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
        CovenantProtectedArtifactErasurePage page,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(page);

        ArgumentNullException.ThrowIfNull(authority);

        Result current = await authority.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Stalled(CovenantErasureBlocker.AuthorityStale);

        }

        // A page computed against a dataset generation the lease did not capture describes artifacts
        // from a database that has since been replaced. Deleting by identity across that boundary is
        // how a reinitialized installation loses rows the operator never asked to lose.
        if (authority.Snapshot.DatasetGeneration is { } generation
            && generation != page.ExpectedDatasetGeneration)
        {

            return Stalled(CovenantErasureBlocker.IntegrityFailure);

        }

        SqliteConnection connection = await _connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        CovenantSqliteAuthorizationKind authorization =
            CovenantManagedFileErasureKernel.Authorization(authority);

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        foreach (CovenantProtectedArtifactErasureItem item in page.Items)
        {

            Result<CovenantArtifactErasureProgress> erased = await EraseOneAsync(
                connection,
                item,
                authority,
                authorization,
                cancellationToken).ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return erased;

            }

            progress = progress.Add(erased.Value);

            // A blocked item stops the page. Continuing would keep deleting under an authority that
            // has already been shown not to hold, and the count would describe a purge nobody
            // authorized.
            if (erased.Value.IsBlocked)
            {

                break;

            }

        }

        return Result<CovenantArtifactErasureProgress>.Success(progress);

    }

    private async Task<Result<CovenantArtifactErasureProgress>> EraseOneAsync(
        SqliteConnection connection,
        CovenantProtectedArtifactErasureItem item,
        CovenantArtifactErasureAuthority authority,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken)
    {

        Result<CovenantSensitiveArtifactPurgeRule> rule = CovenantSensitiveArtifactPurgePolicy.Resolve(item.Kind);

        if (rule.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Failure(rule.Error);

        }

        try
        {

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<CovenantOperationScope?> owner = await ReadCurrentOwnerAsync(
                connection,
                transaction,
                item,
                cancellationToken).ConfigureAwait(false);

            if (owner.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Blocked(CovenantErasureBlocker.IntegrityFailure);

            }

            if (owner.Value is not { } scope)
            {

                // No live label at the exact identity, kind, revision, and digest the page named. The
                // artifact was either already purged or is not the one this page described, and both
                // answers are "do not delete anything here".
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result<CovenantArtifactErasureProgress>.Success(
                    new CovenantArtifactErasureProgress(1, 0, 1, CovenantErasureBlocker.None));

            }

            if (!authority.Covers(scope))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Blocked(CovenantErasureBlocker.ManualOwnershipMismatch);

            }

            using (_initializer.Authorize(connection, authorization))
            {

                await ApplyPlanAsync(
                    connection,
                    transaction,
                    item,
                    rule.Value,
                    ErasureOperationIdentity(authority),
                    cancellationToken).ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(
                    1,
                    1,
                    rule.Value.AppendsErasureReceipt ? 1UL : 0UL,
                    CovenantErasureBlocker.None));

        }
        catch (SqliteException)
        {

            return Blocked(CovenantErasureBlocker.IntegrityFailure);

        }

    }

    /// <summary>
    /// Deletes projections, then the artifact, then the label, and repairs what the rule requires.
    /// </summary>
    /// <remarks>
    /// The order is the one the schema guards already assume: projections point at the artifact, the
    /// artifact is what the label's owner index names, and the current pointer cascades from the
    /// artifact. Removing the label first would leave content nothing admits is tainted, which is the
    /// exact state a purge exists to make impossible.
    /// </remarks>
    private async Task ApplyPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantProtectedArtifactErasureItem item,
        CovenantSensitiveArtifactPurgeRule rule,
        Guid operationId,
        CancellationToken cancellationToken)
    {

        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(item.Kind);

        foreach (CovenantArtifactPurgeTarget projection in plan.Projections)
        {

            await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM {projection.Table} WHERE {projection.KeyColumn} = $artifactId;",
                item,
                cancellationToken).ConfigureAwait(false);

        }

        if (rule.RepairsCurrentPointer && plan.CurrentPointerTable is { } pointer)
        {

            await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM {pointer} WHERE CurrentArtifactId = $artifactId;",
                item,
                cancellationToken).ConfigureAwait(false);

        }

        if (plan.RedactionSql is { } redaction)
        {

            await ExecuteAsync(connection, transaction, redaction, item, cancellationToken).ConfigureAwait(false);

        }

        if (plan.Artifact is { } artifact)
        {

            await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM {artifact.Table} WHERE {artifact.KeyColumn} = $artifactId;",
                item,
                cancellationToken).ConfigureAwait(false);

        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM artifact_sensitivity WHERE LabelId = $labelId AND ArtifactId = $artifactId;",
            item,
            cancellationToken).ConfigureAwait(false);

        if (rule.AppendsErasureReceipt)
        {

            await AppendErasureReceiptAsync(connection, transaction, item, operationId, cancellationToken)
                .ConfigureAwait(false);

        }

        if (rule.RepairsSessionSensitivityState && item.SessionId is { } sessionId)
        {

            await RepairSessionSensitivityAsync(connection, transaction, sessionId, cancellationToken)
                .ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Leaves the immutable tombstone that keeps a purged entry distinguishable from one that never
    /// existed.
    /// </summary>
    /// <remarks>
    /// Bound to the finalization guard rather than to the Entry alone, so a later turn's guard cannot
    /// inherit it. When no finalization guard exists the receipt is skipped rather than invented: a
    /// receipt whose guard digest was fabricated would authorize a 410 for a turn that never
    /// finalized.
    /// </remarks>
    private async Task AppendErasureReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantProtectedArtifactErasureItem item,
        Guid operationId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT OR IGNORE INTO assistant_entry_erasure_receipts (
                AssistantEntryId, SessionId, FinalizationGuardDigest, ErasureReasonCode, OperationId, ErasedAtUtc)
            SELECT $artifactId, SessionId, RequestDigest, 2, $operationId, $now
            FROM assistant_entry_finalizations
            WHERE AssistantEntryId = $artifactId;
            """;

        _ = command.Parameters.AddWithValue("$artifactId", Format(item.ArtifactId));

        _ = command.Parameters.AddWithValue("$operationId", Format(operationId));

        _ = command.Parameters.AddWithValue("$now", Iso(_time.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// The identity an erasure receipt records as the operation that removed the content.
    /// </summary>
    /// <remarks>
    /// An exclusive erasure already has a durable operation identity, and that is the one an operator
    /// can look up. An ordinary retention purge does not, so the lease registration is used instead:
    /// it is the narrowest identity that actually existed at the moment of the delete, and inventing a
    /// fresh identity per artifact would make the receipts of one purge look like many.
    /// </remarks>
    private static Guid ErasureOperationIdentity(CovenantArtifactErasureAuthority authority) =>
        authority.Snapshot.RecoveryOwner is { IsValid: true } owner
            ? owner.OperationId
            : authority.Snapshot.RegistrationId;

    /// <summary>
    /// Recomputes the Session's conservative taint projection from the labels that are still live.
    /// </summary>
    /// <remarks>
    /// Conservative in one direction only: the count drops to what remains, and the maximum stays at
    /// <c>CovenantDerived</c> whenever anything is still counted. It is never lowered to <c>None</c>
    /// by this path even when the count reaches zero, because taint that has been purged still bars a
    /// cached replay.
    /// </remarks>
    private async Task RepairSessionSensitivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE session_sensitivity_state
            SET TaintedArtifactCount = (
                    SELECT COUNT(*) FROM artifact_sensitivity WHERE SessionId = $sessionId
                ),
                Revision = Revision + 1,
                UpdatedAtUtc = $now
            WHERE SessionId = $sessionId;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", Format(sessionId));

        _ = command.Parameters.AddWithValue("$now", Iso(_time.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Rereads the live label and derives the owner scope from it alone.
    /// </summary>
    private static async Task<Result<CovenantOperationScope?>> ReadCurrentOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantProtectedArtifactErasureItem item,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT CampaignId, SessionId
            FROM artifact_sensitivity
            WHERE LabelId = $labelId
                AND ArtifactId = $artifactId
                AND ArtifactKindCode = $kind
                AND ArtifactRevision = $revision
                AND ArtifactContentDigest = $digest;
            """;

        _ = command.Parameters.AddWithValue("$labelId", Format(item.SensitivityLabelId));

        _ = command.Parameters.AddWithValue("$artifactId", Format(item.ArtifactId));

        _ = command.Parameters.AddWithValue("$kind", (long)item.Kind);

        _ = command.Parameters.AddWithValue("$revision", (long)item.ExpectedRevision);

        _ = command.Parameters.AddWithValue("$digest", item.ExpectedArtifactDigest.Bytes.ToArray());

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantOperationScope?>.Success(null);

        }

        Guid? liveSession = reader.IsDBNull(1)
            ? null
            : Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture);

        // The historical owner recorded on the label is what decides coverage. An item whose Session
        // no longer matches the label describes an artifact that has moved owners since the page was
        // computed, and no authority computed against the old owner may erase it.
        if (liveSession != item.SessionId)
        {

            return Result<CovenantOperationScope?>.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The artifact's recorded Session owner changed after this erasure page was computed."));

        }

        return Result<CovenantOperationScope?>.Success(
            reader.IsDBNull(0)
                ? CovenantOperationScope.Global
                : CovenantOperationScope.ForCampaign(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)));

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CovenantProtectedArtifactErasureItem item,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        // All three identities are bound for every statement rather than sniffed out of the text.
        // SQLite ignores a bound parameter a statement does not mention, and choosing which to bind by
        // searching the SQL would make the binding a property of how the statement happens to be
        // spelled.
        _ = command.Parameters.AddWithValue("$artifactId", Format(item.ArtifactId));

        _ = command.Parameters.AddWithValue("$labelId", Format(item.SensitivityLabelId));

        _ = command.Parameters.AddWithValue(
            "$sessionId",
            item.SessionId is { } sessionId ? Format(sessionId) : DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static Result<CovenantArtifactErasureProgress> Stalled(CovenantErasureBlocker blocker) =>
        Result<CovenantArtifactErasureProgress>.Success(
            new CovenantArtifactErasureProgress(0, 0, 0, blocker));

    private static Result<CovenantArtifactErasureProgress> Blocked(CovenantErasureBlocker blocker) =>
        Result<CovenantArtifactErasureProgress>.Success(
            new CovenantArtifactErasureProgress(1, 0, 1, blocker));

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}
