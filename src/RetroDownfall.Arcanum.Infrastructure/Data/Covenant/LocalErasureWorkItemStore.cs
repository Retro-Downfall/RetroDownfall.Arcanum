using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One durable local-erasure work item, as the store reads it back.
/// </summary>
internal sealed record LocalErasureWorkItemRow(
    Guid WorkItemId,
    Guid ErasureOperationId,
    Guid SourceWriteOperationId,
    long ExpectedSourceRevision,
    Guid ArtifactId,
    Guid SourceSensitivityLabelId,
    ManagedFileDurableLocationEvidence Location,
    ManagedFileOwnershipEvidence Ownership,
    LocalErasureWorkItemState State,
    LocalErasureDeletionEvidenceCode? DeletionEvidence,
    long CheckpointRevision);

/// <summary>
/// The fixed-width encoding of the two evidence blobs a work item copies from its producer.
/// </summary>
/// <remarks>
/// A compact binary framing rather than JSON, for one reason that outweighs readability: pre-readiness
/// recovery reads these rows before any Covenant key generation is published, so the bytes cannot be
/// sealed with a Covenant envelope and cannot be allowed to grow unbounded either. Every field is
/// length-prefixed and every bound is checked before an allocation, and the whole row lives inside a
/// SQLCipher-encrypted database file.
/// </remarks>
internal static class ManagedFileEvidenceCodec
{

    private const byte LocationVersion = 1;

    private const byte OwnershipVersion = 1;

    private const byte WriteLocationVersion = 2;

    /// <summary>
    /// Encodes the producer-side location, whose temporary leaf follows its target.
    /// </summary>
    /// <remarks>
    /// A distinct version byte rather than an optional trailing field. A decoder that treated the
    /// temporary leaf as optional would read a producer row as an erasure location and hand the
    /// deleter a target it never verified.
    /// </remarks>
    internal static byte[] EncodeWriteLocation(ManagedFileWriteDurableLocationEvidence location)
    {

        ArgumentNullException.ThrowIfNull(location);

        byte[] target = EncodeLocation(location.Target);

        using MemoryStream buffer = new();

        buffer.WriteByte(WriteLocationVersion);

        buffer.Write(target.AsSpan(1));

        WriteComponent(buffer, location.TemporaryLeaf);

        return buffer.ToArray();

    }

    internal static Result<ManagedFileWriteDurableLocationEvidence> DecodeWriteLocation(byte[] payload)
    {

        if (payload is null || payload.Length < 1 || payload[0] != WriteLocationVersion)
        {

            return Unreadable<ManagedFileWriteDurableLocationEvidence>();

        }

        try
        {

            ReadOnlySpan<byte> span = payload;

            int offset = 1;

            CovenantDigest root = ReadDigest(span, ref offset);

            long revision = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));

            offset += 8;

            int segmentCount = span[offset++];

            ImmutableArray<string>.Builder segments = ImmutableArray.CreateBuilder<string>(segmentCount);

            for (int index = 0; index < segmentCount; index++)
            {

                segments.Add(ReadComponent(span, ref offset));

            }

            CovenantDigest parent = ReadDigest(span, ref offset);

            string leaf = ReadComponent(span, ref offset);

            string temporaryLeaf = ReadComponent(span, ref offset);

