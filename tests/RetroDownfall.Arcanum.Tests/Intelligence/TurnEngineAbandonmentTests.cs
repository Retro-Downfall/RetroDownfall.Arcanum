using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A streaming client disconnect abandons the TurnEngine iterator mid-run (the consumer stops
/// draining and the projection's write throws). The engine must unwind and observe its producer
/// before the shared <see cref="TurnEventEmitter"/> is disposed under it — otherwise the pipeline
/// keeps emitting into a disposed emit gate and the run ends as a faulted, unobserved Task.
/// </summary>
public sealed class TurnEngineAbandonmentTests
{

    [Fact]
    public async Task RunTurnAsync_abandoned_by_its_consumer_unwinds_and_observes_the_producer()
    {

        AbandonmentPipelineRunner runner = new();

        TurnEngine engine = new(runner);

        IAsyncEnumerator<TurnEvent> events = engine
            .RunTurnAsync(CreateTurnRequest(), CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        // The consumer walks away at a `yield return`, exactly as ProjectStreamingAsync does when
        // the transport channel write is cancelled by a dead socket.
        await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            runner.Finished.IsCompletedSuccessfully,
            "The producer must be cancelled and awaited before the iterator finishes disposing.");

        Assert.Null(runner.TerminalEmissionFailure);

    }

    private static TurnExecutionRequest CreateTurnRequest() =>
        new(
            new PingRequest("abandonment"),
            TurnResponseMode.Streaming,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: true,
            HasIdempotencyKey: false,
            AccountingHandle: null);

    private sealed class AbandonmentPipelineRunner : ITurnPipelineRunner
    {

        private readonly TaskCompletionSource _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Finished => _finished.Task;

        public Exception? TerminalEmissionFailure { get; private set; }

        public Task RunBufferedIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            RunAsync(emitter, cancellationToken);

        public Task RunStreamingIntoEmitterAsync(
            TurnExecutionRequest request,
            TurnEventEmitter emitter,
            InferenceAuditContext? auditContext,
            CancellationToken cancellationToken) =>
            RunAsync(emitter, cancellationToken);

        private async Task RunAsync(TurnEventEmitter emitter, CancellationToken cancellationToken)
        {

            try
            {

                await emitter
                    .EmitAsync(new TurnStatusChanged(emitter.NextCorrelation(), "working"), cancellationToken)
                    .ConfigureAwait(false);

                // The Wizard pipeline keeps running after the consumer leaves; only the producer
                // token can stop it.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

                try
                {

                    // Mirrors ProduceAsync's terminal emission from inside its cancellation catch.
                    await emitter
                        .EmitAsync(
                            new TurnStatusChanged(emitter.NextCorrelation(), "abandoned"),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                }
                catch (Exception ex)
                {

                    TerminalEmissionFailure = ex;

                }

            }
            finally
            {

                _ = _finished.TrySetResult();

            }

        }

    }

}
