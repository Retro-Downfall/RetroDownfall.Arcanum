using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider
{
    private const string RecoveryQuestionResult = "The repair paused for a user answer. New guidance will arrive in a subsequent Session turn.";

    private static string? TryGetRecoveryQuestion(FunctionCallContent function)
    {
        if (function.Arguments is null || !function.Arguments.TryGetValue("question", out object? raw)) return null;

        string? question = raw switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(question) ? null : question;
    }

    private static CovenantDigest? GetRecoveryWorkspaceIdentity(string? workspace)
    {
        if (workspace is null || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(workspace, out var metadata)
            || metadata.Kind != FileSystemObjectKind.Directory) return null;

        // The canonical resolver and tool owners grant authority. This observation only checks
        // whether the exact directory from that resolution has stayed in place.
        Span<byte> identity = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64BigEndian(identity, metadata.Identity.VolumeId);

        BinaryPrimitives.WriteUInt64BigEndian(identity[8..], metadata.Identity.FileId);

        return new CovenantDigest(SHA256.HashData(identity));
    }

    private static CovenantDigest GetRecoveryRouteDigest(ProviderSettings provider, string model)
    {
        using MemoryStream bytes = new();

        using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RetroDownfall.Arcanum/SessionRecoveryRoute/v1");
            writer.Write(provider.Name);
            writer.Write((int)provider.Type);
            writer.Write(provider.Endpoint);
            writer.Write(provider.LocalRuntimeId ?? string.Empty);
            writer.Write(provider.Command ?? string.Empty);
            writer.Write(model);
        }

        return new(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private async Task<Result<(SessionRecoveryCheckpointRecord Checkpoint, ProviderSettings Provider, string Model)>> ResolveRecoveryRouteAsync(
        PingRequest request, ArcanumInvocationContext invocation, CancellationToken ct)
    {
        if (request.SessionId is not { } session || request.RecoveryCheckpointId is not { } recovery
            || !invocation.IsOperatorSurface || serviceProvider.GetService<ISessionRecoveryCheckpointStore>() is not { } store)
            return SessionRecoveryRequests.Conflict;

        var found = await store.FindAsync(session, recovery, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        if (found.Value is not { } checkpoint || invocation.Campaign?.CampaignId != checkpoint.Scope.CampaignId)
            return SessionRecoveryRequests.Conflict;

        string? workspace = ResolveSanctumWorkspaceRoot(request, null);

        if (workspace != checkpoint.Scope.Workspace || GetRecoveryWorkspaceIdentity(workspace) != checkpoint.Scope.WorkspaceIdentity)
            return new Error(ErrorCodes.Hub.SessionRecoveryConflict, "The saved repair workspace is unavailable or has changed.")
            {
                FailureEvidence = new(TurnFailurePhase.ToolAuthorization, TurnEffectState.NotDispatched, TurnRecoveryAction.UseAuthorizedScope)
                { RecoveryCheckpoint = checkpoint.ToPublic() },
            };

        if (checkpoint is { Kind: SessionRecoveryKind.ReconciliationRequired, InvocationId: { } receipt })
        {
            var result = await serviceProvider.GetRequiredService<IToolInvocationStore>()
                .ReadAsync(checkpoint.SessionId, receipt, ct).ConfigureAwait(false);

            if (result.IsFailure) return result.Error;

            if (!IsProvenRecoveryBoundary(checkpoint, result.Value))
                return new Error(ErrorCodes.Hub.ToolReconciliationRequired, "The previous tool effect must be reconciled before continuing this repair.")
                {
                    FailureEvidence = new(TurnFailurePhase.ToolExecution, TurnEffectState.Unknown, TurnRecoveryAction.ReconcileTool)
                    { InvocationId = receipt, RecoveryCheckpoint = checkpoint.ToPublic() },
                };
        }

        var configuration = settings.Value;

        ProviderSettings? provider;

        string model;

        if (request.ConfirmRecoveryRouteChange)
        {
            if (string.IsNullOrWhiteSpace(request.Model)
                || !ProviderResolver.TryResolveProviderForModel(configuration, request.Model, out provider, out model))
                return SessionRecoveryRequests.RouteChanged(checkpoint.ToPublic());
        }
        else
        {
            if (!ProviderResolver.TryResolveProviderByName(configuration, checkpoint.Scope.Provider, out var saved)
                || !ProviderResolver.TryResolveProviderForModel(configuration with { Providers = [saved!] }, request.Model,
                    out provider, out model)
                || GetRecoveryRouteDigest(provider!, model) != checkpoint.Scope.ProviderRouteDigest)
                return SessionRecoveryRequests.RouteChanged(checkpoint.ToPublic());
        }

        return (checkpoint, provider!, model);
    }

    private Task<Result<ToolInvocationTurnProgress>> ReadSessionRecoveryProgressAsync(SessionTurnClaimLease owner) =>
        serviceProvider.GetRequiredService<IToolInvocationStore>().ReadTurnProgressAsync(owner.Claim.SessionId,
            owner.FutureAssistantEntryId, CancellationToken.None);

    private async Task<Result<SessionRecoveryCheckpointRecord>> CheckSessionRecoveryBoundaryAsync(
        PingRequest request, ArcanumInvocationContext invocation, ChatClientLease lease, CancellationToken ct)
    {
        var selected = await ResolveRecoveryRouteAsync(request, invocation, ct).ConfigureAwait(false);

        if (selected.IsFailure) return selected.Error;

        (SessionRecoveryCheckpointRecord checkpoint, ProviderSettings provider, string model) = selected.Value;

        if (GetRecoveryRouteDigest(lease.Provider, lease.ResolvedModel) != GetRecoveryRouteDigest(provider, model))
            return SessionRecoveryRequests.RouteChanged(checkpoint.ToPublic());

        return checkpoint;
    }

    private async Task<Result> RetainRecoveryQuestionAsync(Session? thread, Guid entryId, CancellationToken ct)
    {
        if (thread is null) return SessionRecoveryRequests.Conflict;

        ISessionRepository entries = serviceProvider.GetRequiredService<ISessionRepository>();

        Entry? call = thread.Entries.FirstOrDefault(entry => entry.Id == entryId)
            ?? await entries.GetEntryAsync(thread.Id, entryId, ct).ConfigureAwait(false);

        if (call is null || call.ToolName != "ask_human" || call.Sequence == long.MaxValue) return SessionRecoveryRequests.Conflict;

        Entry? result = thread.Entries.FirstOrDefault(entry => entry.Sequence == call.Sequence + 1)
            ?? (await entries.GetEntriesAfterAsync(thread.Id, call.Sequence, 1, ct).ConfigureAwait(false)).SingleOrDefault();

        if (result is null || !TurnContextGuards.IsPersistedToolExchange(call, result, thread.Id)) return SessionRecoveryRequests.Conflict;

        if (!thread.Entries.Any(entry => entry.Id == call.Id)) thread.Entries.Add(call);

        if (!thread.Entries.Any(entry => entry.Id == result.Id)) thread.Entries.Add(result);

        return Result.Success();
    }

    private static bool IsProvenRecoveryBoundary(SessionRecoveryCheckpointRecord checkpoint, ToolInvocationEvidence? evidence) =>
        evidence?.AssistantEntryId == checkpoint.AssistantEntryId
        && (evidence is { State: ToolInvocationState.Prepared }
            or { State: ToolInvocationState.Completed, ResultEntryId: not null });

    private async Task<Result<SessionRecoveryCheckpoint>> SaveSessionRecoveryAsync(ISessionRecoveryCheckpointStore store,
        SessionTurnClaimLease owner, ChatClientLease lease, ArcanumInvocationContext invocation, string? workspace,
        SessionRecoveryKind kind, FunctionCallContent? function, string? question, int round, int index,
        Guid? invocationId, Guid? lastVerifiedInvocationId)
    {
        CovenantDigest? workspaceIdentity = GetRecoveryWorkspaceIdentity(workspace);

        if (workspace is not null && workspaceIdentity is null)
            return new Error(ErrorCodes.Hub.SessionRecoveryConflict, "The repair workspace could not be confirmed for continuation.");

        SessionRecoveryScope scope = new(lease.Provider.Name, lease.ResolvedModel, GetRecoveryRouteDigest(lease.Provider, lease.ResolvedModel),
            invocation.Campaign?.CampaignId, workspace, workspaceIdentity);

        Guid? questionEntryId = null;

        if (function is not null && question is not null)
        {
            string callId = toolExecutionPipeline.ResolveCallId(function);

            ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(new(
                owner.FutureAssistantEntryId.ToString("D"), callId, round, index, "ask_human"));

            string arguments = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(new FunctionCallContent(
                callId, "ask_human", new Dictionary<string, object?> { ["question"] = question }));

            MandatoryToolInteractionAppendResult append = await grimoireTurnWriter.PersistToolInvocationReceiptAsync(
                new(owner.Claim.SessionId, receipt, callId, "ask_human", arguments, RecoveryQuestionResult,
                    lease.ResolvedModel, owner.Claim.CreatedAtUtc), CancellationToken.None).ConfigureAwait(false);

            if (append.Outcome is not (MandatoryToolInteractionAppendOutcome.NewlyCommitted or MandatoryToolInteractionAppendOutcome.RecoveredCommitted))
                return new Error(ErrorCodes.Grimoire.WriteFailed, "The pending repair question could not be saved.");

            questionEntryId = receipt.CallEntryId;
        }

        var record = new SessionRecoveryCheckpointRecord(owner.Claim.ClaimId, owner.Claim.SessionId,
            owner.FutureAssistantEntryId, owner.Claim.ClaimId, kind, scope, questionEntryId, invocationId,
            lastVerifiedInvocationId, null);

        Result<SessionRecoveryCheckpointRecord> result = await store.SaveAsync(owner, record, CancellationToken.None).ConfigureAwait(false);

        return result.IsSuccess ? Result<SessionRecoveryCheckpoint>.Success(result.Value.ToPublic(question)) : result.Error;
    }
}
