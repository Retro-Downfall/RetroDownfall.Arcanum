using System.Text;

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
}

public enum PromptSegmentStability
{
    Stable = 0,
    Volatile = 1,
}

public sealed record PromptSegment(
    PromptSegmentKind Kind,
    PromptSegmentStability Stability,
    string Text,
    bool CacheBoundaryEligible = false);

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
}
