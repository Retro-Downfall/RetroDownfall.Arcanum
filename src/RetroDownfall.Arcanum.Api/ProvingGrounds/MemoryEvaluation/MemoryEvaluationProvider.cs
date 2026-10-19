using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Request-owned exact provider bindings. No productive turn pipeline or ambient authority.</summary>
public sealed class MemoryEvaluationProvider : IMemoryEvaluationProvider
{
    private readonly MemoryEvaluationProviderClientFactory _clients;

    private readonly MemoryEvaluationProviderEpoch _epoch;

    private readonly IModelCallExecutor _executor;

    private readonly IModelTokenEstimator _estimator;

    private readonly ITurnRunWriter _writer;

    private readonly IBudgetReservationService _reservations;

    private readonly ConditionalWeakTable<MemoryEvaluationExecutionPlan, PreparedBinding> _bindings = new();

    public MemoryEvaluationProvider(MemoryEvaluationProviderClientFactory clients, MemoryEvaluationProviderEpoch epoch,
        IModelCallExecutor executor, IModelTokenEstimator estimator, ITurnRunWriter writer, IBudgetReservationService reservations)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));

        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));

        _executor = executor ?? throw new ArgumentNullException(nameof(executor));

        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));

        _writer = writer ?? throw new ArgumentNullException(nameof(writer));

        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
    }

    public Task<Result<MemoryEvaluationExecutionPlan>> PrepareAsync(MemoryEvaluationValidatedRequest request,
        MemoryEvaluationSourceSnapshot snapshot, MemoryEvaluationPromptPair prompts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Result<MemoryEvaluationPromptPair> proof = MemoryEvaluationPromptPolicy.Derive(request, snapshot,
            new(prompts.TreatmentSystemPrompt, prompts.TreatmentUserPrompt, prompts.Occurrences));

        if (proof.IsFailure || proof.Value.ControlSystemPrompt != prompts.ControlSystemPrompt
            || proof.Value.ControlUserPrompt != prompts.ControlUserPrompt || proof.Value.UnchangedSpansDigest != prompts.UnchangedSpansDigest
            || snapshot.ManifestDigest != MemoryEvaluationDigests.SourceManifest(snapshot.Coordinates, snapshot.Dependencies))
            return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Failure(Invalid()));

        MemoryEvaluationProviderSnapshot configuration = _epoch.Capture();

        ArcanumSettings settings = configuration.Read();

        if (!ProviderResolver.TryResolveProviderByName(settings, request.Request.ProviderName, out ProviderSettings? provider)
            || provider!.Type != AiProviderKind.OpenAICompatible
            || !ProviderResolver.TryResolveModelEntry(provider, request.Request.Model, out ModelEntry? entry)
            || !string.Equals(entry!.Name, request.Request.Model, StringComparison.Ordinal)
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not "http" and not "https" || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Failure(Invalid()));

        ReasoningRequestOptions? reasoning = request.Request.Options.ReasoningEffort is { } effort ? new(Effort: effort) : null;

        if (ReasoningRequestValidator.ValidateForModel(reasoning, entry, settings.Features.Reasoning).IsFailure)
            return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Failure(Invalid()));

        bool derived = snapshot.Coordinates.Any(source => source.Sensitivity == ContentSensitivity.CovenantDerived);

        GenerationProvenance provenance = derived ? snapshot.Provenance ?? GenerationProvenance.CreateExact([]) : GenerationProvenance.CreateExact([]);

        if (derived && provenance.IsEmpty) return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Failure(Invalid()));

        CovenantDigest sensitivity = CovenantDigests.Sensitivity(provenance.ToDigestInput(derived ? ContentSensitivity.CovenantDerived : ContentSensitivity.None));

        ContextTokenBreakdown control = _estimator.EstimateMemoryEvaluation(provider, entry.Name, prompts.ControlSystemPrompt, prompts.ControlUserPrompt, request.Request.Options.MaxOutputTokens);

        ContextTokenBreakdown treatment = _estimator.EstimateMemoryEvaluation(provider, entry.Name, prompts.TreatmentSystemPrompt, prompts.TreatmentUserPrompt, request.Request.Options.MaxOutputTokens);

        if (provider.ContextWindowLimit <= 0 || control.TotalTokens > provider.ContextWindowLimit || treatment.TotalTokens > provider.ContextWindowLimit)
            return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Failure(Invalid()));

        ImmutableArray<MemoryEvaluationArm>.Builder order = ImmutableArray.CreateBuilder<MemoryEvaluationArm>(request.Request.PairCount * 2);

        for (int pair = 0; pair < request.Request.PairCount; pair++)
        {
            order.Add(pair % 2 == 0 ? MemoryEvaluationArm.Control : MemoryEvaluationArm.Treatment);

            order.Add(pair % 2 == 0 ? MemoryEvaluationArm.Treatment : MemoryEvaluationArm.Control);
        }

        CovenantDigest modelDigest = Digest("model", provider.Name, entry.Name);

        CovenantDigest destination = Digest("destination", provider.Name, endpoint.AbsoluteUri);

        var mapped = MemoryEvaluationProviderCallFreezer.MapOptions(request.Request.Options, entry.Name);

        MemoryEvaluationExecutionPlan plan = new(request, snapshot, prompts, configuration.Digest, modelDigest,
            Digest("options", MemoryEvaluationDigests.Request(request).ToString()), destination, sensitivity, configuration.Generation, order.MoveToImmutable());

        ModelCallContext controlContext = new(provider, entry.Name, request.Request.Options.MaxOutputTokens, 0, control);

        ModelCallContext treatmentContext = new(provider, entry.Name, request.Request.Options.MaxOutputTokens, 0, treatment);

        ProviderCallEnvelope controlEnvelope = MemoryEvaluationProviderCallFreezer.Freeze(plan, MemoryEvaluationArm.Control, controlContext, mapped,
            MemoryEvaluationProviderCallFreezer.Messages(plan, MemoryEvaluationArm.Control));

        ProviderCallEnvelope treatmentEnvelope = MemoryEvaluationProviderCallFreezer.Freeze(plan, MemoryEvaluationArm.Treatment, treatmentContext, mapped,
            MemoryEvaluationProviderCallFreezer.Messages(plan, MemoryEvaluationArm.Treatment));

        plan = plan with { OptionsDigest = treatmentEnvelope.Options.Digest };

        _bindings.Add(plan, new(configuration, MemoryEvaluationDigests.ExecutionPlan(plan), control, treatment,
            controlEnvelope.Digest, treatmentEnvelope.Digest));

        return Task.FromResult(Result<MemoryEvaluationExecutionPlan>.Success(plan));
    }

    public Task<Result> ValidateAsync(MemoryEvaluationExecutionPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_bindings.TryGetValue(plan, out PreparedBinding? binding) && !binding.Stopped
            && _epoch.IsCurrent(binding.Configuration) && plan.ConfigurationGeneration == binding.Configuration.Generation
            && MemoryEvaluationDigests.ExecutionPlan(plan) == binding.PlanDigest ? Result.Success() : Result.Failure(Invalid()));
    }

    public async Task<Result<MemoryEvaluationArmResult>> SendAsync(MemoryEvaluationExecutionPlan plan, int pairIndex, MemoryEvaluationArm arm,
        ITurnBudget budget, Func<MemoryEvaluationSendEvidence, CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        ArgumentNullException.ThrowIfNull(beforeSend);

        if ((await ValidateAsync(plan, ct).ConfigureAwait(false)).IsFailure || !_bindings.TryGetValue(plan, out PreparedBinding? binding)) return Invalid();

        if (!await binding.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return Invalid();

        TurnAccountingHandle? accounting = null;

        bool valid = false;

        try
        {
            if (binding.NextSend >= plan.SendOrder.Length || pairIndex != binding.NextSend / 2 || arm != plan.SendOrder[binding.NextSend]
                || (await ValidateAsync(plan, ct).ConfigureAwait(false)).IsFailure) return Invalid();

            ArcanumSettings settings = binding.Configuration.Read();

            ProviderResolver.TryResolveProviderByName(settings, plan.Request.Request.ProviderName, out ProviderSettings? provider);

            string model = plan.Request.Request.Model;

            PricingSettings pricing = settings.ResolvePricing();

            ContextTokenBreakdown common = binding.Treatment.InputTokens >= binding.Control.InputTokens ? binding.Treatment : binding.Control;

            decimal reservation = BudgetReservationService.EstimateWorstCaseTurnUsd(pricing.ResolveForModel(model),
                common.ReservedAnswerTokens, common.ReservedAnswerTokens, common.InputTokens);

            var begun = await TurnAccountingHandle.BeginAsync(_writer, _reservations, pricing, model, null,
                "memory-evaluation", "counterfactual-arm", $"{plan.Request.Request.EvaluationId:N}:{binding.NextSend}", ct,
                reservedUsdOverride: reservation).ConfigureAwait(false);

            if (begun.IsFailure) return begun.Error;

            accounting = begun.Value;

            Result admitted = await accounting.EnsureReservationForContextAsync(_reservations, pricing, null, model, common,
                NullLogger.Instance, ct).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            var client = await _clients.ResolveChatAsync(provider!, model, ct).ConfigureAwait(false);

            if (client.IsFailure) return Invalid();

            using ChatClientLease lease = client.Value;

            ContextTokenBreakdown actual = arm == MemoryEvaluationArm.Control ? binding.Control : binding.Treatment;

            ModelCallContext context = new(provider!, model, plan.Request.Request.Options.MaxOutputTokens, 0, actual);

            bool callbackUsed = false;

            async Task Fence(MemoryEvaluationSendEvidence evidence, CancellationToken cancellation)
            {
                if (callbackUsed || evidence.Envelope.Digest != (arm == MemoryEvaluationArm.Control ? binding.ControlEnvelope : binding.TreatmentEnvelope)
                    || evidence.SendIndex != binding.NextSend || (await ValidateAsync(plan, cancellation).ConfigureAwait(false)).IsFailure)
                    throw new InvalidOperationException("The frozen evaluation dispatch is stale.");

                callbackUsed = true;

                // Accounting preflight must finish before the caller's final authority/disclosure fence.
                // If that fence refuses, retain the marker conservatively without inventing a bill.
                await accounting.MarkPossiblyDispatchedAsync(_reservations, cancellation).ConfigureAwait(false);

                if ((await ValidateAsync(plan, cancellation).ConfigureAwait(false)).IsFailure)
                    throw new InvalidOperationException("The evaluation configuration changed during accounting preflight.");

                await beforeSend(evidence, cancellation).ConfigureAwait(false);

                cancellation.ThrowIfCancellationRequested();

                if ((await ValidateAsync(plan, cancellation).ConfigureAwait(false)).IsFailure)
                    throw new InvalidOperationException("The evaluation configuration changed at dispatch.");
            }

            async Task Record(MemoryEvaluationUsage usage)
            {
                if (usage.InputTokens is not long input || usage.OutputTokens is not long output
                    || usage.CachedTokens is not long cached || usage.ReasoningTokens is not long reasoning) return;

                await accounting.RecordUsageAsync(_writer, BillableOperationType.Chat, provider!.Name, model, "counterfactual-arm",
                    input, output, cached, reasoning, pricing.ResolveForModel(model), CancellationToken.None).ConfigureAwait(false);
            }

            var result = await _executor.ExecuteMemoryEvaluationAsync(lease.ChatClient, plan, pairIndex, arm, budget, Fence, Record, context, ct).ConfigureAwait(false);

            if (result.IsFailure) return result.Error;

            valid = result.Value.IsValid && !accounting.AccountingFailed;

            binding.NextSend++;

            return result.Value with { Usage = result.Value.Usage with { Cost = result.Value.UsageKnown ? accounting.AccumulatedCostUsd : null } };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new Error(ErrorCodes.MemoryEvaluation.ProviderFailure, "The evaluation provider or accounting boundary failed.");
        }
        finally
        {
            try
            {
                if (accounting is not null) await accounting.CompleteAsync(_writer, _reservations,
                    valid ? InferenceRunStatus.Completed : InferenceRunStatus.Failed, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                valid = false;

                throw;
            }
            finally
            {
                if (!valid) binding.Stopped = true;

                binding.Gate.Release();
            }
        }
    }

    private static Error Invalid() => new(ErrorCodes.MemoryEvaluation.InvalidProviderPlan, "The exact evaluation provider plan is unavailable or stale.");

    private static CovenantDigest Digest(string domain, params string[] values)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-evaluation." + domain + ".v1");

        foreach (string value in values) writer.WriteUtf8(value);

        return writer.FinalizeDigest();
    }

    private sealed class PreparedBinding(MemoryEvaluationProviderSnapshot configuration, CovenantDigest planDigest,
        ContextTokenBreakdown control, ContextTokenBreakdown treatment, CovenantDigest controlEnvelope, CovenantDigest treatmentEnvelope)
    {
        internal MemoryEvaluationProviderSnapshot Configuration { get; } = configuration;

        internal CovenantDigest PlanDigest { get; } = planDigest;

        internal ContextTokenBreakdown Control { get; } = control;

        internal ContextTokenBreakdown Treatment { get; } = treatment;

        internal CovenantDigest ControlEnvelope { get; } = controlEnvelope;

        internal CovenantDigest TreatmentEnvelope { get; } = treatmentEnvelope;

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal int NextSend;

        internal volatile bool Stopped;
    }
}
