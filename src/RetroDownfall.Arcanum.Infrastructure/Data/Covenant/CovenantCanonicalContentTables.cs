namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The canonical family's content tables: every canonical table except the state singleton.
/// </summary>
/// <remarks>
/// One list, read by everything that empties, proves empty, or counts the canonical family: the family
/// erasure transaction, its storage-health proof, the restore inspector and purger, and the reset
/// inventory. A second copy could only ever differ in the case that matters: a table one reader
/// stopped naming would be a table the erasure left behind, or one its proof never counted.
///
/// <para><c>covenant_state</c> is deliberately absent. The schema installer seeds it on every
/// Covenant-enabled installation and an erasure restamps it rather than deleting it, so counting it
/// would make an installation that merely has the tier installed read as carrying protected state.
/// </para>
///
/// <para>The order is a delete-safe topological order, because every deleter walks it front to back
/// with foreign keys enforced. Decision receipts reference review events, and review events and
/// provenance reference versions; <c>covenant_curation_heads</c> references
/// <c>covenant_curation_versions</c>; <c>covenant_heads</c> references both <c>covenant_versions</c>
/// and <c>covenant_entries</c>; and versions reference entries. Every child therefore precedes its
/// parent.</para>
///
/// <para><c>covenant_key_epochs</c> comes last for two further reasons. Deleting a head fires
/// <c>covenant_heads_key_epoch_delete</c>, which writes an epoch row, so clearing epochs any earlier
/// would leave the rows that trigger then created. And a pin or a mask is recorded against a key's
/// epoch row: every key row created from canonical version 6 on carries binding epoch 0, so curation
/// that outlived the row would bind the next key to take that name. The three curation tables are on
/// this list and ahead of the epochs, so a deleter that walks it removes a key's curation in the same
/// transaction as the key's epoch row.</para>
/// </remarks>
internal static class CovenantCanonicalContentTables
{
    internal static IReadOnlyList<string> InDeletionOrder { get; } = Array.AsReadOnly<string>(
    [
        "covenant_review_decision_receipts",
        "covenant_review_markers",
        "covenant_review_events",
        "covenant_search_outbox",
        "covenant_curation_receipts",
        "covenant_curation_heads",
        "covenant_curation_versions",
        "covenant_heads",
        "covenant_version_attachment_provenance",
        "covenant_versions",
        "covenant_entries",
        "covenant_mutation_receipts",
        "covenant_turn_receipts",
        "covenant_turn_receipt_aggregate",
        "covenant_key_epochs",
    ]);
}
