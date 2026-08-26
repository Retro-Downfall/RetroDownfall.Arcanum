namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// What the installer should do with a tier, before it opens a transaction.
/// </summary>
internal enum GrimoireSchemaEvolutionAction
{

    /// <summary>Nothing is installed. Build the head shape directly; no step ever runs.</summary>
    FreshInstall = 0,

    /// <summary>The tier is already at head. Re-run the idempotent head DDL and re-validate.</summary>
    Converge = 1,

    /// <summary>The tier is at a version this binary knows and can leave. Open a run.</summary>
    BeginRun = 2,

    /// <summary>A run is already in flight and can be finished.</summary>
    ResumeRun = 3,

    /// <summary>Nothing may be done. <see cref="GrimoireSchemaEvolutionDecision.Refusal"/> says why.</summary>
    Refuse = 4,

}

/// <summary>
/// The two fields of a tier's <c>grimoire_feature_schemas</c> row that decide what may happen next.
/// </summary>
internal sealed record GrimoireSchemaRecordedTier(int SchemaVersion, string SourceDefinitionFingerprint);

/// <summary>
/// One classification.
/// </summary>
/// <remarks>
/// <paramref name="ResumeFromVersion"/> is the version the next step leaves, meaningful for
/// <see cref="GrimoireSchemaEvolutionAction.BeginRun"/> and
/// <see cref="GrimoireSchemaEvolutionAction.ResumeRun"/> only.
/// <paramref name="PendingBackfillName"/> is non-null exactly when that step's DDL has already
/// committed and its sweep is still draining, which is the case the caller must not re-run DDL for.
/// </remarks>
internal sealed record GrimoireSchemaEvolutionDecision(
    GrimoireSchemaEvolutionAction Action,
    GrimoireSchemaTierHealth? Refusal,
    int ResumeFromVersion,
    string? PendingBackfillName)
{

    internal static GrimoireSchemaEvolutionDecision Refused(GrimoireSchemaTierHealth health) =>
        new(GrimoireSchemaEvolutionAction.Refuse, health, 0, null);

    internal static GrimoireSchemaEvolutionDecision Simple(GrimoireSchemaEvolutionAction action) =>
        new(action, null, 0, null);

}

/// <summary>
/// Decides what may be done to one tier, from its recorded metadata, its transition journal, and the
/// chain this binary declares.
/// </summary>
/// <remarks>
/// Pure and synchronous on purpose: every arm below is a fail-closed decision about an installation
/// this binary did not create, and a decision that could also read the database would be a decision
/// nobody could test exhaustively.
///
/// <para>One question is therefore <b>not</b> answered here — whether an installation recorded below
/// head already has head's objects, which is the <see cref="GrimoireSchemaTierHealth.MixedCatalogVersions"/>
/// case. Answering it needs a catalog read, so the caller performs that probe on the
/// <see cref="GrimoireSchemaEvolutionAction.BeginRun"/> path before opening a run. Splitting it this
/// way keeps one owner for the condition: this decides that evolution is what the metadata calls
/// for, and the caller checks that the catalog agrees.</para>
/// </remarks>
internal static class GrimoireSchemaEvolutionPlanner
{

    internal static GrimoireSchemaEvolutionDecision Decide(
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaRecordedTier? recorded,
        bool anyOwnedObjectPresent,
        GrimoireSchemaTransitionJournalRow? journal)
    {

        ArgumentNullException.ThrowIfNull(chain);

        // The journal arm runs first: a row describes a state the metadata row alone cannot express,
        // and reading the metadata version without it would classify a half-evolved tier as one that
        // simply needs evolving, and re-run DDL that has already committed.
        if (journal is not null)
        {

            return Resume(chain, recorded, journal);

        }

        if (recorded is null)
        {

            return anyOwnedObjectPresent
                ? GrimoireSchemaEvolutionDecision.Refused(GrimoireSchemaTierHealth.MetadataMissing)
                : GrimoireSchemaEvolutionDecision.Simple(GrimoireSchemaEvolutionAction.FreshInstall);

        }

        if (recorded.SchemaVersion > chain.HeadVersion)
        {

            return GrimoireSchemaEvolutionDecision.Refused(GrimoireSchemaTierHealth.IncompatibleNewerVersion);

        }

        // A version the chain does not cover cannot be recognized, and a recorded fingerprint that is
        // not the one pinned for that version means the installed version is not the version this
        // binary knows by that number. Both are the same refusal: the two disagree about what this
        // version means, so nothing may be run against it.
        string? expected = chain.SourceDefinitionFingerprintFor(recorded.SchemaVersion);

        if (expected is null
            || !string.Equals(expected, recorded.SourceDefinitionFingerprint, StringComparison.Ordinal))
        {

            return GrimoireSchemaEvolutionDecision.Refused(GrimoireSchemaTierHealth.SourceDefinitionMismatch);

        }

        return recorded.SchemaVersion == chain.HeadVersion
            ? GrimoireSchemaEvolutionDecision.Simple(GrimoireSchemaEvolutionAction.Converge)
            : new GrimoireSchemaEvolutionDecision(
                GrimoireSchemaEvolutionAction.BeginRun,
                null,
                recorded.SchemaVersion,
                null);

    }

    /// <summary>
    /// Every reason a journaled run cannot be finished by this binary. Any one of them is
    /// <see cref="GrimoireSchemaTierHealth.TransitionUnresumable"/>.
    /// </summary>
    private static GrimoireSchemaEvolutionDecision Resume(
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaRecordedTier? recorded,
        GrimoireSchemaTransitionJournalRow journal)
    {

        if (recorded is null
            || journal.Family != chain.Family
            || journal.TargetVersion != chain.HeadVersion
            || !string.Equals(
                journal.TargetSourceDefinitionFingerprint,
                chain.HeadManifest.SourceDefinitionFingerprint,
                StringComparison.Ordinal)
            || journal.FromVersion != recorded.SchemaVersion
            || !chain.TryGetStep(journal.CompletedThroughVersion, out GrimoireSchemaVersionStep step))
        {

            return GrimoireSchemaEvolutionDecision.Refused(GrimoireSchemaTierHealth.TransitionUnresumable);

        }

        if (journal.BackfillName is not null
            && !string.Equals(step.Backfill?.Name, journal.BackfillName, StringComparison.Ordinal))
        {

            return GrimoireSchemaEvolutionDecision.Refused(GrimoireSchemaTierHealth.TransitionUnresumable);

        }

        return new GrimoireSchemaEvolutionDecision(
            GrimoireSchemaEvolutionAction.ResumeRun,
            null,
            journal.CompletedThroughVersion,
            journal.BackfillName);

    }

}
