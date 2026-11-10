using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ApprenticeService : IGuildLocalExecutionEngine
{
    private static readonly AsyncLocal<GuildEngineFrame?> GuildEngine = new();

    public async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding,
        GuildLocalExecutionInput input, GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime,
        IGuildLocalEngineWait waitForInput, CancellationToken cancellationToken)
    {
        if (binding is null || input is null || execution is null || runtime is null || waitForInput is null)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The local engine entry is incomplete.");

        var materialized = await execution.Store.MaterializeProviderInputAsync(execution, input, cancellationToken).ConfigureAwait(false);

        if (materialized.IsFailure) return materialized.Error;

        if (materialized.Value.Binding != binding || materialized.Value.Dispatch.Evidence != GuildDispatchEvidence.Prepared
            || materialized.Value.Dispatch.LocalChildId != binding.ApprenticeId)
            return new Error(ErrorCodes.Guild.ReconciliationRequired, "Only an admitted prepared dispatch can enter the local engine.");

        if (input.Mode == GuildLocalExecutionMode.Sterile && execution.Dispatch.IsContinuation)
            return new Error(ErrorCodes.Guild.ReconciliationRequired, "A sterile invocation has no resumable planner checkpoint.");

        while (!TryAcquireExecutionSlot(binding.ApprenticeId, false, out _, out Result<string>? failure))
        {
            if (failure?.Error.Code != ErrorCodes.Apprentice.MaxReached)
                return failure?.Error ?? new Error(ErrorCodes.Guild.Unavailable, "The shared engine is stopping.");

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        long generation;

        lock (_executionLifecycleLock)
        {
            ExecutionReservation reservation = _executionReservations[binding.ApprenticeId];

            generation = reservation.Generation;

            if (_stopping || reservation.StopRequested) linked.Cancel();

            _executionTokens[binding.ApprenticeId] = new(linked, generation);
        }

        GuildEngineFrame? previous = GuildEngine.Value;

        GuildEngineFrame frame = new(this, binding, input, execution, runtime, linked, generation);

        GuildEngine.Value = frame;

        try
        {
            if (input.Mode == GuildLocalExecutionMode.Sterile)
            {
                linked.Token.ThrowIfCancellationRequested();

                if (!admissionGate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? sterileAdmission)
                    || sterileAdmission is null)
                    return new Error(ErrorCodes.Guild.Unavailable, "Sterile execution is unavailable during installation maintenance.");

                await using IGrimoireWorkLease sterileWork = sterileAdmission;

                if (!sterileWork.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? sterileEffectAdmission)
                    || sterileEffectAdmission is null)
                    return new Error(ErrorCodes.Guild.Unavailable, "Sterile execution could not admit its provider effect.");

                await using IGrimoireExternalEffectGroup sterileEffect = sterileEffectAdmission;

                using IDisposable memoryScope = execution.Store.EnterProviderInvocation(execution);

                return await runtime.ExecuteAsync(execution, input, linked.Token).ConfigureAwait(false);
            }

            StringBuilder goal = new(materialized.Value.Goal);

            foreach (GuildLocalInferenceFile file in materialized.Value.Files)
                SystemPromptBuilder.AppendUntrusted(goal, $"Selected file: {file.Name}", file.Text);

            ApprenticeCheckpoint checkpoint = new()
            {
                LaunchRequested = true, ParentApprenticeId = binding.ParentApprenticeId,
                GoalAuthority = materialized.Value.GoalAuthority,
                DelegationChain = input.DelegationChain is { } chain ? [.. chain] : null,
                Timestamp = DateTimeOffset.UtcNow,
            };

            Apprentice initial = new()
            {
                Id = binding.ApprenticeId, SessionId = binding.SessionId, CampaignId = materialized.Value.CampaignId,
                ParentApprenticeId = binding.ParentApprenticeId, Name = "Guild Quest", Goal = goal.ToString(),
                CheckpointData = ApprenticeRepository.SerializeCheckpoint(checkpoint),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };

            GuildApprenticeOverlay repository = new(initial);

            if (execution.Dispatch.IsContinuation)
            {
                var restored = await execution.Store.ReadWaitCheckpointAsync(execution, execution.Dispatch.DispatchId, linked.Token).ConfigureAwait(false);

                if (restored.IsFailure) return restored.Error;

                var continued = await ResumeGuildWaitAsync(frame, repository, restored.Value, waitForInput).ConfigureAwait(false);

                if (continued.IsFailure) return continued.Error;
            }

            ApprenticeExecutionMemory memory = new();

            DateTimeOffset started = DateTimeOffset.UtcNow;

            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();

                long observedGeneration = admissionGate.CurrentGeneration;

                if (!admissionGate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? admitted))
                {
                    await WaitForApprenticeAdmissionAsync(observedGeneration, linked.Token).ConfigureAwait(false);

                    continue;
                }

                ApprenticeUnitDisposition disposition;

                await using (IGrimoireWorkLease lease = admitted!)
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                    using IDisposable memoryScope = frame.Execution.Store.EnterProviderInvocation(frame.Execution);

                    disposition = await ExecuteNextApprenticeUnitAsync(scope.ServiceProvider, repository, lease,
                        binding.ApprenticeId, generation, GetApprenticeSettings(), memory, started, linked).ConfigureAwait(false);
                }

                if (frame.Failure is { } failed) return failed;

                if (disposition == ApprenticeUnitDisposition.DeferredForMaintenance)
                {
                    await WaitForApprenticeAdmissionAsync(observedGeneration, linked.Token).ConfigureAwait(false);

                    continue;
                }

                Apprentice current = (await repository.GetByIdAsync(binding.ApprenticeId, linked.Token).ConfigureAwait(false))!;

                if (ApprenticeExecutionPolicy.IsEscalatedStatus(current.Status))
                {
                    GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, current.CurrentStep,
                        [.. ApprenticeRepository.DeserializePlan(current.Plan)], ApprenticeRepository.DeserializeCheckpoint(current.CheckpointData)!);

                    var continued = await ResumeGuildWaitAsync(frame, repository, snapshot, waitForInput).ConfigureAwait(false);

                    if (continued.IsFailure) return continued.Error;

                    memory = new();

                    continue;
                }

                if (disposition != ApprenticeUnitDisposition.Stop) continue;

                if (current.Status != ApprenticeStatus.Completed.ToString())
                    return new Error(ErrorCodes.Guild.ReconciliationRequired, "The shared engine stopped without a completed result.");

                List<string> completedOutputs = [];

                foreach (PlanStep step in ApprenticeRepository.DeserializePlan(current.Plan))
                    if (step.Status == "completed" && !string.IsNullOrEmpty(step.Result)) completedOutputs.Add(step.Result);

                string output = string.Join("\n\n", completedOutputs);

                return new PromptTurnResult(output, frame.LastResult?.Usage) { Sensitivity = frame.Sensitivity, ModelUsed = frame.LastResult?.ModelUsed };
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return frame.Failure ?? new Error(ErrorCodes.Guild.ReconciliationRequired, "The local execution was stopped; durable effect evidence determines its disposition.");
        }
        catch (Exception)
        {
            return frame.Failure ?? new Error(ErrorCodes.Guild.ReconciliationRequired, "The local engine could not establish a completed result.");
        }
        finally
        {
            GuildEngine.Value = previous;

            await frame.ExpireCapacityAsync().ConfigureAwait(false);

            ReleaseAcquiredExecutionSlot(binding.ApprenticeId);
        }
    }

    private async Task<Result> ResumeGuildWaitAsync(GuildEngineFrame frame, GuildApprenticeOverlay repository,
        GuildLocalWaitSnapshot snapshot, IGuildLocalEngineWait waitForInput)
    {
        if (_executionLeases.TryRemove(frame.Binding.ApprenticeId, out AdmissionGate.Lease? capacity)) capacity.Dispose();

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot);

        ArtifactSensitivityLabel? source = frame.Sensitivity is { } sensitivity ? new(Guid.NewGuid(),
            SensitiveArtifactKind.GuildContent, Guid.NewGuid(), frame.Binding.SessionId, null, null, 1,
            new CovenantDigest(SHA256.HashData(bytes)), sensitivity.Level, sensitivity.Provenance,
            null, null, null, DateTimeOffset.UtcNow) : null;

        var answer = await waitForInput.WaitAsync(frame.Execution, new(frame.Execution.Dispatch.DispatchId, snapshot, source), frame.Cancellation.Token).ConfigureAwait(false);

        if (answer.IsFailure) return answer.Error;

        GuildLocalEngineContinuation resumed = answer.Value;

        if (resumed.Execution is null || !ReferenceEquals(resumed.Execution.Store, frame.Execution.Store)
            || resumed.Continuation.QuestId != frame.Binding.QuestId || resumed.Continuation.ContinuationId != resumed.Execution.Dispatch.DispatchId
            || resumed.Continuation.Authority is not (GuildAuthorityKind.Operator or GuildAuthorityKind.Parent)
            || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resumed.Answer))) != resumed.Continuation.ContentRef.Digest)
            return new Error(ErrorCodes.Guild.AuthorityMismatch, "The resumed answer does not match its accepted continuation.");

        var restored = await resumed.Execution.Store.ReadWaitCheckpointAsync(resumed.Execution,
            resumed.Continuation.ContinuationId, frame.Cancellation.Token).ConfigureAwait(false);

        if (restored.IsFailure) return restored.Error;

        if (!JsonSerializer.SerializeToUtf8Bytes(restored.Value, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot).AsSpan().SequenceEqual(bytes))
            return new Error(ErrorCodes.Guild.AuthorityMismatch, "The resumed plan does not match its admitted wait checkpoint.");

        frame.Execution = resumed.Execution;

        frame.Execution.ChildWait = frame.SuspendForChildAsync;

        if (resumed.SourceLabel is { } answerLabel)
            frame.MergeSensitivity(ProviderCallSensitivities.Create(answerLabel.Sensitivity, answerLabel.Provenance));

        Apprentice current = (await repository.GetByIdAsync(frame.Binding.ApprenticeId, frame.Cancellation.Token).ConfigureAwait(false))!;

        current.Plan = ApprenticeRepository.SerializePlan(restored.Value.Plan);

        current.CurrentStep = restored.Value.CurrentStep;

        current.Status = ApprenticeStatus.Running.ToString();

        current.ErrorMessage = null;

        current.CheckpointData = ApprenticeRepository.SerializeCheckpoint(restored.Value.Checkpoint with
        {
            EscalationReason = null,
            OperatorGuidance = resumed.Continuation.Authority == GuildAuthorityKind.Operator ? resumed.Answer : null,
            ParentGuidance = resumed.Continuation.Authority == GuildAuthorityKind.Parent ? resumed.Answer : null,
            DmGuidance = null,
        });

        await repository.UpdateAsync(current, frame.Cancellation.Token).ConfigureAwait(false);

        while (true)
        {
            frame.Cancellation.Token.ThrowIfCancellationRequested();

            if (_concurrencyGate.TryAcquire(ArcanumSettingClamps.MaxConcurrentApprentices(GetApprenticeSettings().MaxConcurrentApprentices), out AdmissionGate.Lease? reacquired))
            {
                _executionLeases[frame.Binding.ApprenticeId] = reacquired!;

                return Result.Success();
            }

            await Task.Delay(50, frame.Cancellation.Token).ConfigureAwait(false);
        }
    }

    private bool IsGuildExecution(Guid apprenticeId) => GuildEngine.Value is { } frame
        && ReferenceEquals(frame.Owner, this) && frame.Binding.ApprenticeId == apprenticeId;

    private IArcanumIntelligenceProvider ResolveUnitIntelligence(IServiceProvider services, Guid apprenticeId) =>
        GuildEngine.Value is { } frame && ReferenceEquals(frame.Owner, this) && frame.Binding.ApprenticeId == apprenticeId ? frame.Adapter : services.GetRequiredService<IArcanumIntelligenceProvider>();
}
