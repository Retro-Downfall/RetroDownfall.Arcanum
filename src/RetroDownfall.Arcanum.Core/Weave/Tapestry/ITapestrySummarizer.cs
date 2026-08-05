using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>One cluster's summarization request. The child texts are untrusted DATA, never instructions.</summary>
public sealed record TapestrySummaryRequest(
    TapestryScopeKind ScopeKind,
    string ScopeLabel,
    int Layer,
    IReadOnlyList<string> ChildTexts);

/// <summary>
/// The Tapestry's abstractive summary step, behind an interface so the weaving pipeline can be tested
/// without a live model and so provider-context admission stays in one place.
///
/// <para>Deterministic clustering does <b>not</b> make model prose reproducible. Callers must reuse a
/// prior summary only by exact membership/recipe/model/input identity — never by assuming the same
/// cluster yields the same words.</para>
/// </summary>
public interface ITapestrySummarizer
{

    /// <summary>
    /// The model that will actually be asked, after the
    /// <c>Tapestry:SummaryModel</c> → <c>FastModel</c> → <c>DefaultModel</c> fallback chain. Part of a
    /// summary node's reuse identity; <c>null</c> when no model can be resolved, in which case no tree
    /// above the leaf layer can be woven.
    /// </summary>
    string? ResolveSummaryModel();

    /// <summary>
    /// Whether one summary request over <paramref name="childTexts"/> fits the selected model's real
    /// context estimate with output and reasoning tokens reserved. Callers repartition an oversized
    /// cluster — they never truncate the source text to make it fit.
    /// </summary>
    bool FitsOneRequest(TapestrySummaryRequest request);

    /// <summary>
    /// Produces one abstractive summary. Never throws for expected failure modes — a failed
    /// <see cref="Result{T}"/> leaves the prior complete generation active.
    /// </summary>
    Task<Result<string>> SummarizeAsync(
        TapestrySummaryRequest request,
        CancellationToken cancellationToken);

}
