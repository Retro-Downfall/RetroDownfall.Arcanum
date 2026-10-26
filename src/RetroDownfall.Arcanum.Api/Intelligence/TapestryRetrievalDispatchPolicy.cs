using System.ClientModel.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Each native physical send consumes only the current executor attempt, even with caching off.</summary>
internal sealed class TapestryRetrievalDispatchPolicy(string provider, string model) : PipelinePolicy
{
    private static readonly AsyncLocal<Scope?> Active = new();

    internal static Scope Enter(Func<string, string, CancellationToken, Task<Result>> validate)
    {
        Scope scope = new(Active.Value, validate);

        Active.Value = scope;

        return scope;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ValidateAsync(message.CancellationToken).GetAwaiter().GetResult();

        Active.Value?.ReachedProcessNext();

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ValidateAsync(message.CancellationToken).ConfigureAwait(false);

        Active.Value?.ReachedProcessNext();

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private async Task ValidateAsync(CancellationToken token)
    {
        if (Active.Value is not { } scope) return;

        token.ThrowIfCancellationRequested();

        Result current = await scope.Validate(provider, model, token).ConfigureAwait(false);

        if (current.IsFailure) throw scope.Refuse(current.Error);
    }

    internal sealed class Scope(Scope? previous, Func<string, string, CancellationToken, Task<Result>> validate) : IDisposable
    {
        internal Func<string, string, CancellationToken, Task<Result>> Validate { get; } = validate;

        internal PromptCacheDispatchException? Refusal { get; private set; }

        private bool _reachedProcessNext;

        internal void ReachedProcessNext() => _reachedProcessNext = true;

        internal PromptCacheDispatchException Refuse(Error error) => Refusal = new(error, ModelCallRetryDirective.None)
        {
            HasUncertainProviderEffect = _reachedProcessNext,
        };

        internal bool IsTerminalRefusal(AggregateException error) => Refusal is not null
            && error.InnerExceptions.Count > 1 && ReferenceEquals(error.InnerExceptions[^1], Refusal);

        public void Dispose() => Active.Value = previous;
    }
}
