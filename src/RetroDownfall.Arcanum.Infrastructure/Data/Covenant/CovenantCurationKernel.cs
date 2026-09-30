using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Applies one curation change inside the caller's transaction, or refuses it.
/// </summary>
/// <remarks>
/// The mirror of <see cref="CovenantMutationKernel"/> and deliberately a separate type. What it writes
/// is not a version of the operator's text — a pin carries no content, no tombstone, and no lane
/// revision of the entry's own — so folding it into a kernel whose every step is about compiled
/// artifacts, search projections, and Section capacity would mean threading a null through each of
/// them and calling the result one code path.
///
/// <para>It takes one change rather than a batch, because curation has no staging path: an operator
/// asks for exactly one thing and a turn can ask for none. It opens neither connection nor
/// transaction; the caller owns both, so a curation change can share the transaction that carries
/// whatever else the request is doing.</para>
///
/// <para>Receipt first. An exact retry resolves through the durable receipt before revision, epoch, or
/// generation is looked at, so a client that lost its response gets the committed answer back rather
/// than a conflict for work that already happened.</para>
/// </remarks>
internal sealed class CovenantCurationKernel
{
    public async ValueTask<Result<CovenantCurationReceipt>> ApplyAsync(
        CovenantCurationCommit commit,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        ArgumentNullException.ThrowIfNull(transaction);

        CovenantCurationIntent intent = commit.Intent;

        Result<CovenantCurationReceipt?> replayed = await TryReplayAsync(transaction, intent, cancellationToken)
            .ConfigureAwait(false);

        if (replayed.IsFailure)
        {
            return replayed.Error;
        }

        if (replayed.Value is { } committed)
        {
            return committed;
        }

        Result<CanonicalGeneration> generation = await ReadGenerationAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        if (generation.IsFailure)
        {
            return generation.Error;
        }

        if (generation.Value.DatasetGeneration != commit.DatasetGeneration)
        {
            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant dataset generation changed before this curation change could commit.");
        }

        if (generation.Value.KeyReclamationEpoch != commit.ExpectedKeyReclamationEpoch)
        {
            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant key-reclamation epoch changed before this curation change could commit.");
        }

        // Both of the key's epochs are read here, under the write lock, rather than trusted from the
        // request. The dependency epoch is compared with the one the change was prepared against, so a
        // change measured before some other write to the key is refused rather than applied to a key
        // that has moved on.
        CovenantKeyEpochPair epochs = await CovenantKeyEpochs.ReadAsync(
                transaction,
                intent.Subject.NormalizedKey.Value,
                cancellationToken)
            .ConfigureAwait(false);

        if (epochs.Dependency != intent.Subject.KeyDependencyEpoch)
        {
            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This Covenant key changed after the curation change was prepared.");
        }

        // A caller that read the binding epoch itself states it, and a disagreement means the key's
        // epoch row was replaced between that read and this one. A key that was reclaimed and
        // re-created is a different key wearing an old name, and a pin meant for the earlier one must
        // not reach it.
        if (intent.Subject.KeyBindingEpoch is { } asserted && asserted != epochs.Binding)
        {
            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This Covenant key was reclaimed before the curation change could commit.");
        }

        // From here on the subject carries the binding epoch this transaction read. The head and its
        // version are recorded under it, and the receipt returns it.
        CovenantCurationSubject subject = intent.Subject with { KeyBindingEpoch = epochs.Binding };

        HeadRow? head = await ReadHeadAsync(transaction, subject, epochs.Binding, cancellationToken)
            .ConfigureAwait(false);

        long currentRevision = head?.Revision ?? 0;

        if (currentRevision != intent.ExpectedRevision)
        {
            return new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "This Covenant curation subject changed before the requested change could commit.");
        }

        CovenantCurationState current = head is null
            ? CovenantCurationState.None
            : new CovenantCurationState(head.IsPinned, head.IsMasked, head.Revision);

        CovenantCurationState projected = intent.Project(current);

        // A deliberate no-op — pinning what is already pinned — still writes a receipt, so a replay of
        // it is distinguishable from a request that never arrived. It appends no version, because
        // history is a record of changes and nothing changed.
        if (projected.IsPinned == current.IsPinned && projected.IsMasked == current.IsMasked)
        {
            await InsertReceiptAsync(
                    transaction,
                    commit,
                    CovenantMutationOutcome.NoChange,
                    resultingVersionId: null,
                    resultingRevision: null,
                    cancellationToken)
                .ConfigureAwait(false);

            return new CovenantCurationReceipt(
                intent.MutationId,
                CovenantMutationOutcome.NoChange,
                intent.Kind,
                subject,
                current with { Revision = currentRevision },
                null,
                null,
                intent.Authorization.RequestIdempotencyDigest,
                intent.Authorization.FinalMutationDigest,
                intent.Authorization.ResponseReceiptDigest,
                Replayed: false);
        }

        Guid versionId = Guid.CreateVersion7();

        long revision = currentRevision + 1;

        await InsertVersionAsync(
                transaction,
                commit,
                epochs.Binding,
                versionId,
                revision,
                head?.VersionId,
                cancellationToken)
            .ConfigureAwait(false);

