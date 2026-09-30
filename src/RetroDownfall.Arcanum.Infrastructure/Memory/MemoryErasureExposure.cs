using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// What an erase can say about copies of the erased item outside this installation, one fixed
/// evidence value per external channel.
/// </summary>
/// <remarks>
/// <para>Evidence is computed at prepare, recomputed at apply, bound into the effect digest, and stored
/// on the receipt, so every rule here is deterministic over the rows it reads. It never counts
/// disclosures and never names a provider: a channel is Known, a receipt window, not recorded, or not
/// applicable, and nothing finer.</para>
///
/// <para><b>Fixed rules.</b> Saga authorship is always Known, because extraction is the only Saga
/// writer, and so is its embedding, because every insert embeds. Lexicon and Covenant are never
/// embedded. Only a Covenant entry can be carried into a provider's context by a turn, and nothing
/// records any other channel.</para>
///
/// <para><b>Windows.</b> A backup operation counts once its latest receipt is at or after the item's
/// window start: the earliest creation across a Saga twin class, or a Covenant entry's creation. A
/// Lexicon entry records no creation time, and its Annals claim can open after the entry exists, so
/// its window is unbounded and every backup counts: over-reporting is the safe direction. A Covenant
/// provider window is time only, with no generation predicate, because a generation names the dataset
/// a turn read and not which entry it carried. The journal records receipts to the millisecond, so
/// every window starts at the millisecond of its creation, and a receipt taken later in that same
/// millisecond counts.</para>
///
/// <para>A catalog without the disclosure journal has no receipts. Every identity predicate compares the
/// normalized spelling, so a row stored in any spelling of its identity matches.</para>
/// </remarks>
internal static class MemoryErasureExposure
{
    private const int ChannelCount = 5;

    private const string AgentOriginCodes = "(2, 3)";

    internal static async Task<MemoryErasureExternalExposureDto> ReadSagaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> memoryIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(memoryIds);

        if (memoryIds.Count == 0)
        {
            throw new ArgumentException("An erase names at least one memory.", nameof(memoryIds));
        }

        string keys = KeyArray(memoryIds);

