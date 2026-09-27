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
