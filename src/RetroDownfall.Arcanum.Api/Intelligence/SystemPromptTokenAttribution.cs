using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Per-category token counts over one rendered system prompt.
/// </summary>
/// <remarks>
/// Produced by a single <c>EncodeToTokens</c> pass whenever the tokenizer reports usable UTF-16
/// offsets, so the category counts sum exactly to the locally tokenized system total. Summing
/// independently tokenized sections instead would not: a byte-pair token that straddles a section
/// boundary is counted once by the whole-string pass and twice by the per-section one, and the
/// difference is exactly the number an operator would use to argue that a section costs more than
/// it does (§10.13).
/// </remarks>
public sealed class SystemPromptTokenAttribution
{

    private const int CategoryCount = 10;

    private readonly int[] _counts;

    private SystemPromptTokenAttribution(int[] counts, int totalTokens, bool offsetExact)
    {

        _counts = counts;

        TotalTokens = totalTokens;

        OffsetExact = offsetExact;

    }

    public static SystemPromptTokenAttribution Empty { get; } =
        new(new int[CategoryCount], 0, true);

    /// <summary>The locally tokenized total for the whole rendered prompt.</summary>
    public int TotalTokens { get; }

    /// <summary>
    /// Whether the counts came from one offset-bearing pass rather than from per-span tokenization.
    /// </summary>
    /// <remarks>
    /// A profile whose tokenizer normalizes the input, or reports no offsets, cannot support the
    /// exact partition. Reporting that honestly is the point: a per-span fallback is a good estimate
    /// and a bad thing to present as decomposed truth.
    /// </remarks>
    public bool OffsetExact { get; }

    public int this[CovenantPromptAttribution attribution] =>
        _counts[Index(attribution)];

    public int CovenantTokens =>
        this[CovenantPromptAttribution.CovenantConfirmed]
        + this[CovenantPromptAttribution.CovenantProposed];

    /// <summary>Attributes <paramref name="map"/> across its typed spans.</summary>
    public static SystemPromptTokenAttribution Compute(
        Tokenizer tokenizer,
        SystemPromptAttributionMap map)
    {

        ArgumentNullException.ThrowIfNull(tokenizer);

        ArgumentNullException.ThrowIfNull(map);

        if (map.Prompt.Length == 0)
        {
            return Empty;
        }

        IReadOnlyList<EncodedToken> tokens;

        string? normalized;

        try
        {
            tokens = tokenizer.EncodeToTokens(
                map.Prompt,
                out normalized,
                considerPreTokenization: true,
                considerNormalization: false);
        }
        catch (NotSupportedException)
        {
            return PerSpan(tokenizer, map);
        }

        if (normalized is not null && !string.Equals(normalized, map.Prompt, StringComparison.Ordinal))
        {
            return PerSpan(tokenizer, map);
        }

        int[] counts = new int[CategoryCount];

        int total = 0;

        foreach (EncodedToken token in tokens)
        {

            total++;

            (int start, int length) = token.Offset.GetOffsetAndLength(map.Prompt.Length);

            counts[Index(length <= 0
                ? CovenantPromptAttribution.SpecialOrUncovered
                : map.Classify(start))]++;

        }

        return new SystemPromptTokenAttribution(counts, total, offsetExact: true);

    }

    /// <summary>
    /// The fallback for a tokenizer that cannot report stable offsets: count each span alone.
    /// </summary>
    private static SystemPromptTokenAttribution PerSpan(
        Tokenizer tokenizer,
        SystemPromptAttributionMap map)
    {

        int[] counts = new int[CategoryCount];

        int total = 0;

        foreach (SystemPromptAttributionSpan span in map.Spans)
        {

            int count = tokenizer.CountTokens(
                map.Prompt.AsSpan(span.Utf16Start, span.Utf16Length));

            counts[Index(span.Attribution)] += count;

            total += count;

        }

        return new SystemPromptTokenAttribution(counts, total, offsetExact: false);

    }

    private static int Index(CovenantPromptAttribution attribution) =>
        attribution switch
        {
            CovenantPromptAttribution.DataHeader => 0,
            CovenantPromptAttribution.CovenantProposed => 1,
            CovenantPromptAttribution.DataBody => 2,
            CovenantPromptAttribution.WorkspaceContext => 3,
            CovenantPromptAttribution.CovenantConfirmed => 4,
            CovenantPromptAttribution.ContextBody => 5,
            CovenantPromptAttribution.SpecialOrUncovered => 6,
            CovenantPromptAttribution.Preamble => 7,
            CovenantPromptAttribution.Instructions => 8,
            _ => 9,
        };

}
