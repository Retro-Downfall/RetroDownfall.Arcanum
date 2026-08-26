namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// Read access to the Annals.
/// </summary>
/// <remarks>
/// Deliberately read-only. Every write goes through the store that owns the subject row, inside that
/// store's own transaction, so a claim can never commit without the memory it describes and no
/// caller can append a claim for a row it did not write.
/// </remarks>
public interface IAnnalsStore
{

    /// <summary>The claim for one durable row, or <see langword="null"/> when it has none.</summary>
    /// <remarks>
    /// A row with no claim is a first-class state, not an error: it is what a memory written while the
    /// Annals was disabled looks like, and what every row looks like before the upgrade sweep drains.
    /// </remarks>
    Task<AnnalClaimHead?> GetClaimAsync(
        AnnalSubjectStore subjectStore,
        string subjectId,
        CancellationToken cancellationToken);

    /// <summary>Every version of one claim, oldest revision first.</summary>
    Task<IReadOnlyList<AnnalClaimVersion>> GetVersionsAsync(
        string claimId,
        CancellationToken cancellationToken);

    /// <summary>One version's dependency edges, in ordinal order.</summary>
    Task<IReadOnlyList<AnnalDependencyEdge>> GetDependenciesAsync(
        string versionId,
        CancellationToken cancellationToken);

}