        await UpsertHeadAsync(
                transaction,
                commit,
                epochs.Binding,
                versionId,
                revision,
                projected,
                head is not null,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertReceiptAsync(
                transaction,
                commit,
                CovenantMutationOutcome.Applied,
                versionId,
                revision,
                cancellationToken)
            .ConfigureAwait(false);

        return new CovenantCurationReceipt(
            intent.MutationId,
            CovenantMutationOutcome.Applied,
            intent.Kind,
            subject,
            projected with { Revision = revision },
            versionId,
            revision,
            intent.Authorization.RequestIdempotencyDigest,
            intent.Authorization.FinalMutationDigest,
            intent.Authorization.ResponseReceiptDigest,
            Replayed: false);
    }

    private static async ValueTask<Result<CovenantCurationReceipt?>> TryReplayAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationIntent intent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();

        // The subject's current state is joined on the key's current binding epoch, not on the epoch
        // the receipt stored. The receipt keeps the dependency epoch its request digest bound, which
        // has usually moved by the time a replay arrives, and a head is never recorded under it.
        command.CommandText = """
            WITH receipt AS (
                SELECT r.RequestIdempotencyDigest, r.FinalMutationDigest, r.ResponseReceiptDigest,
                       r.CurationKindCode, r.OutcomeCode, r.ResultingVersionId, r.ResultingRevision,
                       r.CampaignId, r.NormalizedKey, r.LaneCode,
                       COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = r.NormalizedKey), 0) AS BindingEpoch
                FROM covenant_curation_receipts r
                WHERE r.MutationId = $mutation
            )
            SELECT receipt.RequestIdempotencyDigest, receipt.FinalMutationDigest, receipt.ResponseReceiptDigest,
                   receipt.CurationKindCode, receipt.OutcomeCode, receipt.ResultingVersionId,
                   receipt.ResultingRevision,
                   COALESCE(h.IsPinned, 0), COALESCE(h.IsMasked, 0), COALESCE(h.CurrentRevision, 0),
                   receipt.BindingEpoch
            FROM receipt
            LEFT JOIN covenant_curation_heads h
                ON h.CampaignId IS receipt.CampaignId AND h.NormalizedKey = receipt.NormalizedKey
                   AND h.LaneCode = receipt.LaneCode AND h.KeyEpoch = receipt.BindingEpoch;
            """;

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<CovenantCurationReceipt?>.Success(null);
        }

        CovenantDigest storedRequest = new((byte[])reader.GetValue(0));

        // The same identity carrying different input is a conflict rather than a second commit: the
        // digest names the whole request, so a client that changed anything about it is asking for
        // something the stored receipt does not answer.
        if (storedRequest != intent.Authorization.RequestIdempotencyDigest)
        {
            return new Error(
                "Security.IdempotencyConflict",
                "This Covenant curation ID was already used with different client input.");
        }

        return Result<CovenantCurationReceipt?>.Success(
            new CovenantCurationReceipt(
                intent.MutationId,
                (CovenantMutationOutcome)reader.GetInt32(4),
                (CovenantCurationKind)reader.GetInt32(3),
                intent.Subject with { KeyBindingEpoch = reader.GetInt64(10) },
                new CovenantCurationState(
                    reader.GetInt32(7) == 1,
                    reader.GetInt32(8) == 1,
                    reader.GetInt64(9)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                storedRequest,
                new CovenantDigest((byte[])reader.GetValue(1)),
                new CovenantDigest((byte[])reader.GetValue(2)),
                Replayed: true));
    }

    private static async ValueTask<Result<CanonicalGeneration>> ReadGenerationAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText =
            "SELECT DatasetGeneration, KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "Covenant canonical state is not present on this installation.");
        }

        return Result<CanonicalGeneration>.Success(
            new CanonicalGeneration(new Guid((byte[])reader.GetValue(0)), reader.GetInt64(1)));
    }

    private static async ValueTask<HeadRow?> ReadHeadAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationSubject subject,
        long bindingEpoch,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT CurrentVersionId, CurrentRevision, IsPinned, IsMasked
            FROM covenant_curation_heads
            WHERE CampaignId IS $campaign AND NormalizedKey = $key AND LaneCode = $lane AND KeyEpoch = $epoch;
            """;

        BindSubject(command, subject, bindingEpoch);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new HeadRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetInt64(1),
                reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1)
            : null;
    }

    private static async ValueTask InsertVersionAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationCommit commit,
        long bindingEpoch,
        Guid versionId,
        long revision,
        Guid? predecessorVersionId,
        CancellationToken cancellationToken)
    {
        CovenantCurationIntent intent = commit.Intent;

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_curation_versions (
                CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                CurationKindCode, Revision, PredecessorVersionId, MutationId,
                RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, CreatedAtUtc)
            VALUES (
                $version, $scope, $campaign, $key, $lane, $epoch,
                $kind, $revision, $predecessor, $mutation,
                $request, $authorization, $final, $created);
            """;

        Bind(command, "$version", versionId.ToString("D"));

        BindSubject(command, intent.Subject, bindingEpoch);

        Bind(command, "$scope", (int)intent.Subject.Scope.Kind);

        Bind(command, "$kind", (int)intent.Kind);

        Bind(command, "$revision", revision);

        Bind(
            command,
            "$predecessor",
            predecessorVersionId is { } predecessor ? predecessor.ToString("D") : DBNull.Value);

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        Bind(command, "$request", intent.Authorization.RequestIdempotencyDigest.Bytes);

        Bind(command, "$authorization", intent.Authorization.AuthorizationDigest.Bytes);

        Bind(command, "$final", intent.Authorization.FinalMutationDigest.Bytes);

        Bind(command, "$created", Iso(commit.CommittedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpsertHeadAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationCommit commit,
        long bindingEpoch,
        Guid versionId,
        long revision,
        CovenantCurationState projected,
        bool headExists,
        CancellationToken cancellationToken)
    {
        CovenantCurationIntent intent = commit.Intent;

        await using SqliteCommand command = transaction.CreateCommand();

        // Written as an explicit update-or-insert rather than an ON CONFLICT upsert, because the
        // subject's identity is two partial unique indexes over a nullable Campaign and a conflict
        // target has to name exactly one of them.
        command.CommandText = headExists
            ? """
                UPDATE covenant_curation_heads
                SET IsPinned = $pinned,
                    IsMasked = $masked,
                    CurrentVersionId = $version,
                    CurrentRevision = $revision,
                    UpdatedAtUtc = $updated
                WHERE CampaignId IS $campaign AND NormalizedKey = $key AND LaneCode = $lane AND KeyEpoch = $epoch;
                """
            : """
                INSERT INTO covenant_curation_heads (
                    ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                    IsPinned, IsMasked, CurrentVersionId, CurrentRevision, UpdatedAtUtc)
                VALUES ($scope, $campaign, $key, $lane, $epoch, $pinned, $masked, $version, $revision, $updated);
                """;

        BindSubject(command, intent.Subject, bindingEpoch);

        Bind(command, "$scope", (int)intent.Subject.Scope.Kind);

        Bind(command, "$pinned", projected.IsPinned ? 1 : 0);

        Bind(command, "$masked", projected.IsMasked ? 1 : 0);

        Bind(command, "$version", versionId.ToString("D"));

        Bind(command, "$revision", revision);

        Bind(command, "$updated", Iso(commit.CommittedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertReceiptAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationCommit commit,
        CovenantMutationOutcome outcome,
        Guid? resultingVersionId,
        long? resultingRevision,
        CancellationToken cancellationToken)
    {
        CovenantCurationIntent intent = commit.Intent;

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_curation_receipts (
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest,
                CurationKindCode, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch,
                OutcomeCode, ResultingVersionId, ResultingRevision, ResponseReceiptDigest, CommittedAtUtc)
            VALUES (
                $mutation, $request, $authorization, $final,
                $kind, $scope, $campaign, $key, $lane, $epoch,
                $outcome, $version, $revision, $response, $committed);
            """;

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        Bind(command, "$request", intent.Authorization.RequestIdempotencyDigest.Bytes);

        Bind(command, "$authorization", intent.Authorization.AuthorizationDigest.Bytes);

        Bind(command, "$final", intent.Authorization.FinalMutationDigest.Bytes);

        Bind(command, "$kind", (int)intent.Kind);

        Bind(command, "$scope", (int)intent.Subject.Scope.Kind);

        // The receipt keeps the dependency epoch, not the binding epoch its head and version carry.
        // The request digest bound the dependency epoch, and a replay recomputes that digest from this
        // column without a token to read it from.
        BindSubject(command, intent.Subject, intent.Subject.KeyDependencyEpoch);

        Bind(command, "$outcome", (int)outcome);

        Bind(
            command,
            "$version",
            resultingVersionId is { } version ? version.ToString("D") : DBNull.Value);

        Bind(command, "$revision", resultingRevision is { } revision ? revision : DBNull.Value);

        Bind(command, "$response", intent.Authorization.ResponseReceiptDigest.Bytes);

        Bind(command, "$committed", Iso(commit.CommittedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds the subject's scoped key and lane, and the epoch the statement's table records.
    /// </summary>
    /// <remarks>
    /// The epoch is a parameter rather than read off the subject, because the tables disagree about
    /// which one they hold: a head and a version record the binding epoch, a receipt the dependency
    /// epoch. Each caller states which, so neither can be bound by default.
    /// </remarks>
    private static void BindSubject(SqliteCommand command, CovenantCurationSubject subject, long epoch)
    {
        Bind(
            command,
            "$campaign",
            subject.Scope.CampaignId is { } campaignId ? campaignId.ToString("D") : DBNull.Value);

        Bind(command, "$key", subject.NormalizedKey.Value);

        Bind(command, "$lane", (int)subject.Lane);

        Bind(command, "$epoch", epoch);
    }

    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static string Iso(DateTimeOffset value) =>
        UtcInstantText.Format(value);

    private readonly record struct CanonicalGeneration(Guid DatasetGeneration, long KeyReclamationEpoch);

    private sealed record HeadRow(Guid VersionId, long Revision, bool IsPinned, bool IsMasked);
}
