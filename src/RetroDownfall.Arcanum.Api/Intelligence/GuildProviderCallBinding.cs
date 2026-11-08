using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>One host-built turn and one physical call. Retained or copied request ids confer no authority.</summary>
internal sealed class GuildProviderCallBinding(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
    PingRequest request, BudgetReservationService daily, ModelPricingEntry pricing,
    GuildProviderPayload? roundPayload = null) : IGuildInvocationBinding, IDisposable
{
    private int _closed;

    private int _called;

    private IChatClient? _client;

    private GuildProviderWirePolicy.PreparedCall? _prepared;

    private int? _maxOutput;

    internal bool IsActive => Volatile.Read(ref _closed) == 0;

    internal bool PossiblyDispatched { get; private set; }

    internal GuildLocalExecutionInput Input => input;

    internal GuildLocalInferenceExecution Execution => execution;

    internal Guid SourceId { get; } = Guid.NewGuid();

    internal GuildProviderCallBinding CreateRound(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> functions) =>
        IsActive ? new(execution, input, request, daily, pricing, new(messages, functions))
            : throw new InvalidOperationException("The Guild turn has ended.");

    internal bool Matches(PingRequest candidate) => IsActive && ReferenceEquals(request, candidate);

    internal bool Matches(ModelCallContext context) => IsActive && ReferenceEquals(context.GuildBinding, this)
        && context.Provider.Name == input.Provider && context.Model == input.Model
        && context.Provider.Type == AiProviderKind.OpenAICompatible;

    internal int? PrepareClient(IChatClient client, int? selectedOutput = null)
    {
        if (!IsActive || Interlocked.CompareExchange(ref _client, client, null) is not null)
            throw new InvalidOperationException("The Guild turn already selected its physical transport.");

        int? boundedOutput = input.MaxOutputTokens is { } accepted
            ? Math.Min(accepted, selectedOutput ?? accepted) : selectedOutput;

        if (boundedOutput is <= 0) throw new InvalidOperationException("The physical output bound must be positive.");

        _prepared = roundPayload is null
            ? GuildProviderWirePolicy.Prepare(client, input.Provider, input.Model, request.Prompt, boundedOutput, pricing)
            : GuildProviderWirePolicy.Prepare(client, input.Provider, input.Model, roundPayload.CreateMessages(),
                roundPayload.CreateFunctions(), boundedOutput, pricing);

        _maxOutput = _prepared?.MaxOutputTokens ?? boundedOutput;

        return _maxOutput;
    }

    internal async Task<Result<ChatResponse>> SendAsync(IChatClient client, IList<ChatMessage> messages,
        ChatOptions options, ModelCallContext context, CancellationToken ct)
    {
        if (!Matches(context) || !ReferenceEquals(client, _client) || !PayloadMatches(messages, options)
            || options.MaxOutputTokens != _maxOutput
            || Interlocked.CompareExchange(ref _called, 1, 0) != 0)
            return new Error(ErrorCodes.Guild.AuthorityMismatch, "The physical payload does not bind its live accepted Guild turn.");

        // Parallel units share this exact live execution, while orphaned/unknown effects still
        // fail the durable store check. Keep the physical call serialized through settlement,
        // and release before the runtime processes tools or waits for a delegated child.
        using IDisposable physicalCall = await execution.AcquirePhysicalCallAsync(ct).ConfigureAwait(false);

        // Reconstruct the complete admitted text-only request before awaiting storage. Caller-owned mutable
        // lists/options never reach the transport, and token point estimates are never used as hard bounds.
        List<ChatMessage> payload = roundPayload?.CreateMessages() ?? [new(ChatRole.User, request.Prompt)];

        ChatOptions providerOptions = new() { ModelId = input.Model, MaxOutputTokens = _maxOutput };

        if (roundPayload?.HasTools == true)
        {
            providerOptions.Tools = [.. roundPayload.CreateFunctions()];

            providerOptions.ToolMode = ChatToolMode.Auto;

            providerOptions.AllowMultipleToolCalls = false;
        }

        GuildProviderWirePolicy.PreparedCall? prepared = _prepared;

        GuildQuestDriver driver = execution.Driver;

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), driver.Execution.QuestId, execution.Dispatch.DispatchId,
            driver.Execution.RunId, GuildBudgetEffectKind.LocalProvider, SourceId, null, prepared?.UsdBound, prepared?.TokenBound);

        DateTimeOffset started = DateTimeOffset.UtcNow;

        Result<ChatResponse> response = await execution.Store.ExecuteProviderEffectAsync<ChatResponse>(execution, daily, effect,
            async token =>
            {
                if (!IsActive) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "The Guild turn ended before provider handoff.");

                PossiblyDispatched = true;

                using IDisposable? wire = prepared?.Enter();

                return await client.GetResponseAsync(payload, providerOptions, token).ConfigureAwait(false);
            }, ct, input).ConfigureAwait(false);

        if (response.IsFailure) return response.Error;

        UsageDetails? usage = response.Value.Usage;

        long inputTokens = usage?.InputTokenCount ?? 0, outputTokens = usage?.OutputTokenCount ?? 0;

        long cached = usage?.CachedInputTokenCount ?? 0, reasoning = usage?.ReasoningTokenCount ?? 0;

        if (inputTokens < 0 || outputTokens < 0 || cached < 0 || cached > inputTokens || reasoning < 0 || reasoning > outputTokens)
            return new Error(ErrorCodes.Guild.ReconciliationRequired, "The provider returned invalid physical usage; its exposure remains held.");

        ModelPricingEntry snapshot = prepared?.Pricing ?? pricing with { };

        BillableOperationRecord bill = new(driver.Execution.RunId, BillableOperationType.Chat, input.Provider, input.Model,
            "guild", started, DateTimeOffset.UtcNow, inputTokens, outputTokens, reasoning, cached,
            prepared?.PricingSnapshotJson() ?? JsonSerializer.Serialize(snapshot, ConfigurationJsonContext.Default.ModelPricingEntry),
            CostCalculator.CalculateCost(inputTokens, outputTokens, cached, reasoning, snapshot),
            usage?.InputTokenCount is not null && usage.OutputTokenCount is not null
                ? BillableOperationStatus.Completed : BillableOperationStatus.Failed, response.Value.ResponseId);

        // Transport cancellation never discards known physical usage. Installation admission can still
        // refuse settlement, in which case the already committed exposure remains explicitly unresolved.
        Result<GuildBudgetEffect> settled = await execution.Store.SettleProviderEffectAsync(execution, daily, effect, bill,
            CancellationToken.None).ConfigureAwait(false);

        return settled.IsFailure ? settled.Error : response;
    }

    public void Dispose() => Interlocked.Exchange(ref _closed, 1);

    private bool PayloadMatches(IList<ChatMessage> messages, ChatOptions options)
    {
        if (roundPayload is null)
            return messages.Count == 1 && messages[0].Role == ChatRole.User && messages[0].Contents.Count == 1
                && messages[0].Contents[0] is TextContent text && text.Text == request.Prompt && options.Tools is null or { Count: 0 };

        AIFunction[] functions = options.Tools?.OfType<AIFunction>().ToArray() ?? [];

        return functions.Length == (options.Tools?.Count ?? 0)
            && roundPayload.Matches(messages as IReadOnlyList<ChatMessage> ?? messages.ToArray(), functions);
    }
}
