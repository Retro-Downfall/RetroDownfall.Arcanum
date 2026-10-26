using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>Operator inspection and exact-target curation, separate from eligible model retrieval.</summary>
public interface ILexiconCurationService
{
    Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowExactAsync(
        LexiconCurationScope scope, string name, ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowEffectiveAsync(
        LexiconCurationScope requestedScope, string name, ICovenantSnapshotReadLease? installationReadLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> ListInspectionAsync(
        ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default);

    Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> SearchInspectionAsync(
        string? query, int? limit, ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default);

    /// <summary>Read-only search page; implementations must retain their inspection authority checks.</summary>
    async Task<Result<LexiconInspectionResult<LexiconSearchRow[]>>> SearchInspectionPageAsync(
        string? query, int limit, LexiconSearchPosition? after, ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default)
    {
        if (after is not null) throw new NotSupportedException("Lexicon search continuation is unavailable.");

        Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>> result =
            await SearchInspectionAsync(query, limit, readLease, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? result.Error : new LexiconInspectionResult<LexiconSearchRow[]>(
            [.. result.Value.Value.Select(entry => new LexiconSearchRow(entry,
                new(entry.Name, entry.ScopeCampaignId?.ToString("D") ?? "", entry.Id.ToString("N"))))],
            result.Value.ContainsProtectedContent);
    }

    Task<Result<LexiconInspectionResult<LexiconInspectionCounts>>> CountInspectionAsync(
        ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> CorrectAsync(
        LexiconCurationTarget target, LexiconReplacementContent replacement, CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default) => UnavailableMutation();

    Task<Result<LexiconCurationResult>> RetireAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default) => UnavailableMutation();

    Task<Result<LexiconCurationResult>> ReinstateAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default) => UnavailableMutation();

    Task<Result<LexiconCurationResult>> PinAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default) => UnavailableMutation();

    Task<Result<LexiconCurationResult>> UnpinAsync(
        LexiconCurationTarget target, CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default) => UnavailableMutation();

    private static Task<Result<LexiconCurationResult>> UnavailableMutation() =>
        Task.FromResult(Result<LexiconCurationResult>.Failure(
            new Error(ErrorCodes.Lexicon.WriteFailed, "Lexicon curation mutation is unavailable.")));
}

/// <summary>Verified retained and retrieval-eligible counts; no canonical content is retained.</summary>
public sealed record LexiconInspectionCounts(int Retained, int Eligible);

/// <summary>Store-selected order metadata; client continuation uses only identity/scope and resolves the name under inspection authority.</summary>
public sealed record LexiconSearchPosition(string Name, string ScopeKey, string Id);

public sealed record LexiconSearchRow(LexiconEntryDto Entry, LexiconSearchPosition Position);
