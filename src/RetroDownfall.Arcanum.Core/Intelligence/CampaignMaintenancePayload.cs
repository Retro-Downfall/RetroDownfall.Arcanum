using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>The complete, immutable two-message request for Campaign summary maintenance.</summary>
public sealed class CampaignMaintenancePayload
{
    public const string SchemaJson = """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""";

    public const string SchemaName = "campaign_summary";

    public const string SchemaDescription = "A bounded continuity summary.";

    public const int MaxOutputTokens = 2048;

    public static ImmutableArray<byte> SchemaUtf8 { get; } = [.. Encoding.UTF8.GetBytes(SchemaJson)];

    public CampaignMaintenancePayload(string systemPrompt, string userPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);

        UTF8Encoding strictUtf8 = new(false, true);

        _ = strictUtf8.GetByteCount(systemPrompt);
        _ = strictUtf8.GetByteCount(userPrompt);

        SystemPrompt = systemPrompt;
        UserPrompt = userPrompt;
    }

    public string SystemPrompt { get; }

    public string UserPrompt { get; }
}

/// <summary>Response text projection; provider message and content collections are not exposed.</summary>
public sealed record CampaignMaintenanceCallResult(
    string ModelCallId,
    string Text,
    UsageDetails? Usage,
    string? FinishReason,
    ContextTokenBreakdown ContextBreakdown);
