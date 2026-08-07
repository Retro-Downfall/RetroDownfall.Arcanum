using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Logical-run producer. Owns the bounded <see cref="TurnEventEmitter"/> channel and drives
/// the single mode-parameterized tool-loop via <see cref="ITurnPipelineRunner"/>.
/// </summary>
internal sealed class TurnEngine(
    ITurnPipelineRunner pipelineRunner,
    ILogger<TurnEngine>? logger = null) : ITurnEventSource
{

    private readonly ITurnPipelineRunner _pipelineRunner =
        pipelineRunner ?? throw new ArgumentNullException(nameof(pipelineRunner));

    private readonly ILogger<TurnEngine>? _logger = logger;

    public IAsyncEnumerable<TurnEvent> RunTurnAsync(
        TurnExecutionRequest request,
        CancellationToken executionToken) =>
        RunTurnCoreAsync(request, auditContext: null, executionToken);

    /// <summary>
    /// Runs a turn with an explicit audit context (Master facade / tests).
    /// </summary>
    public IAsyncEnumerable<TurnEvent> RunTurnAsync(
        TurnExecutionRequest request,
        InferenceAuditContext? auditContext,
        CancellationToken executionToken) =>
        RunTurnCoreAsync(request, auditContext, executionToken);

    private async IAsyncEnumerable<TurnEvent> RunTurnCoreAsync(
        TurnExecutionRequest request,
        InferenceAuditContext? auditContext,
        [EnumeratorCancellation] CancellationToken executionToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid runId = Guid.NewGuid();

        // Producer-owned linked source so an abandoned enumeration (the production client-disconnect
        // path) can unwind the pipeline instead of leaving it running against a disposed emitter.
        using CancellationTokenSource producerCts =
            CancellationTokenSource.CreateLinkedTokenSource(executionToken);

        await using TurnEventEmitter emitter = new(runId);

        Task producer = ProduceAsync(request, emitter, producerCts.Token, auditContext);

        try
        {
            await foreach (TurnEvent evt in emitter.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return evt;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            // Runs before the emitter's DisposeAsync (declared in the enclosing scope), including
            // when the consumer abandons the iterator at a `yield return`. Completing the channel
            // first releases a producer parked on a full bounded write; cancelling then awaiting it
            // guarantees the pipeline is finished — and its Task observed — before the emit gate is
            // disposed under it.
            emitter.CompleteWithoutTerminal();

            await producerCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Abandoned run — the pipeline unwound on the producer token as instructed.
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    "Turn {RunId} producer faulted while unwinding; exception type {ExceptionType}.",
                    runId,
                    ex.GetType().FullName);
            }
        }

        executionToken.ThrowIfCancellationRequested();
    }

    private async Task ProduceAsync(
        TurnExecutionRequest request,
        TurnEventEmitter emitter,
        CancellationToken executionToken,
        InferenceAuditContext? auditContext = null)
    {
        try
        {
            if (request.ResponseMode == TurnResponseMode.Buffered)
            {
                await _pipelineRunner
                    .RunBufferedIntoEmitterAsync(request, emitter, auditContext, executionToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _pipelineRunner
                    .RunStreamingIntoEmitterAsync(request, emitter, auditContext, executionToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            if (!emitter.TerminalEmitted)
            {
                await emitter
                    .EmitAsync(
                        new RunAbandoned(
                            emitter.NextCorrelation(),
                            new Error(ErrorCodes.Hub.Error, "Turn cancelled."),
                            TurnTerminationReason.Cancelled,
                            Usage: null,
                            Warnings: [],
                            Interrupted: true,
                            PartialText: null),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                "Turn {RunId} failed during {ResponseMode} pipeline execution; exception type {ExceptionType}.",
                emitter.RunId,
                request.ResponseMode,
                ex.GetType().FullName);

            if (!emitter.TerminalEmitted)
            {
                await emitter
                    .EmitAsync(
                        new RunFailed(
                            emitter.NextCorrelation(),
                            new Error(
                                ErrorCodes.Hub.Error,
                                PublicInferenceErrorMessages.NativeGenericFailure),
                            TurnTerminationReason.ProviderFailure,
                            Usage: null,
                            Warnings: [],
                            Interrupted: true,
                            PartialText: null),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            emitter.CompleteWithoutTerminal();
        }
    }

}
