using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one writer that turns validated mutation intents into canonical rows.
/// </summary>
/// <remarks>
/// There is exactly one entry point and it takes exactly one sealed batch plus the caller's live
/// transaction. No overload accepts a separate authenticated command or a loose compiled artifact,
/// because a second parameter is a second thing that can disagree with the first.
///
/// <para>Ordering inside a batch matters. Receipt replay resolves first, so an exact retry returns
/// its committed answer even after the head has moved on; only then do compare-and-swap, lifecycle,
/// and epoch checks run. Reversing that order would turn a successful retry into a revision
/// conflict.</para>
///
/// <para>The kernel never opens, commits, rolls back, or retries a transaction. A failure returns a
/// typed error and leaves the caller's transaction to be rolled back as a whole, which is what makes
/// "a failed batch writes nothing" true rather than merely intended.</para>
/// </remarks>
internal sealed class CovenantMutationKernel(CovenantQuotaGuard quotas)
{

    /// <summary>
    /// A kernel over the process-wide connection initializer, for callers with no guard of their own.
    /// </summary>
    internal CovenantMutationKernel()
        : this(new CovenantQuotaGuard())
    {
    }

    public async ValueTask<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyBatchAsync(
        CovenantMutationBatch batch,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(batch);

        ArgumentNullException.ThrowIfNull(transaction);

        Result<CanonicalState> state = await ReadStateAsync(transaction, cancellationToken).ConfigureAwait(false);

        if (state.IsFailure)
        {

            return state.Error;

        }

        if (state.Value.DatasetGeneration != batch.DatasetGeneration)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant dataset generation changed before this mutation batch could commit.");

        }

        if (state.Value.KeyReclamationEpoch != batch.ExpectedKeyReclamationEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant key-reclamation epoch changed before this mutation batch could commit.");

        }

