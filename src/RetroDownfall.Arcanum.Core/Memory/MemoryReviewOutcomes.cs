namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// The closed vocabulary of per-item outcomes every memory-review store reports.
/// </summary>
/// <remarks>
/// One set for Saga, Lexicon, and Covenant, spelled in PascalCase, and enumerated in API §8.34. Before it
/// existed each store invented its own strings and Lexicon mixed casings (<c>corrected</c> beside
/// <c>AlreadyRetired</c>), so a client that switched on an outcome had to know which store answered. A
/// decision that finds its target already in the state it asked for reports <see cref="NoOp"/>: one
/// spelling per action, the same in every store.
///
/// <para>Receipts persist the spelling they were written with, and a replay must keep accepting what an
/// earlier build wrote. <see cref="FromPersisted"/> is that reader for the lowercase Lexicon spellings
/// older builds persisted, which map to their closed equivalents; Covenant's replay additionally reads the
/// <c>NoChange</c> word it persisted for every no-op, and reports <see cref="NoOp"/> for the action.
/// Nothing <em>writes</em> a spelling outside <see cref="All"/>.</para>
/// </remarks>
public static class MemoryReviewOutcomes
{
    /// <summary>A <c>Confirm</c> was recorded.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>A <c>Correct</c> wrote replacement content.</summary>
    public const string Corrected = "Corrected";

    /// <summary>A <c>Retire</c> retired the memory.</summary>
    public const string Retired = "Retired";

    /// <summary>A <c>Pin</c> pinned the memory.</summary>
    public const string Pinned = "Pinned";

    /// <summary>An <c>Unpin</c> unpinned the memory.</summary>
    public const string Unpinned = "Unpinned";

    /// <summary>A <c>Correct</c> carried the content the memory already holds, so it wrote nothing.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>A <c>Retire</c> found the memory already retired.</summary>
    public const string AlreadyRetired = "AlreadyRetired";

    /// <summary>A <c>Pin</c> found the memory already pinned.</summary>
    public const string AlreadyPinned = "AlreadyPinned";

    /// <summary>An <c>Unpin</c> found the memory not pinned.</summary>
    public const string NotPinned = "NotPinned";

    /// <summary>The replacement version a correction wrote was acknowledged in the same transaction.</summary>
    public const string AutoAcknowledged = "AutoAcknowledged";

    /// <summary>Every outcome a store may report.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Confirmed,
        Corrected,
        Retired,
        Pinned,
        Unpinned,
        Unchanged,
        AlreadyRetired,
        AlreadyPinned,
        NotPinned,
        AutoAcknowledged,
    ];

    /// <summary>The outcome of an action that did what it was asked to do.</summary>
    public static string Applied(MemoryReviewAction action) =>
        action switch
        {
            MemoryReviewAction.Confirm => Confirmed,
            MemoryReviewAction.Correct => Corrected,
            MemoryReviewAction.Retire => Retired,
            MemoryReviewAction.Pin => Pinned,
            MemoryReviewAction.Unpin => Unpinned,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    /// <summary>
    /// The outcome of an action that found its target already in the state it asked for, so it wrote nothing.
    /// </summary>
    /// <remarks>
    /// One spelling per action for every store, so a client that switches on an outcome never has to know
    /// which store answered. A <c>Confirm</c> is always recorded, so it has no such outcome.
    /// </remarks>
    public static string NoOp(MemoryReviewAction action) =>
        action switch
        {
            MemoryReviewAction.Correct => Unchanged,
            MemoryReviewAction.Retire => AlreadyRetired,
            MemoryReviewAction.Pin => AlreadyPinned,
            MemoryReviewAction.Unpin => NotPinned,
            MemoryReviewAction.Confirm => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "A Confirm is always recorded, so it has no no-op outcome."),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    /// <summary>Whether <paramref name="outcome"/> is one of the closed set, spelled exactly.</summary>
    public static bool IsKnown(string? outcome) =>
        outcome is not null && All.Contains(outcome, StringComparer.Ordinal);

    /// <summary>
    /// Reads an outcome a receipt persisted, accepting the lowercase Lexicon spellings earlier builds wrote.
    /// </summary>
    /// <returns>The closed spelling, or <see langword="null"/> when the text is not an outcome any build wrote.</returns>
    public static string? FromPersisted(string? persisted) =>
        persisted switch
        {
            "acknowledged" => Confirmed,
            "corrected" => Corrected,
            "retired" => Retired,
            "pinned" => Pinned,
            "unpinned" => Unpinned,
            "auto-acknowledged" => AutoAcknowledged,
            _ when IsKnown(persisted) => persisted,
            _ => null,
        };
}