        await using SqliteCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT memory.CreatedAt
            FROM json_each($ids) AS id
            JOIN saga_memories AS memory ON {CovenantIdentitySql.Keyed("memory.Id", "id.value")};
            """);

        _ = command.Parameters.AddWithValue("$ids", keys);

        DateTimeOffset? windowStart = await EarliestAsync(command, cancellationToken).ConfigureAwait(false);

        return Exposure(
            MemoryExternalEvidence.Known,
            MemoryExternalEvidence.NotRecorded,
            MemoryExternalEvidence.Known,
            await BackupAsync(connection, transaction, windowStart, cancellationToken).ConfigureAwait(false));
    }

    /// <remarks>
    /// Authorship is read from the entry's claim, found by the entry's identity. The backup window is
    /// unbounded. <paramref name="normalizedName"/> and <paramref name="campaignId"/> describe the same
    /// entry and no rule reads them.
    /// </remarks>
    internal static async Task<MemoryErasureExternalExposureDto> ReadLexiconAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid entryId,
        string normalizedName,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _ = normalizedName;

        _ = campaignId;

        string entry = CovenantIdentitySql.Key(entryId);

        bool agentAuthored;

        await using (SqliteCommand authorship = Command(
            connection,
            transaction,
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM annal_versions AS version
                JOIN annal_claims AS claim ON claim.ClaimId = version.ClaimId
                WHERE claim.SubjectStoreCode = 2
                  AND {CovenantIdentitySql.Keyed("claim.SubjectId", "$entry")}
                  AND version.OriginCode IN {AgentOriginCodes});
            """))
        {
            _ = authorship.Parameters.AddWithValue("$entry", entry);

            agentAuthored = await ExistsAsync(authorship, cancellationToken).ConfigureAwait(false);
        }

        // Unbounded: a Lexicon entry records no creation time, and its claim can open long after the
        // entry exists (a scribe with the Annals off, then an operator correction), so no window start
        // is safe. Every backup counts.
        return Exposure(
            agentAuthored ? MemoryExternalEvidence.Known : MemoryExternalEvidence.NotRecorded,
            MemoryExternalEvidence.NotRecorded,
            MemoryExternalEvidence.NotApplicable,
            await BackupAsync(connection, transaction, windowStart: null, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<MemoryErasureExternalExposureDto> ReadCovenantAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string entry = CovenantIdentitySql.Key(entryId);

        bool agentAuthored;

        await using (SqliteCommand authorship = Command(
            connection,
            transaction,
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM covenant_versions
                WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")}
                  AND OriginCode IN {AgentOriginCodes});
            """))
        {
            _ = authorship.Parameters.AddWithValue("$entry", entry);

            agentAuthored = await ExistsAsync(authorship, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset? windowStart;

        await using (SqliteCommand created = Command(
            connection,
            transaction,
            $"""
            SELECT CreatedAtUtc
            FROM covenant_entries
            WHERE {CovenantIdentitySql.Keyed("EntryId", "$entry")};
            """))
        {
            _ = created.Parameters.AddWithValue("$entry", entry);

            windowStart = await EarliestAsync(created, cancellationToken).ConfigureAwait(false);
        }

        return Exposure(
            agentAuthored ? MemoryExternalEvidence.Known : MemoryExternalEvidence.NotRecorded,
            await ProviderContextAsync(connection, transaction, windowStart, cancellationToken).ConfigureAwait(false),
            MemoryExternalEvidence.NotApplicable,
            await BackupAsync(connection, transaction, windowStart, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The five evidence values of an exposure, in channel code order.</summary>
    /// <exception cref="ArgumentException">The exposure does not name each channel exactly once.</exception>
    internal static IReadOnlyList<MemoryExternalEvidence> EvidenceCodes(MemoryErasureExternalExposureDto exposure)
    {
        ArgumentNullException.ThrowIfNull(exposure);

        ArgumentNullException.ThrowIfNull(exposure.Channels);

        MemoryExternalEvidence?[] codes = new MemoryExternalEvidence?[ChannelCount];

        foreach (MemoryExternalExposureChannelDto channel in exposure.Channels)
        {
            int index = (int)channel.Channel - 1;

            if (index is < 0 or >= ChannelCount
                || codes[index] is not null
                || channel.Evidence is not (MemoryExternalEvidence.Known
                    or MemoryExternalEvidence.ReceiptWindow
                    or MemoryExternalEvidence.NotRecorded
                    or MemoryExternalEvidence.NotApplicable))
            {
                throw new ArgumentException("An exposure names each external channel exactly once.", nameof(exposure));
            }

            codes[index] = channel.Evidence;
        }

        return codes.All(static code => code is not null)
            ? [.. codes.Select(static code => code!.Value)]
            : throw new ArgumentException("An exposure names each external channel exactly once.", nameof(exposure));
    }

    private static MemoryErasureExternalExposureDto Exposure(
        MemoryExternalEvidence authorship,
        MemoryExternalEvidence context,
        MemoryExternalEvidence embedding,
        MemoryExternalEvidence backup) =>
        new(
            MemoryExternalRevocation.NotPerformed,
            [
                new(MemoryExternalChannel.InferenceProviderAuthorship, authorship),
                new(MemoryExternalChannel.InferenceProviderContext, context),
                new(MemoryExternalChannel.EmbeddingProvider, embedding),
                new(MemoryExternalChannel.EncryptedBackup, backup),
                new(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded),
            ]);

    /// <summary>
    /// Whether any backup operation's latest receipt is at or after <paramref name="windowStart"/>,
    /// or any backup at all when the start is unknown.
    /// </summary>
    private static async Task<MemoryExternalEvidence> BackupAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset? windowStart,
        CancellationToken cancellationToken)
    {
        if (!await HasJournalAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return MemoryExternalEvidence.NotRecorded;
        }

        // An operation's receipts span its run, and what it archived is bounded by the last of them, so
        // an operation counts by its latest receipt rather than its first.
        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1
                FROM external_disclosure_receipts
                WHERE SubjectKind = 2
                  AND EffectCategoryCode = 4
                  AND DestinationCode = 8
                  AND RevocabilityCode = 2
                GROUP BY OriginInstallationId, SubjectId
                HAVING $windowStartUtc IS NULL OR MAX(DisclosedAtUtc) >= $windowStartUtc);
            """);

        BindWindowStart(command, windowStart);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false)
            ? MemoryExternalEvidence.ReceiptWindow
            : MemoryExternalEvidence.NotRecorded;
    }

    /// <summary>
    /// Whether any nonrevocable, Covenant-derived provider dispatch happened at or after
    /// <paramref name="windowStart"/>. Time only: no generation predicate.
    /// </summary>
    private static async Task<MemoryExternalEvidence> ProviderContextAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset? windowStart,
        CancellationToken cancellationToken)
    {
        if (!await HasJournalAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return MemoryExternalEvidence.NotRecorded;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1
                FROM external_disclosure_receipts
                WHERE ($windowStartUtc IS NULL OR DisclosedAtUtc >= $windowStartUtc)
                  AND EffectCategoryCode = 1
                  AND DestinationCode = 1
                  AND RevocabilityCode = 2
                  AND SensitivityCode = 1);
            """);

        BindWindowStart(command, windowStart);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false)
            ? MemoryExternalEvidence.ReceiptWindow
            : MemoryExternalEvidence.NotRecorded;
    }

    /// <summary>
    /// Binds the window start in the journal's own instant spelling, which orders as text exactly as it
    /// orders in time; null means unbounded.
    /// </summary>
    /// <remarks>
    /// The start is floored to its millisecond first. Receipts are stamped from Unix milliseconds while
    /// creation instants keep every tick, so an unfloored start would put a receipt from later in the
    /// creation's own millisecond before it, and under-report.
    /// </remarks>
    private static void BindWindowStart(SqliteCommand command, DateTimeOffset? windowStart) =>
        _ = command.Parameters.AddWithValue(
            "$windowStartUtc",
            windowStart is { } start
                ? UtcInstantText.Format(start.AddTicks(-(start.UtcTicks % TimeSpan.TicksPerMillisecond)))
                : DBNull.Value);

    private static async Task<bool> HasJournalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'external_disclosure_receipts');");

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The earliest instant a query returns in its first column, parsed rather than compared as text.</summary>
    /// <remarks>
    /// Stored instants may predate the fixed-width spelling, and text order is time order only within
    /// one spelling, so each is parsed. One that cannot be parsed throws, which fails the erase closed.
    /// </remarks>
    private static async Task<DateTimeOffset?> EarliestAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        DateTimeOffset? earliest = null;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset instant = UtcInstantText.Parse(reader.GetString(0));

            if (earliest is null || instant < earliest)
            {
                earliest = instant;
            }
        }

        return earliest;
    }

    /// <summary>The normalized keys of the memory ids, as a JSON array of dash-free lowercase hex.</summary>
    private static string KeyArray(IReadOnlyList<string> memoryIds)
    {
        List<string> keys = new(memoryIds.Count);

        foreach (string memoryId in memoryIds)
        {
            if (!Guid.TryParse(memoryId, out Guid parsed))
            {
                throw new ArgumentException("A Saga memory id is a GUID.", nameof(memoryIds));
            }

            keys.Add($"\"{CovenantIdentitySql.Key(parsed)}\"");
        }

        return $"[{string.Join(',', keys)}]";
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static async Task<bool> ExistsAsync(SqliteCommand command, CancellationToken cancellationToken) =>
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            0L => false,
            1L => true,
            _ => throw new InvalidDataException("An exposure existence check did not answer 0 or 1."),
        };
}

/// <summary>The local copies an erase does not reach, reported and never purged.</summary>
/// <remarks>
/// A constant per store, always present, with the audit log added whenever inference audit files
/// exist. The durable mask form is <see cref="MemoryRetainedLocalCopies"/>.
/// </remarks>
internal static class MemoryErasureRetainedCopies
{
    internal static MemoryRetainedLocalCopy[] For(MemoryReviewStore store, bool auditFilesExist)
    {
        if (store is not (MemoryReviewStore.Covenant or MemoryReviewStore.Saga or MemoryReviewStore.Lexicon))
        {
            throw new ArgumentOutOfRangeException(nameof(store), store, "Retained copies are reported for a recognized store.");
        }

        return
        [
            MemoryRetainedLocalCopy.SessionTranscripts,
            MemoryRetainedLocalCopy.SearchAndSummaryDerivatives,
            MemoryRetainedLocalCopy.Attachments,
            MemoryRetainedLocalCopy.ResponseCaches,
            MemoryRetainedLocalCopy.ApplicationLogs,
            .. auditFilesExist ? (MemoryRetainedLocalCopy[])[MemoryRetainedLocalCopy.AuditLog] : [],
            MemoryRetainedLocalCopy.BackupArchives,
            MemoryRetainedLocalCopy.OtherLocalState,
        ];
    }

    /// <summary>Whether inference audit files exist beside the path the audit logger resolves.</summary>
    internal static bool AuditFilesExist(ArcanumSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return AuditFilesExist(settings.ResolveHostAuditLog().FilePath);
    }

    /// <summary>Whether a dated <c>{stem}-*.jsonl</c> audit file exists beside <paramref name="configuredPath"/>.</summary>
    /// <remarks>
    /// A directory that cannot be listed reports the audit log as retained: saying a copy may exist when
    /// it does not is the safe direction to be wrong in.
    /// </remarks>
    internal static bool AuditFilesExist(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        (string directory, string stem) = InferenceAuditLogger.ResolvePathParts(configuredPath);

        try
        {
            return Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, $"{stem}-*.jsonl", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
