using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

public enum PromptSegmentKind
{
    Preamble = 0,
    Data = 1,
    ContextHeader = 2,
    WorkspaceContext = 3,
    Codex = 4,
    CampaignSummary = 5,
    ContextPlaceholder = 6,
    InstructionsHeader = 7,
    PrimarySpell = 8,
    ResonantSpells = 9,
    TerminalFormatting = 10,
    AdditionalInstructions = 11,
    InstructionsPlaceholder = 12,

    /// <summary>The <c>## DATA</c> header, split out only when Proposed content follows it.</summary>
    DataHeader = 13,

    /// <summary>The fenced, hardened agent-authored Covenant block inside DATA (§10.13).</summary>
    CovenantProposed = 14,

    /// <summary>Operator-authored Global Covenant content inside CONTEXT (§10.13).</summary>
    CovenantGlobalConfirmed = 15,

    /// <summary>Operator-authored Campaign Covenant content inside CONTEXT (§10.13).</summary>
    CovenantCampaignConfirmed = 16,
}

public enum PromptSegmentStability
{
    Stable = 0,
    Volatile = 1,
}

/// <summary>
/// One typed subrange of a segment's own text, used when a segment carries more than one
/// attribution category.
/// </summary>
public sealed record PromptSegmentPart(CovenantPromptAttribution Attribution, int Length);

public sealed record PromptSegment(
    PromptSegmentKind Kind,
    PromptSegmentStability Stability,
    string Text,
    bool CacheBoundaryEligible = false,
    bool Sensitive = false,
    ImmutableArray<PromptSegmentPart> Parts = default)
{

    /// <summary>
    /// The segment's attribution parts, defaulting to one part covering the whole segment.
    /// </summary>
    public ImmutableArray<PromptSegmentPart> ResolvedParts =>
        Parts.IsDefaultOrEmpty
            ? [new PromptSegmentPart(DefaultAttribution(Kind), Text.Length)]
            : Parts;

    private static CovenantPromptAttribution DefaultAttribution(PromptSegmentKind kind) =>
        kind switch
        {
            PromptSegmentKind.Preamble => CovenantPromptAttribution.Preamble,
            PromptSegmentKind.DataHeader => CovenantPromptAttribution.DataHeader,
            PromptSegmentKind.CovenantProposed => CovenantPromptAttribution.CovenantProposed,
            PromptSegmentKind.Data => CovenantPromptAttribution.DataBody,
            PromptSegmentKind.WorkspaceContext => CovenantPromptAttribution.WorkspaceContext,
            PromptSegmentKind.CovenantGlobalConfirmed
                or PromptSegmentKind.CovenantCampaignConfirmed =>
                CovenantPromptAttribution.CovenantConfirmed,
            PromptSegmentKind.ContextHeader
                or PromptSegmentKind.Codex
                or PromptSegmentKind.CampaignSummary
                or PromptSegmentKind.ContextPlaceholder =>
                CovenantPromptAttribution.ContextBody,
            _ => CovenantPromptAttribution.Instructions,
        };

}

/// <summary>One cache-planning view of a segment, projected onto the single rendered string.</summary>
/// <remarks>
/// The descriptor carries offsets rather than a copy of the segment text, so
/// <c>PromptCachePlanner</c> can hash exactly the bytes that were sent without retaining a second
/// copy of sensitive Covenant content in managed memory.
/// </remarks>
public sealed record PromptCacheSegmentDescriptor(
    PromptSegmentKind Kind,
    PromptSegmentStability Stability,
    bool CacheBoundaryEligible,
    bool Sensitive,
    int Utf16Start,
    int Utf16Length);

/// <summary>
/// One rendered system prompt plus the two coordinate systems that describe it.
/// </summary>
/// <remarks>
/// Rendering happens exactly once. Every later consumer — token attribution, the prompt cache
/// planner, the provider-call envelope, and context inspection — reads spans over that same string.
/// A second render would be a second set of bytes, and the whole point of the frozen envelope is
/// that the bytes that were hashed are the bytes that were sent (§10.13).
/// </remarks>
public sealed class SystemPromptBuildResult
{

    internal SystemPromptBuildResult(
        SystemPromptAttributionMap attribution,
        ImmutableArray<PromptCacheSegmentDescriptor> cacheSegments)
    {

        Attribution = attribution;

        CacheSegments = cacheSegments;

    }

    /// <summary>The Core-owned typed partition of the rendered prompt.</summary>
    public SystemPromptAttributionMap Attribution { get; }

    public string Prompt => Attribution.Prompt;

    public ImmutableArray<PromptCacheSegmentDescriptor> CacheSegments { get; }

    public ImmutableArray<SystemPromptAttributionSpan> AttributionSpans => Attribution.Spans;

    /// <summary>Whether any span carries operator-authored or agent-authored Covenant content.</summary>
    public bool HasCovenantContent => Attribution.HasCovenantContent;

}

/// <summary>
/// Immutable ordered representation of the model-visible system prompt.
/// </summary>
public sealed class SystemPromptDocument
{
    private readonly PromptSegment[] _segments;

    public SystemPromptDocument(IEnumerable<PromptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        _segments = [.. segments];
    }

    public IReadOnlyList<PromptSegment> OrderedSegments => _segments;

    public string Render()
    {
        int capacity = 0;

        foreach (PromptSegment segment in _segments)
        {
            capacity = int.CreateSaturating((long)capacity + segment.Text.Length);
        }

        StringBuilder builder = new(capacity);

        foreach (PromptSegment segment in _segments)
        {
            _ = builder.Append(segment.Text);
        }

        return builder.ToString();
    }

    /// <summary>Renders once and projects both coordinate systems onto the produced string.</summary>
    public SystemPromptBuildResult BuildResult()
    {

        string prompt = Render();

        ImmutableArray<PromptCacheSegmentDescriptor>.Builder descriptors =
            ImmutableArray.CreateBuilder<PromptCacheSegmentDescriptor>(_segments.Length);

        List<SystemPromptAttributionSpan> spans = [];

        int offset = 0;

        foreach (PromptSegment segment in _segments)
        {

            descriptors.Add(new PromptCacheSegmentDescriptor(
                segment.Kind,
                segment.Stability,
                segment.CacheBoundaryEligible,
                segment.Sensitive,
                offset,
                segment.Text.Length));

            int partOffset = offset;

            foreach (PromptSegmentPart part in segment.ResolvedParts)
            {

                if (part.Length <= 0)
                {
                    continue;
                }

                if (spans.Count > 0
                    && spans[^1].Attribution == part.Attribution
                    && spans[^1].Utf16End == partOffset)
                {
                    spans[^1] = new SystemPromptAttributionSpan(
                        part.Attribution,
                        spans[^1].Utf16Start,
                        spans[^1].Utf16Length + part.Length);
                }
                else
                {
                    spans.Add(new SystemPromptAttributionSpan(
                        part.Attribution,
                        partOffset,
                        part.Length));
                }

                partOffset += part.Length;

            }

            if (partOffset != offset + segment.Text.Length)
            {
                throw new InvalidOperationException(
                    "A prompt segment's attribution parts did not cover its rendered text exactly.");
            }

            offset += segment.Text.Length;

        }

        return new SystemPromptBuildResult(
            new SystemPromptAttributionMap(prompt, [.. spans]),
            descriptors.ToImmutable());

    }
}
