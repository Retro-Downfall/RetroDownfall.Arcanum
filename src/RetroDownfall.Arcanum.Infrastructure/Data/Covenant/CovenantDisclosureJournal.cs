using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The core disclosure journal writer (§10.13).
/// </summary>
/// <remarks>
/// One immediate transaction per acknowledgement, because the subject's ordinal allocation, its
/// counters, its rolling chain head, and the receipt row have to move together. Allocating an
/// ordinal outside the transaction that inserts the row is how two parallel provider attempts end up
/// claiming the same one, and the chain digest then commits to a sequence that never happened.
/// </remarks>
internal sealed class CovenantDisclosureJournal(
    ICovenantConnectionSource connections,
    Guid bootId) : ICovenantDisclosureJournal
{

    public async ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        ProviderCallSensitivity sensitivity,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(draft);

        ArgumentNullException.ThrowIfNull(sensitivity);

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        // The draft names a sensitivity by digest and the caller supplies the value behind it. If
        // the two disagree, the receipt would record provenance for a payload that was never sent.
        if (sensitivity.Digest != draft.SensitivityDigest)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The supplied sensitivity does not match the disclosure draft's frozen digest.");

        }

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(
            () => AcknowledgeWithinTransactionAsync(
                connection,
                draft,
                category,
                sensitivity,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<CovenantDisclosureReceipt>> AcknowledgeWithinTransactionAsync(
        SqliteConnection connection,
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        ProviderCallSensitivity sensitivity,
        CancellationToken cancellationToken)
    {

        // No authorization scope: the journal is append-only by schema. Its guard triggers refuse
        // update and delete outright rather than gating them on a capability, because a receipt that
        // could be rewritten under some authorization would not be evidence of anything.
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        CovenantMutationTransaction owned = new(connection, transaction);

        ulong? replayed = await ReadReplayedOrdinalAsync(owned, draft, cancellationToken)
            .ConfigureAwait(false);

        if (replayed is { } ordinal)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, ordinal));

        }

        SubjectState state = await ReadOrSeedSubjectAsync(owned, draft, cancellationToken)
            .ConfigureAwait(false);

        if (state.Lifecycle is not (1 or 2))
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This disclosure subject is closed and cannot record another effect.");

        }

        ulong allocated = checked(state.LastAllocatedOrdinal + 1);

        ulong attemptOrdinal = checked(
            await CountCategoryAsync(owned, draft, category, cancellationToken).ConfigureAwait(false) + 1);

        CovenantDisclosureReceipt receipt = new(draft, allocated);

        CovenantDisclosureChain chain = CovenantEvidenceChains.AppendDisclosure(
            new CovenantDisclosureChain(state.ExternalEffectCount, state.ChainHead),
            receipt.Digest);

        await InsertReceiptAsync(
            owned,
            draft,
            category,
            sensitivity,
            allocated,
            attemptOrdinal,
            cancellationToken).ConfigureAwait(false);

        await AdvanceSubjectAsync(
            owned,
            draft,
            allocated,
            category,
            chain,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result<CovenantDisclosureReceipt>.Success(receipt);

    }

    private static async Task<ulong?> ReadReplayedOrdinalAsync(
        CovenantMutationTransaction transaction,
        CovenantDisclosureDraft draft,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT SubjectOrdinal
            FROM external_disclosure_receipts
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject
                AND EffectIdentityDigest = $effect;
            """;

        BindSubject(command, draft);

        _ = command.Parameters.AddWithValue("$effect", draft.EffectIdentityDigest.Bytes);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : (ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private async Task<SubjectState> ReadOrSeedSubjectAsync(
        CovenantMutationTransaction transaction,
        CovenantDisclosureDraft draft,
        CancellationToken cancellationToken)
    {

        await using (SqliteCommand seed = transaction.CreateCommand())
        {

            seed.CommandText = """
                INSERT OR IGNORE INTO disclosure_subject_state (
                    OriginInstallationId, SubjectKind, SubjectId, LifecycleCode, CreatorBootId,
                    LastHeartbeatAtUtc, ClosedAtUtc, ProviderAttemptCount, ExternalEffectCount,
                    LastAllocatedOrdinal, LastFoldedOrdinal, DisclosureChainDigest)
                VALUES ($installation, $kind, $subject, 1, $boot, $now, NULL, 0, 0, 0, 0, $seedChain);
                """;

            BindSubject(seed, draft);

            _ = seed.Parameters.AddWithValue("$boot", bootId.ToString("D"));

            _ = seed.Parameters.AddWithValue("$now", Iso(draft.Timestamp));

            _ = seed.Parameters.AddWithValue(
                "$seedChain",
                CovenantEvidenceChains.SeedDisclosureChain().Head.Bytes);

            _ = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT LifecycleCode, ExternalEffectCount, LastAllocatedOrdinal, DisclosureChainDigest
            FROM disclosure_subject_state
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject;
            """;

        BindSubject(command, draft);

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new SubjectState(
            reader.GetInt64(0),
            (ulong)reader.GetInt64(1),
            (ulong)reader.GetInt64(2),
            new CovenantDigest((byte[])reader.GetValue(3)));

    }

    private static async Task<ulong> CountCategoryAsync(
        CovenantMutationTransaction transaction,
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM external_disclosure_receipts
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject
                AND EffectCategoryCode = $category;
            """;

        BindSubject(command, draft);

        _ = command.Parameters.AddWithValue("$category", (long)category);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return (ulong)Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture);

    }

    private static async Task InsertReceiptAsync(
        CovenantMutationTransaction transaction,
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        ProviderCallSensitivity sensitivity,
        ulong subjectOrdinal,
        ulong attemptOrdinal,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO external_disclosure_receipts (
                OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode,
                RevocabilityCode, DestinationDigest, SensitivityCode, GenerationProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, WardEvidenceDigest, AdmissionEvidenceDigest,
                BackupEvidenceDigest, DisclosedAtUtc)
            VALUES (
                $installation, $kind, $subject, $ordinal, $category,
                $attempt, $effect, $destination,
                $revocability, $destinationDigest, $sensitivity, $provenanceMode,
                $exactIds, $bloom, $ward, $admission,
                $backup, $disclosedAt);
            """;

        BindSubject(command, draft);

        _ = command.Parameters.AddWithValue("$ordinal", (long)subjectOrdinal);

        _ = command.Parameters.AddWithValue("$category", (long)category);

        _ = command.Parameters.AddWithValue("$attempt", (long)attemptOrdinal);

        _ = command.Parameters.AddWithValue("$effect", draft.EffectIdentityDigest.Bytes);

        _ = command.Parameters.AddWithValue("$destination", (long)draft.Destination);

        _ = command.Parameters.AddWithValue("$revocability", (long)draft.Revocability);

        _ = command.Parameters.AddWithValue(
            "$destinationDigest",
            draft.OpaqueDestinationIdentityDigest.Bytes);

        // The exact level and provenance the caller froze, verified against the draft's digest
        // before this method runs. The column pair is exclusive by CHECK: exact identities or a
        // Bloom, never both and never neither.
        _ = command.Parameters.AddWithValue("$sensitivity", (long)sensitivity.Level);

        _ = command.Parameters.AddWithValue("$provenanceMode", (long)sensitivity.Provenance.Mode);

        _ = command.Parameters.AddWithValue(
            "$exactIds",
            sensitivity.Provenance.Mode is GenerationProvenanceMode.Exact
                ? PackGenerationIds(sensitivity.Provenance)
                : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$bloom",
            sensitivity.Provenance.Mode is GenerationProvenanceMode.BloomOverflow
                ? sensitivity.Provenance.BloomBits.ToArray()
                : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$ward",
            draft.WardEvidenceDigest is { } ward ? ward.Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$admission",
            draft.AdmissionDigest is { } admission ? admission.Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$backup",
            draft.BackupEvidenceDigest is { } backup ? backup.Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue("$disclosedAt", Iso(draft.Timestamp));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task AdvanceSubjectAsync(
        CovenantMutationTransaction transaction,
        CovenantDisclosureDraft draft,
        ulong allocatedOrdinal,
        CovenantDisclosureEffectCategory category,
        CovenantDisclosureChain chain,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            UPDATE disclosure_subject_state
            SET LastAllocatedOrdinal = $ordinal,
                ExternalEffectCount = $effects,
                ProviderAttemptCount = ProviderAttemptCount + $providerAttempt,
                DisclosureChainDigest = $chain,
                LastHeartbeatAtUtc = $now
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject;
            """;

        BindSubject(command, draft);

        _ = command.Parameters.AddWithValue("$ordinal", (long)allocatedOrdinal);

        _ = command.Parameters.AddWithValue("$effects", (long)chain.Count);

        _ = command.Parameters.AddWithValue(
            "$providerAttempt",
            category is CovenantDisclosureEffectCategory.ProviderDispatch ? 1L : 0L);

        _ = command.Parameters.AddWithValue("$chain", chain.Head.Bytes);

        _ = command.Parameters.AddWithValue("$now", Iso(draft.Timestamp));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static void BindSubject(SqliteCommand command, CovenantDisclosureDraft draft)
    {

        _ = command.Parameters.AddWithValue(
            "$installation",
            draft.OriginInstallationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$kind", (long)draft.SubjectKind);

        _ = command.Parameters.AddWithValue("$subject", draft.SubjectId.ToString("D"));

    }

    /// <summary>
    /// Packs the canonically ordered generation identities as concatenated raw 16-byte values.
    /// </summary>
    /// <remarks>
    /// The column refuses an empty vector, so an untainted call has nothing to record here: this is
    /// only reachable for a Covenant-derived disclosure, which always carries at least one
    /// generation.
    /// </remarks>
    private static byte[] PackGenerationIds(GenerationProvenance provenance)
    {

        byte[] packed = new byte[provenance.ExactGenerationIds.Length * 16];

        for (int index = 0; index < provenance.ExactGenerationIds.Length; index++)
        {
            // Big-endian, matching the canonical encoder and the schema's memcmp sort CHECK. The
            // little-endian default would store a different byte order than every other Covenant
            // record and would fail the strictly-increasing constraint on valid input.
            _ = provenance.ExactGenerationIds[index].TryWriteBytes(
                packed.AsSpan(index * 16, 16),
                bigEndian: true,
                out _);
        }

        return packed;

    }

    private static string Iso(long timestamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            .UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private readonly record struct SubjectState(
        long Lifecycle,
        ulong ExternalEffectCount,
        ulong LastAllocatedOrdinal,
        CovenantDigest ChainHead);

}