            return offset != span.Length
                ? Unreadable<ManagedFileWriteDurableLocationEvidence>()
                : Result<ManagedFileWriteDurableLocationEvidence>.Success(
                    new ManagedFileWriteDurableLocationEvidence(
                        new ManagedFileDurableLocationEvidence(root, revision, segments.ToImmutable(), parent, leaf),
                        temporaryLeaf));

        }
        catch (Exception exception) when (
            exception is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException)
        {

            return Unreadable<ManagedFileWriteDurableLocationEvidence>();

        }

    }

    internal static byte[] EncodeLocation(ManagedFileDurableLocationEvidence location)
    {

        ArgumentNullException.ThrowIfNull(location);

        using MemoryStream buffer = new();

        buffer.WriteByte(LocationVersion);

        buffer.Write(location.CampaignRootIdentityDigest.Bytes.AsSpan());

        Span<byte> scratch = stackalloc byte[8];

        BinaryPrimitives.WriteInt64LittleEndian(scratch, location.PathRevision);

        buffer.Write(scratch);

        buffer.WriteByte((byte)location.NormalizedParentSegments.Length);

        foreach (string segment in location.NormalizedParentSegments)
        {

            WriteComponent(buffer, segment);

        }

        buffer.Write(location.ParentPhysicalIdentityDigest.Bytes.AsSpan());

        WriteComponent(buffer, location.TargetLeaf);

        return buffer.ToArray();

    }

    internal static byte[] EncodeOwnership(ManagedFileOwnershipEvidence ownership)
    {

        ArgumentNullException.ThrowIfNull(ownership);

        byte[] encoded = new byte[1 + (CovenantLimits.DigestBytes * 2) + 8];

        encoded[0] = OwnershipVersion;

        ownership.PhysicalIdentityDigest.Bytes.AsSpan().CopyTo(encoded.AsSpan(1));

        ownership.ContentHash.Bytes.AsSpan().CopyTo(encoded.AsSpan(1 + CovenantLimits.DigestBytes));

        BinaryPrimitives.WriteInt64LittleEndian(
            encoded.AsSpan(1 + (CovenantLimits.DigestBytes * 2)),
            ownership.ContentLength);

        return encoded;

    }

    internal static Result<ManagedFileDurableLocationEvidence> DecodeLocation(byte[] payload)
    {

        try
        {

            ReadOnlySpan<byte> span = payload;

            if (span.Length < 1 || span[0] != LocationVersion)
            {

                return Unreadable<ManagedFileDurableLocationEvidence>();

            }

            int offset = 1;

            CovenantDigest root = ReadDigest(span, ref offset);

            long revision = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));

            offset += 8;

            int segmentCount = span[offset++];

            ImmutableArray<string>.Builder segments = ImmutableArray.CreateBuilder<string>(segmentCount);

            for (int index = 0; index < segmentCount; index++)
            {

                segments.Add(ReadComponent(span, ref offset));

            }

            CovenantDigest parent = ReadDigest(span, ref offset);

            string leaf = ReadComponent(span, ref offset);

            return offset != span.Length
                ? Unreadable<ManagedFileDurableLocationEvidence>()
                : Result<ManagedFileDurableLocationEvidence>.Success(
                    new ManagedFileDurableLocationEvidence(
                        root,
                        revision,
                        segments.ToImmutable(),
                        parent,
                        leaf));

        }
        catch (Exception exception) when (
            exception is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException)
        {

            return Unreadable<ManagedFileDurableLocationEvidence>();

        }

    }

    internal static Result<ManagedFileOwnershipEvidence> DecodeOwnership(byte[] payload)
    {

        try
        {

            ReadOnlySpan<byte> span = payload;

            if (span.Length != 1 + (CovenantLimits.DigestBytes * 2) + 8 || span[0] != OwnershipVersion)
            {

                return Unreadable<ManagedFileOwnershipEvidence>();

            }

            return Result<ManagedFileOwnershipEvidence>.Success(
                new ManagedFileOwnershipEvidence(
                    new CovenantDigest(span.Slice(1, CovenantLimits.DigestBytes).ToArray()),
                    new CovenantDigest(span.Slice(1 + CovenantLimits.DigestBytes, CovenantLimits.DigestBytes).ToArray()),
                    BinaryPrimitives.ReadInt64LittleEndian(span[(1 + (CovenantLimits.DigestBytes * 2))..])));

        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {

            return Unreadable<ManagedFileOwnershipEvidence>();

        }

    }

    private static void WriteComponent(MemoryStream buffer, string value)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        if (bytes.Length is 0 or > 1024)
        {

            throw new ArgumentOutOfRangeException(nameof(value));

        }

        Span<byte> length = stackalloc byte[2];

        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);

        buffer.Write(length);

        buffer.Write(bytes);

    }

    private static string ReadComponent(ReadOnlySpan<byte> span, ref int offset)
    {

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));

        offset += 2;

        if (length is 0 or > 1024)
        {

            throw new ArgumentOutOfRangeException(nameof(span));

        }

        string value = new UTF8Encoding(false, true).GetString(span.Slice(offset, length));

        offset += length;

        return value;

    }

    private static CovenantDigest ReadDigest(ReadOnlySpan<byte> span, ref int offset)
    {

        CovenantDigest digest = new(span.Slice(offset, CovenantLimits.DigestBytes).ToArray());

        offset += CovenantLimits.DigestBytes;

        return digest;

    }

    private static Result<T> Unreadable<T>() =>
        Result<T>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualArtifactErasureRequired,
                "A durable managed-file erasure work item could not be read by this build."));

}

