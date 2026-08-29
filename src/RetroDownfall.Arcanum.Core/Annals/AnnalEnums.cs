namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// Which durable store holds the row a claim is about.
/// </summary>
/// <remarks>
/// Every code is written literally because it is persisted on <c>annal_claims</c> and
/// <c>annal_heads</c>. Renumbering a member would repoint an existing claim at another store, and a
/// store-scoped erasure would then miss rows it promised to remove.
/// </remarks>
public enum AnnalSubjectStore
{

    /// <summary>A <c>saga_memories</c> row.</summary>
    Saga = 1,

    /// <summary>A <c>lexicon_entries</c> row.</summary>
    Lexicon = 2,

}

/// <summary>
/// What one version does to the claim it belongs to.
/// </summary>
/// <remarks>
/// <see cref="Retire"/> is what Saga's retirement appends. It was declared and constrained before any
/// surface produced one, so the surfaces that do inherit a shape they cannot contradict. A retirement is
/// a tombstone: it binds to no content, which the table enforces rather than trusting a writer to
/// remember.
/// </remarks>
public enum AnnalOperation
{

    /// <summary>Opens a claim at revision one.</summary>
    Assert = 1,

    /// <summary>Restates a claim whose content has changed.</summary>
    Correct = 2,

    /// <summary>Ends a claim. Binds to no content.</summary>
    Retire = 3,

}

/// <summary>
/// Who asserted a claim version.
/// </summary>
/// <remarks>
/// The distinction between <see cref="OperatorStated"/> and the two agent origins is the one curation
/// and trust need: "the operator said this" and "a model inferred this from a transcript" are not the
/// same warrant, and a surface that could not tell them apart could not ask the right question.
///
/// <para><see cref="SystemBackfilled"/> is separate from all three because a backfilled version is
/// evidence of an upgrade rather than of an assertion. Nobody attested it, so it names no Session.</para>
/// </remarks>
public enum AnnalOrigin
{

    /// <summary>The operator said it.</summary>
    OperatorStated = 1,

    /// <summary>A model wrote it through a tool call it chose to make.</summary>
    AgentAsserted = 2,

    /// <summary>Headless extraction inferred it from a finished transcript. No one chose to state it.</summary>
    AgentExtracted = 3,

    /// <summary>An upgrade classified a row written before the Annals existed.</summary>
    SystemBackfilled = 4,

}

/// <summary>
/// What one dependency edge asserts about the version it points at.
/// </summary>
public enum AnnalDependencyRelation
{

    /// <summary>The dependent version replaces the version it names.</summary>
    Supersedes = 1,

    /// <summary>The dependent version was derived from the version it names.</summary>
    DerivedFrom = 2,

    /// <summary>The dependent version independently agrees with the version it names.</summary>
    Corroborates = 3,

}
