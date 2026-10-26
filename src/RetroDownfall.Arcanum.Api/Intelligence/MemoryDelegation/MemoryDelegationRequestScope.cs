using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>The original authenticated request owns the borrowed Turn through complete protected serialization.</summary>
internal sealed class MemoryDelegationRequestScope(HttpContext http, CovenantAuthorityFeature original,
    IOperatorAuthorityContextIssuer issuer, ICovenantOperationGate gate, ArcanumInvocationContext invocation,
    CovenantTurnLease lease, MemoryDelegationRoot root, MemoryDelegationCoordinator coordinator) : IAsyncDisposable
{
    private int _disposed;

    internal PingRequest Request { get; private init; } = null!;

    internal MemoryDelegationRootBinding Binding { get; private set; } = null!;

    internal ArcanumInvocationContext Invocation { get; } = invocation;

    internal CovenantTurnLease Lease { get; } = lease;

    internal MemoryDelegationRoot Root { get; } = root;

    internal MemoryDelegationCoordinator Coordinator { get; } = coordinator;

    internal static async Task<MemoryDelegationOpenOutcome> OpenAsync(HttpContext http, MemoryDelegationRequest body, CancellationToken ct)
    {
        CovenantRequestFeatures.MarkProtectedResponse(http);

        var original = CovenantRequestFeatures.Authority(http);

        var services = http.RequestServices;

        var issuer = services.GetService<IOperatorAuthorityContextIssuer>();

        if (original is null || issuer is null || original.Requirement != CovenantAuthorityRequirement.ProtectedRead
            || !original.Context.Satisfies(CovenantAuthorityRequirement.ProtectedRead)
            || original.AuthorityEpoch != original.Context.AuthorityEpoch || issuer.Revalidate(original.Context).IsFailure
            || ArcanumInvocationContexts.ResolveContextPolicy(http) != CovenantContextPolicy.Default)
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Delegation requires the original protected read authority.");

        if (body?.Parent is not { } parent || !MemoryDelegationDigests.IsValid(body.Grant)
            || string.IsNullOrWhiteSpace(parent.Prompt) && parent.StatelessMessages is not { Count: > 0 })
            return new Error(ErrorCodes.MemoryDelegation.InvalidGrant, "A bounded parent request and exact grant are required.");

        var settings = services.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>().Value;

        Result preflight = PingRequestPreflightValidator.Validate(parent, settings, out PingRequestFailureDetails? diagnostic);

        if (preflight.IsFailure)
        {
            var caller = diagnostic is null ? null : MemoryDelegationCallerDiagnostic.ForPreflight(
                new MemoryDelegationCallerOrigin(original, issuer), diagnostic);

            return new MemoryDelegationOpenOutcome(Result<MemoryDelegationRequestScope>.Failure(preflight.Error), caller);
        }

        Result available = ValidateNativeDelegation(parent, settings, services.GetService<IProviderHealthTracker>());

        if (available.IsFailure) return available.Error;

        var coordinator = services.GetService<MemoryDelegationCoordinator>();

        var reader = services.GetService<IMemoryDelegationSourceReader>();

        var gate = services.GetService<ICovenantOperationGate>();

        if (coordinator is null || reader is null || gate is null || services.GetService<ISubagentRunner>() is null)
            return Unavailable();

        var resolved = await CanonicalPingResolution.ResolveTurnAsync(parent, http, ct).ConfigureAwait(false);

        if (resolved.IsFailure) return resolved.Error;

        if (resolved.Value.Campaign.CampaignId != body.Grant.Scope.CampaignId)
            return new Error(ErrorCodes.MemoryDelegation.InvalidGrant, "The grant scope must match the canonical parent scope.");

        var invocation = ArcanumInvocationContexts.ForTurn(http, resolved.Value.Request, resolved.Value.Campaign);

        var epoch = invocation.ReadAuthorityEpoch;

        if (!invocation.CanReadCovenant || epoch is null || epoch.InstallationIdentity != original.Context.InstallationIdentity
            || epoch.RuntimeAuthorityGeneration != original.Context.RuntimeAuthorityGeneration || epoch.AuthorityEpoch != original.AuthorityEpoch
            || !ReferenceEquals(CovenantRequestFeatures.Authority(http), original) || issuer.Revalidate(original.Context).IsFailure)
            return new Error(ErrorCodes.MemoryDelegation.StaleSource, "The original protected request authority is no longer live.");

        var acquired = await gate.AcquireTurnAsync(resolved.Value.Campaign, ct).ConfigureAwait(false);

        if (acquired.IsFailure) return acquired.Error;

        MemoryDelegationRoot? root = null;

        try
        {
            if (epoch.Matches(acquired.Value.Snapshot) != true || !ReferenceEquals(CovenantRequestFeatures.Authority(http), original)
                || issuer.Revalidate(original.Context).IsFailure)
                return new Error(ErrorCodes.MemoryDelegation.StaleSource, "The original protected request authority is no longer live.");

            Guid rootId = Guid.NewGuid();

            var prepared = await coordinator.PrepareAsync(rootId, body.Grant, invocation, acquired.Value, reader, ct).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            root = prepared.Value;

            var scope = new MemoryDelegationRequestScope(http, original, issuer, gate, invocation, acquired.Value, root, coordinator)
            {
                Request = resolved.Value.Request
            };

            scope.Binding = new(scope.Request, invocation, rootId, root, coordinator, scope.RevalidateAsync);

            Result valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                await scope.DisposeAsync().ConfigureAwait(false);

                return valid.Error;
            }

            http.Response.RegisterForDisposeAsync(scope);

            return scope;
        }
        catch
        {
            if (root is not null) await root.DisposeAsync().ConfigureAwait(false);

            await acquired.Value.DisposeAsync().ConfigureAwait(false);

            throw;
        }
        finally
        {
            // A failed preparation never transfers borrowed ownership to a response.
            if (root is null) await acquired.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static Result ValidateNativeDelegation(PingRequest parent, ArcanumSettings settings, IProviderHealthTracker? healthTracker = null)
    {
        if (parent.DisableAllTools || parent.ToolPolicy is ToolPolicy.NoTools
            || parent.ToolPolicy is { } policy && !Enum.IsDefined(policy))
            return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "The parent must permit native delegation.");

        if (parent.ForwardClientTools
            || parent.ToolPolicy == ToolPolicy.NoForbiddenArts
                && ToolRiskClassifier.BuildForbiddenToolNames(settings.Security?.Ward?.ForbiddenArts).Contains(ArcanumBuiltInToolNames.DelegateTask)
            || MemoryDelegationNativeCandidates.Resolve(settings, parent.Model, healthTracker).Count == 0)
            return Unavailable();

        return Result.Success();
    }

    private static Error Unavailable() => MemoryDelegationNativeCandidates.Unavailable();

    internal async ValueTask<Result> RevalidateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var epoch = Invocation.ReadAuthorityEpoch;

        if (Volatile.Read(ref _disposed) != 0 || http.RequestAborted.IsCancellationRequested
            || !ReferenceEquals(CovenantRequestFeatures.Authority(http), original)
            || original.Requirement != CovenantAuthorityRequirement.ProtectedRead
            || !original.Context.Satisfies(CovenantAuthorityRequirement.ProtectedRead)
            || original.AuthorityEpoch != original.Context.AuthorityEpoch || issuer.Revalidate(original.Context).IsFailure
            || !Invocation.CanReadCovenant || epoch is null || epoch.InstallationIdentity != original.Context.InstallationIdentity
            || epoch.RuntimeAuthorityGeneration != original.Context.RuntimeAuthorityGeneration || epoch.AuthorityEpoch != original.AuthorityEpoch
            || epoch.Matches(Lease.Snapshot) != true)
            return new Error(ErrorCodes.MemoryDelegation.StaleSource, "The original protected request authority is no longer live.");

        return await gate.ValidateTurnLeaseAsync(Lease, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await Root.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Local opening result: only the real preflight branch may carry caller diagnostic provenance.</summary>
internal sealed record MemoryDelegationOpenOutcome(Result<MemoryDelegationRequestScope> Outcome, MemoryDelegationCallerDiagnostic? Diagnostic)
{
    internal bool IsFailure => Outcome.IsFailure;

    internal Error Error => Outcome.Error;

    internal MemoryDelegationRequestScope Value => Outcome.Value;

    public static implicit operator MemoryDelegationOpenOutcome(Error error) => new(Result<MemoryDelegationRequestScope>.Failure(error), null);

    public static implicit operator MemoryDelegationOpenOutcome(MemoryDelegationRequestScope scope) => new(Result<MemoryDelegationRequestScope>.Success(scope), null);
}
