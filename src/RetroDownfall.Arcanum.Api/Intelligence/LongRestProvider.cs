using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Closed physical dispatch and normal paid accounting, independent of effect/publication CAS.</summary>
public sealed class LongRestProvider(LongRestProviderClientFactory factory, IModelCallExecutor executor, LongRestConfigurationEpoch epoch,
    ITurnRunWriter runs, IBudgetReservationService reservations, IInferenceAuditLogger? audit = null,
    ILogger<LongRestProvider>? logger = null) : ILongRestProvider
{
    public async Task<Result<LongRestProviderResult>> ExecuteAsync(LongRestProviderPayload payload, ITurnBudget budget,
        Func<CancellationToken, Task> beforeSend, LongRestProviderAdmission admission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        ArgumentNullException.ThrowIfNull(budget);

        ArgumentNullException.ThrowIfNull(beforeSend);

        ArgumentNullException.ThrowIfNull(reservations);

        LongRestConfigurationSnapshot snapshot = epoch.Capture();

        if (!snapshot.Enabled || payload.Work.Fence.Configuration != snapshot.Stamp
            || payload.Work.Scope.ProtectedPartitionDigest is not null && !snapshot.CovenantEnabled
            || payload.Kind == LongRestProviderPayloadKind.Embedding && admission is not LongRestProviderAdmission.Embedding
            || payload.Kind is LongRestProviderPayloadKind.Summary or LongRestProviderPayloadKind.Adjudication && admission is not LongRestProviderAdmission.Chat)
        {
            return Stale();
        }

        Result<LongRestProviderSelection> resolved = admission is LongRestProviderAdmission.Chat chat
            ? snapshot.ResolveChat(chat.Context.Model) : snapshot.ResolveEmbedding();

        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        LongRestProviderSelection selected = resolved.Value;

        if (selected.Provider.Type != AiProviderKind.OpenAICompatible || !Matches(admission, selected))
        {
            return Stale();
        }

        // The admission uses these exact selected values, never a mutable provider after key resolution.
        LongRestProviderAdmission frozen = admission switch
        {
            LongRestProviderAdmission.Chat value => new LongRestProviderAdmission.Chat(value.Context with { Provider = selected.Provider }),
            LongRestProviderAdmission.Embedding value => value with { Provider = selected.Provider },
            _ => throw new InvalidOperationException(ErrorCodes.LongRest.InvalidAdmission),
        };

        string purpose = payload.Kind switch
        {
            LongRestProviderPayloadKind.Summary => "long-rest-summary",
            LongRestProviderPayloadKind.Adjudication => "long-rest-adjudication",
            _ => "long-rest-embedding",
        };

        PricingSettings pricing = new() { DefaultPricing = selected.Pricing };

        decimal reservation = frozen switch
        {
            LongRestProviderAdmission.Embedding value => BudgetReservationService.EstimateWorstCaseEmbeddingUsd(selected.Pricing, value.ReservedInputTokens),
            LongRestProviderAdmission.Chat value => BudgetReservationService.EstimateWorstCaseTurnUsd(selected.Pricing, value.Context.ReservedAnswerTokens,
                value.Context.ReservedReasoningTokens, value.Context.PrecomputedBreakdown?.InputTokens ?? 0),
            _ => 0m,
        };

        Result<TurnAccountingHandle> opened = await TurnAccountingHandle.BeginAsync(runs, reservations, pricing, selected.Model,
            sessionId: null, surface: "long-rest", purpose, Guid.NewGuid().ToString("N"), ct, reservedUsdOverride: reservation).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        TurnAccountingHandle accounting = opened.Value;

        InferenceRunStatus status = InferenceRunStatus.Failed;

        int sends = 0;

        LongRestProviderUsage? observedUsage = null;

        bool accounted = false;

        LongRestProviderResult PaidRefusal(string reason) =>
            new(payload.Kind, LongRestProviderResultDisposition.Invalid, null, null, [], observedUsage!, reason);

        long started = Stopwatch.GetTimestamp();

        bool finalAccountingFailed = false;

        try
        {
            return await DispatchAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (observedUsage is not null && finalAccountingFailed)
        {
            logger?.LogWarning("Long Rest accounting completion failed ({ExceptionType}).", exception.GetType().FullName);

            if (accounting.RunId is { } runId)
            {
                try
                {
                    await runs.CompleteRunAsync(runId, InferenceRunStatus.Failed, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception settlement)
                {
                    logger?.LogWarning("Long Rest failed run settlement was refused ({ExceptionType}).", settlement.GetType().FullName);
                }
            }

            return PaidRefusal(ErrorCodes.LongRest.AccountingCompletionRefused);
        }

        async Task<Result<LongRestProviderResult>> DispatchAsync()
        {
            try
            {
                async Task BeforeSend(CancellationToken token)
                {
                    if (Interlocked.Exchange(ref sends, 1) != 0 || !epoch.IsCurrent(snapshot.Stamp))
                    {
                        throw new InvalidOperationException(ErrorCodes.LongRest.StaleFence);
                    }

                    await beforeSend(token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    if (!epoch.IsCurrent(snapshot.Stamp))
                    {
                        throw new InvalidOperationException(ErrorCodes.LongRest.StaleFence);
                    }
                }

                async Task RecordPaid(LongRestProviderUsage usage)
                {
                    observedUsage = usage;

                    await accounting.RecordUsageAsync(runs, payload.Kind == LongRestProviderPayloadKind.Embedding ? BillableOperationType.Embedding : BillableOperationType.Chat,
                        selected.Provider.Name, selected.Model, purpose, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens,
                        usage.ReasoningTokens, selected.Pricing, CancellationToken.None).ConfigureAwait(false);

                    accounted = true;

                    if (audit is not null)
                    {
                        try
                        {
                            static int Count(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

                            await audit.LogAsync(new InferenceAuditRecord(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                                null, purpose, selected.Model, selected.Provider.Name, Count(usage.InputTokens), Count(usage.OutputTokens), Count(usage.TotalTokens),
                                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, [], null, null, null, null,
                                payload.Work.Scope.CampaignId?.ToString("D"), Count(usage.ReasoningTokens)), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            logger?.LogWarning("Long Rest paid audit failed ({ExceptionType}).", exception.GetType().FullName);
                        }
                    }
                }

                Result<LongRestProviderResult> result;

                if (payload.Kind == LongRestProviderPayloadKind.Embedding)
                {
                    Result<EmbeddingGeneratorLease> client = await factory.ResolveEmbeddingAsync(selected, ct).ConfigureAwait(false);

                    if (client.IsFailure)
                    {
                        return client.Error;
                    }

                    using EmbeddingGeneratorLease lease = client.Value;

                    result = await executor.ExecuteLongRestEmbeddingAsync(lease.Generator, payload, budget, BeforeSend, RecordPaid, frozen, ct).ConfigureAwait(false);
                }
                else
                {
                    Result<ChatClientLease> client = await factory.ResolveChatAsync(selected, ct).ConfigureAwait(false);

                    if (client.IsFailure)
                    {
                        return client.Error;
                    }

                    using ChatClientLease lease = client.Value;

                    result = await executor.ExecuteLongRestChatAsync(lease.ChatClient, payload, budget, BeforeSend, RecordPaid, frozen, ct).ConfigureAwait(false);
                }

                if (observedUsage is not null && !accounted)
                {
                    return PaidRefusal(ErrorCodes.LongRest.AccountingRefused);
                }

                ct.ThrowIfCancellationRequested();

                if (!epoch.IsCurrent(snapshot.Stamp))
                {
                    return observedUsage is null ? Stale() : PaidRefusal(ErrorCodes.LongRest.ConfigurationChanged);
                }

                if (observedUsage is not null && result.IsFailure)
                {
                    return PaidRefusal(ErrorCodes.LongRest.InvalidOutput);
                }

                if (result.IsSuccess && result.Value.Disposition == LongRestProviderResultDisposition.Valid)
                {
                    status = InferenceRunStatus.Completed;
                }

                return result;
            }
            catch (OperationCanceledException) when (observedUsage is not null)
            {
                return PaidRefusal(accounted ? ErrorCodes.LongRest.CancelledAfterPayment : ErrorCodes.LongRest.AccountingRefused);
            }
            finally
            {
                try
                {
                    await accounting.CompleteAsync(runs, reservations, status, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    finalAccountingFailed = true;

                    throw;
                }
            }
        }
    }

    private static bool Matches(LongRestProviderAdmission admission, LongRestProviderSelection selected)
    {
        ProviderSettings provider;

        switch (admission)
        {
            case LongRestProviderAdmission.Chat chat:
                provider = chat.Context.Provider;

                if (chat.Context.Model != selected.Model)
                {
                    return false;
                }

                break;

            case LongRestProviderAdmission.Embedding embedding:
                provider = embedding.Provider;

                if (embedding.Model != selected.Model || embedding.Dimensions != selected.Dimensions || embedding.ReservedInputTokens <= 0)
                {
                    return false;
                }

                break;

            default:
                return false;
        }

        return JsonSerializer.SerializeToUtf8Bytes(provider, ConfigurationJsonContext.Default.ProviderSettings).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(selected.Provider, ConfigurationJsonContext.Default.ProviderSettings));
    }

    private static Error Stale() => new(ErrorCodes.LongRest.InvalidRequest, "The exact Long Rest provider configuration or admission is unavailable or stale.");
}
