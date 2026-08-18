using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

/// <summary>
/// Raw-SQL persistence for The Lexicon, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection. Neither <c>lexicon_entries</c> nor <c>lexicon_fts</c> is part of the compiled EF
/// model (they are declared in <c>Data/Schema/</c> and installed with the rest of the schema), so all
/// access goes through <see cref="DbCommand"/> rather than LINQ, mirroring <c>SagaMemoryStore</c>.
/// Writes use <c>BEGIN IMMEDIATE</c> inside <see cref="SqliteBusyRetry"/> so concurrent
/// <c>scribe_lexicon</c> appends serialize and cannot lose facts.
/// </summary>
internal sealed class LexiconService(
    ArcanumDbContext db,
    ILogger<LexiconService> logger) : ILexiconService
{

    private const string SelectColumns = "Id, Name, Type, FactsJson, UpdatedAt";

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        CancellationToken cancellationToken = default) =>
        UpsertCoreAsync(name, type, facts, provenance: null, cancellationToken);

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        AttachmentMemoryProvenance provenance,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(provenance);

        return UpsertCoreAsync(name, type, facts, provenance, cancellationToken);

    }

    private async Task<Result<LexiconEntryDto>> UpsertCoreAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        AttachmentMemoryProvenance? provenance,
        CancellationToken cancellationToken)
    {

        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "Lexicon entity name is required.");
        }

        if (trimmedName.Length > LexiconLimits.MaxNameLength)
        {
            return new Error(
                ErrorCodes.Lexicon.InvalidName,
                $"Lexicon entity name exceeds the {LexiconLimits.MaxNameLength} character limit.");
        }

        string normalized = NormalizeName(trimmedName);

        List<string> incoming = NormalizeIncomingFacts(facts);

        if (incoming.Count == 0)
        {
            return new Error(ErrorCodes.Lexicon.InvalidFact, "scribe_lexicon requires at least one non-empty fact.");
        }

        string trimmedType = type?.Trim() ?? string.Empty;

        if (trimmedType.Length > LexiconLimits.MaxTypeLength)
        {
            return new Error(
                ErrorCodes.Lexicon.InvalidFact,
                $"Lexicon entity type exceeds the {LexiconLimits.MaxTypeLength} character limit.");
        }

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    // BEGIN IMMEDIATE acquires the write lock up front so the read-merge-write is
                    // serialized against other concurrent scribe_lexicon appends. SqliteBusyRetry
                    // re-runs the whole delegate (fresh BEGIN) on SQLITE_BUSY/LOCKED.
                    await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                    try
                    {
                        LexiconEntryDto? existing = await ReadByNormalizedAsync(connection, normalized, cancellationToken).ConfigureAwait(false);

                        string resolvedType = ResolveType(existing, trimmedType);

                        List<string> merged = MergeFacts(existing, incoming);

                        DateTimeOffset now = DateTimeOffset.UtcNow;

                        string factsJson = SerializeFacts(merged);

                        string factsText = BuildFactsText(merged);

                        Guid id = existing?.Id ?? Guid.NewGuid();

                        if (existing is null)
                        {
                            await InsertAsync(connection, id, trimmedName, normalized, resolvedType, factsJson, factsText, now, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await UpdateAsync(connection, id, trimmedName, normalized, resolvedType, factsJson, factsText, now, cancellationToken).ConfigureAwait(false);
                        }

                        LexiconFactProvenance[] factProvenance = await ReplaceFactProvenanceAsync(
                            connection,
                            id,
                            existing?.FactProvenance ?? [],
                            incoming,
                            merged,
                            provenance,
                            cancellationToken).ConfigureAwait(false);

                        await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                        return Result<LexiconEntryDto>.Success(
                            new LexiconEntryDto(
                                id,
                                trimmedName,
                                resolvedType,
                                merged.ToArray(),
                                now,
                                factProvenance));
                    }
                    catch
                    {
                        await TryRollbackAsync(connection, trimmedName).ConfigureAwait(false);

                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lexicon upsert failed for entity {Name}.", trimmedName);

            return new Error(ErrorCodes.Lexicon.WriteFailed, "Lexicon write failed.");
        }

    }

    public async Task<Result<bool>> DeleteByNameAsync(string name, CancellationToken cancellationToken = default)
    {

        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "Lexicon entity name is required.");
        }

        string normalized = NormalizeName(trimmedName);

        try
        {
            bool removed = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                    try
                    {
                        await using DbCommand cmd = connection.CreateCommand();

                        cmd.CommandText =
                            """
                            DELETE FROM lexicon_fact_attachment_provenance
                            WHERE EntryId = (
                                SELECT Id FROM lexicon_entries WHERE NameNormalized = @normalized
                            );

                            DELETE FROM lexicon_entries WHERE NameNormalized = @normalized;
                            """;

                        AddParameter(cmd, "@normalized", normalized);

                        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                        return affected > 0;
                    }
                    catch
                    {
                        await TryRollbackAsync(connection, trimmedName).ConfigureAwait(false);

                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            return Result<bool>.Success(removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lexicon delete failed for entity {Name}.", trimmedName);

            return new Error(ErrorCodes.Lexicon.WriteFailed, "Lexicon delete failed.");
        }

    }

    public async Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(
        IReadOnlyList<string> entities,
        int limit,
        CancellationToken cancellationToken = default)
    {

        if (entities is null || entities.Count == 0)
        {
            return Result<IReadOnlyList<LexiconEntryDto>>.Success(Array.Empty<LexiconEntryDto>());
        }

        int clampedLimit = Math.Clamp(limit, 1, 100);

        List<string> normalized = entities
            .Select(static e => NormalizeName(e ?? string.Empty))
            .Where(static n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
        {
            return Result<IReadOnlyList<LexiconEntryDto>>.Success(Array.Empty<LexiconEntryDto>());
        }

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    // Exact-name hits first (ordered by UpdatedAt DESC), then FTS hits (bm25 ASC).
                    // Deduplicate by Id across both passes.
                    List<LexiconEntryDto> exactHits = new(clampedLimit);

                    HashSet<Guid> seenIds = new(clampedLimit);

                    HashSet<string> exactNames = new(StringComparer.Ordinal);

                    await FillExactMatchesAsync(connection, normalized, clampedLimit, exactHits, seenIds, exactNames, cancellationToken).ConfigureAwait(false);

                    List<LexiconEntryDto> ftsHits = new(Math.Max(0, clampedLimit - exactHits.Count));

                    if (exactHits.Count < clampedLimit)
                    {
                        List<string> unresolved = normalized.Where(n => !exactNames.Contains(n)).ToList();

                        if (unresolved.Count > 0)
                        {
                            await FillFtsMatchesAsync(connection, unresolved, clampedLimit - exactHits.Count, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    List<LexiconEntryDto> ordered = new(exactHits.Count + ftsHits.Count);

                    ordered.AddRange(exactHits);

                    ordered.AddRange(ftsHits);

                    if (ordered.Count > clampedLimit)
                    {
                        ordered = ordered.GetRange(0, clampedLimit);
                    }

                    Dictionary<Guid, LexiconFactProvenance[]> provenance = await ReadFactProvenanceBatchAsync(
                        connection,
                        ordered.ConvertAll(static entry => entry.Id),
                        cancellationToken).ConfigureAwait(false);

                    for (int index = 0; index < ordered.Count; index++)
                    {

                        LexiconEntryDto entry = ordered[index];

                        ordered[index] = entry with
                        {
                            FactProvenance = provenance.TryGetValue(entry.Id, out LexiconFactProvenance[]? facts)
                                ? facts
                                : [],
                        };

                    }

                    return Result<IReadOnlyList<LexiconEntryDto>>.Success(ordered);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lexicon match failed.");

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon search failed.");
        }

    }

    public async Task<Result<LexiconEntryDto?>> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {

        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "Lexicon entity name is required.");
        }

        string normalized = NormalizeName(trimmedName);

        try
        {
            LexiconEntryDto? entry = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    return await ReadByNormalizedAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            return Result<LexiconEntryDto?>.Success(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lexicon get failed for entity {Name}.", trimmedName);

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon lookup failed.");
        }

    }

    public async Task<Result<IReadOnlyList<LexiconEntryDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {

        try
        {

            return await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    await using DbCommand command = connection.CreateCommand();

                    command.CommandText =
                        """
                        SELECT Id, Name, Type, FactsJson, UpdatedAt
                        FROM lexicon_entries
                        ORDER BY Name COLLATE NOCASE, Id
                        """;

                    await using DbDataReader reader = await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);

                    List<LexiconEntryDto> entries = [];

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        entries.Add(ReadEntry(reader));

                    }

                    await reader.DisposeAsync().ConfigureAwait(false);

                    if (entries.Count > 0)
                    {

                        Dictionary<Guid, LexiconFactProvenance[]> provenance = await ReadAllFactProvenanceAsync(
                            connection,
                            cancellationToken).ConfigureAwait(false);

                        for (int index = 0; index < entries.Count; index++)
                        {

                            LexiconEntryDto entry = entries[index];

                            entries[index] = entry with
                            {
                                FactProvenance = provenance.TryGetValue(entry.Id, out LexiconFactProvenance[]? facts)
                                    ? facts
                                    : [],
                            };

                        }

                    }

                    return Result<IReadOnlyList<LexiconEntryDto>>.Success(entries);

                },
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            logger.LogWarning(exception, "Lexicon list failed.");

            return new Error(
                ErrorCodes.Lexicon.SearchFailed,
                "Lexicon listing failed.");

        }

    }

    private static string NormalizeName(string value) =>
        value.Trim().ToUpperInvariant();

    private static List<string> NormalizeIncomingFacts(IReadOnlyList<string> facts)
    {

        List<string> result = [];

        if (facts is null)
        {
            return result;
        }

        foreach (string fact in facts)
        {
            string trimmed = fact?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    private static string ResolveType(LexiconEntryDto? existing, string incomingType)
    {

        if (!string.IsNullOrEmpty(incomingType))
        {
            return incomingType;
        }

        if (existing is not null)
        {
            return existing.Type;
        }

        return LexiconLimits.DefaultType;
    }

    private static List<string> MergeFacts(LexiconEntryDto? existing, List<string> incoming)
    {

        List<string> merged = [];

        HashSet<string> seen = new(StringComparer.Ordinal);

        if (existing is not null)
        {
            foreach (string fact in existing.Facts)
            {
                if (seen.Add(fact))
                {
                    merged.Add(fact);
                }
            }
        }

        foreach (string fact in incoming)
        {
            if (seen.Add(fact))
            {
                merged.Add(fact);
            }
        }

        return merged;
    }

    private static string SerializeFacts(List<string> facts) =>
        JsonSerializer.Serialize(facts, LexiconJsonContext.Default.ListString);

    private static string BuildFactsText(List<string> facts)
    {

        StringBuilder sb = new(facts.Count * 32);

        for (int i = 0; i < facts.Count; i++)
        {
            if (i > 0)
            {
                _ = sb.Append('\n');
            }

            _ = sb.Append(facts[i]);
        }

        return sb.ToString();
    }

    private static string[] DeserializeFacts(string factsJson)
    {

        if (string.IsNullOrWhiteSpace(factsJson))
        {
            return [];
        }

        try
        {
            List<string>? facts = JsonSerializer.Deserialize(factsJson, LexiconJsonContext.Default.ListString);

            return facts?.ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

    }

    private async Task FillExactMatchesAsync(
        DbConnection connection,
        List<string> normalized,
        int limit,
        List<LexiconEntryDto> exactHits,
        HashSet<Guid> seenIds,
        HashSet<string> exactNames,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new();
        _ = sql.Append("SELECT ")
            .Append(SelectColumns)
            .Append(" FROM lexicon_entries WHERE NameNormalized IN (");

        for (int i = 0; i < normalized.Count; i++)
        {
            if (i > 0)
            {
                _ = sql.Append(", ");
            }

            string paramName = "@n" + i.ToString(CultureInfo.InvariantCulture);

            _ = sql.Append(paramName);

            AddParameter(cmd, paramName, normalized[i]);
        }

        _ = sql.Append(") ORDER BY UpdatedAt DESC LIMIT @limit");

        AddParameter(cmd, "@limit", limit);

        cmd.CommandText = sql.ToString();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            LexiconEntryDto entry = ReadEntry(reader);

            if (seenIds.Add(entry.Id))
            {
                exactHits.Add(entry);

                exactNames.Add(NormalizeName(entry.Name));
            }
        }

    }

    private async Task FillFtsMatchesAsync(
        DbConnection connection,
        List<string> unresolved,
        int remaining,
        HashSet<Guid> seenIds,
        List<LexiconEntryDto> ftsHits,
        CancellationToken cancellationToken)
    {

        string matchQuery = BuildFtsMatchQuery(unresolved);

        if (matchQuery.Length == 0)
        {
            return;
        }

        try
        {
            await FillFtsMatchesViaMatchAsync(connection, matchQuery, remaining, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Lexicon FTS MATCH failed; falling back to LIKE search.");

            await FillFtsMatchesViaLikeAsync(connection, unresolved, remaining, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
        }

    }

    private static async Task FillFtsMatchesViaMatchAsync(
        DbConnection connection,
        string matchQuery,
        int remaining,
        HashSet<Guid> seenIds,
        List<LexiconEntryDto> ftsHits,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        // bm25() weight arguments map positionally to the FTS5 columns (Name, Type, FactsText):
        // 3.0 for Name, 2.0 for Type, 1.0 for FactsText. FTS5 bm25 returns more-negative values for
        // better matches, so sort ASC. No Lucene caret boosting inside MATCH — SQLite FTS5 does not
        // support it; mathematical boosting lives only in bm25().
        cmd.CommandText =
            """
            SELECT e.Id, e.Name, e.Type, e.FactsJson, e.UpdatedAt
            FROM lexicon_fts
            INNER JOIN lexicon_entries e ON e.rowid = lexicon_fts.rowid
            WHERE lexicon_fts MATCH @query
            ORDER BY bm25(lexicon_fts, 3.0, 2.0, 1.0) ASC
            LIMIT @limit
            """;

        AddParameter(cmd, "@query", matchQuery);

        AddParameter(cmd, "@limit", remaining);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ftsHits.Count >= remaining)
            {
                break;
            }

            LexiconEntryDto entry = ReadEntry(reader);

            if (seenIds.Add(entry.Id))
            {
                ftsHits.Add(entry);
            }
        }

    }

    private static async Task FillFtsMatchesViaLikeAsync(
        DbConnection connection,
        List<string> unresolved,
        int remaining,
        HashSet<Guid> seenIds,
        List<LexiconEntryDto> ftsHits,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new();
        _ = sql.Append("SELECT ")
            .Append(SelectColumns)
            .Append(" FROM lexicon_entries WHERE ");

        for (int i = 0; i < unresolved.Count; i++)
        {
            if (i > 0)
            {
                _ = sql.Append(" OR ");
            }

            string nameParam = "@name" + i.ToString(CultureInfo.InvariantCulture);

            string factsParam = "@facts" + i.ToString(CultureInfo.InvariantCulture);

            _ = sql.Append("Name LIKE ").Append(nameParam).Append(" ESCAPE '\\'");

            _ = sql.Append(" OR FactsText LIKE ").Append(factsParam).Append(" ESCAPE '\\'");

            AddParameter(cmd, nameParam, "%" + EscapeLikePattern(unresolved[i]) + "%");

            AddParameter(cmd, factsParam, "%" + EscapeLikePattern(unresolved[i]) + "%");
        }

        _ = sql.Append(" ORDER BY UpdatedAt DESC LIMIT @limit");

        AddParameter(cmd, "@limit", remaining);

        cmd.CommandText = sql.ToString();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ftsHits.Count >= remaining)
            {
                break;
            }

            LexiconEntryDto entry = ReadEntry(reader);

            if (seenIds.Add(entry.Id))
            {
                ftsHits.Add(entry);
            }
        }

    }

    private static string BuildFtsMatchQuery(List<string> unresolved)
    {

        StringBuilder sb = new();

        foreach (string term in unresolved)
        {
            string sanitized = FtsMatchQuerySanitizer.Sanitize(term);

            if (sanitized.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append(" OR ");
            }

            _ = sb.Append(sanitized);
        }

        return sb.ToString();
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<LexiconEntryDto?> ReadByNormalizedAsync(
        DbConnection connection,
        string normalized,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = $"SELECT {SelectColumns} FROM lexicon_entries WHERE NameNormalized = @normalized LIMIT 1";

        AddParameter(cmd, "@normalized", normalized);

        LexiconEntryDto? entry;

        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return null;

            }

            entry = ReadEntry(reader);

        }

        LexiconFactProvenance[] provenance = await ReadFactProvenanceAsync(
            connection,
            entry.Id,
            cancellationToken).ConfigureAwait(false);

        return entry with { FactProvenance = provenance };

    }

    private static async Task<LexiconFactProvenance[]> ReplaceFactProvenanceAsync(
        DbConnection connection,
        Guid entryId,
        IReadOnlyList<LexiconFactProvenance> existing,
        IReadOnlyList<string> incoming,
        IReadOnlyList<string> retained,
        AttachmentMemoryProvenance? provenance,
        CancellationToken cancellationToken)
    {

        // MergeFacts is an uncapped union, so `retained` grows monotonically over an entry's life.
        // Probing it with Enumerable.Contains made both loops below O(existing x retained) ordinal
        // scans inside the BEGIN IMMEDIATE critical section.
        HashSet<string> retainedSet = new(retained, StringComparer.Ordinal);

        Dictionary<string, AttachmentMemoryProvenance> sources = existing
            .Where(item => retainedSet.Contains(item.Fact))
            .ToDictionary(item => item.Fact, item => item.Source, StringComparer.Ordinal);

        if (provenance is not null)
        {

            foreach (string fact in incoming)
            {

                if (retainedSet.Contains(fact))
                {

                    sources[fact] = provenance;

                }

            }

        }

        if (HasProvenanceChanged(existing, sources))
        {

            await using (DbCommand delete = connection.CreateCommand())
            {

                delete.CommandText =
                    "DELETE FROM lexicon_fact_attachment_provenance WHERE EntryId = @entryId";

                AddParameter(delete, "@entryId", entryId.ToString("N"));

                _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            // One command, re-executed with fresh parameter values per row. A multi-row INSERT would
            // bind ten parameters per fact and collide with SQLite's bound-parameter ceiling at exactly
            // the entry sizes that make this loop worth caring about.
            await using DbCommand insert = connection.CreateCommand();

            insert.CommandText =
                """
                INSERT INTO lexicon_fact_attachment_provenance (
                    EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey,
                    Version, ContentHash, MaterializedAt, SourceType)
                VALUES (
                    @entryId, @factHash, @fact, @sessionId, @attachmentId, @logicalKey,
                    @version, @contentHash, @materializedAt, @sourceType)
                """;

            foreach ((string fact, AttachmentMemoryProvenance source) in sources)
            {

                insert.Parameters.Clear();

                AddParameter(insert, "@entryId", entryId.ToString("N"));

                AddParameter(insert, "@factHash", HashFact(fact));

                AddParameter(insert, "@fact", fact);

                AddProvenanceParameters(insert, source);

                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

        }

        return
        [
            .. sources.Select(
                pair => new LexiconFactProvenance(
                    pair.Key,
                    pair.Value with { Availability = AttachmentSourceAvailability.Available })),
        ];

    }

    /// <summary>
    /// Whether the provenance rows on disk already say exactly what <paramref name="sources"/> says.
    /// </summary>
    /// <remarks>
    /// <c>Availability</c> is deliberately excluded: it is never stored, it is recomputed on every read
    /// by the <c>EXISTS(... State = 'Bound')</c> subquery, so a difference in it is not a difference in
    /// the rows. Skipping the rewrite also preserves the per-entry rowid contiguity that the
    /// <c>ORDER BY p.rowid</c> hydration relies on for insertion order.
    /// </remarks>
    private static bool HasProvenanceChanged(
        IReadOnlyList<LexiconFactProvenance> existing,
        Dictionary<string, AttachmentMemoryProvenance> sources)
    {

        if (existing.Count != sources.Count)
        {
            return true;
        }

        foreach (LexiconFactProvenance item in existing)
        {

            if (!sources.TryGetValue(item.Fact, out AttachmentMemoryProvenance? source)
                || item.Source with { Availability = AttachmentSourceAvailability.Available }
                    != source with { Availability = AttachmentSourceAvailability.Available })
            {
                return true;
            }

        }

        return false;

    }

    private static async Task<LexiconFactProvenance[]> ReadFactProvenanceAsync(
        DbConnection connection,
        Guid entryId,
        CancellationToken cancellationToken)
    {

        Dictionary<Guid, LexiconFactProvenance[]> provenance = await ReadFactProvenanceBatchAsync(
            connection,
            [entryId],
            cancellationToken).ConfigureAwait(false);

        return provenance.TryGetValue(entryId, out LexiconFactProvenance[]? facts) ? facts : [];

    }

    /// <summary>
    /// Provenance for a whole result set in one read, keyed by entry id. Hydrating per entry made
    /// every listing and every per-turn match an N+1 against the encrypted Grimoire.
    /// </summary>
    private static Task<Dictionary<Guid, LexiconFactProvenance[]>> ReadFactProvenanceBatchAsync(
        DbConnection connection,
        IReadOnlyList<Guid> entryIds,
        CancellationToken cancellationToken) =>
        entryIds.Count == 0
            ? Task.FromResult(new Dictionary<Guid, LexiconFactProvenance[]>(0))
            : ReadFactProvenanceCoreAsync(connection, entryIds, cancellationToken);

    /// <summary>
    /// Provenance for every entry in one read. <see cref="ListAsync"/> returns the entire
    /// <c>lexicon_entries</c> table, so an <c>IN</c> clause would add one parameter per entry without
    /// narrowing anything — and would eventually collide with SQLite's bound-parameter ceiling.
    /// </summary>
    private static Task<Dictionary<Guid, LexiconFactProvenance[]>> ReadAllFactProvenanceAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        ReadFactProvenanceCoreAsync(connection, entryIds: null, cancellationToken);

    private static async Task<Dictionary<Guid, LexiconFactProvenance[]>> ReadFactProvenanceCoreAsync(
        DbConnection connection,
        IReadOnlyList<Guid>? entryIds,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        StringBuilder sql = new(
            """
            SELECT p.EntryId, p.Fact, p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                   p.ContentHash, p.MaterializedAt, p.SourceType,
                   EXISTS(
                       SELECT 1 FROM "SessionAttachments" a
                       WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                   )
            FROM lexicon_fact_attachment_provenance p
            """);

        if (entryIds is not null)
        {

            _ = sql.Append(" WHERE p.EntryId IN (");

            for (int index = 0; index < entryIds.Count; index++)
            {

                if (index > 0)
                {
                    _ = sql.Append(", ");
                }

                string parameterName = "@e" + index.ToString(CultureInfo.InvariantCulture);

                _ = sql.Append(parameterName);

                AddParameter(command, parameterName, entryIds[index].ToString("N"));

            }

            _ = sql.Append(')');

        }

        // rowid ordering is global here, which preserves each entry's own insertion order because
        // ReplaceFactProvenanceAsync rewrites one entry's rows contiguously.
        _ = sql.Append(" ORDER BY p.rowid");

        command.CommandText = sql.ToString();

        Dictionary<Guid, List<LexiconFactProvenance>> grouped = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            AttachmentMemoryProvenance source = new(
                Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetString(8),
                reader.GetInt32(9) == 1
                    ? AttachmentSourceAvailability.Available
                    : AttachmentSourceAvailability.Unavailable);

            Guid entryId = Guid.Parse(reader.GetString(0));

            if (!grouped.TryGetValue(entryId, out List<LexiconFactProvenance>? facts))
            {

                facts = [];

                grouped[entryId] = facts;

            }

            facts.Add(new LexiconFactProvenance(reader.GetString(1), source));

        }

        Dictionary<Guid, LexiconFactProvenance[]> results = new(grouped.Count);

        foreach ((Guid entryId, List<LexiconFactProvenance> facts) in grouped)
        {

            results[entryId] = [.. facts];

        }

        return results;

    }

    private static void AddProvenanceParameters(
        DbCommand command,
        AttachmentMemoryProvenance provenance)
    {

        AddParameter(command, "@sessionId", provenance.SessionId.ToString());

        AddParameter(command, "@attachmentId", provenance.AttachmentId.ToString());

        AddParameter(command, "@logicalKey", provenance.LogicalKey);

        AddParameter(command, "@version", provenance.Version);

        AddParameter(command, "@contentHash", provenance.ContentHash);

        AddParameter(
            command,
            "@materializedAt",
            provenance.MaterializedAt.ToString("o", CultureInfo.InvariantCulture));

        AddParameter(command, "@sourceType", provenance.SourceType);

    }

    private static string HashFact(string fact) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fact))).ToLowerInvariant();

    private static async Task InsertAsync(
        DbConnection connection,
        Guid id,
        string name,
        string normalized,
        string type,
        string factsJson,
        string factsText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES (@id, @name, @normalized, @type, @factsJson, @factsText, @updatedAt)
            """;

        AddParameter(cmd, "@id", id.ToString("N"));
        AddParameter(cmd, "@name", name);
        AddParameter(cmd, "@normalized", normalized);
        AddParameter(cmd, "@type", type);
        AddParameter(cmd, "@factsJson", factsJson);
        AddParameter(cmd, "@factsText", factsText);
        AddParameter(cmd, "@updatedAt", updatedAt.ToString("o", CultureInfo.InvariantCulture));

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task UpdateAsync(
        DbConnection connection,
        Guid id,
        string name,
        string normalized,
        string type,
        string factsJson,
        string factsText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            UPDATE lexicon_entries
            SET Name = @name,
                NameNormalized = @normalized,
                Type = @type,
                FactsJson = @factsJson,
                FactsText = @factsText,
                UpdatedAt = @updatedAt
            WHERE Id = @id
            """;

        AddParameter(cmd, "@id", id.ToString("N"));
        AddParameter(cmd, "@name", name);
        AddParameter(cmd, "@normalized", normalized);
        AddParameter(cmd, "@type", type);
        AddParameter(cmd, "@factsJson", factsJson);
        AddParameter(cmd, "@factsText", factsText);
        AddParameter(cmd, "@updatedAt", updatedAt.ToString("o", CultureInfo.InvariantCulture));

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static LexiconEntryDto ReadEntry(DbDataReader reader)
    {

        Guid id = Guid.Parse(reader.GetString(0));

        string name = reader.GetString(1);

        string type = reader.GetString(2);

        string factsJson = reader.GetString(3);

        DateTimeOffset updatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

        return new LexiconEntryDto(id, name, type, DeserializeFacts(factsJson), updatedAt);

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;

    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, CancellationToken cancellationToken, string commandText)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = commandText;

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Releases the <c>BEGIN IMMEDIATE</c> write lock on the shared scoped connection.
    /// </summary>
    /// <remarks>
    /// The <c>ROLLBACK</c> is deliberately issued on <see cref="CancellationToken.None"/>. Cancellation
    /// is the main reason this path runs at all — an aborted <c>DELETE /api/memory/lexicon/{name}</c>
    /// hands the handler <c>RequestAborted</c> — and <c>DbCommand.ExecuteNonQueryAsync</c> returns a
    /// cancelled task before issuing any SQL when the token is already signalled, so rolling back on the
    /// caller's token would skip the release on exactly the path it exists for. This matches
    /// <c>CampaignRepository.TryRollbackAsync</c> and <c>SessionEntryPersistence</c>, which both clean up
    /// on a token the caller cannot cancel.
    /// </remarks>
    private async Task TryRollbackAsync(DbConnection connection, string entityName)
    {

        try
        {
            await ExecuteNonQueryAsync(connection, CancellationToken.None, "ROLLBACK").ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is SqliteException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            // Best-effort rollback: a failed BEGIN or an already-closed connection leaves nothing to
            // release. Anything outside these three is unexpected and must not be swallowed silently.
            logger.LogWarning(
                ex,
                "Lexicon rollback failed for entity {Name}; the write transaction was left to the connection reset.",
                entityName);
        }

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

}
