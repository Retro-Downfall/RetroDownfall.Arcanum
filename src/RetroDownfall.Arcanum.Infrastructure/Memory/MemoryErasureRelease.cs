using System.Data;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The operator's explicit release of erasure fingerprints, for every store (spec §5.5).
/// </summary>
/// <remarks>
/// <para><b>What it deletes.</b> The fingerprint the supplied scope and identity compute to, and nothing
/// else. Saga content is tried exactly and trimmed, because content read from a file usually carries a
/// trailing newline the stored memory did not; any other difference, Unicode normalization included, is
/// a different identity. A Lexicon name is trimmed and case-folded the way the scribe chokepoint reads it.
/// A Covenant key must already be well formed, which makes it the normalized key.</para>
///
/// <para><b>The key.</b> A store with no fingerprints answers <see cref="MemoryErasureReleaseOutcome.NotFingerprinted"/>
/// without touching the credential store, so a release never creates the key and never needs it. Otherwise
/// the key is re-probed, as every operator call re-probes it, before the transaction opens: a key that is
/// not present refuses the release, and fingerprints the present key cannot match are lost evidence, not
/// absent evidence, so they refuse it too. Nothing is ever deleted that the key in hand did not
/// compute.</para>
///
/// <para>Release takes no mutation id: deleting a row that is already gone answers
/// <see cref="MemoryErasureReleaseOutcome.NotFingerprinted"/>, so a retry is harmless. It keeps every
/// receipt. Its one log line carries the store and a count, never the identity.</para>
/// </remarks>
internal sealed class MemoryErasureRelease(
    ArcanumDbContext db,
    IMemoryErasureKeyProvider keys,
    ILogger<MemoryErasureRelease> logger) : IMemoryErasureRelease
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Error InvalidSagaBody = new(
        ErrorCodes.Validation.InvalidBody,
        "A Saga release names a recognized scopeKind, a campaignId exactly when the scope is Campaign, and nonempty content.");

    private static readonly Error KeyLost = new(
        ErrorCodes.MemoryErasure.KeyLost,
        "Erasure fingerprints exist for this store that the erasure key cannot verify, so nothing was released. Run 'arcanum memory erasure status'.");

    private static readonly Error KeyUnavailable = new(
        ErrorCodes.MemoryErasure.KeyUnavailable,
        "The erasure key could not be read, so nothing was released.");

    private static readonly Error Unavailable = new(
        ErrorCodes.MemoryErasure.Unavailable,
        "Selective erasure is unavailable until the Grimoire reaches its current schema.");

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseSagaAsync(
        SagaErasureReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool paired = request.ScopeKind is SagaMemoryScopeKind.Campaign
            ? request.CampaignId is { } campaign && campaign != Guid.Empty
            : request.CampaignId is null;

        if (!Enum.IsDefined(request.ScopeKind)
            || !paired
            || string.IsNullOrEmpty(request.Content)
            || !IsStrictUtf8(request.Content))
        {
            return Task.FromResult(Result<MemoryErasureReleaseResultDto>.Failure(InvalidSagaBody));
        }

        List<MemoryErasureIdentity> candidates =
        [
            MemoryErasureIdentity.ForSaga(request.ScopeKind, request.CampaignId, request.Content),
        ];

        string trimmed = request.Content.Trim();

        if (trimmed.Length > 0 && !string.Equals(trimmed, request.Content, StringComparison.Ordinal))
        {
            candidates.Add(MemoryErasureIdentity.ForSaga(request.ScopeKind, request.CampaignId, trimmed));
        }

        return ReleaseCoreAsync(MemoryReviewStore.Saga, candidates, cancellationToken);
    }

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseLexiconAsync(
        LexiconErasureReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Scope is null)
        {
            return Task.FromResult(Result<MemoryErasureReleaseResultDto>.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                "A Lexicon release names a scope and a name.")));
        }

        Result scope = request.Scope.Validate();

        if (scope.IsFailure)
        {
            return Task.FromResult(Result<MemoryErasureReleaseResultDto>.Failure(scope.Error));
        }

        if (string.IsNullOrWhiteSpace(request.Name) || !IsStrictUtf8(request.Name))
        {
            return Task.FromResult(Result<MemoryErasureReleaseResultDto>.Failure(new Error(
                ErrorCodes.Lexicon.InvalidName,
                "A Lexicon release names a nonblank entry name.")));
        }

        return ReleaseCoreAsync(
            MemoryReviewStore.Lexicon,
            [MemoryErasureIdentity.ForLexicon(request.Scope.CampaignId, request.Name)],
            cancellationToken);
    }

    public Task<Result<MemoryErasureReleaseResultDto>> ReleaseCovenantAsync(
        CovenantErasureReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return Task.FromResult(Result<MemoryErasureReleaseResultDto>.Failure(validated.Error));
        }

        return ReleaseCoreAsync(
            MemoryReviewStore.Covenant,
            [MemoryErasureIdentity.ForCovenant(request.Scope, request.CampaignId, request.Key)],
            cancellationToken);
    }

    /// <summary>Deletes the candidates' fingerprints, or says why it would not.</summary>
    private async Task<Result<MemoryErasureReleaseResultDto>> ReleaseCoreAsync(
        MemoryReviewStore store,
        IReadOnlyList<MemoryErasureIdentity> candidates,
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return Unavailable;
        }

        if (!await MemoryErasureEvidence.AnyAsync(connection, null, store, cancellationToken).ConfigureAwait(false))
        {
            return Released(store, MemoryErasureReleaseOutcome.NotFingerprinted, 0);
        }

        // The operator re-probe, outside every transaction: a key that is not present refuses here, and
        // the credential store is never read once the write lock below is held.
        MemoryErasureKeyOpenResult opened = keys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        if (opened.State is not MemoryErasureKeyState.Present)
        {
            opened.Key?.Dispose();

            return MemoryErasureGuard.RefusalFor(opened.State).Code == ErrorCodes.MemoryErasure.KeyLost
                ? KeyLost
                : KeyUnavailable;
        }

        using MemoryErasureKey key = opened.Key
            ?? throw new InvalidOperationException("A present erasure key was opened without its material.");

        byte[] keyId = key.KeyId.ToArray();

        // Null when nothing matched and the store holds rows another key recorded.
        int? released = await SqliteBusyRetry.ExecuteAsync<int?>(
            async () =>
            {
                // A fresh BEGIN IMMEDIATE per attempt, so a busy retry never reuses a rolled-back one.
                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                int deleted = await MemoryErasureFingerprintRelease
                    .DeleteCandidatesAsync(connection, transaction, key, candidates, cancellationToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return deleted;
                }

                bool foreign = await MemoryErasureEvidence
                    .AnyForeignAsync(connection, transaction, store, keyId, cancellationToken)
                    .ConfigureAwait(false);

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return foreign ? null : 0;
            },
            cancellationToken).ConfigureAwait(false);

        return released switch
        {
            null => KeyLost,
            0 => Released(store, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
            { } count => Released(store, MemoryErasureReleaseOutcome.Released, count),
        };
    }

    private Result<MemoryErasureReleaseResultDto> Released(MemoryReviewStore store, MemoryErasureReleaseOutcome outcome, int count)
    {
        logger.LogInformation("Erasure release for {Store} removed {Count} fingerprints.", store, count);

        return new MemoryErasureReleaseResultDto(store, outcome, count);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Whether the text encodes as strict UTF-8, which every fingerprint preimage requires: an unpaired
    /// surrogate names no identity, so it is a malformed request rather than a failure.
    /// </summary>
    private static bool IsStrictUtf8(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);

            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
