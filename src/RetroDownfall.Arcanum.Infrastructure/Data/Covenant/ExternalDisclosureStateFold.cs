using System.Collections.Immutable;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Which producer is folding a subject's tail, and therefore how much the folded count may claim.
/// </summary>
internal enum ExternalDisclosureFoldOrigin : byte
{
    /// <summary>The journal, inside the acknowledgement that wrote the newest receipt.</summary>
    Live = 1,

    /// <summary>Restore staging, over tails an archive carried in.</summary>
    RestoreStaging = 2,
}

/// <summary>
/// The identity a disclosure subject's ordinals, counters and watermark are keyed by.
/// </summary>
internal readonly record struct CovenantDisclosureSubject(
    Guid OriginInstallationId,
    CovenantDisclosureSubjectKind Kind,
    Guid SubjectId)
{
    internal static CovenantDisclosureSubject From(CovenantDisclosureDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new CovenantDisclosureSubject(draft.OriginInstallationId, draft.SubjectKind, draft.SubjectId);
    }
}

/// <summary>
/// Folds a disclosure subject's unfolded receipts into <c>external_disclosure_state</c> and advances
/// its watermark, on the caller's connection and inside the caller's transaction (§10.13).
/// </summary>
/// <remarks>
/// The watermark is <c>disclosure_subject_state.LastFoldedOrdinal</c>, and it moves by compare and
/// swap in the same transaction that writes the buckets. That is what lets a repeated or concurrent
/// fold count nothing twice: a second fold of the same tail either sees the advanced watermark and
/// finds nothing, or loses the swap and rolls back everything it wrote.
///
/// <para>Each receipt is rebuilt from its own row, so the Bloom evidence a bucket carries is the
/// receipt digest the journal committed to. A row that cannot be rebuilt is still counted, with
/// evidence derived from its effect identity instead: a fold that skipped it would understate what
/// left, and the journal may overstate but never understate (§10.13).</para>
///
/// <para>A tail longer than one receipt holds receipts a build without the live fold wrote. Nothing
/// proves those are the whole of that history, so the buckets they reach become lower bounds. Restore
/// staging always folds as a lower bound for the same reason.</para>
/// </remarks>
internal static class ExternalDisclosureStateFold
{
    /// <summary>
    /// The instant an unparseable <c>DisclosedAtUtc</c> contributes: the earliest representable one, so
    /// it can never move a bucket's newest instant past what some receipt proves.
    /// </summary>
    private const long UnknownInstantTicks = 1;

    /// <summary>
    /// Folds one subject's whole unfolded tail.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The subject has no state row, or its watermark moved during the fold. Either rolls back the
    /// caller's transaction, and with it the acknowledgement that asked for the fold.
    /// </exception>
    internal static async Task FoldSubjectTailAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantDisclosureSubject subject,
        ExternalDisclosureFoldOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        RequireOrigin(origin);

        ExternalDisclosureSubjectKey key = ExternalDisclosureSubjectKey.From(subject);

        long? watermark = await ReadWatermarkAsync(connection, transaction, key, cancellationToken)
            .ConfigureAwait(false);

        if (watermark is not { } folded)
        {
            throw new InvalidOperationException("The disclosure subject being folded has no state row.");
        }

        _ = await FoldAsync(connection, transaction, key, folded, origin, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Folds every subject whose watermark trails its last allocated ordinal.
    /// </summary>
    /// <returns>The number of subjects whose tail held at least one receipt and was folded.</returns>
    internal static async Task<int> FoldAllUnfoldedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExternalDisclosureFoldOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        RequireOrigin(origin);

        int folded = 0;

        foreach (ExternalDisclosureUnfoldedSubject subject in
                 await ReadUnfoldedSubjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            if (await FoldAsync(
                    connection,
                    transaction,
                    subject.Key,
                    subject.LastFoldedOrdinal,
                    origin,
                    cancellationToken).ConfigureAwait(false))
            {
                folded++;
            }
        }

        return folded;
    }

