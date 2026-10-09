using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignMaintenanceProviderCallFreezerTests
{
    [Fact]
    public void Closed_maintenance_payload_preserves_the_existing_v1_wire_digest()
    {
        CampaignMaintenancePayload payload = new("Maintain continuity.", "User: a decision\nAssistant: completed.");

        using JsonDocument schema = JsonDocument.Parse(CampaignMaintenancePayload.SchemaJson);

        ChatOptions options = new()
        {
            MaxOutputTokens = CampaignRollupLimits.OutputTokens,
            Tools = [],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement,
                "campaign_summary", "A bounded continuity summary."),
        };

        List<ChatMessage> messages =
        [
            new(ChatRole.System, payload.SystemPrompt),
            new(ChatRole.User, payload.UserPrompt),
        ];

        ContextTokenBreakdown breakdown = Breakdown(messages, options);

        ProviderCallSensitivity sensitivity = CleanSensitivity();

        Result<ProviderCallEnvelope> ordinary = CovenantProviderCallFreezer.TryFreeze(new(
            "provider|https://provider.test/v1", "model", CovenantProviderDispatchMode.Buffered,
            breakdown.Profile.ProfileId, 32768, 0, payload.SystemPrompt, null, messages, options),
            sensitivity, new(false, []));

        Result<ProviderCallEnvelope> closed = CampaignMaintenanceProviderCallFreezer.TryFreeze(payload,
            "provider|https://provider.test/v1", "model", breakdown, 32768, sensitivity);

        Assert.True(ordinary.IsSuccess);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : string.Empty);

        Assert.Equal(ordinary.Value.Digest, closed.Value.Digest);

        Assert.Empty(closed.Value.ToolDefinitions);

        Assert.Equal(ProviderToolChoice.None, closed.Value.Options.ToolChoice);

        Assert.Equal(ProviderResponseFormat.JsonSchema, closed.Value.Options.ResponseFormat);

        Assert.Equal((ulong)CampaignRollupLimits.OutputTokens, closed.Value.Options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Either_message_change_changes_the_closed_maintenance_digest(bool changeSystem)
    {
        CampaignMaintenancePayload original = new("Maintain continuity.", "Completed one decision.");

        CampaignMaintenancePayload changed = changeSystem
            ? new("Maintain changed continuity.", original.UserPrompt)
            : new(original.SystemPrompt, "Completed a different decision.");

        ContextTokenBreakdown breakdown = Breakdown([new(ChatRole.User, "measurement")], new());

        Result<ProviderCallEnvelope> first = CampaignMaintenanceProviderCallFreezer.TryFreeze(original,
            "provider|https://provider.test/v1", "model", breakdown, 32768, CleanSensitivity());

        Result<ProviderCallEnvelope> second = CampaignMaintenanceProviderCallFreezer.TryFreeze(changed,
            "provider|https://provider.test/v1", "model", breakdown, 32768, CleanSensitivity());

        Assert.True(first.IsSuccess);

        Assert.True(second.IsSuccess);

        Assert.NotEqual(first.Value.Digest, second.Value.Digest);
    }

    private static ContextTokenBreakdown Breakdown(List<ChatMessage> messages, ChatOptions options)
    {
        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        return estimator.EstimateContext(new(
            new ProviderSettings { Name = "provider", Endpoint = "https://provider.test/v1", ContextWindowLimit = 32768 },
            "model", messages, options, CampaignRollupLimits.OutputTokens, 0));
    }

    private static ProviderCallSensitivity CleanSensitivity()
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([]);

        return new(ContentSensitivity.None, provenance,
            CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.None)));
    }
}
