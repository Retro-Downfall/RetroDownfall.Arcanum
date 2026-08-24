using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Why one turn carries no Covenant plan.
/// </summary>
/// <remarks>
/// A closed reason rather than a null plan and a log line. "The prompt has no Covenant bytes" and
/// "the operator's profile was silently unavailable" look identical to a caller and mean completely
/// different things to an operator reading <c>context inspect</c>.
/// </remarks>
public enum CovenantTurnAbsence : byte
{
    /// <summary>A plan is present.</summary>
    None = 0,

    /// <summary>The invocation may not read Covenant content at all.</summary>
    NotEligible = 1,

    /// <summary>The capability is switched off for this installation.</summary>
    FeatureDisabled = 2,

    /// <summary>The canonical tier is absent, damaged, or closed.</summary>
    CapabilityUnavailable = 3,

    /// <summary>Campaign resolution never ran for this surface.</summary>
    NoCampaign = 4,

    /// <summary>Eligible, loaded, and empty: nothing to inject and nothing to stage against.</summary>
    Empty = 5,
}

/// <summary>
/// One logical turn's immutable Covenant plan, its lease, and its staging collector.
/// </summary>
/// <remarks>
/// Exactly one of these exists per logical live turn. Buffered inference, streaming inference,
/// provider retry, tool loops, and compression rebuilds all reuse it without a second database read,
/// which is what stops a retry from observing a mutation another turn published halfway through this
/// one (§10.13).
///
/// <para>Disposal releases the turn lease. Holding it for the whole turn is deliberate: a destructive
/// operation drains live leases before it reports that it may change anything, so a turn that gave
/// its lease back early could still be rendering Covenant bytes into a response while reset was
/// telling the operator the data was gone.</para>
/// </remarks>
public sealed class CovenantTurnContext : IAsyncDisposable
{

    private readonly CovenantTurnLease? _lease;

    private CovenantTurnContext(
        CovenantTurnAbsence absence,
        CovenantTurnPlan? plan,
        CovenantTurnLease? lease,
        ICovenantMutationCollector? collector,
        ICovenantTurnHeadProbe? headProbe,
        Guid logicalTurnId)
    {

        Absence = absence;

        Plan = plan;

        _lease = lease;

        Collector = collector;

        HeadProbe = headProbe;

        LogicalTurnId = logicalTurnId;

    }

    /// <summary>The value every turn that carries no Covenant plan uses.</summary>
    public static CovenantTurnContext Absent(CovenantTurnAbsence absence) =>
        absence is CovenantTurnAbsence.None
            ? throw new ArgumentOutOfRangeException(nameof(absence))
            : new CovenantTurnContext(absence, null, null, null, null, Guid.Empty);

    public static CovenantTurnContext ForPlan(
        CovenantTurnPlan plan,
        CovenantTurnLease lease,
        ICovenantMutationCollector? collector,
        Guid logicalTurnId,
        ICovenantTurnHeadProbe? headProbe = null)
    {

        ArgumentNullException.ThrowIfNull(plan);

        ArgumentNullException.ThrowIfNull(lease);

        return new CovenantTurnContext(
            CovenantTurnAbsence.None,
            plan,
            lease,
            collector,
            headProbe,
            CovenantValidation.RequireNonEmpty(logicalTurnId, nameof(logicalTurnId)));

    }

    public CovenantTurnAbsence Absence { get; }

    public CovenantTurnPlan? Plan { get; }

    /// <summary>The staging collector, present only for an eligible attended session-backed turn.</summary>
    public ICovenantMutationCollector? Collector { get; }

    /// <summary>The one bounded head read a staging tool call may make, scoped to this turn.</summary>
    /// <remarks>
    /// Paired with the collector on purpose: a turn that may stage a mutation is exactly the turn that
    /// may need to learn whether the key it is proposing already has a head. A turn that may not stage
    /// has no reason to probe, and carries neither.
    /// </remarks>
    public ICovenantTurnHeadProbe? HeadProbe { get; }

    public Guid LogicalTurnId { get; }

    public bool HasPlan => Plan is not null;

    /// <summary>The unpressured sections this plan would inject.</summary>
    public CovenantPromptContent PlanContent =>
        Plan is null ? CovenantPromptContent.None : CovenantPromptContent.FromPlan(Plan);

    /// <summary>Revalidates the turn lease against a change that may have closed its scope.</summary>
    public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
        _lease is null
            ? ValueTask.FromResult(Result.Success())
            : _lease.RevalidateAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {

        Collector?.Discard();

        if (_lease is not null)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }

    }

}

/// <summary>
/// Builds one immutable Covenant plan per logical turn.
/// </summary>
/// <remarks>
/// Modeled on Microsoft Agent Framework's <c>AIContextProvider</c> lifecycle shape without taking a
/// dependency on it: Arcanum's provider-neutral seam is where the exact prompt placement, authority
/// firewall, SQL transaction, and ledger accounting actually live (§10.13).
/// </remarks>
public interface ICovenantContextProvider
{

    ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
        ArcanumInvocationContext invocation,
        Guid logicalTurnId,
        CancellationToken cancellationToken);

}
