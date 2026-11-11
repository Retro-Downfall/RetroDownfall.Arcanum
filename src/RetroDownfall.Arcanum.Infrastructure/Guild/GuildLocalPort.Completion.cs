using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalPort
{
    internal async Task<Result<GuildDispatchObservation>> CompleteAsync(LocalRun running, GuildLocalPortState before,
        GuildDispatchRequest request, Result<PromptTurnResult> result, CancellationToken ct)
    {
        if (result.IsFailure)
            return await CompleteRefusalAsync(running, request, result.Error, ct).ConfigureAwait(false);

        // The completed public result remains in this awaited scope while asynchronous children
        // finish. No root gate is held between polls, and the existing port pulse keeps the LRO owned.
        byte[] body = Encoding.UTF8.GetBytes(result.Value.Text);

        byte[]? designated = body;

        try
        {
            // Inspection retains the ordinary public text representation. Voting additionally
            // requires lossless encoding; replacement fallback cannot repair an invalid result.
            _ = new UTF8Encoding(false, true).GetByteCount(result.Value.Text);
        }
        catch (EncoderFallbackException)
        {
            designated = null;
        }

        string digest = Convert.ToHexString(SHA256.HashData(body));

        Guid resultId = Guid.NewGuid();

        GuildDataRef content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, digest, "text/plain", body.Length, false);

        ArtifactSensitivityLabel? label = result.Value.Sensitivity is { } sensitivity ? new(Guid.NewGuid(),
            SensitiveArtifactKind.GuildContent, content.ContentId, before.Binding.SessionId, before.Quest.Quest.CampaignId,
            null, 1, new CovenantDigest(SHA256.HashData(body)), sensitivity.Level, sensitivity.Provenance,
            null, null, null, time.GetUtcNow()) : null;

        while (true)
        {
            await running.Serial.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                GuildLocalInferenceExecution invocation = running.Execution;

                var refreshed = await quests.RefreshLocalExecutionAsync(invocation, time.GetUtcNow(), false, ct).ConfigureAwait(false);

                if (refreshed.IsFailure || refreshed.Value) return await ParkAsync(invocation, request.DispatchId, ct).ConfigureAwait(false);

                var resolved = await quests.ResolveLocalDispatchAsync(invocation.Driver, invocation.Dispatch.DispatchId, ct).ConfigureAwait(false);

                if (resolved.IsFailure) return await ParkAsync(invocation, request.DispatchId, ct).ConfigureAwait(false);

                var stopped = await quests.LocalChildrenStoppedAsync(invocation.Driver, ct).ConfigureAwait(false);

                if (stopped.IsFailure) return await ParkAsync(invocation, request.DispatchId, ct).ConfigureAwait(false);

                if (stopped.Value)
                {
                    var current = await quests.ReadLocalPortAsync(invocation.Driver, invocation.Dispatch.DispatchId, ct).ConfigureAwait(false);

                    if (current.IsFailure) return current.Error;

                    GuildQuestResult completion = new(new(request.QuestId, resultId, digest, content, QuestState.Completed, null),
                        body, label, !current.Value.HadPhysicalDispatch, WorkStoppedProven: true,
                        DesignatedResultCount: 1, DesignatedResultUtf8: designated, DesignatedResultMimeType: "text/plain");

                    var committed = await quests.CommitResultAsync(invocation.Driver, new(resultId, digest),
                        current.Value.Quest.Quest.Revision, completion, ct).ConfigureAwait(false);

                    if (committed.IsSuccess)
                        return await ObserveAsync(invocation.Driver.Execution, request.QuestId, request.DispatchId, ct).ConfigureAwait(false);

                    if (committed.Error.Code != ErrorCodes.Guild.RevisionConflict)
                        return await ParkAsync(invocation, request.DispatchId, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                running.Serial.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct).ConfigureAwait(false);
        }
    }

    private async Task<Result<GuildDispatchObservation>> CompleteRefusalAsync(LocalRun running,
        GuildDispatchRequest request, Error error, CancellationToken ct)
    {
        await running.Serial.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            GuildLocalInferenceExecution execution = running.Execution;

            if (error.Code == ErrorCodes.Guild.LegacyToolName)
                return await ParkAsync(execution, request.DispatchId, ct, ErrorCodes.Guild.LegacyToolName).ConfigureAwait(false);

            // Only known preflight decisions can become a failed result. An arbitrary adapter
            // error remains reconciliation work unless durable cancellation can prove a stop.
            if (error.Code is not (ErrorCodes.Guild.Unavailable or ErrorCodes.Guild.PriceUnavailable
                or ErrorCodes.Guild.BudgetCapabilityUnavailable or ErrorCodes.Guild.GrantExceeded
                or ErrorCodes.Hub.ContextBudgetExceeded
                or ErrorCodes.Guardrails.Blocked or ErrorCodes.Guardrails.PiiDetected))
                return await ParkAsync(execution, request.DispatchId, ct).ConfigureAwait(false);

            var state = await quests.ReadLocalPortAsync(execution.Driver, request.DispatchId, ct).ConfigureAwait(false);

            if (state.IsFailure || state.Value.HadPhysicalDispatch || state.Value.Quest.Quest.CancellationRequested)
                return await ParkAsync(execution, request.DispatchId, ct).ConfigureAwait(false);

            var resolved = await quests.ResolveLocalDispatchAsync(execution.Driver, request.DispatchId, ct).ConfigureAwait(false);

            if (resolved.IsFailure || resolved.Value.Evidence != GuildDispatchEvidence.ResolvedNoDispatch)
                return await ParkAsync(execution, request.DispatchId, ct).ConfigureAwait(false);

            Guid resultId = Guid.NewGuid();

            string digest = Convert.ToHexString(SHA256.HashData([]));

            GuildQuestResult failure = new(new(request.QuestId, resultId, digest, null, QuestState.Failed, error.Code),
                null, NoDispatchProven: true, WorkStoppedProven: true);

            var committed = await quests.CommitResultAsync(execution.Driver, new(resultId, digest),
                state.Value.Quest.Quest.Revision, failure, ct).ConfigureAwait(false);

            return committed.IsSuccess
                ? await ObserveAsync(execution.Driver.Execution, request.QuestId, request.DispatchId, ct).ConfigureAwait(false)
                : await ParkAsync(execution, request.DispatchId, ct).ConfigureAwait(false);
        }
        finally
        {
            running.Serial.Release();
        }
    }
}
