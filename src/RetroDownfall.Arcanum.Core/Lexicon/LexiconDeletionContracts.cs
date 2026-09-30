namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>Who asked for a Lexicon entry to be deleted.</summary>
/// <remarks>
/// The operator may delete any entry. An agent may delete only an active, unpinned one: a retired or
/// pinned entry is curated state the operator owns, and the delete re-checks that inside its own
/// transaction rather than trusting any earlier read.
/// </remarks>
public enum LexiconDeletionOrigin
{
    Operator = 1,

    Agent = 2,
}

/// <summary>
/// The content-free facts an agent's delete is decided on: the exact-scope entry's identity and
/// whether it is retired or pinned.
/// </summary>
public sealed record LexiconAgentDeletionTarget(Guid EntryId, bool IsRetired, bool IsPinned);

/// <summary>The agent-facing refusals of Lexicon writes the operator manages.</summary>
/// <remarks>
/// <see cref="OperatorManaged"/> is shared by a scribe of an erased name and a delete of a pinned
/// entry, so the tool result never says which of the two withheld it.
/// </remarks>
public static class LexiconAgentRefusals
{
    public const string OperatorManaged = "This Lexicon entry is managed by the operator in this scope, so nothing was recorded.";

    public const string RetiredDeletion = "A retired Lexicon entry can only be removed by the operator; it was left unchanged.";
}
