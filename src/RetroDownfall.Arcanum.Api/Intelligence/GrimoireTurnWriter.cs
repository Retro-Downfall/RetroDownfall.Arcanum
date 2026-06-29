using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Grimoire begin/finalize/discard and interrupt-cleanup side-effects shared by buffered and
/// streaming inference paths in <see cref="WizardIntelligenceProvider"/>.
/// </summary>
public sealed class GrimoireTurnWriter(
    IGrimoireRepository grimoire,
    SessionEventHub sessionEventHub,
    ILogger<GrimoireTurnWriter> logger)
{

    public sealed class TurnHandle
    {

        public Guid? AssistantEntryId { get; internal set; }

        public Guid? SessionId { get; internal set; }

        public bool IsFinalized { get; internal set; }

    }

    public async Task<TurnHandle> TryBeginBufferedAssistantReplyAsync(
        PingRequest request,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken)
    {

        return await TryBeginAssistantReplyCoreAsync(
            request,
            prompt,
            targetModel,
            cancellationToken,
            "Grimoire could not begin assistant reply for model {ModelName}.").ConfigureAwait(false);

    }

    public async Task<TurnHandle> TryBeginStreamedAssistantReplyAsync(
        PingRequest request,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken)
    {

        return await TryBeginAssistantReplyCoreAsync(
            request,
            prompt,
            targetModel,
            cancellationToken,
            "Grimoire could not start streamed session persistence for model {ModelName}.").ConfigureAwait(false);

    }

    public async Task TryFinalizeBufferedAssistantEntryAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken)
    {

        await TryFinalizeAssistantEntryCoreAsync(
            handle,
            finalText,
            targetModel,
            cancellationToken,
            "Grimoire could not finalize assistant entry for model {ModelName}.").ConfigureAwait(false);

    }

    public async Task TryFinalizeStreamedAssistantEntryAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken)
    {

        await TryFinalizeAssistantEntryCoreAsync(
            handle,
            finalText,
            targetModel,
            cancellationToken,
            "Grimoire could not finalize streamed assistant entry for model {ModelName}.").ConfigureAwait(false);

    }

    public async Task ResolveInterruptedAsync(
        TurnHandle handle,
        string? streamedContent,
        CancellationToken cancellationToken)
    {

        if (handle.AssistantEntryId is not { } entryId)
        {

            return;

        }

        try
        {

            if (!string.IsNullOrEmpty(streamedContent))
            {

                await grimoire
                    .FinalizeAssistantEntryAsync(entryId, streamedContent, cancellationToken)
                    .ConfigureAwait(false);

            }
            else
            {

                await grimoire
                    .DiscardAssistantEntryAsync(entryId, cancellationToken)
                    .ConfigureAwait(false);

            }

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Grimoire could not resolve interrupted assistant entry {AssistantEntryId}.",
                entryId);

        }

    }

    public async Task ResolveInterruptedAndMarkFinalizedAsync(
        TurnHandle handle,
        string? streamedContent,
        CancellationToken cancellationToken)
    {

        if (handle.IsFinalized)
        {

            return;

        }

        await ResolveInterruptedAsync(handle, streamedContent, cancellationToken).ConfigureAwait(false);

        handle.IsFinalized = true;

    }

    public async Task TryResolveInterruptedOnStreamExitAsync(
        TurnHandle handle,
        string? streamedContent)
    {

        if (handle.IsFinalized || handle.AssistantEntryId is null)
        {

            return;

        }

        try
        {

            await ResolveInterruptedAsync(handle, streamedContent, CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Grimoire could not resolve interrupted streamed assistant entry during cleanup.");

        }

    }

    public async Task TryAppendToolInteractionAsync(
        Guid? sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken)
    {

        if (!sessionId.HasValue)
        {

            return;

        }

        try
        {

            await grimoire
                .AppendToolInteractionAsync(
                    sessionId.Value,
                    toolName,
                    arguments,
                    result,
                    modelUsed,
                    cancellationToken)
                .ConfigureAwait(false);

            await PublishLatestSavedEntriesAsync(sessionId.Value, 2, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Grimoire could not append tool interaction for tool {ToolName}.", toolName);

        }

    }

    private async Task<TurnHandle> TryBeginAssistantReplyCoreAsync(
        PingRequest request,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken,
        string beginFailureLogMessage)
    {

        TurnHandle handle = new();

        if (IsStateless(request))
        {

            return handle;

        }

        try
        {

            (Guid sid, Guid aid) = await grimoire
                .BeginAssistantReplyAsync(request.SessionId, prompt, targetModel, cancellationToken)
                .ConfigureAwait(false);

            handle.SessionId = sid;

            handle.AssistantEntryId = aid;

            await PublishLatestSavedEntriesAsync(sid, 2, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, beginFailureLogMessage, targetModel);

        }

        return handle;

    }

    private async Task TryFinalizeAssistantEntryCoreAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken,
        string finalizeFailureLogMessage)
    {

        if (handle.AssistantEntryId is not { } finalizeId)
        {

            return;

        }

        try
        {

            await grimoire
                .FinalizeAssistantEntryAsync(finalizeId, finalText, cancellationToken)
                .ConfigureAwait(false);

            handle.IsFinalized = true;

            if (handle.SessionId is { } publishSessionId)
            {

                await PublishSavedEntryByIdAsync(publishSessionId, finalizeId, cancellationToken)
                    .ConfigureAwait(false);

            }

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, finalizeFailureLogMessage, targetModel);

        }

    }

    private async Task PublishLatestSavedEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken)
    {

        List<GrimoireEntryDto>? entries = await grimoire
            .GetRecentSessionEntriesAsync(sessionId, takeLast, cancellationToken)
            .ConfigureAwait(false);

        if (entries is null || entries.Count == 0)
        {

            return;

        }

        foreach (GrimoireEntryDto dto in entries)
        {

            sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));

        }

    }

    private async Task PublishSavedEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken)
    {

        GrimoireEntryDto? dto = await grimoire
            .GetEntryByIdAsync(sessionId, entryId, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {

            return;

        }

        sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));

    }

    private static Entry ToEntry(GrimoireEntryDto dto, Guid sessionId) =>
        new()
        {

            Id = dto.Id,

            SessionId = sessionId,

            Role = dto.Role,

            Content = dto.Content,

            ModelUsed = dto.ModelUsed,

            CreatedAt = dto.CreatedAt,

        };

    private static bool IsStateless(PingRequest request) =>
        request.StatelessMessages is { Count: > 0 };

}
