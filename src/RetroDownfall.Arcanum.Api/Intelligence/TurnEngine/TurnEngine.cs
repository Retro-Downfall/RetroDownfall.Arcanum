using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Logical-run producer. Owns the bounded <see cref="TurnEventEmitter"/> channel and drives
/// the single mode-parameterized tool-loop via <see cref="ITurnPipelineRunner"/>.
/// </summary>
internal sealed class TurnEngine(ITurnPipelineRunner pipelineRunner) : ITurnEventSource
{

    private readonly ITurnPipelineRunner _pipelineRunner =
        pipelineRunner ?? throw new ArgumentNullException(nameof(pipelineRunner));

    public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
        TurnExecutionRequest request,
        [EnumeratorCancellation] CancellationToken executionToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid runId = Guid.NewGuid();

        await using TurnEventEmitter emitter = new(runId);

        Task producer = ProduceAsync(request, emitter, executionToken);

        await foreach (TurnEvent evt in emitter.Reader.ReadAllAsync(executionToken).ConfigureAwait(false))
        {
            yield return evt;
        }

        await producer.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a turn with an explicit audit context (Wizard facade / tests).
    /// </summary>
    public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
        TurnExecutionRequest request,
        InferenceAuditContext? auditContext,
        [EnumeratorCancellation] CancellationToken executionToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid runId = Guid.NewGuid();

        await using TurnEventEmitter emitter = new(runId);

        Task producer = ProduceAsync(request, emitter, executionToken, auditContext);

        await foreach (TurnEvent evt in emitter.Reader.ReadAllAsync(executionToken).ConfigureAwait(false))
        {
            yield return evt;
        }

        await producer.ConfigureAwait(false);
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
            if (!emitter.TerminalEmitted)
            {
                await emitter
                    .EmitAsync(
                        new RunFailed(
                            emitter.NextCorrelation(),
                            new Error(ErrorCodes.Hub.Error, ex.Message),
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
