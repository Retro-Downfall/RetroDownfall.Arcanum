using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider : IGuildLocalInferenceRuntime
{
    bool IGuildLocalInferenceRuntime.IsAvailable => budgetReservationService is BudgetReservationService
        && chatClientFactory is ChatClientFactory && ModelCallExecutor is RetroDownfall.Arcanum.Api.Intelligence.ModelCallExecutor;

    Task<Result<PromptTurnResult>> IGuildLocalInferenceRuntime.ExecuteAsync(GuildLocalInferenceExecution execution,
        GuildLocalExecutionInput input, CancellationToken cancellationToken) =>
        ExecuteGuildAsync(execution, input, null, cancellationToken);

    Task<Result<PromptTurnResult>> IGuildLocalInferenceRuntime.ExecuteTurnAsync(GuildLocalInferenceExecution execution,
        GuildLocalExecutionInput input, GuildLocalInferenceTurn turn, CancellationToken cancellationToken) =>
        ExecuteGuildAsync(execution, input, turn, cancellationToken);

    private async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalInferenceExecution execution,
        GuildLocalExecutionInput input, GuildLocalInferenceTurn? turn, CancellationToken ct)
    {
        if (execution is null || budgetReservationService is not BudgetReservationService daily || input is null
            || turn is null && input.Mode != GuildLocalExecutionMode.Sterile
            || turn is not null && input.Mode != GuildLocalExecutionMode.Apprentice)
            return GuildContextUnavailable;

        Result<GuildLocalInferenceMaterialization> materialized = await execution.Store.MaterializeProviderInputAsync(execution, input, ct).ConfigureAwait(false);

        if (materialized.IsFailure) return materialized.Error;

        string prompt;

        if (turn is not null)
        {
            prompt = turn.Prompt;
        }
        else
        {
            StringBuilder builder = new(GuildPromptBuilder.Build(execution.Driver.Execution.QuestId == execution.Driver.Execution.RootQuestId
                ? GuildAuthorityKind.Operator : GuildAuthorityKind.Parent, GuildEnvelopeKind.QuestIssued,
                execution.Dispatch.DispatchId, materialized.Value.Goal));

            foreach (GuildLocalInferenceFile file in materialized.Value.Files)
                SystemPromptBuilder.AppendUntrusted(builder, file.Name, file.Text);

            prompt = builder.ToString();
        }

        PingRequest request = new(prompt, input.Model, DisableMcpTools: true, UnattendedMode: true,
            SkipSpellRouting: true, MaxOutputTokens: input.MaxOutputTokens, ToolPolicy: ToolPolicy.NoTools,
            DisableAllTools: turn?.DisableAllTools ?? true);

        using GuildProviderCallBinding binding = new(execution, input, request, daily,
            settings.Value.ResolvePricing().ResolveForModel(input.Model) with { });

        using IDisposable accounting = TurnAccountingAmbient.Suspend();

        using IDisposable invocation = execution.Store.EnterProviderInvocation(execution);

        return await ExecutePromptAsync(request, ArcanumInvocationContext.ForGuild(binding), ct).ConfigureAwait(false);
    }

    private async Task<Result<PromptTurnResult>> ExecuteGuildPromptCoreAsync(GuildProviderCallBinding binding,
        PingRequest request, CancellationToken ct)
    {
        if (!binding.Matches(request)) return GuildContextUnavailable;

        Result preflight = PingRequestPreflightValidator.Validate(request, settings.Value);

        if (preflight.IsFailure) return preflight.Error;

        Result guardrails = await FilterGuardrailsInputAsync(request, ct).ConfigureAwait(false);

        if (guardrails.IsFailure) return guardrails.Error;

        ProviderSettings? provider = settings.Value.Providers?.SingleOrDefault(item => item.Name == binding.Input.Provider);

        if (provider is null || provider.Type != AiProviderKind.OpenAICompatible
            || !provider.Models.Any(item => item.Name == binding.Input.Model)) return GuildContextUnavailable;

        using ChatClientLease lease = await chatClientFactory.ResolveClientAsync(provider, binding.Input.Model, ct).ConfigureAwait(false);

        if (lease.Provider.Name != binding.Input.Provider || lease.ResolvedModel != binding.Input.Model) return GuildContextUnavailable;

        using GuildLocalToolRegistry registry = new(binding.Execution, binding.Input,
            serviceProvider?.GetService<IGuildLocalChildToolDispatcher>());

        bool Eligible(GuildToolGrant tool)
        {
            ArcanumSettings current = serviceProvider?.GetService<IOptionsMonitor<ArcanumSettings>>()?.CurrentValue ?? settings.Value;

            return !request.DisableAllTools && current.Features.Guild
                && (tool.Name == ArcanumBuiltInToolNames.DelegateTask || tool.Name == "cast_sending" && current.Features.Conclave);
        }

        GuildToolInvocationContext tools = binding.Execution.Store.CreateToolContext(binding.Execution, registry, Eligible);

        using IDisposable toolScope = GuildToolInvocationAmbient.Begin(tools);

        List<AIFunction> functions = [];

        foreach (GuildToolGrant tool in GuildLocalToolRegistry.SupportedTools)
        {
            GuildSchemaFunction template = tool.Name == ArcanumBuiltInToolNames.DelegateTask
                ? new(tool.Name, "Run a self-contained task in an isolated child with an explicit budget and return its result.", ArcanumDelegateTaskTool.GuildSchema)
                : new(tool.Name, "Delegate a task to an Apprentice Quest.", ArcanumInternalToolServer.BuildCastSendingSchema());

            if (await tools.AdvertiseAsync(template, tool, ct).ConfigureAwait(false) is { } advertised) functions.Add(advertised);
        }

        List<ChatMessage> messages = [new(ChatRole.User, request.Prompt)];

        ChatCompletionUsage? usage = null;

        int roundOrdinal = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using GuildProviderCallBinding round = binding.CreateRound(messages, functions);

            // The provider's external ceiling is a spend proof, not the requested answer size.
            // Reuse the existing resolved local reservation for an unspecified output limit.
            int requestedOutput = binding.Input.MaxOutputTokens ?? BuildModelCallContext(lease, request).ReservedAnswerTokens;

            int? physicalOutput = round.PrepareClient(lease.ChatClient, requestedOutput);

            ModelCallContext context = BuildModelCallContext(lease, request with { MaxOutputTokens = physicalOutput }) with { GuildBinding = round };

            ChatOptions options = new() { ModelId = binding.Input.Model, MaxOutputTokens = physicalOutput };

            if (functions.Count > 0)
            {
                options.Tools = [.. functions];

                options.ToolMode = ChatToolMode.Auto;

                options.AllowMultipleToolCalls = false;
            }

            ModelCallOutcome called = await ModelCallExecutor.ExecuteBufferedAsync(lease.ChatClient,
                messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, ct, context).ConfigureAwait(false);

            if (called.IsFailure) return called.Error;

            usage = AccumulateUsage(usage, MapUsageDetails(called.Value.Usage));

            List<FunctionCallContent> calls = ToolExecutionPipeline.CollectActionableFunctionCalls(called.Value.Response);

            if (calls.Count == 0)
            {
                string text = called.Value.Response.Text;

                Result output = await FilterGuardrailsOutputAsync(text, null, binding.Input.Model, ct).ConfigureAwait(false);

                if (output.IsFailure) return output.Error;

                return new PromptTurnResult(text, usage, FinishReason: called.Value.Response.FinishReason?.ToString()) { ModelUsed = binding.Input.Model };
            }

            for (int ordinal = 0; ordinal < calls.Count; ordinal++)
            {
                FunctionCallContent call = calls[ordinal];

                GuildToolGrant? grant = GuildLocalToolRegistry.SupportedTools.FirstOrDefault(item => item.Name == call.Name);

                if (grant is null || !functions.Any(item => item.Name == call.Name))
                    return new Error(ErrorCodes.Guild.ToolNotGranted, "The provider requested an unadvertised Guild tool.");

                // This identity is host-generated from the persisted physical source and ordinal;
                // provider call ids and JSON arguments cannot select a child issuance authority.
                Guid logicalCall = new(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{round.SourceId:N}:{ordinal}"))[..16]);

                using IDisposable invocation = registry.BeginInvocation(round.SourceId, logicalCall, grant);

                ToolExecutionPipeline.ProcessedToolCall processed = await toolExecutionPipeline.ProcessSingleToolCallAsync(
                    call, request, options, null, null, new() { GuildTools = tools, ProtectedContent = true },
                    suppressInvocationFailures: false, ct, toolRoundOrdinal: roundOrdinal, callOrdinal: ordinal).ConfigureAwait(false);

                if (processed.Sensitivity is not null) return GuildContextUnavailable;

                ToolExecutionPipeline.AppendToolExchangeToMessages(messages, call, processed.CallId, processed.ResultText);
            }

            roundOrdinal++;
        }
    }

    private sealed class GuildSchemaFunction(string name, string description, JsonElement schema) : AIFunction
    {
        public override string Name => name;

        public override string Description => description;

        public override JsonElement JsonSchema => schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A schema does not confer Guild tool authority.");
    }
}
