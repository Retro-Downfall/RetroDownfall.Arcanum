using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider
{
    internal sealed class WorkspaceMutationRuntime(WizardIntelligenceProvider owner, GuildProviderCallBinding binding) : IGuildWorkspaceMutationRuntime
    {
        public Task<ResourceLimits> GetResourceLimitsAsync(string root, CancellationToken cancellationToken) =>
            owner.GetGuildWorkspaceMutationLimitsAsync(root, cancellationToken);

        public Task<Result<ApplyPatchToolExecutionResponse>> ExecutePatchAsync(GuildChildToolAdmission call, Guid session, CanonicalCampaignContext campaign,
            Func<ApplyPatchInvocationContext, CancellationToken, Task<ApplyPatchToolExecutionResponse>> apply, CancellationToken cancellationToken) =>
            owner.ExecuteGuildPatchAsync(binding, call, session, campaign, apply, cancellationToken);
    }

    private Task<ResourceLimits> GetGuildWorkspaceMutationLimitsAsync(string root, CancellationToken ct) =>
        sanctumGuard.GetEffectiveResourceLimitsForWorkspaceAsync(root, ct);

    private async Task<Result<ApplyPatchToolExecutionResponse>> ExecuteGuildPatchAsync(GuildProviderCallBinding binding, GuildChildToolAdmission call, Guid session,
        CanonicalCampaignContext campaign, Func<ApplyPatchInvocationContext, CancellationToken, Task<ApplyPatchToolExecutionResponse>> apply,
        CancellationToken ct)
    {
        if (!binding.IsActive || !ReferenceEquals(call.Registry.Parent, binding.Execution)
            || !call.Registry.Owns(call) || !ReferenceEquals(call.Registry.CaptureInvocation(call.Tool), call.Frame)
            || call.Tool.Name != "apply_patch") return GuildContextUnavailable;

        const string prompt = "Apply an authorized Guild workspace patch.";

        PingRequest request = new(prompt, binding.Input.Model, SessionId: session, DisableAllTools: true);

        var started = await grimoireTurnWriter.BeginBufferedAssistantReplyAsync(request,
            ArcanumInvocationContext.ForGuild(binding, campaign), prompt, binding.Input.Model, ct).ConfigureAwait(false);

        if (started.IsFailure) return started.Error;

        GrimoireTurnWriter.TurnHandle turn = started.Value;

        try
        {
            if (turn.SessionId != session || turn.AssistantEntryId is not Guid assistant) return GuildContextUnavailable;

            ToolInvocationIdentity identity = new($"guild:{binding.Execution.Driver.Execution.RunId:N}:{call.LogicalCallId:N}",
                call.LogicalCallId.ToString("D"), ToolRoundOrdinal: 0, CallOrdinal: 0, "apply_patch");

            // Receipt identity is stable per host call. The exact canonical argument digest is
            // enough to reject replay collisions without publishing patch text into Session history.
            ApplyPatchInvocationContext context = new(session, assistant, identity,
                $$"""{"sha256":"{{call.ArgumentsDigest}}","assistantEntryId":"{{assistant:D}}"}""", binding.Input.Model, DateTimeOffset.UtcNow,
                new ToolExecutionPipeline.GrimoirePendingReceiptSink(grimoireTurnWriter));

            using IDisposable persisted = PersistedToolInvocationAmbient.Begin(new(session, assistant));

            using IDisposable patch = ApplyPatchInvocationAmbient.Begin(context);

            ApplyPatchToolExecutionResponse response = await apply(context, ct).ConfigureAwait(false);

            string summary = response.RequiresReconciliation ? "Guild workspace patch requires reconciliation." : "Guild workspace patch processed.";

            return await grimoireTurnWriter.TryFinalizeBufferedAssistantEntryAsync(turn, summary,
                binding.Input.Model, CancellationToken.None).ConfigureAwait(false)
                ? response : new Error(ErrorCodes.Guild.ReconciliationRequired, "The workspace patch turn receipt requires reconciliation.");
        }
        catch (ApplyPatchReceiptHandoffException)
        {
            _ = await grimoireTurnWriter.TryFinalizeBufferedAssistantEntryAsync(turn, "Guild workspace patch requires reconciliation.",
                binding.Input.Model, CancellationToken.None).ConfigureAwait(false);

            return new Error(ErrorCodes.Guild.ReconciliationRequired, "The workspace patch receipt requires reconciliation.");
        }
        finally
        {
            await grimoireTurnWriter.TryResolveInterruptedOnStreamExitAsync(turn, null).ConfigureAwait(false);

            // This synthetic receipt cannot enqueue Saga extraction. The resolved assistant
            // therefore has no remaining handoff that could hold its Session ownership.
            if (turn.IsFinalized) grimoireTurnWriter.CompleteSagaExtractionHandoff(turn);
        }
    }
}
