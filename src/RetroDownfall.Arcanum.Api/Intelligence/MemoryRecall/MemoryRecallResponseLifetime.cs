using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryRecall;

internal sealed class MemoryRecallResponseLifetime(ICovenantAuthoritySnapshotProvider? authority = null) : IAsyncDisposable
{
    private readonly object _sync = new();

    private ArcanumInvocationContext? _invocation;

    private CovenantTurnScope? _retained;

    private bool _disposed;

    internal void Bind(ArcanumInvocationContext invocation)
    {
        lock (_sync)
        {
            if (_disposed || _invocation is not null)
            {
                throw new InvalidOperationException("A recall response may bind one original invocation.");
            }

            _invocation = invocation;
        }
    }

    internal bool TryRetain(ArcanumInvocationContext invocation, CovenantTurnScope scope)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(invocation, _invocation)
                || (scope.Plan is null && scope.DerivedSensitivity?.Level is not Core.Covenant.ContentSensitivity.CovenantDerived)
                || _retained is not null)
            {
                return false;
            }

            _retained = scope;

            return true;
        }
    }

    internal IResult Wrap(IResult result) => new ResponseResult(this, result);

    private async ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken)
    {
        CovenantTurnScope? scope;

        ArcanumInvocationContext? invocation;

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            scope = _retained;

            invocation = _invocation;
        }

        return scope is null
            || (invocation?.ReadAuthorityEpoch?.Matches(authority?.Current) is true
                && (await scope.RevalidateAsync(cancellationToken).ConfigureAwait(false)).IsSuccess
                && invocation.ReadAuthorityEpoch.Matches(authority?.Current));
    }

    public async ValueTask DisposeAsync()
    {
        CovenantTurnScope? scope;

        lock (_sync)
        {
            _disposed = true;

            scope = _retained;

            _retained = null;
        }

        if (scope is not null)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ResponseResult(MemoryRecallResponseLifetime owner, IResult inner) : IResult
    {
        private int _executed;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (Interlocked.Exchange(ref _executed, 1) != 0)
            {
                throw new InvalidOperationException("A recall response may execute once.");
            }

            try
            {
                if (owner._retained is not null && !httpContext.Response.HasStarted)
                {
                    CovenantProtectedResponseHeaders.Apply(httpContext.Response);
                }

                if (!httpContext.Response.HasStarted
                    && !await owner.RevalidateAsync(httpContext.RequestAborted).ConfigureAwait(false))
                {
                    await CovenantAuthorityRefusal.Stale(httpContext).ExecuteAsync(httpContext).ConfigureAwait(false);

                    return;
                }

                await inner.ExecuteAsync(httpContext).ConfigureAwait(false);
            }
            finally
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
