using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Sole semantic consumer of <see cref="TurnEvent"/>s. Applies exactly one projection per request.
/// Does not serialize HTTP — streaming projections write typed output into transport channels.
/// Production <c>/v1</c> currently selects the native intelligence-event projection and performs a
/// parity-tested OpenAI reshape in the endpoint writer; <see cref="OpenAiSseProjection"/> remains a
/// separate semantic projection path rather than the instance used by that route.
/// </summary>
internal sealed class TurnExecutionCoordinator(ITurnEventSource turnEventSource) : ITurnExecutionFacade
{

    private readonly ITurnEventSource _turnEventSource =
        turnEventSource ?? throw new ArgumentNullException(nameof(turnEventSource));

    public Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        TurnExecutionRequest turnRequest = new(
            request,
            TurnResponseMode.Buffered,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: false,
            hasIdempotencyKey,
            AccountingHandle: TurnAccountingAmbient.Current);

        return ExecuteBufferedCoreAsync(turnRequest, executionToken, auditContext);
    }

    public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        TurnExecutionRequest turnRequest = new(
            request,
            TurnResponseMode.Streaming,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: true,
            hasIdempotencyKey,
            AccountingHandle: TurnAccountingAmbient.Current);

        return ExecuteIntelligenceStreamCoreAsync(turnRequest, executionToken, auditContext);
    }

    public async Task<Result<PromptTurnResult>> ExecuteBufferedCoreAsync(
        TurnExecutionRequest request,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ResponseMode != TurnResponseMode.Buffered)
        {
            throw new ArgumentException(
                "Buffered projection requires TurnResponseMode.Buffered.",
                nameof(request));
        }

        BufferedTurnProjection projection = new();

        IAsyncEnumerable<TurnEvent> events = _turnEventSource is TurnEngine engine
            ? engine.RunTurnAsync(request, auditContext, executionToken)
            : _turnEventSource.RunTurnAsync(request, executionToken);

        await foreach (TurnEvent evt in events.ConfigureAwait(false))
        {
            projection.Apply(evt);
        }

        return projection.ToResult();
    }

    public async IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamCoreAsync(
        TurnExecutionRequest request,
        [EnumeratorCancellation] CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ResponseMode != TurnResponseMode.Streaming)
        {
            throw new ArgumentException(
                "Intelligence-event projection requires TurnResponseMode.Streaming.",
                nameof(request));
        }

        Channel<IntelligenceEvent> transport = Channel.CreateBounded<IntelligenceEvent>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        IntelligenceEventProjection projection = new(transport.Writer);

        Task produce = ProjectStreamingAsync(
            request,
            projection,
            transport.Writer,
            executionToken,
            auditContext);

        await foreach (IntelligenceEvent frame in transport.Reader.ReadAllAsync(executionToken).ConfigureAwait(false))
        {
            yield return frame;
        }

        await produce.ConfigureAwait(false);
    }

    public IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseAsync(
        PingRequest request,
        bool hasIdempotencyKey,
        string completionId,
        string model,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        TurnExecutionRequest turnRequest = new(
            request,
            TurnResponseMode.Streaming,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: true,
            hasIdempotencyKey,
            AccountingHandle: TurnAccountingAmbient.Current);

        return ExecuteOpenAiSseCoreAsync(turnRequest, completionId, model, executionToken, auditContext);
    }

    public async IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseCoreAsync(
        TurnExecutionRequest request,
        string completionId,
        string model,
        [EnumeratorCancellation] CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ResponseMode != TurnResponseMode.Streaming)
        {
            throw new ArgumentException(
                "OpenAI SSE projection requires TurnResponseMode.Streaming.",
                nameof(request));
        }

        Channel<OpenAiChatChunk> transport = Channel.CreateBounded<OpenAiChatChunk>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        OpenAiSseProjection projection = new(transport.Writer, completionId, model);

        Task produce = ProjectOpenAiAsync(
            request,
            projection,
            transport.Writer,
            executionToken,
            auditContext);

        await foreach (OpenAiChatChunk chunk in transport.Reader.ReadAllAsync(executionToken).ConfigureAwait(false))
        {
            yield return chunk;
        }

        await produce.ConfigureAwait(false);
    }

    private async Task ProjectStreamingAsync(
        TurnExecutionRequest request,
        IntelligenceEventProjection projection,
        ChannelWriter<IntelligenceEvent> writer,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext)
    {
        try
        {
            IAsyncEnumerable<TurnEvent> events = _turnEventSource is TurnEngine engine
                ? engine.RunTurnAsync(request, auditContext, executionToken)
                : _turnEventSource.RunTurnAsync(request, executionToken);

            await foreach (TurnEvent evt in events.ConfigureAwait(false))
            {
                await projection.ApplyAsync(evt, executionToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (writer.TryComplete(exception))
        {
            // The channel propagates producer failures to its reader.
        }
        finally
        {
            _ = writer.TryComplete();
        }
    }

    private async Task ProjectOpenAiAsync(
        TurnExecutionRequest request,
        OpenAiSseProjection projection,
        ChannelWriter<OpenAiChatChunk> writer,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext)
    {
        try
        {
            IAsyncEnumerable<TurnEvent> events = _turnEventSource is TurnEngine engine
                ? engine.RunTurnAsync(request, auditContext, executionToken)
                : _turnEventSource.RunTurnAsync(request, executionToken);

            await foreach (TurnEvent evt in events.ConfigureAwait(false))
            {
                await projection.ApplyAsync(evt, executionToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (writer.TryComplete(exception))
        {
            // The channel propagates producer failures to its reader.
        }
        finally
        {
            _ = writer.TryComplete();
        }
    }

}
