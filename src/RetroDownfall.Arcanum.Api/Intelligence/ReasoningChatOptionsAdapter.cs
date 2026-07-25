using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Maps normalized reasoning controls to provider request options. Output remains an Arcanum-local
/// exposure preference and is placed only on the typed Microsoft.Extensions.AI surface as a
/// best-effort hint; this adapter never invents a provider-specific <c>reasoning_output</c> field.
/// Explicitly configured numeric dialects are applied through the concrete provider representation.
/// </summary>
internal static class ReasoningChatOptionsAdapter
{
    internal static void Apply(
        ChatOptions options,
        ReasoningRequestOptions? reasoning,
        ReasoningWireDialect wireDialect)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reasoning is null)
        {
            return;
        }

        if (reasoning.Effort is not null || reasoning.Output is not null)
        {
            options.Reasoning = new ReasoningOptions
            {
                Effort = MapEffort(reasoning.Effort),
                Output = MapOutput(reasoning.Output),
            };
        }

        bool useConcreteMinimal = reasoning.Effort == ReasoningEffortLevel.Minimal;

        if (!useConcreteMinimal && reasoning.BudgetTokens is null)
        {
            return;
        }

        if (reasoning.BudgetTokens is not null
            && wireDialect == ReasoningWireDialect.Standard)
        {
            throw new InvalidOperationException(
                "A numeric reasoning budget requires an explicitly configured nonstandard wire dialect.");
        }

        options.RawRepresentationFactory = _ =>
            CreateRawOptions(useConcreteMinimal, reasoning.BudgetTokens, wireDialect);
    }

#pragma warning disable OPENAI001 // Chat reasoning effort is experimental in OpenAI 2.12.
#pragma warning disable SCME0001 // JsonPatch is the supported OpenAI 2.12 additional-property API.
    private static ChatCompletionOptions CreateRawOptions(
        bool useConcreteMinimal,
        int? budgetTokens,
        ReasoningWireDialect wireDialect)
    {
        ChatCompletionOptions rawOptions = new();

        if (useConcreteMinimal)
        {
            rawOptions.ReasoningEffortLevel = ChatReasoningEffortLevel.Minimal;
        }

        if (budgetTokens is not { } budget)
        {
            return rawOptions;
        }

        switch (wireDialect)
        {
            case ReasoningWireDialect.OpenRouter:
                rawOptions.Patch.Set("$.reasoning.max_tokens"u8, budget);
                break;

            case ReasoningWireDialect.TopLevelReasoningBudget:
                rawOptions.Patch.Set("$.reasoning_budget"u8, budget);
                break;

            case ReasoningWireDialect.AnthropicThinking:
                rawOptions.Patch.Set("$.thinking.type"u8, "enabled");
                rawOptions.Patch.Set("$.thinking.budget_tokens"u8, budget);
                break;

            case ReasoningWireDialect.Standard:
                throw new InvalidOperationException(
                    "A numeric reasoning budget cannot use the standard wire dialect.");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(wireDialect),
                    wireDialect,
                    "Unknown reasoning wire dialect.");
        }

        return rawOptions;
    }
#pragma warning restore SCME0001
#pragma warning restore OPENAI001

    private static ReasoningEffort? MapEffort(ReasoningEffortLevel? effort) =>
        effort switch
        {
            null => null,
            ReasoningEffortLevel.None => ReasoningEffort.None,
            // MEAI 10.8.1 has no Minimal value. The concrete OpenAI option is applied separately.
            ReasoningEffortLevel.Minimal => null,
            ReasoningEffortLevel.Low => ReasoningEffort.Low,
            ReasoningEffortLevel.Medium => ReasoningEffort.Medium,
            ReasoningEffortLevel.High => ReasoningEffort.High,
            ReasoningEffortLevel.ExtraHigh => ReasoningEffort.ExtraHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort."),
        };

    private static ReasoningOutput? MapOutput(ReasoningOutputMode? output) =>
        output switch
        {
            null => null,
            ReasoningOutputMode.None => ReasoningOutput.None,
            ReasoningOutputMode.Summary => ReasoningOutput.Summary,
            ReasoningOutputMode.Full => ReasoningOutput.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(output), output, "Unknown reasoning output mode."),
        };
}
