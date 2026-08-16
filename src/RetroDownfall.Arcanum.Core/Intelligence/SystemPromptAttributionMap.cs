using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// One rendered system prompt and the ordered, nonoverlapping typed partition of it.
/// </summary>
/// <remarks>
/// The map is the single source of truth for "which source produced this stretch of prompt".
/// Everything downstream — token attribution, the provider-call envelope, context inspection — asks
/// this type instead of re-reading the Markdown, because a heading lookalike inside untrusted
/// content, a fence info string such as <c>```text</c>, or a fence edge case would otherwise be
/// able to move tokens from an agent-authored source to an operator-authored one (§10.13).
/// </remarks>
public sealed class SystemPromptAttributionMap
{

    /// <summary>The map of an empty prompt: no spans and nothing to attribute.</summary>
    public static SystemPromptAttributionMap Empty { get; } = new(string.Empty, []);

    public SystemPromptAttributionMap(
        string prompt,
        ImmutableArray<SystemPromptAttributionSpan> spans)
    {

        ArgumentNullException.ThrowIfNull(prompt);

        if (spans.IsDefault)
        {
            throw new ArgumentException("The attribution partition must be initialized.", nameof(spans));
        }

        int offset = 0;

        foreach (SystemPromptAttributionSpan span in spans)
        {

            if (span.Utf16Start != offset || span.Utf16Length <= 0)
            {
                throw new ArgumentException(
                    "Attribution spans must be ordered, nonempty, and contiguous from zero.",
                    nameof(spans));
            }

            offset = span.Utf16End;

        }

        if (offset != prompt.Length)
        {
            throw new ArgumentException(
                "Attribution spans must partition the whole rendered prompt.",
                nameof(spans));
        }

        Prompt = prompt;

        Spans = spans;

    }

    public string Prompt { get; }

    public ImmutableArray<SystemPromptAttributionSpan> Spans { get; }

    public bool HasCovenantContent =>
        Spans.Any(static span => IsCovenant(span.Attribution));

    /// <summary>
    /// The category owning <paramref name="utf16Index"/>, or
    /// <see cref="CovenantPromptAttribution.SpecialOrUncovered"/> when nothing covers it.
    /// </summary>
    /// <remarks>
    /// An out-of-range offset is never attributed to the nearest neighbour. A tokenizer special
    /// token has no position in the rendered text, and silently charging it to whichever source
    /// happens to sit beside it would make one source's count depend on another source's presence.
    /// </remarks>
    public CovenantPromptAttribution Classify(int utf16Index)
    {

        if (utf16Index < 0 || utf16Index >= Prompt.Length)
        {
            return CovenantPromptAttribution.SpecialOrUncovered;
        }

        int low = 0;

        int high = Spans.Length - 1;

        while (low <= high)
        {

            int middle = low + ((high - low) / 2);

            SystemPromptAttributionSpan span = Spans[middle];

            if (span.Covers(utf16Index))
            {
                return span.Attribution;
            }

            if (utf16Index < span.Utf16Start)
            {
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }

        }

        return CovenantPromptAttribution.SpecialOrUncovered;

    }

    /// <summary>Concatenates every span carrying <paramref name="attribution"/>, in wire order.</summary>
    public string Concatenate(CovenantPromptAttribution attribution)
    {

        StringBuilder builder = new();

        foreach (SystemPromptAttributionSpan span in Spans)
        {

            if (span.Attribution == attribution)
            {
                _ = builder.Append(Prompt.AsSpan(span.Utf16Start, span.Utf16Length));
            }

        }

        return builder.ToString();

    }

    /// <summary>
    /// The prompt with every Covenant span removed, in wire order.
    /// </summary>
    /// <remarks>
    /// Covenant sections are always whole lines, so the remainder keeps the exact line structure —
    /// and therefore the exact per-source accounting — a pre-Covenant prompt would have had.
    /// </remarks>
    public string ExcludingCovenant()
    {

        if (!HasCovenantContent)
        {
            return Prompt;
        }

        StringBuilder builder = new(Prompt.Length);

        foreach (SystemPromptAttributionSpan span in Spans)
        {

            if (!IsCovenant(span.Attribution))
            {
                _ = builder.Append(Prompt.AsSpan(span.Utf16Start, span.Utf16Length));
            }

        }

        return builder.ToString();

    }

    public static bool IsCovenant(CovenantPromptAttribution attribution) =>
        attribution is CovenantPromptAttribution.CovenantConfirmed
            or CovenantPromptAttribution.CovenantProposed;

}
