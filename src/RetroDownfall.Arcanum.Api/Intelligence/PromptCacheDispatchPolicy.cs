using System.ClientModel.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Lease-owned SDK boundary: revalidate each physical attempt without changing retry policy.</summary>
internal sealed class PromptCacheDispatchPolicy : PipelinePolicy
{
    private readonly AsyncLocal<Scope?> _current = new();

    internal Scope Enter(Func<ValueTask<(Error Error, ModelCallRetryDirective RetryDirective)?>> validate)
    {
        Scope scope = new(this, _current.Value, validate);

        _current.Value = scope;

        return scope;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ValidateAsync(message).AsTask().GetAwaiter().GetResult();

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ValidateAsync(message).ConfigureAwait(false);

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private async ValueTask ValidateAsync(PipelineMessage message)
    {
        if (_current.Value is not { } scope) return;

        message.CancellationToken.ThrowIfCancellationRequested();

        if (await scope.Validate().ConfigureAwait(false) is { } refusal)
        {
            throw scope.Refuse(refusal.Error, refusal.RetryDirective);
        }
    }

    internal sealed class Scope(PromptCacheDispatchPolicy owner, Scope? previous,
        Func<ValueTask<(Error Error, ModelCallRetryDirective RetryDirective)?>> validate) : IDisposable
    {
        internal Func<ValueTask<(Error Error, ModelCallRetryDirective RetryDirective)?>> Validate { get; } = validate;

        internal PromptCacheDispatchException? Refusal { get; private set; }

        internal PromptCacheDispatchException Refuse(Error error, ModelCallRetryDirective directive) =>
            Refusal = new(error, directive);

        // The pinned SDK aggregates earlier connection failures with its terminal exception.
        // Recognize only this operation's exact issued refusal in that terminal position.
        internal bool IsTerminalRefusal(AggregateException exception) => Refusal is not null
            && exception.InnerExceptions.Count > 1 && ReferenceEquals(exception.InnerExceptions[^1], Refusal);

        public void Dispose() => owner._current.Value = previous;
    }
}

/// <summary>Private control transfer preserves the validator's typed refusal across SDK frames.</summary>
internal sealed class PromptCacheDispatchException(Error error, ModelCallRetryDirective retryDirective)
    : Exception("The bound provider request requires readmission.")
{
    internal Error Error { get; } = error;

    internal ModelCallRetryDirective RetryDirective { get; } = retryDirective;
}
