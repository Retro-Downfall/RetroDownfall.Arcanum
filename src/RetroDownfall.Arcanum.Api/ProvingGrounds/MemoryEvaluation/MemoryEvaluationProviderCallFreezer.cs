using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Closed two-message SDK mapping and its exact immutable disclosure envelope.</summary>
public static class MemoryEvaluationProviderCallFreezer
{
    public static ChatOptions MapOptions(MemoryEvaluationOptions requested, string model)
    {
        ChatOptions options = new()
        {
            ModelId = model,
            MaxOutputTokens = requested.MaxOutputTokens,
            Temperature = (float?)requested.Temperature,
            TopP = (float?)requested.TopP,
            Seed = requested.Seed,
            Tools = [],
            ToolMode = ChatToolMode.None,
        };

        ReasoningChatOptionsAdapter.Apply(options,
            requested.ReasoningEffort is { } effort ? new(Effort: effort) : null, ReasoningWireDialect.Standard);

        return options;
    }

    public static ProviderCallEnvelope Freeze(MemoryEvaluationExecutionPlan plan, MemoryEvaluationArm arm,
        ModelCallContext context, ChatOptions mapped, IReadOnlyList<ChatMessage> messages)
    {
        string system = messages[0].Text;

        string user = messages[1].Text;

        bool derived = arm == MemoryEvaluationArm.Treatment && plan.Sources.Coordinates.Any(source => source.Sensitivity == ContentSensitivity.CovenantDerived);

        GenerationProvenance provenance = derived ? plan.Sources.Provenance! : GenerationProvenance.CreateExact([]);

        ContentSensitivity level = derived ? ContentSensitivity.CovenantDerived : ContentSensitivity.None;

        ProviderCallSensitivity sensitivity = new(level, provenance, CovenantDigests.Sensitivity(provenance.ToDigestInput(level)));

        FrozenProviderOptions options = FrozenProviderOptions.Create(new(
            (ulong?)mapped.MaxOutputTokens, mapped.Temperature, mapped.TopP, null, null, mapped.Seed, null, [],
            ProviderToolChoice.None, null, CovenantTriStateBoolean.Absent, ProviderResponseFormat.Text,
            null, null, null, CovenantTriStateBoolean.Absent,
            plan.Request.Request.Options.ReasoningEffort is { } effort ? (CovenantReasoningEffort)((int)effort + 1) : null,
            null, null, CovenantReasoningWireDialect.Standard, []));

        UTF8Encoding utf8 = new(false, true);

        byte[] systemBytes = utf8.GetBytes(system);

        List<ProviderPromptSpan> spans = [];

        if (arm == MemoryEvaluationArm.Treatment)
        {
            foreach (var occurrence in plan.Prompts.Occurrences.Where(item => item.Placement == MemoryEvaluationPromptPart.System).OrderBy(item => item.StartUtf8))
            {
                var source = plan.Sources.Coordinates[occurrence.SourceIndex];

                CovenantPromptAttribution attribution = source.Store == MemoryEvaluationSource.Covenant
                    ? source.Lane == CovenantLane.Confirmed ? CovenantPromptAttribution.CovenantConfirmed : CovenantPromptAttribution.CovenantProposed
                    : CovenantPromptAttribution.DataBody;

                // Occurrence offsets are UTF-8 bytes. The envelope's system attribution is UTF-16.
                uint start = (uint)utf8.GetCharCount(systemBytes.AsSpan(0, occurrence.StartUtf8));

                ReadOnlySpan<byte> segment = systemBytes.AsSpan(occurrence.StartUtf8, occurrence.LengthUtf8);

                spans.Add(new(attribution, start, (uint)utf8.GetCharCount(segment), new(SHA256.HashData(segment))));
            }
        }

        return new(context.Provider.Name, context.Model, CovenantProviderDispatchMode.Buffered,
            context.PrecomputedBreakdown!.Profile.ProfileId, (ulong)context.Provider.ContextWindowLimit, 0,
            sensitivity, options, systemBytes, spans, new(false, []),
            [new(CovenantProviderRole.System, null, null, [ProviderContentPartEnvelope.Text(system)]),
             new(CovenantProviderRole.User, null, null, [ProviderContentPartEnvelope.Text(user)])], [], null);
    }

    internal static List<ChatMessage> Messages(MemoryEvaluationExecutionPlan plan, MemoryEvaluationArm arm) =>
        [new(ChatRole.System, arm == MemoryEvaluationArm.Control ? plan.Prompts.ControlSystemPrompt : plan.Prompts.TreatmentSystemPrompt),
         new(ChatRole.User, arm == MemoryEvaluationArm.Control ? plan.Prompts.ControlUserPrompt : plan.Prompts.TreatmentUserPrompt)];
}