        // Only a batch that bound the registry is checked against it. A Campaign mutation reaches one
        // Campaign and states no requirement here, and comparing it to a stand-in value would refuse
        // it on every installation whose registry had ever advanced — which is every installation that
        // has a Campaign for it to apply to.
        if (batch.ExpectedCampaignRegistryEpoch is { } expectedRegistryEpoch)
        {

            long registryEpoch = await ReadCampaignRegistryEpochAsync(transaction, cancellationToken)
                .ConfigureAwait(false);

            if (registryEpoch != expectedRegistryEpoch)
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The Campaign registry epoch changed before this mutation batch could commit.");

            }

        }

        // One capacity check per scope for the whole batch, before anything is appended. Checking
        // per intent would let two intents in the same batch each see room that only one can take.
        foreach (IGrouping<(CovenantScope Kind, Guid Campaign), CovenantMutationIntent> group in batch.Intents
            .GroupBy(static intent => (
                intent.Target.Scope.Kind,
                intent.Target.Scope.CampaignId ?? Guid.Empty)))
        {

            CovenantOperationScope scope = group.Key.Kind == CovenantScope.Global
                ? CovenantOperationScope.Global
                : CovenantOperationScope.ForCampaign(group.Key.Campaign);

            Result<CovenantQuotaSnapshot> capacity = await quotas
                .CheckCanonicalCapacityAsync(scope, CovenantQuotaDemand.ForBatch(group), transaction, cancellationToken)
                .ConfigureAwait(false);

            if (capacity.IsFailure)
            {

                return capacity.Error;

            }

        }

        List<CovenantMutationReceipt> receipts = [];

        int changedHeads = 0;

        long searchSequence = 0;

        foreach (CovenantMutationIntent intent in batch.Intents)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Result<CovenantMutationReceipt?> replayed = await TryReplayAsync(transaction, intent, cancellationToken)
                .ConfigureAwait(false);

            if (replayed.IsFailure)
            {

                return replayed.Error;

            }

            if (replayed.Value is { } existing)
            {

                receipts.Add(existing);

                continue;

            }

            if (searchSequence == 0)
            {

                // One sequence per batch, allocated lazily: a batch that turns out to be entirely
                // NoChange must not advance a counter the accelerator uses to detect real work.
                searchSequence = checked(state.Value.CanonicalSearchSequence + 1);

            }

            Result<AppliedMutation> applied = await ApplyIntentAsync(
                    transaction,
                    batch,
                    intent,
                    searchSequence,
                    changedHeads,
                    cancellationToken)
                .ConfigureAwait(false);

            if (applied.IsFailure)
            {

                return applied.Error;

            }

            receipts.Add(applied.Value.Receipt);

            if (applied.Value.HeadChanged)
            {

                changedHeads = checked(changedHeads + 1);

            }

        }

        if (changedHeads > 0)
        {

            await AdvanceSearchSequenceAsync(transaction, searchSequence, batch.CommittedAtUtc, cancellationToken)
                .ConfigureAwait(false);

        }

        return receipts;

    }

    private static async ValueTask<Result<CovenantMutationReceipt?>> TryReplayAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationIntent intent,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // The receipt table carries no EntryId column, so the committed entry has to be recovered
        // through what the row does record. An Applied outcome names a resulting version, and every
        // version belongs to exactly one entry. A NoChange outcome has a NULL version by the table's
        // own CHECK, so it resolves through the scoped key instead, which the partial unique indexes
        // on covenant_entries make a single row.
        command.CommandText = """
            SELECT r.RequestIdempotencyDigest, r.FinalMutationDigest, r.ResponseReceiptDigest, r.MutationKindCode,
                   r.ScopeCode, r.CampaignId, r.LaneCode, r.OutcomeCode, r.ResultingVersionId, r.ResultingLaneRevision,
                   COALESCE(
                       (SELECT v.EntryId FROM covenant_versions v WHERE v.VersionId = r.ResultingVersionId),
                       (SELECT e.EntryId FROM covenant_entries e
                        WHERE e.ScopeCode = r.ScopeCode AND e.CampaignId IS r.CampaignId
                          AND e.NormalizedKey = $key))
            FROM covenant_mutation_receipts r
            WHERE r.MutationId = $mutation;
            """;

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        Bind(command, "$key", intent.Target.NormalizedKey.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantMutationReceipt?>.Success(null);

        }

        CovenantDigest storedRequest = new((byte[])reader.GetValue(0));

        if (storedRequest != intent.Authorization.RequestIdempotencyDigest)
        {

            return new Error(
                "Security.IdempotencyConflict",
                "This Covenant mutation ID was already used with different client input.");

        }

        Guid? resultingVersionId = reader.IsDBNull(8)
            ? null
            : Guid.Parse(reader.GetString(8), CultureInfo.InvariantCulture);

        // The scope comes from the committed row rather than from the retry, so a replay reports
        // what was actually recorded instead of echoing back whatever the caller resent. The
        // normalized key is the one field the table does not store, so it still comes from the
        // intent, and the resolved entry above is the cross-check that it named the same row.
        CovenantOperationScope storedScope = (CovenantScope)reader.GetInt32(4) == CovenantScope.Global
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(Guid.Parse(reader.GetString(5), CultureInfo.InvariantCulture));

        // Only null when the entry itself is gone, which owner cleanup does to the receipts in the
        // same transaction. There is then no identity to name and Guid.Empty is the honest answer.
        Guid entryId = reader.IsDBNull(10)
            ? Guid.Empty
            : Guid.Parse(reader.GetString(10), CultureInfo.InvariantCulture);

        return Result<CovenantMutationReceipt?>.Success(
            new CovenantMutationReceipt(
                intent.MutationId,
                (CovenantMutationOutcome)reader.GetInt32(7),
                (CovenantMutationKind)reader.GetInt32(3),
                storedScope,
                intent.Target.NormalizedKey.Value,
                (CovenantLane)reader.GetInt32(6),
                entryId,
                resultingVersionId,
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                storedRequest,
                new CovenantDigest((byte[])reader.GetValue(1)),
                new CovenantDigest((byte[])reader.GetValue(2)),
                Replayed: true));

    }

    private static async ValueTask<Result<AppliedMutation>> ApplyIntentAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationBatch batch,
        CovenantMutationIntent intent,
        long searchSequence,
        int ordinal,
        CancellationToken cancellationToken)
    {

        HeadRow? head = await ReadHeadAsync(transaction, intent, cancellationToken).ConfigureAwait(false);

        long keyEpoch = await ReadKeyEpochAsync(transaction, intent.Target.NormalizedKey.Value, cancellationToken)
            .ConfigureAwait(false);

        if (keyEpoch != intent.ExpectedKeyEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This normalized key changed after the mutation was prepared.");

        }

        long currentRevision = head?.LaneRevision ?? 0;

        if (currentRevision != intent.ExpectedLaneRevision)
        {

            return new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "The targeted Covenant lane head is not at the expected revision.");

        }

        bool retired = head is { OperationCode: (int)CovenantOperation.Retire };

        Result<bool> lifecycle = ValidateLifecycle(intent, head, retired);

        if (lifecycle.IsFailure)
        {

            return lifecycle.Error;

        }

        if (!lifecycle.Value)
        {

            CovenantMutationReceipt noChange = new(
                intent.MutationId,
                CovenantMutationOutcome.NoChange,
                intent.Kind,
                intent.Target.Scope,
                intent.Target.NormalizedKey.Value,
                intent.Target.Lane,
                head!.EntryId,
                ResultingVersionId: null,
                ResultingLaneRevision: null,
                intent.Authorization.RequestIdempotencyDigest,
                intent.Authorization.FinalMutationDigest,
                intent.Authorization.ResponseReceiptDigest,
                Replayed: false);

            await InsertReceiptAsync(transaction, batch, intent, noChange, cancellationToken).ConfigureAwait(false);

            return new AppliedMutation(noChange, HeadChanged: false);

        }

        Guid entryId = head?.EntryId
            ?? await ResolveOrCreateEntryAsync(transaction, batch, intent, cancellationToken).ConfigureAwait(false);

        long nextRevision = checked(currentRevision + 1);

        Guid versionId = Guid.NewGuid();

        CovenantDigest provenanceDigest = ProvenanceDigest(intent.Provenance);

        await InsertVersionAsync(
                transaction,
                batch,
                intent,
                versionId,
                entryId,
                nextRevision,
                head?.CurrentVersionId,
                provenanceDigest,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (CovenantMutationProvenanceLeaf leaf in intent.Provenance)
        {

            await InsertProvenanceAsync(transaction, versionId, leaf, cancellationToken).ConfigureAwait(false);

        }

        long searchRowId = head?.SearchRowId
            ?? await AllocateSearchRowIdAsync(transaction, batch.CommittedAtUtc, cancellationToken)
                .ConfigureAwait(false);

        await UpsertHeadAsync(
                transaction,
                batch,
                intent,
                entryId,
                versionId,
                nextRevision,
                searchRowId,
                head is not null,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertOutboxAsync(
                transaction,
                searchSequence,
                ordinal,
                searchRowId,
                entryId,
                intent.Target.Lane,
                versionId,
                cancellationToken)
            .ConfigureAwait(false);

        CovenantMutationReceipt receipt = new(
            intent.MutationId,
            CovenantMutationOutcome.Applied,
            intent.Kind,
            intent.Target.Scope,
            intent.Target.NormalizedKey.Value,
            intent.Target.Lane,
            entryId,
            versionId,
            nextRevision,
            intent.Authorization.RequestIdempotencyDigest,
            intent.Authorization.FinalMutationDigest,
            intent.Authorization.ResponseReceiptDigest,
            Replayed: false);

        await InsertReceiptAsync(transaction, batch, intent, receipt, cancellationToken).ConfigureAwait(false);

        return new AppliedMutation(receipt, HeadChanged: true);

    }

    /// <summary>
    /// Decides whether this intent may append at all. <see langword="true"/> means append,
    /// <see langword="false"/> means the deliberate no-op, and a failure means a lifecycle refusal.
    /// </summary>
    private static Result<bool> ValidateLifecycle(CovenantMutationIntent intent, HeadRow? head, bool retired)
    {

        if (intent.Operation == CovenantOperation.Retire)
        {

            if (head is null)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "There is no Covenant head in this scope and lane to retire.");

            }

            // Retiring a tombstone is a deliberate no-op rather than an error: the caller's intent is
            // already satisfied.
            return Result<bool>.Success(!retired);

        }

        if (retired)
        {

            if (intent.Target.Lane == CovenantLane.Proposed)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "An agent cannot reactivate a retired Proposed Covenant lane.");

            }

            if (!intent.Reactivate)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This Covenant head is retired and reactivation was not requested.");

            }

            return Result<bool>.Success(true);

        }

        if (head is null)
        {

            return Result<bool>.Success(true);

        }

        // Byte-identical authored and compiled content against a live head changes nothing, so it
        // must not consume a revision that other callers are comparing against.
        bool identical = head.AuthoredHash == intent.Artifact!.AuthoredHash
            && head.RenderedHash == intent.Artifact.RenderedHash;

        return Result<bool>.Success(!identical);

    }

    private static CovenantDigest ProvenanceDigest(ImmutableArray<CovenantMutationProvenanceLeaf> provenance) =>
        CovenantDigests.Materialization(
            new MaterializationDigestInput(
                Unprovenanced: provenance.Length == 0,
                [.. provenance.Select(static leaf => new MaterializationSourceDigestInput(
                    leaf.AttachmentId,
                    leaf.AttachmentVersionIdentity,
                    leaf.LogicalKey,
                    leaf.ContentHash,
                    leaf.SourceRangeKind,
                    leaf.SourceStart is { } start ? checked((uint)start) : null,
                    leaf.SourceEnd is { } end ? checked((uint)end) : null,
                    []))]));

    private static async ValueTask<Result<CanonicalState>> ReadStateAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration, CanonicalSearchSequence, KeyReclamationEpoch, NextSearchRowId
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant canonical tier has no state row, so nothing can be published against it.");

        }

        return new CanonicalState(
            new Guid((byte[])reader.GetValue(0)),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));

    }

    private static async ValueTask<long> ReadCampaignRegistryEpochAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async ValueTask<long> ReadKeyEpochAsync(
        CovenantMutationTransaction transaction,
        string normalizedKey,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT COALESCE(MAX(KeyEpoch), 0) FROM covenant_key_epochs WHERE NormalizedKey = $key;";

        Bind(command, "$key", normalizedKey);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async ValueTask<HeadRow?> ReadHeadAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationIntent intent,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = $"""
            SELECT h.EntryId, h.CurrentVersionId, h.CurrentLaneRevision, h.CurrentOperationCode, h.SearchRowId,
                   v.AuthoredHash, v.RenderedHash
            FROM covenant_heads h
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE {(intent.Target.Scope.Kind == CovenantScope.Campaign
                ? "h.CampaignId = $campaign"
                : "h.CampaignId IS NULL")}
              AND h.NormalizedKey = $key AND h.LaneCode = $lane;
            """;

        Bind(command, "$key", intent.Target.NormalizedKey.Value);

        Bind(command, "$lane", (int)intent.Target.Lane);

        if (intent.Target.Scope.Kind == CovenantScope.Campaign)
        {

            Bind(command, "$campaign", intent.Target.Scope.CampaignId!.Value.ToString("D"));

        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new HeadRow(
            Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : new CovenantDigest((byte[])reader.GetValue(5)),
            reader.IsDBNull(6) ? null : new CovenantDigest((byte[])reader.GetValue(6)));

    }

    private static async ValueTask<Guid> ResolveOrCreateEntryAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationBatch batch,
        CovenantMutationIntent intent,
        CancellationToken cancellationToken)
    {

        await using (SqliteCommand existing = transaction.CreateCommand())
        {

            existing.CommandText = $"""
                SELECT EntryId
                FROM covenant_entries
                WHERE {(intent.Target.Scope.Kind == CovenantScope.Campaign
                    ? "CampaignId = $campaign"
                    : "CampaignId IS NULL")}
                  AND NormalizedKey = $key;
                """;

            Bind(existing, "$key", intent.Target.NormalizedKey.Value);

            if (intent.Target.Scope.Kind == CovenantScope.Campaign)
            {

                Bind(existing, "$campaign", intent.Target.Scope.CampaignId!.Value.ToString("D"));

            }

            object? found = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (found is string text)
            {

                return Guid.Parse(text, CultureInfo.InvariantCulture);

            }

        }

        Guid entryId = Guid.NewGuid();

        await using SqliteCommand insert = transaction.CreateCommand();

        insert.CommandText = """
            INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ($entry, $scope, $campaign, $authored, $normalized, $created);
            """;

        Bind(insert, "$entry", entryId.ToString("D"));

        Bind(insert, "$scope", (int)intent.Target.Scope.Kind);

        Bind(
            insert,
            "$campaign",
            intent.Target.Scope.CampaignId is { } campaignId ? campaignId.ToString("D") : DBNull.Value);

        Bind(insert, "$authored", intent.Target.AuthoredKey);

        Bind(insert, "$normalized", intent.Target.NormalizedKey.Value);

        Bind(insert, "$created", Iso(batch.CommittedAtUtc));

        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return entryId;

    }

    private static async ValueTask InsertVersionAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationBatch batch,
        CovenantMutationIntent intent,
        Guid versionId,
        Guid entryId,
        long laneRevision,
        Guid? predecessorVersionId,
        CovenantDigest provenanceDigest,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_versions (
                VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
                AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
                RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
                AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
                RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
                AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
            VALUES (
                $version, $entry, $lane, $revision, $operation, $authored, $compiled,
                $authoredHash, $renderedHash, $byteCost, $fence, $compilerPolicy,
                $rendererPolicy, $origin, $turn, $toolCall, $basePlan,
                $admission, $ward, $authorizationMode, $mutation,
                $requestDigest, $authorizationDigest, $finalDigest, $predecessor,
                $provenanceCount, $provenanceDigest, $created);
            """;

        CovenantMutationArtifact? artifact = intent.Artifact;

        Bind(command, "$version", versionId.ToString("D"));

        Bind(command, "$entry", entryId.ToString("D"));

        Bind(command, "$lane", (int)intent.Target.Lane);

        Bind(command, "$revision", laneRevision);

        Bind(command, "$operation", (int)intent.Operation);

        Bind(command, "$authored", (object?)artifact?.AuthoredContent ?? DBNull.Value);

        Bind(command, "$compiled", (object?)artifact?.CompiledContent ?? DBNull.Value);

        Bind(command, "$authoredHash", (object?)artifact?.AuthoredHash.Bytes ?? DBNull.Value);

        Bind(command, "$renderedHash", (object?)artifact?.RenderedHash.Bytes ?? DBNull.Value);

        Bind(command, "$byteCost", artifact?.CompiledByteCost ?? 0);

        Bind(command, "$fence", artifact?.RequiredFenceLength ?? 0);

        Bind(command, "$compilerPolicy", artifact?.CompilerPolicyVersion ?? CovenantCompiler.CompilerPolicyVersion);

        Bind(command, "$rendererPolicy", artifact?.RendererPolicyVersion ?? CovenantCompiler.RendererPolicyVersion);

        Bind(command, "$origin", (int)intent.Origin);

        Bind(
            command,
            "$turn",
            intent.SourceTurnId is { } turnId ? turnId.ToString("D") : DBNull.Value);

        Bind(command, "$toolCall", (object?)intent.SourceToolCallId ?? DBNull.Value);

        Bind(command, "$basePlan", (object?)intent.BasePlanDigest?.Bytes ?? DBNull.Value);

        Bind(command, "$admission", (object?)intent.AdmissionReceiptDigest?.Bytes ?? DBNull.Value);

        Bind(command, "$ward", (object?)intent.Authorization.WardReceiptDigest?.Bytes ?? DBNull.Value);

        Bind(
            command,
            "$authorizationMode",
            intent.Origin == CovenantOrigin.AgentApproved ? (int)intent.Authorization.Mode : DBNull.Value);

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        Bind(command, "$requestDigest", intent.Authorization.RequestIdempotencyDigest.Bytes);

        Bind(command, "$authorizationDigest", intent.Authorization.AuthorizationDigest.Bytes);

        Bind(command, "$finalDigest", intent.Authorization.FinalMutationDigest.Bytes);

        Bind(
            command,
            "$predecessor",
            predecessorVersionId is { } predecessor ? predecessor.ToString("D") : DBNull.Value);

        Bind(command, "$provenanceCount", intent.Provenance.Length);

        Bind(command, "$provenanceDigest", provenanceDigest.Bytes);

        Bind(command, "$created", Iso(batch.CommittedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask InsertProvenanceAsync(
        CovenantMutationTransaction transaction,
        Guid versionId,
        CovenantMutationProvenanceLeaf leaf,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_version_attachment_provenance (
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference)
            VALUES ($version, $ordinal, $attachment, $identity, $logical, $hash, $rangeKind, $start, $end, $turn, $reference);
            """;

        Bind(command, "$version", versionId.ToString("D"));

        Bind(command, "$ordinal", leaf.Ordinal);

        Bind(command, "$attachment", leaf.AttachmentId.ToString("D"));

        Bind(command, "$identity", leaf.AttachmentVersionIdentity.ToString("D"));

        Bind(command, "$logical", leaf.LogicalKey);

        Bind(command, "$hash", leaf.ContentHash.Bytes);

        Bind(command, "$rangeKind", (int)leaf.SourceRangeKind);

        Bind(command, "$start", (object?)leaf.SourceStart ?? DBNull.Value);

        Bind(command, "$end", (object?)leaf.SourceEnd ?? DBNull.Value);

        Bind(command, "$turn", leaf.SourceTurnId is { } turnId ? turnId.ToString("D") : DBNull.Value);

        Bind(command, "$reference", (object?)leaf.MaterializationReference ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask<long> AllocateSearchRowIdAsync(
        CovenantMutationTransaction transaction,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // The counter advances before the head exists, because the head insert trigger refuses a row
        // ID at or past it. The ID is never reused within a dataset generation, so an accelerator row
        // can never be claimed by a different head after a delete and recreate.
        command.CommandText = """
            UPDATE covenant_state
            SET NextSearchRowId = NextSearchRowId + 1, UpdatedAtUtc = $updated
            WHERE StateKey = 1
            RETURNING NextSearchRowId - 1;
            """;

        Bind(command, "$updated", Iso(committedAtUtc));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async ValueTask UpsertHeadAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationBatch batch,
        CovenantMutationIntent intent,
        Guid entryId,
        Guid versionId,
        long laneRevision,
        long searchRowId,
        bool exists,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = exists
            ? """
              UPDATE covenant_heads
              SET CurrentVersionId = $version,
                  CurrentLaneRevision = $revision,
                  CurrentOperationCode = $operation,
                  CompiledByteCost = $cost,
                  OriginCode = $origin,
                  UpdatedAtUtc = $updated
              WHERE EntryId = $entry AND LaneCode = $lane AND CurrentLaneRevision = $expected;
              """
            : """
              INSERT INTO covenant_heads (
                  EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode, ScopeCode,
                  CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
              VALUES ($entry, $lane, $version, $revision, $operation, $scope, $campaign, $key, $cost, $origin, $row, $updated);
              """;

        Bind(command, "$entry", entryId.ToString("D"));

        Bind(command, "$lane", (int)intent.Target.Lane);

        Bind(command, "$version", versionId.ToString("D"));

        Bind(command, "$revision", laneRevision);

        Bind(command, "$operation", (int)intent.Operation);

        Bind(command, "$cost", intent.Artifact?.CompiledByteCost ?? 0);

        Bind(command, "$origin", (int)intent.Origin);

        Bind(command, "$updated", Iso(batch.CommittedAtUtc));

        if (exists)
        {

            Bind(command, "$expected", intent.ExpectedLaneRevision);

        }
        else
        {

            Bind(command, "$scope", (int)intent.Target.Scope.Kind);

            Bind(
                command,
                "$campaign",
                intent.Target.Scope.CampaignId is { } campaignId ? campaignId.ToString("D") : DBNull.Value);

            Bind(command, "$key", intent.Target.NormalizedKey.Value);

            Bind(command, "$row", searchRowId);

        }

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {

            // The revision matched when it was read and did not match when it was written, so another
            // writer committed in between. Failing here rolls the whole batch back.
            throw new InvalidOperationException(
                "The Covenant lane head changed between its compare and its swap.");

        }

    }

    private static async ValueTask InsertOutboxAsync(
        CovenantMutationTransaction transaction,
        long searchSequence,
        int ordinal,
        long searchRowId,
        Guid entryId,
        CovenantLane lane,
        Guid desiredVersionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            VALUES ($sequence, $ordinal, $row, $entry, $lane, $desired);
            """;

        Bind(command, "$sequence", searchSequence);

        Bind(command, "$ordinal", ordinal);

        Bind(command, "$row", searchRowId);

        Bind(command, "$entry", entryId.ToString("D"));

        Bind(command, "$lane", (int)lane);

        Bind(command, "$desired", desiredVersionId.ToString("D"));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask InsertReceiptAsync(
        CovenantMutationTransaction transaction,
        CovenantMutationBatch batch,
        CovenantMutationIntent intent,
        CovenantMutationReceipt receipt,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_mutation_receipts (
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, MutationKindCode,
                ScopeCode, CampaignId, TargetIdentityDigest, LaneCode, OutcomeCode, ResultingVersionId,
                ResultingLaneRevision, ResponseReceiptDigest, SourceTurnId, CommittedAtUtc)
            VALUES (
                $mutation, $requestDigest, $authorizationDigest, $finalDigest, $kind,
                $scope, $campaign, $target, $lane, $outcome, $version,
                $revision, $responseDigest, $turn, $committed);
            """;

        Bind(command, "$mutation", intent.MutationId.ToString("D"));

        Bind(command, "$requestDigest", intent.Authorization.RequestIdempotencyDigest.Bytes);

        Bind(command, "$authorizationDigest", intent.Authorization.AuthorizationDigest.Bytes);

        Bind(command, "$finalDigest", intent.Authorization.FinalMutationDigest.Bytes);

        Bind(command, "$kind", (int)intent.Kind);

        Bind(command, "$scope", (int)intent.Target.Scope.Kind);

        Bind(
            command,
            "$campaign",
            intent.Target.Scope.CampaignId is { } campaignId ? campaignId.ToString("D") : DBNull.Value);

        Bind(command, "$target", intent.Target.IdentityDigest.Bytes);

        Bind(command, "$lane", (int)intent.Target.Lane);

        Bind(command, "$outcome", (int)receipt.Outcome);

        Bind(
            command,
            "$version",
            receipt.ResultingVersionId is { } versionId ? versionId.ToString("D") : DBNull.Value);

        Bind(command, "$revision", (object?)receipt.ResultingLaneRevision ?? DBNull.Value);

        Bind(command, "$responseDigest", intent.Authorization.ResponseReceiptDigest.Bytes);

        Bind(command, "$turn", intent.SourceTurnId is { } turnId ? turnId.ToString("D") : DBNull.Value);

        Bind(command, "$committed", Iso(batch.CommittedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask AdvanceSearchSequenceAsync(
        CovenantMutationTransaction transaction,
        long searchSequence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            UPDATE covenant_state
            SET CanonicalSearchSequence = $sequence, UpdatedAtUtc = $updated
            WHERE StateKey = 1 AND CanonicalSearchSequence = $sequence - 1;
            """;

        Bind(command, "$sequence", searchSequence);

        Bind(command, "$updated", Iso(committedAtUtc));

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {

            throw new InvalidOperationException(
                "The Covenant canonical search sequence moved while this batch was publishing.");

        }

    }

    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private readonly record struct CanonicalState(
        Guid DatasetGeneration,
        long CanonicalSearchSequence,
        long KeyReclamationEpoch,
        long NextSearchRowId);

    private sealed record HeadRow(
        Guid EntryId,
        Guid CurrentVersionId,
        long LaneRevision,
        int OperationCode,
        long SearchRowId,
        CovenantDigest? AuthoredHash,
        CovenantDigest? RenderedHash);

    private readonly record struct AppliedMutation(CovenantMutationReceipt Receipt, bool HeadChanged);

}
