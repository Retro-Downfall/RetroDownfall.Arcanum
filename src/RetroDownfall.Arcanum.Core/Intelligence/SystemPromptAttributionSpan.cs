using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// One typed, half-open UTF-16 range of the single rendered system-prompt string.
/// </summary>
/// <remarks>
/// Spans are an ordered, nonoverlapping partition of the whole prompt, so category token counts sum
/// exactly to the locally tokenized system total. They exist so that admission, tokenization, and
/// the provider envelope can all say which source a byte came from without reparsing headings or
/// Markdown fences: a user-authored heading lookalike, an info string such as <c>```text</c>, or a
/// fence edge case would otherwise be able to move tokens between sources (§10.13).
///
/// <para>The span references the one rendered string by offset and never duplicates its text.</para>
/// </remarks>
public readonly record struct SystemPromptAttributionSpan
{

    public SystemPromptAttributionSpan(
        CovenantPromptAttribution attribution,
        int utf16Start,
        int utf16Length)
    {

        ArgumentOutOfRangeException.ThrowIfNegative(utf16Start);

        ArgumentOutOfRangeException.ThrowIfNegative(utf16Length);

        if (!Enum.IsDefined(attribution))
        {
            throw new ArgumentOutOfRangeException(nameof(attribution));
        }

        Attribution = attribution;

        Utf16Start = utf16Start;

        Utf16Length = utf16Length;

    }

    public CovenantPromptAttribution Attribution { get; }

    public int Utf16Start { get; }

    public int Utf16Length { get; }

    public int Utf16End => Utf16Start + Utf16Length;

    /// <summary>Whether <paramref name="utf16Index"/> falls inside this span.</summary>
    public bool Covers(int utf16Index) =>
        utf16Index >= Utf16Start && utf16Index < Utf16End;

}