    /// <summary>
    /// Every subject whose watermark trails its last allocated ordinal, found through
    /// <c>idx_disclosure_subject_state_unfolded</c>.
    /// </summary>
    internal static async Task<List<ExternalDisclosureUnfoldedSubject>> ReadUnfoldedSubjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        List<ExternalDisclosureUnfoldedSubject> subjects = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT OriginInstallationId, SubjectKind, SubjectId, LastFoldedOrdinal
            FROM disclosure_subject_state
            WHERE LastFoldedOrdinal < LastAllocatedOrdinal
            ORDER BY OriginInstallationId, SubjectKind, SubjectId;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            subjects.Add(new ExternalDisclosureUnfoldedSubject(
                new ExternalDisclosureSubjectKey(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)),
                reader.GetInt64(3)));
        }

        return subjects;
    }

    /// <summary>
    /// Reads and rebuilds every receipt of <paramref name="subject"/> past
    /// <paramref name="lastFoldedOrdinal"/>, in ordinal order.
    /// </summary>
    internal static async Task<List<ExternalDisclosureFoldableReceipt>> ReadTailAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ExternalDisclosureSubjectKey subject,
        long lastFoldedOrdinal,
        CancellationToken cancellationToken)
    {
        List<ExternalDisclosureFoldableReceipt> tail = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectIdentityDigest,
                   DestinationCode, RevocabilityCode, DestinationDigest, SensitivityCode,
                   GenerationProvenanceModeCode, ExactGenerationIds, GenerationBloom,
                   WardEvidenceDigest, AdmissionEvidenceDigest, BackupEvidenceDigest, DisclosedAtUtc
            FROM external_disclosure_receipts
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject
                AND SubjectOrdinal > $folded
            ORDER BY SubjectOrdinal;
            """;

        subject.Bind(command);

        _ = command.Parameters.AddWithValue("$folded", lastFoldedOrdinal);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tail.Add(Rebuild(reader));
        }

        return tail;
    }

    private static async Task<bool> FoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExternalDisclosureSubjectKey subject,
        long lastFoldedOrdinal,
        ExternalDisclosureFoldOrigin origin,
        CancellationToken cancellationToken)
    {
        List<ExternalDisclosureFoldableReceipt> tail = await ReadTailAsync(
            connection,
            transaction,
            subject,
            lastFoldedOrdinal,
            cancellationToken).ConfigureAwait(false);

        if (tail.Count == 0)
        {
            return false;
        }

        Dictionary<(CovenantEgressDestination, CovenantDisclosureRevocability), CovenantDisclosureState> buckets = [];

        long newestTicks = UnknownInstantTicks;

        foreach (ExternalDisclosureFoldableReceipt receipt in tail)
        {
            (CovenantEgressDestination, CovenantDisclosureRevocability) bucket =
                (receipt.Destination, receipt.Revocability);

            if (!buckets.TryGetValue(bucket, out CovenantDisclosureState? state))
            {
                state = await ExternalDisclosureStateStore.ReadAsync(
                        connection,
                        transaction,
                        receipt.Destination,
                        receipt.Revocability,
                        cancellationToken).ConfigureAwait(false)
                    ?? CovenantDisclosureState.Empty(receipt.Destination, receipt.Revocability);
            }

            buckets[bucket] = receipt.FoldInto(state);

            newestTicks = Math.Max(newestTicks, receipt.DisclosedAtUtcTicks);
        }

        bool lowerBound = origin is ExternalDisclosureFoldOrigin.RestoreStaging || tail.Count > 1;

        DateTimeOffset updatedAtUtc = new(newestTicks, TimeSpan.Zero);

        foreach (CovenantDisclosureState state in buckets.Values)
        {
            await ExternalDisclosureStateStore.WriteAsync(
                connection,
                transaction,
                lowerBound ? CovenantDisclosureStateAlgebra.WeakenToLowerBound(state) : state,
                updatedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        await AdvanceWatermarkAsync(
            connection,
            transaction,
            subject,
            lastFoldedOrdinal,
            checked((long)tail[^1].Ordinal),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static async Task<long?> ReadWatermarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExternalDisclosureSubjectKey subject,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT LastFoldedOrdinal
            FROM disclosure_subject_state
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject;
            """;

        subject.Bind(command);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Moves the watermark from exactly <paramref name="expected"/> to <paramref name="last"/>.
    /// </summary>
    /// <remarks>
    /// The comparison is the guard, not a formality. If another fold moved the watermark after this
    /// one read it, the buckets this transaction just wrote count receipts that fold already counted,
    /// and the only safe outcome is to throw and let the whole transaction roll back.
    /// </remarks>
    private static async Task AdvanceWatermarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExternalDisclosureSubjectKey subject,
        long expected,
        long last,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE disclosure_subject_state
            SET LastFoldedOrdinal = $last
            WHERE OriginInstallationId = $installation
                AND SubjectKind = $kind
                AND SubjectId = $subject
                AND LastFoldedOrdinal = $folded;
            """;

        subject.Bind(command);

        _ = command.Parameters.AddWithValue("$last", last);

        _ = command.Parameters.AddWithValue("$folded", expected);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The disclosure fold watermark moved while the fold ran; nothing it wrote is kept.");
        }
    }

    /// <summary>
    /// Rebuilds one receipt from the reader's current row, in <see cref="ReadTailAsync"/>'s column
    /// order.
    /// </summary>
    private static ExternalDisclosureFoldableReceipt Rebuild(SqliteDataReader reader)
    {
        ulong ordinal = checked((ulong)reader.GetInt64(3));

        byte[] effect = reader.GetFieldValue<byte[]>(4);

        CovenantEgressDestination destination = (CovenantEgressDestination)checked((byte)reader.GetInt64(5));

        CovenantDisclosureRevocability revocability = (CovenantDisclosureRevocability)checked((byte)reader.GetInt64(6));

        string disclosedAt = reader.GetString(15);

        long ticks = UtcInstantText.TryParse(disclosedAt, out DateTimeOffset parsed)
            ? Math.Max(parsed.UtcTicks, UnknownInstantTicks)
            : UnknownInstantTicks;

        byte[] evidence;

        try
        {
            CovenantDisclosureDraft draft = new(
                Guid.Parse(reader.GetString(0)),
                (CovenantDisclosureSubjectKind)Code(reader.GetInt64(1)),
                Guid.Parse(reader.GetString(2)),
                new CovenantDigest(effect),
                destination,
                revocability,
                new CovenantDigest(reader.GetFieldValue<byte[]>(7)),
                CovenantDigests.Sensitivity(new SensitivityDigestInput(
                    (ContentSensitivity)Code(reader.GetInt64(8)),
                    (GenerationProvenanceMode)Code(reader.GetInt64(9)),
                    UnpackGenerationIds(OptionalBlob(reader, 10)),
                    OptionalBlob(reader, 11) is { } bloom ? [.. bloom] : [])),
                OptionalDigest(reader, 12),
                OptionalDigest(reader, 13),
                OptionalDigest(reader, 14),
                UtcInstantText.Parse(disclosedAt).ToUnixTimeMilliseconds());

            evidence = new CovenantDisclosureReceipt(draft, ordinal).EvidenceBloom;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Never assume the row is what the current journal would have written. A receipt whose
            // digest cannot be recomputed is still a disclosure; it keeps its count and carries
            // evidence derived from the one identity every row is guaranteed to hold.
            evidence = CovenantDisclosureStateAlgebra.CreateEvidenceBloom(new CovenantDigest(effect));
        }

        return new ExternalDisclosureFoldableReceipt(ordinal, destination, revocability, ticks, evidence);
    }

    /// <summary>
    /// Unpacks concatenated 16-byte identities, big-endian as the journal packed them.
    /// </summary>
    private static ImmutableArray<Guid> UnpackGenerationIds(byte[]? packed)
    {
        if (packed is null)
        {
            return [];
        }

        if (packed.Length % 16 != 0)
        {
            throw new ArgumentException("Packed generation identities are not whole identities.", nameof(packed));
        }

        ImmutableArray<Guid>.Builder identities = ImmutableArray.CreateBuilder<Guid>(packed.Length / 16);

        for (int offset = 0; offset < packed.Length; offset += 16)
        {
            identities.Add(new Guid(packed.AsSpan(offset, 16), bigEndian: true));
        }

        return identities.MoveToImmutable();
    }

    private static byte Code(long value) =>
        value is >= byte.MinValue and <= byte.MaxValue
            ? (byte)value
            : throw new ArgumentOutOfRangeException(nameof(value));

    private static byte[]? OptionalBlob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);

    private static CovenantDigest? OptionalDigest(SqliteDataReader reader, int ordinal) =>
        OptionalBlob(reader, ordinal) is { } bytes ? new CovenantDigest(bytes) : null;

    private static void RequireOrigin(ExternalDisclosureFoldOrigin origin)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }
    }
}

/// <summary>
/// A subject key exactly as <c>disclosure_subject_state</c> stores it, so a fold binds the same text
/// the journal wrote rather than a respelling of it.
/// </summary>
internal readonly record struct ExternalDisclosureSubjectKey(
    string OriginInstallationId,
    long SubjectKind,
    string SubjectId)
{
    /// <summary>The journal's own spelling: lowercase <c>D</c>-format identities.</summary>
    internal static ExternalDisclosureSubjectKey From(CovenantDisclosureSubject subject) =>
        new(subject.OriginInstallationId.ToString("D"), (long)subject.Kind, subject.SubjectId.ToString("D"));

    internal void Bind(SqliteCommand command)
    {
        _ = command.Parameters.AddWithValue("$installation", OriginInstallationId);

        _ = command.Parameters.AddWithValue("$kind", SubjectKind);

        _ = command.Parameters.AddWithValue("$subject", SubjectId);
    }
}

/// <summary>A subject with an unfolded tail, and where that tail starts.</summary>
internal readonly record struct ExternalDisclosureUnfoldedSubject(
    ExternalDisclosureSubjectKey Key,
    long LastFoldedOrdinal);

/// <summary>
/// What a fold needs from one receipt: its bucket, its instant, and its evidence.
/// </summary>
internal sealed record ExternalDisclosureFoldableReceipt(
    ulong Ordinal,
    CovenantEgressDestination Destination,
    CovenantDisclosureRevocability Revocability,
    long DisclosedAtUtcTicks,
    byte[] EvidenceBloom)
{
    /// <summary>Adds this one receipt to <paramref name="state"/>.</summary>
    internal CovenantDisclosureState FoldInto(CovenantDisclosureState state) =>
        CovenantDisclosureStateAlgebra.IncrementLocal(state, 1, DisclosedAtUtcTicks, EvidenceBloom);
}
