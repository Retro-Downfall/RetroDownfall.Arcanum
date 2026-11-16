using System.ClientModel.Primitives;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Last synchronous validation before the SDK enters its transport; never waits for admission.</summary>
internal sealed class ProviderWorkDispatchPolicy(ProviderWorkClientIdentity identity, IOptionsMonitor<ArcanumSettings>? settings) : PipelinePolicy
{
    private readonly AsyncLocal<Scope?> _current = new();

    internal Scope CreateCall() => new(this, null);

    internal Scope Enter()
    {
        Scope scope = new(this, _current.Value);

        _current.Value = scope;

        return scope;
    }

    internal IDisposable Enter(Scope scope)
    {
        Scope? previous = _current.Value;

        _current.Value = scope;

        return new Restore(this, previous);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Validate(message);

        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Validate(message);

        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Validate(PipelineMessage message)
    {
        message.CancellationToken.ThrowIfCancellationRequested();

        if (!identity.IsCurrent(settings?.CurrentValue))
        {
            throw _current.Value?.Refuse() ?? new RouteRefusal();
        }

        // Once the next transport stage has been entered, a later refusal cannot establish no-send.
        _current.Value?.EnterTransport();
    }

    private sealed class RouteRefusal() : Exception("The configured provider route changed before transport.");

    internal sealed class Scope(ProviderWorkDispatchPolicy owner, Scope? previous) : IDisposable
    {
        private Exception? _refusal;

        private bool _transportEntered;

        internal Exception Refuse() => _refusal = new RouteRefusal();

        internal void EnterTransport() => _transportEntered = true;

        internal bool IsNoSend(Exception exception) => !_transportEntered && ReferenceEquals(_refusal, exception);

        public void Dispose() => owner._current.Value = previous;
    }

    private sealed class Restore(ProviderWorkDispatchPolicy owner, Scope? previous) : IDisposable
    {
        public void Dispose() => owner._current.Value = previous;
    }
}

