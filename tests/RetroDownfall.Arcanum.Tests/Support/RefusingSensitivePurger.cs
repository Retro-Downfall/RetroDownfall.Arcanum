using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A sensitivity purger that refuses every call with one fixed error and removes nothing.
/// </summary>
/// <remarks>
/// It stands in for the purge walk refusing a page, which is the condition a route's mapping of that
/// refusal has to be driven by: the real kernel reaches each of its refusals only through a particular
/// label state, and what the route does with the error is the same whichever one it was.
/// </remarks>
internal sealed class RefusingSensitivePurger(Error refusal) : ICovenantSensitiveArtifactPurger
{
    public ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<CovenantSensitivePurgeOutcome>.Failure(refusal));
}
