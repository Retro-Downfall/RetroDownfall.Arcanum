using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ApprenticeService
{
    private sealed partial class GuildEngineFrame
    {
        private readonly Lock _gate = new();

        internal GuildEngineFrame(ApprenticeService owner, GuildLocalExecutionBinding binding, GuildLocalExecutionInput input,
            GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime, CancellationTokenSource cancellation, long generation)
        {
            Owner = owner;

            Binding = binding;

            Input = input;

            Execution = execution;

            Runtime = runtime;

            Cancellation = cancellation;

            Generation = generation;

            LifetimeToken = cancellation.Token;

            execution.ChildWait = SuspendForChildAsync;

            Adapter = new GuildUnitIntelligence(this);
        }

        internal ApprenticeService Owner { get; }

        internal long Generation { get; }

        internal CancellationToken LifetimeToken { get; }

        internal GuildLocalExecutionBinding Binding { get; }

        internal GuildLocalExecutionInput Input { get; }

        internal GuildLocalInferenceExecution Execution { get; set; }

        internal IGuildLocalInferenceRuntime Runtime { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal IArcanumIntelligenceProvider Adapter { get; }

        internal Error? Failure { get; private set; }

        internal ProviderCallSensitivity? Sensitivity { get; private set; }

        internal PromptTurnResult? LastResult { get; private set; }

        internal void MergeSensitivity(ProviderCallSensitivity sensitivity)
        {
            lock (_gate) Sensitivity = ProviderCallSensitivities.Merge(Sensitivity, sensitivity);
        }

        internal void Observe(PromptTurnResult result)
        {
            lock (_gate)
            {
                if (result.Sensitivity is { } sensitivity) Sensitivity = ProviderCallSensitivities.Merge(Sensitivity, sensitivity);

                LastResult = result;
            }
        }

        internal void Refuse(Error error)
        {
            lock (_gate) Failure ??= error;

            Cancellation.Cancel();
        }
    }

    private sealed class GuildUnitIntelligence(GuildEngineFrame frame) : IArcanumIntelligenceProvider
    {
        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken, InferenceAuditContext? auditContext = null)
        {
            GuildProviderActivity? previous = GuildActivity.Value;

            GuildProviderActivity? activity = null;

            try
            {
                activity = await frame.BeginActivityAsync(cancellationToken).ConfigureAwait(false);

                GuildActivity.Value = activity;

                cancellationToken.ThrowIfCancellationRequested();

                var result = await frame.Runtime.ExecuteTurnAsync(frame.Execution, frame.Input,
                    new(request.Prompt, DisableAllTools: request.DisableAllTools), cancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    frame.Refuse(result.Error);

                    throw new GuildUnitFailureException();
                }

                if (result.Value.ToolCalls is { Count: > 0 })
                {
                    frame.Refuse(new(ErrorCodes.Guild.AuthorityUnavailable, "The local unit requested an unavailable tool boundary."));

                    throw new GuildUnitFailureException();
                }

                frame.Observe(result.Value);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                frame.Refuse(frame.Failure ?? new(ErrorCodes.Guild.ReconciliationRequired, "The local unit did not establish a reliable provider result."));

                throw new GuildUnitFailureException();
            }
            finally
            {
                GuildActivity.Value = previous;

                if (activity is not null) await frame.EndActivityAsync(activity).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(PingRequest request, ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken, InferenceAuditContext? auditContext = null)
        {
            var result = await ExecutePromptAsync(request, invocationContext, cancellationToken, auditContext).ConfigureAwait(false);

            yield return new(IntelligenceEventType.Result, result.Value.Text, Usage: result.Value.Usage,
                FinishReason: result.Value.FinishReason);
        }
    }

    private sealed class GuildUnitFailureException() : Exception("The qualified Guild unit stopped; consult durable effect evidence.");
}