/// <summary>
/// The only reader and writer of <c>local_erasure_work_items</c>.
/// </summary>
/// <remarks>
/// Every statement here is executed on a caller-supplied live transaction. The store never opens a
/// transaction, never borrows an authorization, and never decides a state: the schema triggers own
/// the closed edge list, the compare-and-swap, and the producer ordering, and a second opinion in C#
/// would be a rule that could disagree with the one the database actually enforces (§10.17).
/// </remarks>
internal static class LocalErasureWorkItemStore
{

    /// <summary>How many nonterminal rows one pre-readiness pass will adopt.</summary>
    internal const int RecoveryPageSize = 256;

    internal static async Task InsertPreparedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        Guid erasureOperationId,
        Guid sourceWriteOperationId,
        long expectedSourceRevision,
        Guid artifactId,
        Guid sourceSensitivityLabelId,
        ManagedFileDurableLocationEvidence location,
        ManagedFileOwnershipEvidence ownership,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO local_erasure_work_items (
                WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision, ArtifactId,
                SourceSensitivityLabelId, DurableLocationEvidence, ExpectedOwnershipEvidence, StateCode,
                DeletionEvidenceCode, CheckpointRevision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($workItem, $operation, $source, $revision, $artifact, $label, $location, $ownership, 1,
                NULL, 0, 0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$workItem", Format(workItemId));

        _ = command.Parameters.AddWithValue("$operation", Format(erasureOperationId));

        _ = command.Parameters.AddWithValue("$source", Format(sourceWriteOperationId));

        _ = command.Parameters.AddWithValue("$revision", expectedSourceRevision);

        _ = command.Parameters.AddWithValue("$artifact", Format(artifactId));

        _ = command.Parameters.AddWithValue("$label", Format(sourceSensitivityLabelId));

        _ = command.Parameters.AddWithValue("$location", ManagedFileEvidenceCodec.EncodeLocation(location));

        _ = command.Parameters.AddWithValue("$ownership", ManagedFileEvidenceCodec.EncodeOwnership(ownership));

        _ = command.Parameters.AddWithValue("$now", Iso(utcNow));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    internal static async Task<Result<LocalErasureWorkItemRow?>> TryReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workItemId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"{SelectColumns} WHERE WorkItemId = $workItem;";

        _ = command.Parameters.AddWithValue("$workItem", Format(workItemId));

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<LocalErasureWorkItemRow?>.Success(null);

        }

        Result<LocalErasureWorkItemRow> row = Read(reader);

        return row.IsFailure
            ? Result<LocalErasureWorkItemRow?>.Failure(row.Error)
            : Result<LocalErasureWorkItemRow?>.Success(row.Value);

    }

    /// <summary>
    /// Reads the bounded set of rows a pre-readiness pass must resolve, oldest first.
    /// </summary>
    internal static async Task<Result<IReadOnlyList<LocalErasureWorkItemRow>>> ListNonTerminalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"{SelectColumns} WHERE StateCode IN (1, 2) ORDER BY CreatedAtUtc, WorkItemId LIMIT {RecoveryPageSize};";

        List<LocalErasureWorkItemRow> rows = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Result<LocalErasureWorkItemRow> row = Read(reader);

            if (row.IsFailure)
            {

                return Result<IReadOnlyList<LocalErasureWorkItemRow>>.Failure(row.Error);

            }

