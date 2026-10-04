using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

/// <summary>
/// Raw-SQL persistence for The Lexicon, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection. Neither <c>lexicon_entries</c> nor <c>lexicon_fts</c> is part of the compiled EF
/// model (they are declared in <c>Data/Schema/</c> and installed with the rest of the schema), so all
/// access goes through <see cref="DbCommand"/> rather than LINQ, mirroring <c>SagaMemoryStore</c>.
/// Writes use <c>BEGIN IMMEDIATE</c> inside <see cref="SqliteBusyRetry"/> so concurrent
/// <c>scribe_lexicon</c> appends serialize and cannot lose facts.
/// </summary>
internal sealed partial class LexiconService(
    ArcanumDbContext db,
    ILogger<LexiconService> logger,
    IOptionsMonitor<ArcanumSettings> options,
    IMemoryErasureKeyProvider erasureKeys,
    ICovenantLabeledArtifactGuard? labeledArtifactGuard = null,
    IMemoryReviewTokenCodec? reviewTokenCodec = null,
    TimeProvider? timeProvider = null,
    LexiconErasureDependencies? erasure = null) : ILexiconService, ILexiconCurationService, ILexiconMemoryReviewService
{
    private readonly ICovenantLabeledArtifactGuard? _labeledArtifactGuard = labeledArtifactGuard;

    private readonly IMemoryReviewTokenCodec _reviewTokenCodec =
        reviewTokenCodec ?? new MemoryReviewTokenCodec(TimeProvider.System);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const string SelectColumns = "Id, Name, Type, FactsJson, UpdatedAt, ScopeCampaignId, RetiredAtUtc, PinnedAtUtc, CurationGeneration";

    private const string LegacySelectColumns = "Id, Name, Type, FactsJson, UpdatedAt, ScopeCampaignId, NULL AS RetiredAtUtc, NULL AS PinnedAtUtc, 1 AS CurationGeneration";

    private static string EntryColumnsFor(bool curation) => curation ? SelectColumns : LegacySelectColumns;

    private static async Task<bool> HasCurationAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken).ConfigureAwait(false) >= 11;

    private static readonly Error CurationUnavailableError = new(ErrorCodes.Lexicon.CurationUnavailable,
        "Lexicon curation is temporarily unavailable while the Core schema transition completes.");

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        UpsertCoreAsync(name, type, facts, scope, provenance: null, cancellationToken);

    public Task<Result<LexiconEntryDto>> UpsertAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        AttachmentMemoryProvenance provenance,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        return UpsertCoreAsync(name, type, facts, scope, provenance, cancellationToken);
    }

    private async Task<Result<LexiconEntryDto>> UpsertCoreAsync(
        string name,
        string? type,
        IReadOnlyList<string> facts,
        LexiconScope scope,
        AttachmentMemoryProvenance? provenance,
        CancellationToken cancellationToken)
    {
        Result<LexiconCanonicalValue> incoming = LexiconValueNormalizer.NormalizeScribe(name, type, facts);

        if (incoming.IsFailure)
        {
            return incoming.Error;
        }

        string trimmedName = incoming.Value.Name;

        string normalized = incoming.Value.NameNormalized;

        try
        {
            // Opened before the erasure guard's first phase, which must see the connection outside any
            // transaction: resolving the erasure key can read the OS credential store, and that never
            // happens under a SQLite lock, raw BEGIN IMMEDIATE included.
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            return await MemoryErasureGuard.RunWithRetryAsync(
                (SqliteConnection)connection,
                MemoryReviewStore.Lexicon,
                erasureKeys,
                guard => SqliteBusyRetry.ExecuteAsync(
                    async () =>
                    {
                        // BEGIN IMMEDIATE acquires the write lock up front so the read-merge-write is
                        // serialized against other concurrent scribe_lexicon appends. SqliteBusyRetry
                        // re-runs the whole delegate (fresh BEGIN) on SQLITE_BUSY/LOCKED.
                        await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                        try
                        {
                            bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

                            LexiconEntryDto? existing = await ReadByNormalizedAsync(connection, normalized, scope.Key, cancellationToken).ConfigureAwait(false);

                            // The erasure chokepoint, inside the write lock and whether or not a row still
                            // holds the name: an erased name is refused in exactly the scope it was erased
                            // in. The identity is always the incoming name under the current rule, never a
                            // stored normalized spelling, and is derived only when the store holds evidence,
                            // so a scribe on an installation that has erased nothing is never failed by it.
                            // The raw BEGIN leaves no transaction object to pass.
                            MemoryErasureGuardVerdict erasure = await MemoryErasureGuard.CheckAsync(
                                (SqliteConnection)connection,
                                null,
                                guard,
                                () => MemoryErasureIdentity.ForLexicon(scope.CampaignId, trimmedName),
                                cancellationToken).ConfigureAwait(false);

                            switch (erasure)
                            {
                                case MemoryErasureGuardVerdict.Allowed:
                                    break;

                                case MemoryErasureGuardVerdict.Withheld:
                                    throw new InspectionException(new Error(ErrorCodes.Lexicon.SuppressedNameRefused,
                                        LexiconAgentRefusals.OperatorManaged));

                                case MemoryErasureGuardVerdict.RetryWithKey:
                                    throw new MemoryErasureRetryException();

                                default:
                                    throw new MemoryErasureGuardException(MemoryErasureGuard.KeyLostError);
                            }

                            if (existing?.RetiredAtUtc is not null)
                            {
                                throw new InspectionException(new Error(ErrorCodes.Lexicon.RetiredMutationRefused,
                                    "A retired Lexicon entry must be reinstated before scribing."));
                            }

                            Result<LexiconCanonicalValue> merged = LexiconValueNormalizer.NormalizeScribe(
                                existing?.Name ?? trimmedName, type, incoming.Value.Facts, existing?.Type, existing?.Facts);

                            if (merged.IsFailure)
                            {
                                throw new InspectionException(merged.Error);
                            }

                            LexiconCanonicalValue content = merged.Value;

                            ArtifactSensitivityLabel? label = existing is null ? null : await ReadVerifiedLabelAsync(connection,
                                existing.Id, new(scope.IsGlobal ? LexiconScopeKind.Global : LexiconScopeKind.Campaign, scope.CampaignId),
                                cancellationToken).ConfigureAwait(false);

                            AnnalClaimVersion? head = existing is null ? null : await ReadVerifiedScribeHeadAsync(
                                connection, existing, label, cancellationToken).ConfigureAwait(false);

                            bool changed = existing is null || existing.Type != content.Type
                                || !existing.Facts.SequenceEqual(content.Facts, StringComparer.Ordinal);

                            if (!changed)
                            {
                                await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                                return Result<LexiconEntryDto>.Success(existing!);
                            }

                            if (label is not null)
                            {
                                throw new InspectionException(new Error(ErrorCodes.Lexicon.ProtectedMutationRefused,
                                    "A protected Lexicon entry requires operator correction to change its content."));
                            }

                            if (existing?.CurationGeneration == long.MaxValue)
                            {
                                throw new InspectionException(new Error(ErrorCodes.Lexicon.CurationGenerationExhausted,
                                    "The Lexicon curation generation is exhausted."));
                            }

                            DateTimeOffset now = NextRecordedAt(existing?.UpdatedAt, head);

                            Guid id = existing?.Id ?? Guid.NewGuid();

                            if (existing is null)
                            {
                                await InsertAsync(connection, id, content.Name, normalized, scope.Key, content.Type, content.FactsJson, content.FactsText, now, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await UpdateAsync(connection, id, content.Name, normalized, scope.Key, content.Type, content.FactsJson, content.FactsText, now, cancellationToken).ConfigureAwait(false);
                            }

                            LexiconFactProvenance[] factProvenance = await ReplaceFactProvenanceAsync(
                                connection, id, existing?.FactProvenance ?? [], incoming.Value.Facts, content.Facts,
                                provenance, cancellationToken).ConfigureAwait(false);

                            if (options.CurrentValue.Features.Annals || head is not null)
                            {
                                // Publish after replacing current provenance so immutable ordinal evidence
                                // describes the resulting canonical value. Existing history must continue
                                // to describe its row even when ordinary capture is disabled.
                                _ = await AnnalsClaimWriter.AppendCorrectionAsync(
                                    connection,
                                    transaction: null,
                                    AnnalSubjectStore.Lexicon,
                                    // The "N" format, because that is the exact text lexicon_entries.Id
                                    // stores. A subject id in any other format is one nothing can ever join
                                    // back to its row: not an erasure, not a retention sweep, not a reader.
                                    id.ToString("N"),
                                    AnnalOrigin.AgentAsserted,
                                    scope.CampaignId is null ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
                                    scope.CampaignId?.ToString(),
                                    ContentSensitivity.None,
                                    curation ? AnnalContentHashFormat.LexiconStructuredSnapshot : AnnalContentHashFormat.LegacyStoreDigest,
                                    curation ? LexiconSnapshotDigest.Compute(content) : AnnalContentDigest.ForLexiconEntry(content.Type, content.FactsText),
                                    now,
                                    now,
                                    sourceSessionId: null,
                                    cancellationToken,
                                    legacySchema: !curation).ConfigureAwait(false);
                            }

                            await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                            return Result<LexiconEntryDto>.Success(
                                new LexiconEntryDto(
                                    id,
                                    content.Name,
                                    content.Type,
                                    content.Facts.ToArray(),
                                    now,
                                    factProvenance,
                                    scope.CampaignId,
                                    existing?.RetiredAtUtc,
                                    existing?.PinnedAtUtc,
                                    !curation || existing is null ? 1 : existing.CurationGeneration + 1,
                                    existing?.Eligibility ?? LexiconRetrievalEligibility.Eligible));
                        }
                        catch
                        {
                            await TryRollbackAsync(connection, trimmedName).ConfigureAwait(false);

                            throw;
                        }
                    },
                    cancellationToken),
                cancellationToken,
                // An Unseen Servant entry can never be erased, so it can never be fingerprinted, and a key
                // that is not there has nothing to verify about it. Only the key-state gate is lifted: a
                // present key that cannot verify the store's evidence still refuses the write.
                exemptFromKeyState: LexiconDaemonStateNames.Is(normalized)).ConfigureAwait(false);
        }
        catch (InspectionException exception)
        {
            return exception.Error;
        }
        catch (MemoryErasureGuardException exception)
        {
            return exception.Error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lexicon upsert failed for entity {Name}.", trimmedName);

            return new Error(ErrorCodes.Lexicon.WriteFailed, "Lexicon write failed.");
        }
    }

    private static async Task<AnnalClaimVersion?> ReadVerifiedScribeHeadAsync(
        DbConnection connection, LexiconEntryDto existing, ArtifactSensitivityLabel? label, CancellationToken cancellationToken)
    {
        try
        {
            InspectionRow row = await ReadInspectionRowAsync(connection, existing.Id, cancellationToken).ConfigureAwait(false);

            row = row with { Entry = row.Entry with { FactProvenance = existing.FactProvenance } };

            VerifyCurrentProvenance(row.Entry);

            return await ReadVerifiedHeadAsync(connection, row, label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException
            or InvalidCastException or InvalidDataException or InvalidOperationException or JsonException)
        {
            throw new InspectionException(IntegrityError);
        }
    }

    public async Task<Result<Guid?>> FindAllLifecycleIdentityForDeletionAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > LexiconLimits.MaxNameLength)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "A valid Lexicon entity name is required.");
        }

        try
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            return Result<Guid?>.Success(await ReadAllLifecycleIdentityForDeletionAsync(
                connection, NormalizeName(name), scope.Key, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Lexicon deletion identity lookup failed.");

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon deletion identity lookup failed.");
        }
    }

    public async Task<Result<LexiconAgentDeletionTarget?>> FindAgentDeletionTargetAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > LexiconLimits.MaxNameLength)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "A valid Lexicon entity name is required.");
        }

        try
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // The capability read and the row read share one snapshot, so an upgrade committed between
            // them cannot pair the legacy projection with a curated row.
            LexiconAgentDeletionTarget? target = await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
            {
                bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

                return await ReadAgentDeletionTargetAsync(connection, NormalizeName(name), scope.Key, curation, cancellationToken)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return Result<LexiconAgentDeletionTarget?>.Success(target);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Lexicon agent deletion lookup failed.");

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon agent deletion lookup failed.");
        }
    }

    public Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        DeleteByNameAsync(name, scope, LexiconDeletionOrigin.Operator, cancellationToken);

    public async Task<Result<bool>> DeleteByNameAsync(
        string name,
        LexiconScope scope,
        LexiconDeletionOrigin origin,
        CancellationToken cancellationToken = default)
    {
        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "Lexicon entity name is required.");
        }

        string normalized = NormalizeName(trimmedName);

        try
        {
            Result<bool> removed = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                    try
                    {
                        // An agent may remove only an active, unpinned entry. Decided here, under the write
                        // lock, whatever the tool read before it: a retired or pinned entry is curated state
                        // the operator owns. Only an explicit operator origin skips the check, so an
                        // undefined origin fails closed as an agent's.
                        if (origin != LexiconDeletionOrigin.Operator)
                        {
                            bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

                            LexiconAgentDeletionTarget? target = await ReadAgentDeletionTargetAsync(
                                connection, normalized, scope.Key, curation, cancellationToken).ConfigureAwait(false);

                            if (target is { IsRetired: true } or { IsPinned: true })
                            {
                                await ExecuteNonQueryAsync(connection, CancellationToken.None, "ROLLBACK").ConfigureAwait(false);

                                return Result<bool>.Failure(target.IsRetired
                                    ? new Error(ErrorCodes.Lexicon.RetiredMutationRefused, LexiconAgentRefusals.RetiredDeletion)
                                    : new Error(ErrorCodes.Lexicon.PinnedMutationRefused, LexiconAgentRefusals.OperatorManaged));
                            }
                        }

                        // Deletion must guard the exact identity even when it is retired. Operational
                        // retrieval deliberately cannot see that row, and no content is needed here.
                        if (_labeledArtifactGuard is { } guard
                            && await ReadAllLifecycleIdentityForDeletionAsync(connection, normalized, scope.Key, cancellationToken)
                                .ConfigureAwait(false) is { } identity)
                        {
                            Result unlabeled = await guard.EnsureUnlabeledAsync(SensitiveArtifactKind.Lexicon, identity, cancellationToken)
                                .ConfigureAwait(false);

                            if (unlabeled.IsFailure)
                            {
                                await ExecuteNonQueryAsync(connection, CancellationToken.None, "ROLLBACK").ConfigureAwait(false);

                                return Result<bool>.Failure(unlabeled.Error);
                            }
                        }

                        // Before the entity goes, because the claim is found through the row that names
                        // it. Deliberately ungated: a claim written while the Annals was enabled has to
                        // stay removable after it is disabled, or turning the feature off would strand
                        // records no surface can reach and no reset can clear.
                        await AnnalsClaimWriter.DeleteClaimsForSubjectQueryAsync(
                            connection,
                            transaction: null,
                            AnnalSubjectStore.Lexicon,
                            """
                            SELECT Id FROM lexicon_entries
                            WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey
                            """,
                            cancellationToken,
                            ("@normalized", normalized),
                            ("@scopeKey", scope.Key)).ConfigureAwait(false);

                        await using DbCommand cmd = connection.CreateCommand();

                        cmd.CommandText =
                            """
                            DELETE FROM lexicon_fact_attachment_provenance
                            WHERE EntryId = (
                                SELECT Id FROM lexicon_entries
                                WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey
                            );

                            DELETE FROM lexicon_entries
                            WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey;
                            """;

                        AddParameter(cmd, "@normalized", normalized);

                        AddParameter(cmd, "@scopeKey", scope.Key);

                        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                        return Result<bool>.Success(affected > 0);
                    }
                    catch
                    {
                        await TryRollbackAsync(connection, trimmedName).ConfigureAwait(false);

                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            return removed;
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
        LexiconScope scope,
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

                    return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
                    {
                        // The Campaign tier is resolved whole - exact hits, then FTS hits - before the
                        // global one, and a name the Campaign answered is never answered again from the
                        // global tier. Shadowing rather than merging: two contradictory facts about one term
                        // reaching the same prompt is the outcome this scoping exists to prevent.
                        HashSet<Guid> seenIds = new(clampedLimit);

                        HashSet<string> shadowedNames = new(StringComparer.Ordinal);

                        List<LexiconEntryDto> ordered = new(clampedLimit);

                        if (!scope.IsGlobal)
                        {
                            await FillTierAsync(
                                connection,
                                scope.Key,
                                normalized,
                                clampedLimit,
                                seenIds,
                                shadowedNames,
                                ordered,
                                cancellationToken).ConfigureAwait(false);
                        }

                        if (ordered.Count < clampedLimit)
                        {
                            await FillTierAsync(
                                connection,
                                LexiconScope.Global.Key,
                                normalized,
                                clampedLimit,
                                seenIds,
                                shadowedNames,
                                ordered,
                                cancellationToken).ConfigureAwait(false);
                        }

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
                    }, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lexicon match failed.");

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon search failed.");
        }
    }

    public async Task<Result<LexiconEntryDto?>> GetByNameAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
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

                    return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
                    {
                        // The Campaign tier answers first and the global one only when it has not, which is
                        // the same shadowing MatchEntitiesAsync applies. A lookup that resolved differently
                        // from a match would let inspection describe an entity no turn would ever see.
                        return await ReadActiveByNormalizedAsync(connection, normalized, scope.Key, cancellationToken)
                                .ConfigureAwait(false)
                            ?? (scope.IsGlobal
                                ? null
                                : await ReadActiveByNormalizedAsync(
                                    connection,
                                    normalized,
                                    LexiconScope.Global.Key,
                                    cancellationToken).ConfigureAwait(false));
                    }, cancellationToken).ConfigureAwait(false);
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

    public async Task<Result<LexiconEntryDto?>> GetByNameInScopeAsync(
        string name,
        LexiconScope scope,
        CancellationToken cancellationToken = default)
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

                    return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection,
                        () => ReadActiveByNormalizedAsync(connection, normalized, scope.Key, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            return Result<LexiconEntryDto?>.Success(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lexicon scoped get failed for entity {Name}.", trimmedName);

            return new Error(ErrorCodes.Lexicon.SearchFailed, "Lexicon lookup failed.");
        }
    }

    private static string NormalizeName(string value) =>
        value.Trim().ToUpperInvariant();

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

    /// <summary>
    /// Runs one tier's whole exact-then-FTS pass and appends what it admits, minus anything a
    /// higher-precedence tier already answered.
    /// </summary>
    /// <remarks>
    /// <paramref name="shadowedNames"/> is the shadowing record, and it collects a tier's hits by the
    /// entity's own normalized name rather than by the term that found it: an FTS hit may be reached by
    /// a word that appears in its facts, and shadowing has to be about which entity answered for a name.
    /// </remarks>
    private async Task FillTierAsync(
        DbConnection connection,
        string scopeKey,
        List<string> normalized,
        int limit,
        HashSet<Guid> seenIds,
        HashSet<string> shadowedNames,
        List<LexiconEntryDto> ordered,
        CancellationToken cancellationToken)
    {
        int remaining = limit - ordered.Count;

        if (remaining <= 0)
        {
            return;
        }

        List<string> unshadowed = normalized.Where(name => !shadowedNames.Contains(name)).ToList();

        if (unshadowed.Count == 0)
        {
            return;
        }

        List<LexiconEntryDto> exactHits = new(remaining);

        HashSet<string> exactNames = new(StringComparer.Ordinal);

        await FillExactMatchesAsync(connection, scopeKey, unshadowed, remaining, exactHits, seenIds, exactNames, cancellationToken).ConfigureAwait(false);

        List<LexiconEntryDto> ftsHits = new(Math.Max(0, remaining - exactHits.Count));

        if (exactHits.Count < remaining)
        {
            List<string> unresolved = unshadowed.Where(n => !exactNames.Contains(n)).ToList();

            if (unresolved.Count > 0)
            {
                await FillFtsMatchesAsync(connection, scopeKey, unresolved, remaining - exactHits.Count, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (LexiconEntryDto entry in exactHits.Concat(ftsHits))
        {
            if (ordered.Count >= limit)
            {
                break;
            }

            _ = shadowedNames.Add(NormalizeName(entry.Name));

            ordered.Add(entry);
        }
    }

    private async Task FillExactMatchesAsync(
        DbConnection connection,
        string scopeKey,
        List<string> normalized,
        int limit,
        List<LexiconEntryDto> exactHits,
        HashSet<Guid> seenIds,
        HashSet<string> exactNames,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new();
        _ = sql.Append("SELECT ")
            .Append(EntryColumnsFor(curation))
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

        _ = sql.Append(") AND ScopeCampaignId = @scopeKey")
            .Append(curation ? " AND RetiredAtUtc IS NULL" : "")
            .Append(" ORDER BY UpdatedAt DESC LIMIT @limit");

        AddParameter(cmd, "@scopeKey", scopeKey);

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
        string scopeKey,
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
            await FillFtsMatchesViaMatchAsync(connection, scopeKey, matchQuery, remaining, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Lexicon FTS MATCH failed; falling back to LIKE search.");

            await FillFtsMatchesViaLikeAsync(connection, scopeKey, unresolved, remaining, seenIds, ftsHits, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task FillFtsMatchesViaMatchAsync(
        DbConnection connection,
        string scopeKey,
        string matchQuery,
        int remaining,
        HashSet<Guid> seenIds,
        List<LexiconEntryDto> ftsHits,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        // bm25() weight arguments map positionally to the FTS5 columns (Name, Type, FactsText):
        // 3.0 for Name, 2.0 for Type, 1.0 for FactsText. FTS5 bm25 returns more-negative values for
        // better matches, so sort ASC. No Lucene caret boosting inside MATCH — SQLite FTS5 does not
        // support it; mathematical boosting lives only in bm25().
        cmd.CommandText =
            $"""
            SELECT e.Id, e.Name, e.Type, e.FactsJson, e.UpdatedAt, e.ScopeCampaignId,
                   {(curation ? "e.RetiredAtUtc" : "NULL")} AS RetiredAtUtc,
                   {(curation ? "e.PinnedAtUtc" : "NULL")} AS PinnedAtUtc,
                   {(curation ? "e.CurationGeneration" : "1")} AS CurationGeneration
            FROM lexicon_fts
            INNER JOIN lexicon_entries e ON e.rowid = lexicon_fts.rowid
            WHERE lexicon_fts MATCH @query AND e.ScopeCampaignId = @scopeKey {(curation ? "AND e.RetiredAtUtc IS NULL" : "")}
            ORDER BY bm25(lexicon_fts, 3.0, 2.0, 1.0) ASC
            LIMIT @limit
            """;

        AddParameter(cmd, "@scopeKey", scopeKey);

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
        string scopeKey,
        List<string> unresolved,
        int remaining,
        HashSet<Guid> seenIds,
        List<LexiconEntryDto> ftsHits,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new();
        _ = sql.Append("SELECT ")
            .Append(EntryColumnsFor(curation))
            .Append(" FROM lexicon_entries WHERE ScopeCampaignId = @scopeKey")
            .Append(curation ? " AND RetiredAtUtc IS NULL" : "")
            .Append(" AND (");

        AddParameter(cmd, "@scopeKey", scopeKey);

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

        _ = sql.Append(") ORDER BY UpdatedAt DESC LIMIT @limit");

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

    private async Task<LexiconEntryDto?> ReadActiveByNormalizedAsync(
        DbConnection connection,
        string normalized,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        return await ReadNamedEntryAsync(connection, normalized, scopeKey,
            $"SELECT {EntryColumnsFor(curation)} FROM lexicon_entries WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey {(curation ? "AND RetiredAtUtc IS NULL" : "")} LIMIT 1",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> ReadAllLifecycleIdentityForDeletionAsync(
        DbConnection connection,
        string normalized,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Id FROM lexicon_entries WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey LIMIT 1";

        AddParameter(command, "@normalized", normalized);

        AddParameter(command, "@scopeKey", scopeKey);

        object? identity = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return identity is string value ? Guid.Parse(value) : null;
    }

    /// <summary>
    /// The content-free facts an agent's delete is decided on, for the exact-scope row including a
    /// retired one.
    /// </summary>
    /// <remarks>
    /// Below Core version 11 nothing can be retired or pinned, so the legacy projection reads both as
    /// absent.
    /// </remarks>
    private static async Task<LexiconAgentDeletionTarget?> ReadAgentDeletionTargetAsync(
        DbConnection connection,
        string normalized,
        string scopeKey,
        bool curation,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {(curation ? "Id, RetiredAtUtc, PinnedAtUtc" : "Id, NULL AS RetiredAtUtc, NULL AS PinnedAtUtc")} FROM lexicon_entries WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey LIMIT 1";

        AddParameter(command, "@normalized", normalized);

        AddParameter(command, "@scopeKey", scopeKey);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new LexiconAgentDeletionTarget(
            Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture), !reader.IsDBNull(1), !reader.IsDBNull(2));
    }

    private async Task<LexiconEntryDto?> ReadByNormalizedAsync(
        DbConnection connection,
        string normalized,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        return await ReadNamedEntryAsync(connection, normalized, scopeKey,
            $"SELECT {EntryColumnsFor(curation)} FROM lexicon_entries WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey LIMIT 1",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LexiconEntryDto?> ReadNamedEntryAsync(
        DbConnection connection,
        string normalized,
        string scopeKey,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = sql;

        AddParameter(cmd, "@normalized", normalized);

        AddParameter(cmd, "@scopeKey", scopeKey);

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
        // The caller supplies the complete resulting fact set: a scribe union or an operator
        // replacement. Only exact unchanged canonical facts retain their attachment evidence.
        // Hash membership keeps both paths linear inside BEGIN IMMEDIATE.
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
    /// Provenance for every entry in one read. <see cref="ListInspectionAsync"/> returns the entire
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
                AnnalsStore.ReadCode(reader, 5, 1, int.MaxValue),
                reader.GetString(6),
                UtcInstantText.Parse(reader.GetString(7)),
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

        AddParameter(command, "@attachmentId", provenance.AttachmentId.ToString().ToUpperInvariant());

        AddParameter(command, "@logicalKey", provenance.LogicalKey);

        AddParameter(command, "@version", provenance.Version);

        AddParameter(command, "@contentHash", provenance.ContentHash);

        AddParameter(
            command,
            "@materializedAt",
            UtcInstantText.Format(provenance.MaterializedAt));

        AddParameter(command, "@sourceType", provenance.SourceType);
    }

    private static string HashFact(string fact) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fact))).ToLowerInvariant();

    private static async Task InsertAsync(
        DbConnection connection,
        Guid id,
        string name,
        string normalized,
        string scopeKey,
        string type,
        string factsJson,
        string factsText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO lexicon_entries (
                Id, Name, NameNormalized, ScopeCampaignId, Type, FactsJson, FactsText, UpdatedAt)
            VALUES (@id, @name, @normalized, @scopeKey, @type, @factsJson, @factsText, @updatedAt)
            """;

        AddParameter(cmd, "@scopeKey", scopeKey);

        AddParameter(cmd, "@id", id.ToString("N"));
        AddParameter(cmd, "@name", name);
        AddParameter(cmd, "@normalized", normalized);
        AddParameter(cmd, "@type", type);
        AddParameter(cmd, "@factsJson", factsJson);
        AddParameter(cmd, "@factsText", factsText);
        AddParameter(cmd, "@updatedAt", UtcInstantText.Format(updatedAt));

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateAsync(
        DbConnection connection,
        Guid id,
        string name,
        string normalized,
        string scopeKey,
        string type,
        string factsJson,
        string factsText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        bool curation = await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            $"""
            UPDATE lexicon_entries
            SET Name = @name,
                NameNormalized = @normalized,
                ScopeCampaignId = @scopeKey,
                Type = @type,
                FactsJson = @factsJson,
                FactsText = @factsText,
                UpdatedAt = @updatedAt{(curation ? ", CurationGeneration = CurationGeneration + 1" : "")}
            WHERE Id = @id
            """;

        AddParameter(cmd, "@scopeKey", scopeKey);

        AddParameter(cmd, "@id", id.ToString("N"));
        AddParameter(cmd, "@name", name);
        AddParameter(cmd, "@normalized", normalized);
        AddParameter(cmd, "@type", type);
        AddParameter(cmd, "@factsJson", factsJson);
        AddParameter(cmd, "@factsText", factsText);
        AddParameter(cmd, "@updatedAt", UtcInstantText.Format(updatedAt));

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LexiconEntryDto ReadEntry(DbDataReader reader)
    {
        Guid id = Guid.Parse(reader.GetString(0));

        string name = reader.GetString(1);

        string type = reader.GetString(2);

        string factsJson = reader.GetString(3);

        DateTimeOffset updatedAt = UtcInstantText.Parse(reader.GetString(4));

        string scopeKey = reader.GetString(5);

        DateTimeOffset? retiredAt = reader.IsDBNull(6) ? null : UtcInstantText.Parse(reader.GetString(6));

        return new LexiconEntryDto(
            id,
            name,
            type,
            DeserializeFacts(factsJson),
            updatedAt,
            FactProvenance: null,
            ScopeCampaignId: scopeKey.Length == 0 ? null : Guid.Parse(scopeKey),
            RetiredAtUtc: retiredAt,
            PinnedAtUtc: reader.IsDBNull(7) ? null : UtcInstantText.Parse(reader.GetString(7)),
            CurationGeneration: ReadPositiveInteger(reader, 8),
            Eligibility: retiredAt is null ? LexiconRetrievalEligibility.Eligible : LexiconRetrievalEligibility.Retired);
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
        catch (Exception ex)
        {
            // Best-effort rollback, and deliberately for every failure kind. A failed BEGIN or an
            // already-closed connection leaves nothing to release, and SqliteException,
            // InvalidOperationException and ObjectDisposedException are the shapes that realistically
            // carries — but narrowing the catch to those three lets anything else escape, and both
            // callers invoke this from inside `catch { ...; throw; }`. An exception raised in a catch
            // block discards the one being handled, so a surprising rollback failure would replace the
            // real error the operator needs; worse, the outer handler filters on
            // `ex is not OperationCanceledException`, so it would also turn an aborted request into a
            // generic Lexicon write failure. The Warning is the diagnostic, and the original exception
            // keeps the right of way.
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
