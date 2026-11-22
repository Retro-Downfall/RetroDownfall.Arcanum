using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class SessionRecoveryRequests
{
    internal static Error Conflict => new(ErrorCodes.Hub.SessionRecoveryConflict,
        "This recovery checkpoint cannot be used for the current Session turn.");

    internal static Error RouteChanged(SessionRecoveryCheckpoint checkpoint) =>
        new Error(ErrorCodes.Hub.SessionRecoveryRouteChanged,
            "The saved provider and model are unavailable or changed. Restore the saved route or explicitly choose a model and confirm the route change.")
        {
            FailureEvidence = new(TurnFailurePhase.ProviderRequest, TurnEffectState.NotDispatched, TurnRecoveryAction.CheckProvider)
            {
                RecoveryCheckpoint = checkpoint,
            },
        };

    internal static async Task<Result<PingRequest>> PrepareAsync(PingRequest request, HttpContext http, CancellationToken ct)
    {
        if (request.RecoveryCheckpointId is not { } checkpointId)
            return request.ConfirmRecoveryRouteChange ? Conflict : Result<PingRequest>.Success(request);

        if (request.SessionId is not { } sessionId || sessionId == Guid.Empty || checkpointId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Prompt) || !http.Request.Headers.ContainsKey("Idempotency-Key")
            || request.StatelessMessages is { Count: > 0 } || request.UnattendedMode || request.ForwardClientTools
            || request.ConfirmRecoveryRouteChange && string.IsNullOrWhiteSpace(request.Model))
            return Conflict;

        var store = http.RequestServices.GetRequiredService<ISessionRecoveryCheckpointStore>();

        Result<SessionRecoveryCheckpointRecord?> found = await store.FindAsync(sessionId, checkpointId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        if (found.Value is not { } checkpoint) return Conflict;

        if (request.CampaignId is { } namedCampaign && namedCampaign != checkpoint.Scope.CampaignId) return Conflict;

        return Result<PingRequest>.Success(request with
        {
            Model = string.IsNullOrWhiteSpace(request.Model) ? checkpoint.Scope.Model : request.Model,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? checkpoint.Scope.Workspace ?? string.Empty : request.WorkingDirectory,
        });
    }

    internal sealed record Status(SessionRecoveryCheckpoint? Checkpoint, TurnFailureEvidence[]? InterruptedToolEffects, Guid? NextAfter);

    internal static async Task<Result<Status>> ReadCurrentAsync(Guid sessionId, Guid? after, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetService<ISessionRecoveryCheckpointStore>();

        if (store is null) return new Status(null, null, null);

        var result = await store.ReadStatusAsync(sessionId, after, ct).ConfigureAwait(false);

        if (result.IsFailure) return result.Error;

        SessionRecoveryCheckpoint? checkpoint = result.Value.Checkpoint is { } current
            ? await ProjectAsync(current, services, ct).ConfigureAwait(false) : null;

        InterruptedToolEffectPage page = result.Value.InterruptedEffects;

        TurnFailureEvidence[]? evidence = page.Outstanding.Length == 0 && page.NextAfter is null ? null
            : [.. page.Outstanding.Select(static effect => new TurnFailureEvidence(
                TurnFailurePhase.ToolExecution, TurnEffectState.Unknown, TurnRecoveryAction.ReconcileTool,
                effect.Call, effect.Round) { InvocationId = effect.ReceiptId })];

        return new Status(checkpoint, evidence, page.NextAfter);
    }

    internal static async Task<SessionRecoveryCheckpoint> ProjectAsync(SessionRecoveryCheckpointRecord checkpoint, IServiceProvider services, CancellationToken ct)
    {
        if (checkpoint.QuestionEntryId is not { } id) return checkpoint.ToPublic();

        var entry = await services.GetRequiredService<ISessionRepository>().GetEntryAsync(checkpoint.SessionId, id, ct).ConfigureAwait(false);

        if (entry is not { ToolName: "ask_human", ToolArguments: { } arguments }) return checkpoint.ToPublic();

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("question", out JsonElement question) && question.ValueKind == JsonValueKind.String
                ? checkpoint.ToPublic(question.GetString()) : checkpoint.ToPublic();
        }
        catch (JsonException)
        {
            return checkpoint.ToPublic();
        }
    }
}