            rows.Add(row.Value);

        }

        return Result<IReadOnlyList<LocalErasureWorkItemRow>>.Success(rows);

    }

    /// <summary>
    /// Reads the complete work-item inventory in canonical identity order.
    /// </summary>
    /// <remarks>
    /// Every row, including the terminal ones. The active-source uniqueness index is partial — it
    /// constrains only <c>StateCode IN (1, 2)</c> — so refused work items from earlier operations
    /// accumulate legitimately, and an inventory that counted only the unfinished ones could not state
    /// that every work item this installation ever created has been accounted for.
    ///
    /// <para>Identities are stored as uppercase RFC-4122 text, whose lexicographic order is exactly the
    /// network-byte order the inventory vector commits to. One row past the ceiling is read on purpose,
    /// so an inventory too large to authenticate is detected rather than truncated.</para>
    /// </remarks>
    internal static async Task<Result<IReadOnlyList<LocalErasureWorkItemRow>>> ListInventoryAsync(
        SqliteConnection connection,
        int ceiling,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"{SelectColumns} ORDER BY WorkItemId LIMIT {ceiling + 1};";

        List<LocalErasureWorkItemRow> rows = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Result<LocalErasureWorkItemRow> row = Read(reader);

            if (row.IsFailure)
            {

                return Result<IReadOnlyList<LocalErasureWorkItemRow>>.Failure(row.Error);

            }

            rows.Add(row.Value);

        }

        return Result<IReadOnlyList<LocalErasureWorkItemRow>>.Success(rows);

    }

    /// <summary>
    /// Reads the one active work item a producer already has, if it has one.
    /// </summary>
    /// <remarks>
    /// Active means <c>Prepared</c> or <c>DeletionVerified</c>, which is exactly the predicate the
    /// partial unique index constrains, so at most one row can answer. A caller that minted a fresh
    /// identity instead of reusing this one would be asking the database to authorize a second effect
    /// against a file the first one already committed to removing.
    ///
    /// <para>Two rows are read rather than one so a database that somehow holds both is refused rather
    /// than silently resolved to whichever sorted first.</para>
    /// </remarks>
    internal static async Task<Result<Guid?>> TryReadActiveWorkItemIdForSourceAsync(
        SqliteConnection connection,
        Guid sourceWriteOperationId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT WorkItemId
            FROM local_erasure_work_items
            WHERE SourceWriteOperationId = $source AND StateCode IN (1, 2)
            ORDER BY WorkItemId
            LIMIT 2;
            """;

        _ = command.Parameters.AddWithValue("$source", Format(sourceWriteOperationId));

        List<Guid> identities = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            identities.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));

        }

        return identities.Count switch
        {
            0 => Result<Guid?>.Success(null),
            1 => Result<Guid?>.Success(identities[0]),
            _ => Result<Guid?>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "A managed-file producer has more than one active local erasure work item.")),
        };

    }

    /// <summary>
    /// Advances one work item, compare-and-swapping on its exact checkpoint revision and state.
    /// </summary>
    internal static async Task<bool> TryAdvanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        LocalErasureWorkItemState expectedState,
        long expectedCheckpointRevision,
        LocalErasureWorkItemState nextState,
        LocalErasureDeletionEvidenceCode? evidence,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE local_erasure_work_items
            SET StateCode = $nextState,
                DeletionEvidenceCode = $evidence,
                CheckpointRevision = CheckpointRevision + 1,
                UpdatedAtUtc = $now
            WHERE WorkItemId = $workItem
                AND StateCode = $expectedState
                AND CheckpointRevision = $expectedRevision;
            """;

        _ = command.Parameters.AddWithValue("$nextState", (int)nextState);

        _ = command.Parameters.AddWithValue(
            "$evidence",
            evidence is { } code ? (int)code : DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Iso(utcNow));

        _ = command.Parameters.AddWithValue("$workItem", Format(workItemId));

        _ = command.Parameters.AddWithValue("$expectedState", (int)expectedState);

        _ = command.Parameters.AddWithValue("$expectedRevision", expectedCheckpointRevision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

    }

    private const string SelectColumns = """
        SELECT WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision, ArtifactId,
            SourceSensitivityLabelId, DurableLocationEvidence, ExpectedOwnershipEvidence, StateCode,
            DeletionEvidenceCode, CheckpointRevision
        FROM local_erasure_work_items
        """;

    private static Result<LocalErasureWorkItemRow> Read(SqliteDataReader reader)
    {

        Result<ManagedFileDurableLocationEvidence> location =
            ManagedFileEvidenceCodec.DecodeLocation((byte[])reader.GetValue(6));

        if (location.IsFailure)
        {

            return Result<LocalErasureWorkItemRow>.Failure(location.Error);

        }

        Result<ManagedFileOwnershipEvidence> ownership =
            ManagedFileEvidenceCodec.DecodeOwnership((byte[])reader.GetValue(7));

        if (ownership.IsFailure)
        {

            return Result<LocalErasureWorkItemRow>.Failure(ownership.Error);

        }

        return Result<LocalErasureWorkItemRow>.Success(
            new LocalErasureWorkItemRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetInt64(3),
                Guid.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                location.Value,
                ownership.Value,
                (LocalErasureWorkItemState)reader.GetInt32(8),
                reader.IsDBNull(9) ? null : (LocalErasureDeletionEvidenceCode)reader.GetInt32(9),
                reader.GetInt64(10)));

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}
