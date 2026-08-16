using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What one downstream consumer of possibly-tainted content is required to do about it.
/// </summary>
/// <remarks>
/// Four policies rather than a boolean, because "handles taint" is not one behaviour. A summarizer
/// propagates the label onto its output; a plaintext export refuses the input outright; a metrics
/// sink may see that something was tainted but never what; a transcript route may return the content
/// but only under a proven-current lease. Collapsing these would make the safest of them the only
/// one anybody implemented, or the loosest of them the default (§10.12).
/// </remarks>
public enum DerivedOutputPolicy : byte
{

    /// <summary>Writes its output and that output's label in the same transaction, or writes neither.</summary>
    PropagateLabel = 1,

    /// <summary>Refuses tainted input rather than emitting a redacted or partial form of it.</summary>
    RefuseTainted = 2,

    /// <summary>Receives that content was tainted and never any part of the content itself.</summary>
    ContentFreeMetadataOnly = 3,

    /// <summary>Returns protected content only while a clean Covenant read lease is held.</summary>
    ProtectedReadRequired = 4,

}

/// <summary>One declared producer or consumer of Covenant-derived output.</summary>
/// <remarks>
/// <paramref name="OwningTypeName"/> is the full name of the type that implements the policy. The
/// architecture suite resolves it, so an entry for a type that was renamed or deleted fails rather
/// than sitting in the inventory describing a guarantee nothing provides any more.
/// </remarks>
public sealed record CovenantDerivedOutputConsumer(
    string Name,
    string OwningTypeName,
    SensitiveArtifactKind? ArtifactKind,
    DerivedOutputPolicy Policy,
    string Rationale);

/// <summary>
/// The closed inventory of everything that produces or consumes Covenant-derived output.
/// </summary>
/// <remarks>
/// Closed, and enforced in both directions by the architecture suite: every declared entry must name
/// a type that exists, and every type implementing a labelled-write or protected-read contract must
/// be declared here. That bidirectional check is the whole mechanism. A new sink added without a
/// declared policy is the failure this inventory exists to make loud, because the alternative —
/// noticing later that a new export path never learned about taint — is not something a test can do
/// after the fact (§10.12).
///
/// <para>Entries whose owning type is not yet composed in the running host are still declared. The
/// inventory describes the contract each sink is held to, and an undeclared sink is the problem
/// whether or not it is wired up yet.</para>
/// </remarks>
public static class CovenantDerivedOutputInventory
{

    private static readonly ImmutableArray<CovenantDerivedOutputConsumer> Declared =
    [
        new(
            "assistant-entry-finalization",
            "RetroDownfall.Arcanum.Infrastructure.Repositories.GrimoireRepository",
            SensitiveArtifactKind.AssistantEntry,
            DerivedOutputPolicy.PropagateLabel,
            "The response and its label commit in the one finalization transaction, so a tainted "
            + "answer can never reach storage without the evidence that it is tainted."),

        new(
            "session-summary-replacement",
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.SessionDerivedArtifactStore",
            SensitiveArtifactKind.Summary,
            DerivedOutputPolicy.PropagateLabel,
            "A summary of tainted history is tainted whether or not it quotes a fragment, and the "
            + "immutable artifact gives its label a revision that cannot change underneath it."),

        new(
            "session-title-replacement",
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.SessionDerivedArtifactStore",
            SensitiveArtifactKind.SessionTitle,
            DerivedOutputPolicy.PropagateLabel,
            "A model-generated title propagates taint; a clean operator title is the only "
            + "replacement that removes the prior label, and only with the bytes it described."),

        new(
            "artifact-sensitivity-ledger",
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.ArtifactSensitivityLedger",
            null,
            DerivedOutputPolicy.PropagateLabel,
            "The single writer every other producer routes through, so a label is never written by "
            + "one path and skipped by another."),

        new(
            "protected-assistant-read",
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.ProtectedAssistantArtifactReader",
            SensitiveArtifactKind.AssistantEntry,
            DerivedOutputPolicy.ProtectedReadRequired,
            "Artifact and label are read in one snapshot under a lease that is revalidated before "
            + "the value is handed back, so a reset cannot be beaten by a fast query."),

        new(
            "covenant-protected-log-scope",
            "RetroDownfall.Arcanum.Core.Logging.CovenantProtectedLogScope",
            null,
            DerivedOutputPolicy.ContentFreeMetadataOnly,
            "Logs, metrics, traces, and progress carry the decision and never the content behind "
            + "it, because they travel to sinks that have no authority to hold protected text."),

        new(
            "host-process-tools-transition",
            "RetroDownfall.Arcanum.Infrastructure.Security.HostProcessToolsTransitionService",
            null,
            DerivedOutputPolicy.RefuseTainted,
            "The escape hatch may not be enabled while any Covenant canonical row or protected "
            + "artifact remains, so the transition inventories both and refuses rather than erasing."),
    ];

    /// <summary>Every declared producer and consumer, in declaration order.</summary>
    public static IReadOnlyList<CovenantDerivedOutputConsumer> Consumers => Declared;

    /// <summary>Whether a sink with this name has a declared policy.</summary>
    public static bool IsDeclared(string name) =>
        Declared.Any(consumer => string.Equals(consumer.Name, name, StringComparison.Ordinal));

    /// <summary>Every declaration naming this owning type.</summary>
    public static IReadOnlyList<CovenantDerivedOutputConsumer> ForOwningType(string owningTypeName) =>
        [.. Declared.Where(consumer =>
            string.Equals(consumer.OwningTypeName, owningTypeName, StringComparison.Ordinal))];

}
