using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>Drives a real admission gate through the maintenance edges that raise its generation.</summary>
internal static class GrimoireGateTestEdges
{
    internal const string WorkDrainTimeoutCode = "Grimoire.WorkDrainTimeout";

    internal static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    /// <summary>
    /// A fully drained, proven non-destructive abort: the generation advances and the floor stays.
    /// No request or work lease may be held by the caller.
    /// </summary>
    internal static async Task DrainedAbortAsync(IGrimoireConnectionAdmissionGate gate, byte seed = 1)
    {
        long before = gate.CurrentGeneration;

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner(seed));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        await using IGrimoireClosingOwner closing = begun.Value;

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(before + 1, gate.CurrentGeneration);
    }

    /// <summary>
    /// An abort taken while a lease is still held: stage one times out, so the generation advances and the
    /// drained-abort floor is raised to it. The gate must have been built with a short work-drain checkpoint.
    /// </summary>
    internal static async Task UndrainedAbortAsync(IGrimoireConnectionAdmissionGate gate, byte seed = 2)
    {
        long before = gate.CurrentGeneration;

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner(seed));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        await using IGrimoireClosingOwner closing = begun.Value;

        Result drain = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkDrainTimeoutCode, drain.Error.Code);

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(before + 1, gate.CurrentGeneration);
    }
}
