using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Annals;

/// <summary>
/// One table an Annals erasure clears, and the rows of it that erasure owns.
/// </summary>
internal sealed record AnnalsErasureStep(string Table, string Predicate);

/// <summary>
/// The ordered statements every Annals erasure runs, stated once.
/// </summary>
/// <remarks>
/// Both the claim writer and the memory-reset executor read this. Two lists would be two ideas of which
/// rows an erasure owns, and the one that under-reached would leave a record pointing at content the
/// operator asked to remove — a data-deletion hole that a green test over the other list could not see.
///
/// <para>The order is load-bearing. SQLite enforces an immediate foreign key as each row is deleted
/// rather than at the end of the statement, so a head must release its version before the version may
/// go, and a version must go before the claim it belongs to. Edges lead because they name versions from
/// both ends.</para>
///
/// <para>Edges would cascade with their versions, and are deleted explicitly anyway.
/// <c>SagaMemoryStore</c> already deletes <c>saga_memory_attachment_provenance</c> explicitly although
/// that table declares <c>ON DELETE CASCADE</c>, and an erasure the operator asked for is the wrong
/// place to depend on a pragma being what it is expected to be.</para>
/// </remarks>
internal static class AnnalsErasurePlan
{

    /// <summary>Every claim belonging to one store, leaving the other store's untouched.</summary>
    internal static IReadOnlyList<AnnalsErasureStep> ForStore(AnnalSubjectStore subjectStore) =>
        Build($"SubjectStoreCode = {(int)subjectStore}", (int)subjectStore);

    /// <summary>
    /// Every claim over the subject rows one query selects, which is how a Campaign-targeted reset
    /// reaches exactly the memories that Campaign owns.
    /// </summary>
    /// <param name="subjectIdQuery">
    /// A <c>SELECT</c> of subject ids. It is interpolated into the predicate rather than parameterized,
    /// so it must be a code-owned literal and never anything a caller supplied; the parameters it
    /// references are bound by the executor that runs the resulting statement.
    /// </param>
    internal static IReadOnlyList<AnnalsErasureStep> ForSubjectQuery(
        AnnalSubjectStore subjectStore,
        string subjectIdQuery) =>
        Build(
            $"SubjectStoreCode = {(int)subjectStore} AND SubjectId IN ({subjectIdQuery})",
            (int)subjectStore,
            headPredicate: $"ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = {(int)subjectStore} AND SubjectId IN ({subjectIdQuery}))");

    private static IReadOnlyList<AnnalsErasureStep> Build(
        string claimPredicate,
        int subjectStoreCode,
        string? headPredicate = null)
    {

        string claimScope = $"SELECT ClaimId FROM annal_claims WHERE {claimPredicate}";

        string versionScope = $"SELECT VersionId FROM annal_versions WHERE ClaimId IN ({claimScope})";

        return
        [
            // Both endpoint columns, because an edge dies when either end does: a claim being erased may
            // be the target of an edge asserted by a version that survives, and leaving that edge would
            // leave a dependency pointing at nothing.
            new(
                "annal_dependencies",
                $"DependentVersionId IN ({versionScope}) OR DependencyVersionId IN ({versionScope})"),

            // The head carries its own store column, so a store-wide erasure needs no join to find it.
            new("annal_heads", headPredicate ?? $"SubjectStoreCode = {subjectStoreCode}"),

            new("annal_versions", $"ClaimId IN ({claimScope})"),

            new("annal_claims", claimPredicate),
        ];

    }

}
