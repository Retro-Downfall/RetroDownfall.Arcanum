using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>The response owns the genuine borrowed Turn, including serialization after the runner returns.</summary>
internal sealed class MemoryEvaluationRequestScope(
    HttpContext httpContext,
    CovenantAuthorityFeature original,
    IOperatorAuthorityContextIssuer issuer,
    ICovenantOperationGate gate,
    ArcanumInvocationContext invocation,
    CovenantTurnLease lease) : IAsyncDisposable
{
    private int _disposed;

    public ArcanumInvocationContext Invocation { get; } = invocation;

    public CovenantTurnLease Lease { get; } = lease;

    public CancellationToken Revocation => Lease.Revocation;

    internal CancellationToken RequestAborted => httpContext.RequestAborted;

    internal Guid InstallationId => Guid.Parse(original.Context.InstallationIdentity);

    public async ValueTask<Result> RevalidateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _disposed) != 0 || httpContext.RequestAborted.IsCancellationRequested
            || !ReferenceEquals(CovenantRequestFeatures.Authority(httpContext), original)
            || original.Requirement != CovenantAuthorityRequirement.ProtectedRead
            || issuer.Revalidate(original.Context).IsFailure || !Invocation.CanReadCovenant
            || Invocation.ReadAuthorityEpoch?.Matches(Lease.Snapshot) != true)
            return new Error(ErrorCodes.MemoryEvaluation.StaleAuthority, "The request's original protected read authority is no longer live.");

        return await gate.ValidateTurnLeaseAsync(Lease, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await Lease.DisposeAsync().ConfigureAwait(false);
    }
}
